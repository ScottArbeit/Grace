namespace Grace.Server

open Giraffe
open Grace.Actors.Constants
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Server.ApplicationContext
open Grace.Server.Services
open Grace.Shared
open Grace.Shared.Extensions
open Grace.Shared.Parameters.Policy
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors
open Grace.Shared.Validation.Utilities
open Grace.Types.Policy
open Grace.Types.Common
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Threading.Tasks

/// Contains Grace Server policy behavior and supporting helpers.
module Policy =
    /// Represents validations used by Grace Server APIs and background services.
    type Validations<'T when 'T :> PolicyParameters> = 'T -> ValueTask<Result<unit, PolicyError>> array

    /// Represents seed policy snapshot parameters used by Grace Server APIs and background services.
    type SeedPolicySnapshotParameters() =
        inherit PolicyParameters()
        /// Identifies the default policy record returned by policy endpoints.
        member val public PolicySnapshotId = String.Empty with get, set

    let log = ApplicationContext.loggerFactory.CreateLogger("Policy.Server")

    let activitySource = new ActivitySource("Policy")

    let private seededSnapshots = ConcurrentDictionary<BranchId, PolicySnapshot>()

    /// Gets try get seeded snapshot data needed by the server flow.
    let internal tryGetSeededSnapshot (targetBranchId: BranchId) =
        match seededSnapshots.TryGetValue targetBranchId with
        | true, snapshot -> Some snapshot
        | _ -> None

    /// Determines whether usable snapshot.
    let internal isUsableSnapshot (snapshot: PolicySnapshot) = not (String.IsNullOrEmpty snapshot.PolicySnapshotId)

    /// Coordinates seed enabled processing for Grace Server.
    let private seedEnabled () =
        /// Determines whether development.
        let isDevelopment value = String.Equals(value, "Development", StringComparison.OrdinalIgnoreCase)

        Environment.GetEnvironmentVariable("GRACE_TESTING") = "1"
        || isDevelopment (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"))
        || isDevelopment (Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"))

    /// Coordinates process command processing for Grace Server.
    let processCommand<'T when 'T :> PolicyParameters> (context: HttpContext) (validations: Validations<'T>) (command: 'T -> ValueTask<PolicyCommand>) =
        task {
            let commandName = context.Items["Command"] :?> string
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let parameterDictionary = Dictionary<string, obj>()

            try
                use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
                let! parameters = context |> parse<'T>
                parameterDictionary.AddRange(getParametersAsDictionary parameters)

                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                /// Coordinates handle command processing for Grace Server.
                let handleCommand targetBranchId cmd =
                    task {
                        let actorProxy = Policy.CreateActorProxy targetBranchId graceIds.RepositoryId correlationId
                        let metadata = createMetadata context

                        match! actorProxy.Handle cmd metadata with
                        | Ok graceReturnValue ->
                            graceReturnValue
                                .enhance(parameterDictionary)
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof BranchId, targetBranchId)
                                .enhance("Command", commandName)
                                .enhance ("Path", context.Request.Path.Value)
                            |> ignore

                            return! context |> result200Ok graceReturnValue
                        | Error graceError ->
                            graceError
                                .enhance(parameterDictionary)
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof BranchId, targetBranchId)
                                .enhance("Command", commandName)
                                .enhance ("Path", context.Request.Path.Value)
                            |> ignore

                            return! context |> result400BadRequest graceError
                    }

                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    let! cmd = command parameters
                    let targetBranchId = Guid.Parse(parameters.TargetBranchId)
                    return! handleCommand targetBranchId cmd
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = PolicyError.getErrorMessage error

                    let graceError =
                        (GraceError.Create errorMessage correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance("Command", commandName)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result400BadRequest graceError
            with
            | ex ->
                log.LogError(
                    ex,
                    "{CurrentInstant}: Exception in Policy.Server.processCommand. CorrelationId: {correlationId}.",
                    getCurrentInstantExtended (),
                    correlationId
                )

                let graceError =
                    (GraceError.CreateWithException ex String.Empty correlationId)
                        .enhance(parameterDictionary)
                        .enhance(nameof OwnerId, graceIds.OwnerId)
                        .enhance(nameof OrganizationId, graceIds.OrganizationId)
                        .enhance(nameof RepositoryId, graceIds.RepositoryId)
                        .enhance ("Path", context.Request.Path.Value)

                return! context |> result500ServerError graceError
        }

    /// Coordinates process query processing for Grace Server.
    let processQuery<'T, 'U when 'T :> PolicyParameters>
        (context: HttpContext)
        (parameters: 'T)
        (validations: Validations<'T>)
        (query: QueryResult<IPolicyActor, 'U>)
        =
        task {
            use activity = activitySource.StartActivity("processQuery", ActivityKind.Server)
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let parameterDictionary = getParametersAsDictionary parameters

            try
                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    let targetBranchId = Guid.Parse(parameters.TargetBranchId)
                    let actorProxy = Policy.CreateActorProxy targetBranchId graceIds.RepositoryId correlationId
                    let! queryResult = query context 0 actorProxy

                    let graceReturnValue =
                        (GraceReturnValue.Create queryResult correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance(nameof BranchId, targetBranchId)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result200Ok graceReturnValue
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = PolicyError.getErrorMessage error

                    let graceError =
                        (GraceError.Create errorMessage correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result400BadRequest graceError
            with
            | ex ->
                let graceError =
                    (GraceError.CreateWithException ex String.Empty correlationId)
                        .enhance(parameterDictionary)
                        .enhance(nameof OwnerId, graceIds.OwnerId)
                        .enhance(nameof OrganizationId, graceIds.OrganizationId)
                        .enhance(nameof RepositoryId, graceIds.RepositoryId)
                        .enhance ("Path", context.Request.Path.Value)

                return! context |> result500ServerError graceError
        }

    /// Validates validate acknowledge parameters inputs before server processing continues.
    let internal validateAcknowledgeParameters (parameters: AcknowledgePolicyParameters) =
        [|
            Guid.isValidAndNotEmptyGuid parameters.TargetBranchId PolicyError.InvalidTargetBranchId
            String.isNotEmpty parameters.PolicySnapshotId PolicyError.InvalidPolicySnapshotId
        |]

    /// Validates validate seed snapshot parameters inputs before server processing continues.
    let internal validateSeedSnapshotParameters (parameters: SeedPolicySnapshotParameters) =
        [|
            Guid.isValidAndNotEmptyGuid parameters.TargetBranchId PolicyError.InvalidTargetBranchId
            String.isNotEmpty parameters.PolicySnapshotId PolicyError.InvalidPolicySnapshotId
        |]

    /// Handles the Grace Server seed snapshot request.
    let SeedSnapshot: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                if seedEnabled () |> not then
                    return! result404NotFound context
                else
                    let graceIds = getGraceIds context
                    let correlationId = getCorrelationId context
                    let! parameters = context |> parse<SeedPolicySnapshotParameters>

                    parameters.OwnerId <- graceIds.OwnerIdString
                    parameters.OrganizationId <- graceIds.OrganizationIdString
                    parameters.RepositoryId <- graceIds.RepositoryIdString

                    let validationResults = validateSeedSnapshotParameters parameters
                    let! validationsPassed = validationResults |> allPass

                    if validationsPassed then
                        let targetBranchId = Guid.Parse(parameters.TargetBranchId)
                        let policySnapshotId = PolicySnapshotId parameters.PolicySnapshotId

                        let snapshot =
                            { PolicySnapshot.Default with
                                PolicySnapshotId = policySnapshotId
                                OwnerId = graceIds.OwnerId
                                OrganizationId = graceIds.OrganizationId
                                RepositoryId = graceIds.RepositoryId
                                TargetBranchId = targetBranchId
                                ParserVersion = "Grace.Server.Tests"
                                SourceHash = policySnapshotId
                                CreatedAt = getCurrentInstant ()
                            }

                        seededSnapshots[targetBranchId] <- snapshot

                        let graceReturnValue =
                            (GraceReturnValue.Create "Policy snapshot seeded." correlationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance (nameof PolicySnapshotId, policySnapshotId)

                        return! context |> result200Ok graceReturnValue
                    else
                        let! error = validationResults |> getFirstError
                        let errorMessage = PolicyError.getErrorMessage error
                        let graceError = GraceError.Create errorMessage correlationId
                        return! context |> result400BadRequest graceError
            }

    /// Gets the current policy snapshot.
    let GetCurrent: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                /// Implements validations for the server request pipeline.
                let validations (parameters: GetPolicyParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.TargetBranchId PolicyError.InvalidTargetBranchId
                    |]

                let! parameters = context |> parse<GetPolicyParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                /// Implements query for the server request pipeline.
                let query (context: HttpContext) _ (actorProxy: IPolicyActor) =
                    task {
                        let! current = actorProxy.GetCurrent(getCorrelationId context)

                        return
                            match current with
                            | Some snapshot when isUsableSnapshot snapshot -> current
                            | _ -> tryGetSeededSnapshot (Guid.Parse(parameters.TargetBranchId))
                    }

                context.Items[ "Command" ] <- "GetCurrent"
                return! processQuery context parameters validations query
            }

    /// Acknowledges a policy snapshot.
    let Acknowledge: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                /// Implements validations for the server request pipeline.
                let validations (parameters: AcknowledgePolicyParameters) = validateAcknowledgeParameters parameters

                /// Implements command for the server request pipeline.
                let command (parameters: AcknowledgePolicyParameters) =
                    let policySnapshotId = PolicySnapshotId parameters.PolicySnapshotId
                    let note = if String.IsNullOrEmpty(parameters.Note) then None else Some parameters.Note

                    let principal =
                        if
                            isNull context.User
                            || isNull context.User.Identity
                            || String.IsNullOrEmpty(context.User.Identity.Name)
                        then
                            Constants.GraceSystemUser
                        else
                            context.User.Identity.Name

                    PolicyCommand.Acknowledge(policySnapshotId, UserId principal, note)
                    |> returnValueTask

                context.Items[ "Command" ] <- nameof Acknowledge
                return! processCommand context validations command
            }
