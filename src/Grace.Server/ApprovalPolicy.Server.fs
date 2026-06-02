namespace Grace.Server

open Giraffe
open Grace.Server.Security
open Grace.Shared
open Grace.Shared.Parameters.Approval
open Grace.Shared.Utilities
open Grace.Types
open Grace.Types.Authorization
open Grace.Types.Types
open Grace.Types.Webhooks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open NodaTime
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Threading.Tasks

module ApprovalStore =

    type ApprovalPolicyDto = Grace.Types.Webhooks.ApprovalPolicy
    type ApprovalRequestDto = Grace.Types.Webhooks.ApprovalRequest

    let private policies = ConcurrentDictionary<ApprovalPolicyId, ApprovalPolicyDto>()
    let private requests = ConcurrentDictionary<ApprovalRequestId, ApprovalRequestDto>()
    let private history = ConcurrentDictionary<ApprovalRequestId, ResizeArray<ApprovalRequestDto>>()

    let private requestSeedDirectory = Path.Combine(Path.GetTempPath(), "Grace.ApprovalRequestSeeds")

    let private requestSeedPath approvalRequestId = Path.Combine(requestSeedDirectory, $"{approvalRequestId:N}.json")

    let private persistSeededRequest (request: ApprovalRequestDto) =
        Directory.CreateDirectory(requestSeedDirectory)
        |> ignore

        File.WriteAllText(requestSeedPath request.ApprovalRequestId, JsonSerializer.Serialize(request, Constants.JsonSerializerOptions))

    let private tryReadSeededRequest approvalRequestId =
        let path = requestSeedPath approvalRequestId

        if File.Exists path then
            try
                let request = JsonSerializer.Deserialize<ApprovalRequestDto>(File.ReadAllText path, Constants.JsonSerializerOptions)

                if isNull (box request) then
                    None
                else
                    requests[approvalRequestId] <- request
                    Some request
            with
            | _ -> None
        else
            None

    let clearForTests () =
        policies.Clear()
        requests.Clear()
        history.Clear()

        if Directory.Exists requestSeedDirectory then
            Directory.Delete(requestSeedDirectory, true)

    let upsertPolicy (policy: ApprovalPolicyDto) =
        policies[policy.ApprovalPolicyId] <- policy
        policy

    let tryGetPolicy approvalPolicyId =
        match policies.TryGetValue approvalPolicyId with
        | true, policy -> Some policy
        | _ -> None

    let listPolicies includeDeleted =
        policies.Values
        |> Seq.filter (fun policy ->
            includeDeleted
            || policy.Status <> ApprovalPolicyStatus.Deleted)
        |> Seq.toArray
        :> IReadOnlyList<ApprovalPolicyDto>

    let seedGeneratedRequest (request: ApprovalRequestDto) =
        requests[request.ApprovalRequestId] <- request
        persistSeededRequest request
        let entries = history.GetOrAdd(request.ApprovalRequestId, (fun _ -> ResizeArray()))
        entries.Add request
        request

    let tryGetRequest approvalRequestId =
        match requests.TryGetValue approvalRequestId with
        | true, request -> Some request
        | _ -> tryReadSeededRequest approvalRequestId

    let updateRequest (request: ApprovalRequestDto) =
        requests[request.ApprovalRequestId] <- request
        persistSeededRequest request
        let entries = history.GetOrAdd(request.ApprovalRequestId, (fun _ -> ResizeArray()))
        entries.Add request
        request

    let listRequests includeTerminal =
        requests.Values
        |> Seq.filter (fun request -> includeTerminal || not request.Status.IsTerminal)
        |> Seq.toArray
        :> IReadOnlyList<ApprovalRequestDto>

    let requestHistory approvalRequestId =
        match history.TryGetValue approvalRequestId with
        | true, entries -> entries.ToArray() :> IReadOnlyList<ApprovalRequestDto>
        | _ -> Array.empty<ApprovalRequestDto> :> IReadOnlyList<ApprovalRequestDto>

module ApprovalCommon =

    let tryParseGuid value =
        let mutable parsed = Guid.Empty

        if String.IsNullOrWhiteSpace value |> not
           && Guid.TryParse(value, &parsed)
           && parsed <> Guid.Empty then
            Some parsed
        else
            None

    let scopeFromPolicyParameters (parameters: ApprovalPolicyParameters) =
        let ownerId =
            tryParseGuid parameters.OwnerId
            |> Option.defaultValue OwnerId.Empty

        let organizationId =
            tryParseGuid parameters.OrganizationId
            |> Option.defaultValue OrganizationId.Empty

        let repositoryId =
            tryParseGuid parameters.RepositoryId
            |> Option.defaultValue RepositoryId.Empty

        let targetBranchId =
            tryParseGuid parameters.TargetBranchId
            |> Option.defaultValue BranchId.Empty

        { ApprovalScope.Default with OwnerId = ownerId; OrganizationId = organizationId; RepositoryId = repositoryId; TargetBranchId = targetBranchId }

    let scopeFromRequestParameters (parameters: ApprovalRequestParameters) =
        let ownerId =
            tryParseGuid parameters.OwnerId
            |> Option.defaultValue OwnerId.Empty

        let organizationId =
            tryParseGuid parameters.OrganizationId
            |> Option.defaultValue OrganizationId.Empty

        let repositoryId =
            tryParseGuid parameters.RepositoryId
            |> Option.defaultValue RepositoryId.Empty

        let targetBranchId =
            tryParseGuid parameters.TargetBranchId
            |> Option.defaultValue BranchId.Empty

        { ApprovalScope.Default with OwnerId = ownerId; OrganizationId = organizationId; RepositoryId = repositoryId; TargetBranchId = targetBranchId }

    let resourceFromApprovalScope (scope: ApprovalScope) =
        if scope.TargetBranchId <> BranchId.Empty then
            Resource.Branch(scope.OwnerId, scope.OrganizationId, scope.RepositoryId, scope.TargetBranchId)
        else
            Resource.Repository(scope.OwnerId, scope.OrganizationId, scope.RepositoryId)

    let currentUserId (context: HttpContext) =
        PrincipalMapper.tryGetUserId context.User
        |> Option.defaultValue "unknown"
        |> UserId

    let error context message = GraceError.Create message (Services.getCorrelationId context)

module ApprovalPolicy =

    open ApprovalCommon

    let private validateNotificationUrl (context: HttpContext) (parameters: CreateApprovalPolicyParameters) =
        if String.IsNullOrWhiteSpace parameters.NotificationUrl then
            Ok None
        else
            let configuration = context.RequestServices.GetRequiredService<IConfiguration>()
            let hostEnvironment = context.RequestServices.GetService<IHostEnvironment>()

            let request: OutboundUrlSafety.ValidationRequest =
                {
                    Url = parameters.NotificationUrl
                    RequestedSafety = parameters.NotificationUrlSafety
                    AcknowledgeUnsafeLocalDevelopment = parameters.AcknowledgeUnsafeLocalDevelopment
                }

            match OutboundUrlSafety.validate hostEnvironment configuration request with
            | Ok validated -> Ok(Some validated.ScopedUrl)
            | Error failure -> Error $"NotificationUrl is not allowed: {failure}."

    let private buildPolicy context approvalPolicyId version status (parameters: CreateApprovalPolicyParameters) =
        task {
            match validateNotificationUrl context parameters with
            | Error message -> return Error message
            | Ok notificationUrl ->
                let scope = { scopeFromPolicyParameters parameters with ApprovalPolicyId = Some approvalPolicyId; ApprovalPolicyVersion = Some version }

                let timeoutSeconds =
                    if parameters.TimeoutSeconds.HasValue then
                        Some parameters.TimeoutSeconds.Value
                    else
                        None

                return
                    Ok
                        { Grace.Types.Webhooks.ApprovalPolicy.Default with
                            ApprovalPolicyId = approvalPolicyId
                            Version = version
                            Name = parameters.Name
                            Subject = parameters.Subject
                            Scope = scope
                            RequiredResponder = parameters.RequiredResponder
                            NotificationUrl = notificationUrl
                            TimeoutSeconds = timeoutSeconds
                            OnTimeout = parameters.OnTimeout
                            Status = status
                            CreatedBy = currentUserId context
                            CreatedAt = getCurrentInstant ()
                        }
        }

    let Create: HttpHandler =
        fun _ context ->
            task {
                let! parameters = Services.parse<CreateApprovalPolicyParameters> context
                let approvalPolicyId = Guid.NewGuid()

                match! buildPolicy context approvalPolicyId 1 ApprovalPolicyStatus.Disabled parameters with
                | Error message ->
                    return!
                        context
                        |> Services.result400BadRequest (error context message)
                | Ok policy ->
                    return!
                        context
                        |> Services.result200Ok (ApprovalStore.upsertPolicy policy)
            }

    let List: HttpHandler =
        fun _ context ->
            task {
                let! parameters = Services.parse<ListApprovalPoliciesParameters> context

                return!
                    context
                    |> Services.result200Ok (ApprovalStore.listPolicies parameters.IncludeDeleted)
            }

    let Show: HttpHandler =
        fun _ context ->
            task {
                let! parameters = Services.parse<ShowApprovalPolicyParameters> context

                match tryParseGuid parameters.ApprovalPolicyId
                      |> Option.bind ApprovalStore.tryGetPolicy
                    with
                | Some policy -> return! context |> Services.result200Ok policy
                | None -> return! Services.result404NotFound context
            }

    let Update: HttpHandler =
        fun _ context ->
            task {
                let! parameters = Services.parse<UpdateApprovalPolicyParameters> context

                match tryParseGuid parameters.ApprovalPolicyId
                      |> Option.bind ApprovalStore.tryGetPolicy
                    with
                | None -> return! Services.result404NotFound context
                | Some existing ->
                    match! buildPolicy context existing.ApprovalPolicyId (existing.Version + 1) existing.Status parameters with
                    | Error message ->
                        return!
                            context
                            |> Services.result400BadRequest (error context message)
                    | Ok policy ->
                        return!
                            context
                            |> Services.result200Ok (
                                ApprovalStore.upsertPolicy
                                    { policy with CreatedBy = existing.CreatedBy; CreatedAt = existing.CreatedAt; UpdatedAt = Some(getCurrentInstant ()) }
                            )
            }

    let private setStatus status : HttpHandler =
        fun _ context ->
            task {
                let! parameters = Services.parse<ApprovalPolicyParameters> context

                match tryParseGuid parameters.ApprovalPolicyId
                      |> Option.bind ApprovalStore.tryGetPolicy
                    with
                | None -> return! Services.result404NotFound context
                | Some policy ->
                    return!
                        context
                        |> Services.result200Ok (ApprovalStore.upsertPolicy { policy with Status = status; UpdatedAt = Some(getCurrentInstant ()) })
            }

    let Enable: HttpHandler = setStatus ApprovalPolicyStatus.Enabled

    let Disable: HttpHandler = setStatus ApprovalPolicyStatus.Disabled

    let Delete: HttpHandler = setStatus ApprovalPolicyStatus.Deleted

    let Evaluate: HttpHandler =
        fun _ context ->
            task {
                let! parameters = Services.parse<EvaluateApprovalPolicyParameters> context
                let scope = scopeFromPolicyParameters parameters

                let matches =
                    ApprovalStore.listPolicies false
                    |> Seq.filter (fun policy ->
                        policy.Status = ApprovalPolicyStatus.Enabled
                        && policy.Scope.OwnerId = scope.OwnerId
                        && policy.Scope.OrganizationId = scope.OrganizationId
                        && policy.Scope.RepositoryId = scope.RepositoryId
                        && policy.Scope.TargetBranchId = scope.TargetBranchId
                        && (String.IsNullOrWhiteSpace parameters.Subject
                            || policy.Subject = parameters.Subject))
                    |> Seq.toArray
                    :> IReadOnlyList<Grace.Types.Webhooks.ApprovalPolicy>

                return! context |> Services.result200Ok matches
            }
