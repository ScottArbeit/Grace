namespace Grace.Server

open Giraffe
open Grace.Actors.Interfaces
open Grace.Actors.Extensions
open Grace.Server.Security
open Grace.Shared
open Grace.Shared.Parameters.Approval
open Grace.Shared.Utilities
open Grace.Types
open Grace.Types.Authorization
open Grace.Types.Common
open Grace.Types.Webhooks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open NodaTime
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks

/// Contains Grace Server approval store behavior and supporting helpers.
module ApprovalStore =

    /// Represents approval policy dto used by Grace Server APIs and background services.
    type ApprovalPolicyDto = Grace.Types.Webhooks.ApprovalPolicy
    /// Represents approval request dto used by Grace Server APIs and background services.
    type ApprovalRequestDto = Grace.Types.Webhooks.ApprovalRequest

    let private policies = ConcurrentDictionary<ApprovalPolicyId, ApprovalPolicyDto>()

    /// Computes scope matches data used by Grace Server.
    let private scopeMatches (expected: ApprovalScope) (actual: ApprovalScope) =
        isNull (box actual) |> not
        && actual.OwnerId = expected.OwnerId
        && actual.OrganizationId = expected.OrganizationId
        && actual.RepositoryId = expected.RepositoryId
        && actual.TargetBranchId = expected.TargetBranchId

    /// Implements generated request matches for the server request pipeline.
    let private generatedRequestMatches (candidate: ApprovalRequestDto) (existing: ApprovalRequestDto) =
        existing.ApprovalPolicyId = candidate.ApprovalPolicyId
        && existing.ApprovalPolicyVersion = candidate.ApprovalPolicyVersion
        && existing.Subject = candidate.Subject
        && existing.Scope = candidate.Scope
        && existing.RequiredResponder = candidate.RequiredResponder

    /// Implements request is complete for the server request pipeline.
    let private requestIsComplete (request: ApprovalRequestDto) =
        request.ApprovalRequestId
        <> ApprovalRequestId.Empty
        && (isNull (box request.Scope) |> not)
        && (isNull (box request.Status) |> not)
        && (String.IsNullOrWhiteSpace request.RequiredResponder
            |> not)

    /// Implements usable events for the server request pipeline.
    let private usableEvents (events: IReadOnlyList<ApprovalRequestEvent>) =
        events
        |> Seq.filter (fun event -> isNull (box event) |> not)
        |> Seq.filter (fun event -> isNull (box event.Event) |> not)

    /// Implements request from events for the server request pipeline.
    let private requestFromEvents (events: IReadOnlyList<ApprovalRequestEvent>) =
        let request =
            events
            |> usableEvents
            |> Seq.fold (fun current event -> ApprovalRequest.UpdateDto event current) ApprovalRequest.Default

        if request.ApprovalRequestId = ApprovalRequestId.Empty then
            None
        else
            Some request

    /// Coordinates clear for tests processing for Grace Server.
    let clearForTests () = policies.Clear()

    /// Coordinates upsert policy processing for Grace Server.
    let upsertPolicy (policy: ApprovalPolicyDto) =
        policies[policy.ApprovalPolicyId] <- policy
        policy

    /// Gets try get policy data needed by the server flow.
    let tryGetPolicy approvalPolicyId =
        match policies.TryGetValue approvalPolicyId with
        | true, policy -> Some policy
        | _ -> None

    /// Lists list policies data for the server response.
    let listPolicies scope includeDeleted =
        policies.Values
        |> Seq.filter (fun policy ->
            scopeMatches scope policy.Scope
            && (includeDeleted
                || policy.Status <> ApprovalPolicyStatus.Deleted))
        |> Seq.toArray
        :> IReadOnlyList<ApprovalPolicyDto>

    /// Computes metadata data used by Grace Server.
    let private metadata correlationId principal =
        let eventMetadata = EventMetadata.New correlationId principal
        eventMetadata

    /// Prefixes a text segment with its length so generated approval identifiers cannot collide across adjacent fields.
    let private canonicalSegment (value: string) =
        let segment = if isNull value then String.Empty else value

        $"{segment.Length}:{segment}"

    /// Formats a GUID in lower-case D form before it is fed into an approval identifier seed.
    let private canonicalGuid (value: Guid) = value.ToString("D").ToLowerInvariant()

    /// Converts optional GUID segments into stable seed text for deterministic approval identifiers.
    let private canonicalOptionalGuid (value: Guid option) =
        match value with
        | Some guid -> canonicalGuid guid
        | None -> String.Empty

    /// Converts optional integer segments into culture-invariant seed text for deterministic approval identifiers.
    let private canonicalOptionalInt (value: int option) =
        match value with
        | Some attempt -> attempt.ToString(CultureInfo.InvariantCulture)
        | None -> String.Empty

    /// Derives a version-marked deterministic GUID from the approval-request seed hash.
    let private createDeterministicGuid (seed: string) =
        let seedBytes = Encoding.UTF8.GetBytes(seed)

        use hasher = SHA256.Create()
        let hash = hasher.ComputeHash(seedBytes)
        let guidBytes = hash[0..15]
        guidBytes[6] <- (guidBytes[6] &&& 0x0Fuy) ||| 0x50uy
        guidBytes[8] <- (guidBytes[8] &&& 0x3Fuy) ||| 0x80uy
        Guid(guidBytes)

    /// Combines approval policy, scope, subject, and operation details into the deterministic request-id seed.
    let buildGeneratedApprovalRequestId (request: ApprovalRequestDto) =
        let scope = if isNull (box request.Scope) then ApprovalScope.Default else request.Scope

        [|
            "grace.approval-request.generated.v1"
            canonicalGuid request.ApprovalPolicyId
            request.ApprovalPolicyVersion.ToString(CultureInfo.InvariantCulture)
            request.Subject
            canonicalGuid scope.OwnerId
            canonicalGuid scope.OrganizationId
            canonicalGuid scope.RepositoryId
            canonicalGuid scope.TargetBranchId
            canonicalOptionalGuid scope.PromotionSetId
            canonicalOptionalInt scope.StepsComputationAttempt
            request.RequiredResponder
        |]
        |> Array.map canonicalSegment
        |> String.concat "|"
        |> createDeterministicGuid

    /// Normalizes normalize request data for stable server comparisons.
    let private normalizeRequest approvalRequestId fallbackScope (approvalRequest: ApprovalRequestDto) =
        { approvalRequest with
            ApprovalRequestId =
                if approvalRequest.ApprovalRequestId = ApprovalRequestId.Empty then
                    approvalRequestId
                else
                    approvalRequest.ApprovalRequestId
            Scope =
                if isNull (box approvalRequest.Scope) then
                    fallbackScope
                else
                    approvalRequest.Scope
            Status =
                if isNull (box approvalRequest.Status) then
                    ApprovalRequestStatus.Pending
                else
                    approvalRequest.Status
        }

    /// Implements request from json for the server request pipeline.
    let private requestFromJson (requestJson: string option) =
        requestJson
        |> Option.bind (fun json ->
            if String.IsNullOrWhiteSpace json then
                None
            else
                Some(deserialize<ApprovalRequestDto> json))

    /// Gets try get request from actor async data needed by the server flow.
    let private tryGetRequestFromActorAsync approvalRequestId fallbackScope correlationId =
        task {
            let actor = ActorProxy.ApprovalRequest.CreateActorProxy approvalRequestId fallbackScope.RepositoryId correlationId
            let! requestJson = actor.GetJson correlationId

            match requestFromJson requestJson with
            | Some approvalRequest when requestIsComplete approvalRequest -> return Some(normalizeRequest approvalRequestId fallbackScope approvalRequest)
            | Some _ ->
                let! events = actor.GetEvents correlationId

                return
                    requestFromEvents events
                    |> Option.map (normalizeRequest approvalRequestId fallbackScope)
            | None -> return None
        }

    /// Gets try get request async data needed by the server flow.
    let tryGetRequestAsync approvalRequestId fallbackScope correlationId = tryGetRequestFromActorAsync approvalRequestId fallbackScope correlationId

    /// Implements register generated index request async for the server request pipeline.
    let private registerGeneratedIndexRequestAsync (indexActor: IApprovalRequestIndexActor) (request: ApprovalRequestDto) correlationId =
        indexActor.RegisterGeneratedRequest(
            request.ApprovalRequestId,
            request.ApprovalPolicyId,
            request.ApprovalPolicyVersion,
            request.Subject,
            request.Scope.OwnerId,
            request.Scope.OrganizationId,
            request.Scope.RepositoryId,
            request.Scope.TargetBranchId,
            request.Scope.PromotionSetId,
            request.Scope.StepsComputationAttempt,
            request.RequiredResponder,
            $"{request.CreatedBy}",
            metadata correlationId "system"
        )

    /// Attempts to find generated request async and returns an option or result instead of throwing.
    let private tryFindGeneratedRequestAsync (candidate: ApprovalRequestDto) correlationId =
        task {
            let indexActor = ActorProxy.ApprovalRequest.CreateIndexActorProxy candidate.Scope correlationId
            let! requestIds = indexActor.List correlationId
            let mutable index = 0
            let mutable matchedRequest = None

            while index < requestIds.Length && matchedRequest.IsNone do
                let! indexedRequestJson = indexActor.GetRequestJson requestIds[index] correlationId

                let! request =
                    match requestFromJson indexedRequestJson with
                    | Some request -> Task.FromResult(Some request)
                    | None -> tryGetRequestAsync requestIds[index] candidate.Scope correlationId

                match request with
                | Some existing when generatedRequestMatches candidate existing -> matchedRequest <- Some existing
                | _ -> ()

                index <- index + 1

            return matchedRequest
        }

    /// Coordinates seed generated request async processing for Grace Server.
    let seedGeneratedRequestAsync (request: ApprovalRequestDto) correlationId =
        task {
            let approvalRequestId =
                if request.ApprovalRequestId = Guid.Empty then
                    buildGeneratedApprovalRequestId request
                else
                    request.ApprovalRequestId

            let requestToCreate = { request with ApprovalRequestId = approvalRequestId }

            match! tryFindGeneratedRequestAsync requestToCreate correlationId with
            | Some existingRequest -> return Ok existingRequest
            | None ->
                let actor = ActorProxy.ApprovalRequest.CreateActorProxy requestToCreate.ApprovalRequestId requestToCreate.Scope.RepositoryId correlationId

                match!
                    actor.CreateGenerated
                        (
                            requestToCreate.ApprovalRequestId,
                            requestToCreate.ApprovalPolicyId,
                            requestToCreate.ApprovalPolicyVersion,
                            requestToCreate.Subject,
                            requestToCreate.Scope.OwnerId,
                            requestToCreate.Scope.OrganizationId,
                            requestToCreate.Scope.RepositoryId,
                            requestToCreate.Scope.TargetBranchId,
                            requestToCreate.Scope.PromotionSetId,
                            requestToCreate.Scope.StepsComputationAttempt,
                            requestToCreate.RequiredResponder,
                            $"{requestToCreate.CreatedBy}",
                            metadata correlationId $"{requestToCreate.CreatedBy}"
                        )
                    with
                | Error error -> return Error error
                | Ok result ->
                    let indexActor = ActorProxy.ApprovalRequest.CreateIndexActorProxy requestToCreate.Scope correlationId

                    let storedRequest =
                        if requestIsComplete result.ReturnValue.Request then
                            result.ReturnValue.Request
                        else
                            requestToCreate

                    let! indexResult = registerGeneratedIndexRequestAsync indexActor storedRequest correlationId

                    match indexResult with
                    | Error error -> return Error error
                    | Ok _ -> return Ok storedRequest
        }

    /// Coordinates handle request command async processing for Grace Server.
    let handleRequestCommandAsync approvalRequestId repositoryId command correlationId principal =
        task {
            let actor = ActorProxy.ApprovalRequest.CreateActorProxy approvalRequestId repositoryId correlationId

            match command with
            | ApprovalRequestCommand.RecordDecision decision ->
                let! result =
                    actor.RecordDecisionGenerated(
                        $"{decision.Decision}",
                        $"{decision.DecidedBy}",
                        decision.Reason,
                        decision.ClientDecisionId,
                        metadata correlationId principal
                    )

                return result
            | _ ->
                let! result = actor.Handle command (metadata correlationId principal)
                return result
        }

    /// Lists list requests async data for the server response.
    let listRequestsAsync scope includeTerminal correlationId =
        task {
            let indexActor = ActorProxy.ApprovalRequest.CreateIndexActorProxy scope correlationId
            let! requestIds = indexActor.List correlationId
            let requests = ResizeArray<ApprovalRequestDto>()
            let mutable index = 0

            while index < requestIds.Length do
                let! request = tryGetRequestAsync requestIds[index] scope correlationId

                match request with
                | Some approvalRequest when
                    scopeMatches scope approvalRequest.Scope
                    && (includeTerminal
                        || not approvalRequest.Status.IsTerminal)
                    ->
                    requests.Add approvalRequest
                | _ -> ()

                index <- index + 1

            return requests.ToArray() :> IReadOnlyList<ApprovalRequestDto>
        }

    /// Implements request history async for the server request pipeline.
    let requestHistoryAsync approvalRequestId fallbackScope correlationId =
        task {
            match! tryGetRequestAsync approvalRequestId fallbackScope correlationId with
            | None -> return Array.empty<ApprovalRequestDto> :> IReadOnlyList<ApprovalRequestDto>
            | Some request ->
                let actor = ActorProxy.ApprovalRequest.CreateActorProxy approvalRequestId request.Scope.RepositoryId correlationId
                let! historyJson = actor.GetHistoryJson correlationId
                return deserialize<ApprovalRequestDto array> historyJson :> IReadOnlyList<ApprovalRequestDto>
        }

/// Contains Grace Server approval common behavior and supporting helpers.
module ApprovalCommon =

    /// Parses try parse guid input into the server model.
    let tryParseGuid value =
        let mutable parsed = Guid.Empty

        if String.IsNullOrWhiteSpace value |> not
           && Guid.TryParse(value, &parsed)
           && parsed <> Guid.Empty then
            Some parsed
        else
            None

    /// Computes scope from policy parameters data used by Grace Server.
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

    /// Computes scope from request parameters data used by Grace Server.
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

    /// Computes resource from approval scope data used by Grace Server.
    let resourceFromApprovalScope (scope: ApprovalScope) =
        if scope.TargetBranchId <> BranchId.Empty then
            Resource.Branch(scope.OwnerId, scope.OrganizationId, scope.RepositoryId, scope.TargetBranchId)
        else
            Resource.Repository(scope.OwnerId, scope.OrganizationId, scope.RepositoryId)

    /// Computes scope equals data used by Grace Server.
    let scopeEquals left right =
        left.OwnerId = right.OwnerId
        && left.OrganizationId = right.OrganizationId
        && left.RepositoryId = right.RepositoryId
        && left.TargetBranchId = right.TargetBranchId

    /// Implements current user id for the server request pipeline.
    let currentUserId (context: HttpContext) =
        PrincipalMapper.tryGetUserId context.User
        |> Option.defaultValue "unknown"
        |> UserId

    /// Computes error data used by Grace Server.
    let error context message = GraceError.Create message (Services.getCorrelationId context)

/// Contains Grace Server approval policy behavior and supporting helpers.
module ApprovalPolicy =

    open ApprovalCommon

    /// Validates validate notification url inputs before server processing continues.
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

    /// Validates creation parameters and materializes the approval policy record that will be sent to the actor.
    let private buildPolicy context approvalPolicyId version status (parameters: CreateApprovalPolicyParameters) =
        task {
            if String.IsNullOrWhiteSpace parameters.RequiredResponder then
                return Error "RequiredResponder is required."
            else
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
                                RequiredResponder = parameters.RequiredResponder.Trim()
                                NotificationUrl = notificationUrl
                                TimeoutSeconds = timeoutSeconds
                                OnTimeout = parameters.OnTimeout
                                Status = status
                                CreatedBy = currentUserId context
                                CreatedAt = getCurrentInstant ()
                            }
        }

    /// Implements policy id from context for the server request pipeline.
    let private policyIdFromContext<'T when 'T :> ApprovalPolicyParameters> (context: HttpContext) =
        task {
            context.Request.EnableBuffering()
            let! parameters = context.BindJsonAsync<'T>()

            context.Request.Body.Seek(0L, IO.SeekOrigin.Begin)
            |> ignore

            return
                tryParseGuid parameters.ApprovalPolicyId
                |> Option.bind ApprovalStore.tryGetPolicy
        }

    /// Resolves resolve stored policy for manage data from request or repository state.
    let resolveStoredPolicyForManage<'T when 'T :> ApprovalPolicyParameters> (context: HttpContext) =
        task {
            let! policy = policyIdFromContext<'T> context

            return
                match policy with
                | Some approvalPolicy -> Ok(Operation.ApprovalPolicyManage, resourceFromApprovalScope approvalPolicy.Scope)
                | None -> Error(error context "Approval policy was not found.")
        }

    /// Handles the Grace Server create request.
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

    /// Handles the Grace Server list request.
    let List: HttpHandler =
        fun _ context ->
            task {
                let! parameters = Services.parse<ListApprovalPoliciesParameters> context
                let scope = scopeFromPolicyParameters parameters

                return!
                    context
                    |> Services.result200Ok (ApprovalStore.listPolicies scope parameters.IncludeDeleted)
            }

    /// Handles the Grace Server show request.
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

    /// Handles the Grace Server update request.
    let Update: HttpHandler =
        fun _ context ->
            task {
                let! parameters = Services.parse<UpdateApprovalPolicyParameters> context

                match tryParseGuid parameters.ApprovalPolicyId
                      |> Option.bind ApprovalStore.tryGetPolicy
                    with
                | None -> return! Services.result404NotFound context
                | Some existing ->
                    let requestedScope = scopeFromPolicyParameters parameters

                    if scopeEquals requestedScope existing.Scope |> not then
                        return!
                            context
                            |> Services.result400BadRequest (error context "Approval policy scope cannot be changed.")
                    else
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

    /// Handles the Grace Server set status request.
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

    /// Handles the Grace Server enable request.
    let Enable: HttpHandler = setStatus ApprovalPolicyStatus.Enabled

    /// Handles the Grace Server disable request.
    let Disable: HttpHandler = setStatus ApprovalPolicyStatus.Disabled

    /// Handles the Grace Server delete request.
    let Delete: HttpHandler = setStatus ApprovalPolicyStatus.Deleted

    /// Handles the Grace Server evaluate request.
    let Evaluate: HttpHandler =
        fun _ context ->
            task {
                let! parameters = Services.parse<EvaluateApprovalPolicyParameters> context
                let scope = scopeFromPolicyParameters parameters

                let matches =
                    ApprovalStore.listPolicies scope false
                    |> Seq.filter (fun policy ->
                        policy.Status = ApprovalPolicyStatus.Enabled
                        && (String.IsNullOrWhiteSpace parameters.Subject
                            || policy.Subject = parameters.Subject))
                    |> Seq.toArray
                    :> IReadOnlyList<Grace.Types.Webhooks.ApprovalPolicy>

                return! context |> Services.result200Ok matches
            }
