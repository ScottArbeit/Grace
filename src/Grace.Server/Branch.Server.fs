namespace Grace.Server

open Dapr.Actors
open Giraffe
open Grace.Actors
open Grace.Actors.Commands.Branch
open Grace.Actors.Constants
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Server.Services
open Grace.Server.Validations
open Grace.Shared
open Grace.Shared.Dto.Diff
open Grace.Shared.Parameters.Branch
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors.Branch
open Grace.Shared.Validation.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Linq
open System.Text.Json
open System.Threading.Tasks

module Branch =
    open Grace.Shared.Services

    type Validations<'T when 'T :> BranchParameters> = 'T -> ValueTask<Result<unit, BranchError>> array

    let activitySource = new ActivitySource("Branch")

    let log = ApplicationContext.loggerFactory.CreateLogger("Branch.Server")

    let processCommand<'T when 'T :> BranchParameters> (context: HttpContext) (validations: Validations<'T>) (command: 'T -> ValueTask<BranchCommand>) =
        task {
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let parameterDictionary = Dictionary<string, string>()

            try
                let commandName = context.Items["Command"] :?> string
                use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
                let! parameters = context |> parse<'T>
                parameterDictionary.AddRange(getParametersAsDictionary parameters)

                // We know these Id's from ValidateIds.Middleware, so let's set them so we never have to resolve them again.
                parameters.OwnerId <- graceIds.OwnerId
                parameters.OrganizationId <- graceIds.OrganizationId
                parameters.RepositoryId <- graceIds.RepositoryId
                parameters.BranchId <- graceIds.BranchId

                let handleCommand (branchId: string) cmd =
                    task {
                        let branchGuid = Guid.Parse(branchId)
                        let actorProxy = Branch.CreateActorProxy branchGuid correlationId

                        match! actorProxy.Handle cmd (createMetadata context) with
                        | Ok graceReturnValue ->
                            graceReturnValue
                                .enhance(parameterDictionary)
                                .enhance(nameof (OwnerId), graceIds.OwnerId)
                                .enhance(nameof (OrganizationId), graceIds.OrganizationId)
                                .enhance(nameof (RepositoryId), graceIds.RepositoryId)
                                .enhance(nameof (BranchId), graceIds.BranchId)
                                .enhance("Command", commandName)
                                .enhance ("Path", context.Request.Path)
                            |> ignore

                            return! context |> result200Ok graceReturnValue
                        | Error graceError ->
                            graceError
                                .enhance(parameterDictionary)
                                .enhance(nameof (OwnerId), graceIds.OwnerId)
                                .enhance(nameof (OrganizationId), graceIds.OrganizationId)
                                .enhance(nameof (RepositoryId), graceIds.RepositoryId)
                                .enhance(nameof (BranchId), graceIds.BranchId)
                                .enhance("Command", commandName)
                                .enhance ("Path", context.Request.Path)
                            |> ignore

                            log.LogError(
                                "{CurrentInstant}: In Branch.Server.handleCommand: error from actorProxy.Handle: {error}",
                                getCurrentInstantExtended (),
                                (graceError.ToString())
                            )

                            return! context |> result400BadRequest graceError
                    }

                let validationResults = validations parameters

                let! validationsPassed = validationResults |> allPass

                log.LogDebug(
                    "{CurrentInstant}: In Branch.Server.processCommand: validationsPassed: {validationsPassed}.",
                    getCurrentInstantExtended (),
                    validationsPassed
                )

                if validationsPassed then
                    let! cmd = command parameters
                    let! result = handleCommand graceIds.BranchId cmd

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Finished {path}; Status code: {statusCode}; OwnerId: {ownerId}; OrganizationId: {organizationId}; RepositoryId: {repositoryId}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId,
                        context.Request.Path,
                        context.Response.StatusCode,
                        graceIds.OwnerId,
                        graceIds.OrganizationId,
                        graceIds.RepositoryId,
                        graceIds.BranchId
                    )

                    return result
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = BranchError.getErrorMessage error
                    log.LogDebug("{CurrentInstant}: error: {error}", getCurrentInstantExtended (), errorMessage)

                    let graceError =
                        (GraceError.Create errorMessage correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof (OwnerId), graceIds.OwnerId)
                            .enhance(nameof (OrganizationId), graceIds.OrganizationId)
                            .enhance(nameof (RepositoryId), graceIds.RepositoryId)
                            .enhance(nameof (BranchId), graceIds.BranchId)
                            .enhance ("Path", context.Request.Path)

                    return! context |> result400BadRequest graceError
            with ex ->
                log.LogError(
                    ex,
                    "{CurrentInstant}: Exception in Organization.Server.processCommand. CorrelationId: {correlationId}.",
                    getCurrentInstantExtended (),
                    (getCorrelationId context)
                )

                let graceError =
                    (GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" correlationId)
                        .enhance(parameterDictionary)
                        .enhance(nameof (OwnerId), graceIds.OwnerId)
                        .enhance(nameof (OrganizationId), graceIds.OrganizationId)
                        .enhance(nameof (RepositoryId), graceIds.RepositoryId)
                        .enhance(nameof (BranchId), graceIds.BranchId)
                        .enhance ("Path", context.Request.Path)

                return! context |> result500ServerError graceError
        }

    let processQuery<'T, 'U when 'T :> BranchParameters>
        (context: HttpContext)
        (parameters: 'T)
        (validations: Validations<'T>)
        maxCount
        (query: QueryResult<IBranchActor, 'U>)
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
                    // Get the actor proxy for the branch.
                    let branchGuid = Guid.Parse(graceIds.BranchId)
                    let actorProxy = Branch.CreateActorProxy branchGuid correlationId

                    // Execute the query.
                    let! queryResult = query context maxCount actorProxy

                    // Wrap the query result in a GraceReturnValue.
                    let graceReturnValue =
                        (GraceReturnValue.Create queryResult correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof (OwnerId), graceIds.OwnerId)
                            .enhance(nameof (OrganizationId), graceIds.OrganizationId)
                            .enhance(nameof (RepositoryId), graceIds.RepositoryId)
                            .enhance(nameof (BranchId), graceIds.BranchId)
                            .enhance ("Path", context.Request.Path)

                    return! context |> result200Ok graceReturnValue
                else
                    let! error = validationResults |> getFirstError

                    let graceError =
                        (GraceError.Create (BranchError.getErrorMessage error) correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof (OwnerId), graceIds.OwnerId)
                            .enhance(nameof (OrganizationId), graceIds.OrganizationId)
                            .enhance(nameof (RepositoryId), graceIds.RepositoryId)
                            .enhance(nameof (BranchId), graceIds.BranchId)
                            .enhance ("Path", context.Request.Path)

                    return! context |> result400BadRequest graceError
            with ex ->
                let graceError =
                    (GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" correlationId)
                        .enhance(parameterDictionary)
                        .enhance(nameof (OwnerId), graceIds.OwnerId)
                        .enhance(nameof (OrganizationId), graceIds.OrganizationId)
                        .enhance(nameof (RepositoryId), graceIds.RepositoryId)
                        .enhance(nameof (BranchId), graceIds.BranchId)
                        .enhance ("Path", context.Request.Path)

                return! context |> result500ServerError graceError
        }

    /// Creates a new branch.
    let Create: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                let validations (parameters: CreateBranchParameters) =
                    [| Guid.isValidAndNotEmptyGuid parameters.ParentBranchId InvalidBranchId
                       String.isValidGraceName parameters.ParentBranchName InvalidBranchName
                       Branch.branchExists
                           graceIds.OwnerId
                           graceIds.OrganizationId
                           graceIds.RepositoryId
                           parameters.ParentBranchId
                           parameters.ParentBranchName
                           parameters.CorrelationId
                           ParentBranchDoesNotExist
                       Branch.branchNameDoesNotExist
                           parameters.OwnerId
                           parameters.OrganizationId
                           parameters.RepositoryId
                           parameters.BranchName
                           parameters.CorrelationId
                           BranchNameAlreadyExists |]

                let command (parameters: CreateBranchParameters) =
                    task {
                        match! (resolveBranchId parameters.RepositoryId parameters.ParentBranchId parameters.ParentBranchName parameters.CorrelationId) with
                        | Some parentBranchId ->
                            let parentBranchGuid = Guid.Parse(parentBranchId)
                            let parentBranchActorProxy = Branch.CreateActorProxy parentBranchGuid parameters.CorrelationId

                            let! parentBranch = parentBranchActorProxy.Get parameters.CorrelationId

                            return
                                Create(
                                    (Guid.Parse(parameters.BranchId)),
                                    (BranchName parameters.BranchName),
                                    (Guid.Parse(parentBranchId)),
                                    parentBranch.BasedOn.ReferenceId,
                                    Guid.Parse(parameters.RepositoryId),
                                    parameters.InitialPermissions
                                )
                        | None ->
                            return
                                Create(
                                    (Guid.Parse(parameters.BranchId)),
                                    (BranchName parameters.BranchName),
                                    Constants.DefaultParentBranchId,
                                    ReferenceId.Empty, // This is fucked.
                                    Guid.Parse(parameters.RepositoryId),
                                    parameters.InitialPermissions
                                )
                    }
                    |> ValueTask<BranchCommand>

                context.Items.Add("Command", nameof (Create))
                return! processCommand context validations command
            }

    /// Rebases a branch on its parent branch.
    let Rebase: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: RebaseParameters) =
                    [| Branch.referenceIdExists parameters.BasedOn parameters.CorrelationId ReferenceIdDoesNotExist
                       Branch.branchAllowsReferenceType
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.BranchId
                           parameters.BranchName
                           ReferenceType.Commit
                           parameters.CorrelationId
                           CommitIsDisabled |]

                let command (parameters: RebaseParameters) = BranchCommand.Rebase parameters.BasedOn |> returnValueTask

                context.Items.Add("Command", nameof (Rebase))
                return! processCommand context validations command
            }

    /// Assigns a specific directory version as the next promotion reference for a branch.
    let Assign: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                let validations (parameters: AssignParameters) =
                    [| String.isValidSha256Hash parameters.Sha256Hash Sha256HashIsRequired
                       Input.oneOfTheseValuesMustBeProvided
                           [| parameters.DirectoryVersionId; parameters.Sha256Hash |]
                           EitherDirectoryVersionIdOrSha256HashRequired
                       Branch.branchAllowsAssign
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.BranchId
                           parameters.BranchName
                           parameters.CorrelationId
                           AssignIsDisabled |]

                let command (parameters: AssignParameters) =
                    task {
                        if parameters.DirectoryVersionId <> Guid.Empty then
                            let directoryVersionActorProxy = DirectoryVersion.CreateActorProxy parameters.DirectoryVersionId parameters.CorrelationId

                            let! directoryVersion = directoryVersionActorProxy.Get(parameters.CorrelationId)

                            return Some(Assign(parameters.DirectoryVersionId, directoryVersion.Sha256Hash, ReferenceText parameters.Message))
                        elif not <| String.IsNullOrEmpty(parameters.Sha256Hash) then
                            match! getDirectoryBySha256Hash (Guid.Parse(graceIds.RepositoryId)) parameters.Sha256Hash parameters.CorrelationId with
                            | Some directoryVersion ->
                                return Some(Assign(directoryVersion.DirectoryVersionId, directoryVersion.Sha256Hash, ReferenceText parameters.Message))
                            | None -> return None
                        else
                            return None
                    }

                let! parameters = context |> parse<AssignParameters>
                context.Items.Add("Command", nameof (Assign))
                context.Items["AssignParameters"] <- parameters
                context.Request.Body.Seek(0L, IO.SeekOrigin.Begin) |> ignore

                match! command parameters with
                | Some command -> return! processCommand context validations (fun parameters -> ValueTask<BranchCommand>(command))
                | None ->
                    return!
                        context
                        |> result400BadRequest (
                            GraceError.Create (BranchError.getErrorMessage EitherDirectoryVersionIdOrSha256HashRequired) (getCorrelationId context)
                        )
            }

    /// Creates a promotion reference in the parent of the specified branch, based on the most-recent commit.
    let Promote: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: CreateReferenceParameters) =
                    [| String.isNotEmpty parameters.Message MessageIsRequired
                       String.maxLength parameters.Message 2048 StringIsTooLong
                       String.isValidSha256Hash parameters.Sha256Hash Sha256HashIsRequired
                       Branch.branchAllowsReferenceType
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.BranchId
                           parameters.BranchName
                           ReferenceType.Promotion
                           parameters.CorrelationId
                           PromotionIsDisabled |]

                let command (parameters: CreateReferenceParameters) =
                    Promote(parameters.DirectoryVersionId, parameters.Sha256Hash, ReferenceText parameters.Message)
                    |> returnValueTask

                context.Items.Add("Command", nameof (Promote))
                return! processCommand context validations command
            }

    /// Creates a commit reference pointing to the current root directory version in the branch.
    let Commit: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: CreateReferenceParameters) =
                    [| String.isNotEmpty parameters.Message MessageIsRequired
                       String.maxLength parameters.Message 2048 StringIsTooLong
                       String.isValidSha256Hash parameters.Sha256Hash Sha256HashIsRequired
                       Branch.branchAllowsReferenceType
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.BranchId
                           parameters.BranchName
                           ReferenceType.Commit
                           parameters.CorrelationId
                           CommitIsDisabled |]

                let command (parameters: CreateReferenceParameters) =
                    BranchCommand.Commit(parameters.DirectoryVersionId, parameters.Sha256Hash, ReferenceText parameters.Message)
                    |> returnValueTask

                context.Items.Add("Command", nameof (Commit))
                return! processCommand context validations command
            }

    /// Creates a checkpoint reference pointing to the current root directory version in the branch.
    let Checkpoint: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: CreateReferenceParameters) =
                    [| String.maxLength parameters.Message 2048 StringIsTooLong
                       String.isValidSha256Hash parameters.Sha256Hash Sha256HashIsRequired
                       Branch.branchAllowsReferenceType
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.BranchId
                           parameters.BranchName
                           ReferenceType.Checkpoint
                           parameters.CorrelationId
                           CheckpointIsDisabled |]

                let command (parameters: CreateReferenceParameters) =
                    BranchCommand.Checkpoint(parameters.DirectoryVersionId, parameters.Sha256Hash, ReferenceText parameters.Message)
                    |> returnValueTask

                context.Items.Add("Command", nameof (Checkpoint))
                return! processCommand context validations command
            }

    /// Creates a save reference pointing to the current root directory version in the branch.
    let Save: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: CreateReferenceParameters) =
                    [| String.maxLength parameters.Message 4096 StringIsTooLong
                       String.isValidSha256Hash parameters.Sha256Hash Sha256HashIsRequired
                       Branch.branchAllowsReferenceType
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.BranchId
                           parameters.BranchName
                           ReferenceType.Save
                           parameters.CorrelationId
                           SaveIsDisabled |]

                let command (parameters: CreateReferenceParameters) =
                    BranchCommand.Save(parameters.DirectoryVersionId, parameters.Sha256Hash, ReferenceText parameters.Message)
                    |> returnValueTask

                context.Items.Add("Command", nameof (Save))
                return! processCommand context validations command
            }

    /// Creates a tag reference pointing to the specified root directory version in the branch.
    let Tag: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: CreateReferenceParameters) =
                    [| String.maxLength parameters.Message 2048 StringIsTooLong
                       String.isValidSha256Hash parameters.Sha256Hash Sha256HashIsRequired
                       Branch.branchAllowsReferenceType
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.BranchId
                           parameters.BranchName
                           ReferenceType.Tag
                           parameters.CorrelationId
                           TagIsDisabled |]

                let command (parameters: CreateReferenceParameters) =
                    BranchCommand.Tag(parameters.DirectoryVersionId, parameters.Sha256Hash, ReferenceText parameters.Message)
                    |> returnValueTask

                context.Items.Add("Command", nameof (Tag))
                return! processCommand context validations command
            }

    /// Creates an external reference pointing to the specified root directory version in the branch.
    let CreateExternal: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: CreateReferenceParameters) =
                    [| String.maxLength parameters.Message 2048 StringIsTooLong
                       String.isValidSha256Hash parameters.Sha256Hash Sha256HashIsRequired
                       Branch.branchAllowsReferenceType
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.BranchId
                           parameters.BranchName
                           ReferenceType.External
                           parameters.CorrelationId
                           ExternalIsDisabled |]

                let command (parameters: CreateReferenceParameters) =
                    BranchCommand.CreateExternal(parameters.DirectoryVersionId, parameters.Sha256Hash, ReferenceText parameters.Message)
                    |> returnValueTask

                context.Items.Add("Command", nameof (CreateExternal))
                return! processCommand context validations command
            }


    /// Enables and disables `grace assign` commands in the provided branch.
    let EnableAssign: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: EnableFeatureParameters) = [||]

                let command (parameters: EnableFeatureParameters) = EnableAssign(parameters.Enabled) |> returnValueTask

                context.Items.Add("Command", nameof (EnableAssign))
                return! processCommand context validations command
            }

    /// Enables and disables promotion references in the provided branch.
    let EnablePromotion: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: EnableFeatureParameters) = [||]

                let command (parameters: EnableFeatureParameters) = EnablePromotion(parameters.Enabled) |> returnValueTask

                context.Items.Add("Command", nameof (EnablePromotion))
                return! processCommand context validations command
            }

    /// Enables and disables commit references in the provided branch.
    let EnableCommit: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: EnableFeatureParameters) = [||]

                let command (parameters: EnableFeatureParameters) = EnableCommit(parameters.Enabled) |> returnValueTask

                context.Items.Add("Command", nameof (EnableCommit))
                return! processCommand context validations command
            }

    /// Enables and disables checkpoint references in the provided branch.
    let EnableCheckpoint: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: EnableFeatureParameters) = [||]

                let command (parameters: EnableFeatureParameters) = EnableCheckpoint(parameters.Enabled) |> returnValueTask

                context.Items.Add("Command", nameof (EnableCheckpoint))
                return! processCommand context validations command
            }

    /// Enables and disables save references in the provided branch.
    let EnableSave: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: EnableFeatureParameters) = [||]

                let command (parameters: EnableFeatureParameters) = EnableSave(parameters.Enabled) |> returnValueTask

                context.Items.Add("Command", nameof (EnableSave))
                return! processCommand context validations command
            }

    /// Enables and disables tag references in the provided branch.
    let EnableTag: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: EnableFeatureParameters) = [||]

                let command (parameters: EnableFeatureParameters) = EnableTag(parameters.Enabled) |> returnValueTask

                context.Items.Add("Command", nameof (EnableTag))
                return! processCommand context validations command
            }

    /// Enables and disables external references in the provided branch.
    let EnableExternal: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: EnableFeatureParameters) = [||]

                let command (parameters: EnableFeatureParameters) = EnableExternal(parameters.Enabled) |> returnValueTask

                context.Items.Add("Command", nameof (EnableExternal))
                return! processCommand context validations command
            }

    /// Enables and disables auto-rebase for the provided branch.
    let EnableAutoRebase: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: EnableFeatureParameters) = [||]

                let command (parameters: EnableFeatureParameters) = EnableAutoRebase(parameters.Enabled) |> returnValueTask

                context.Items.Add("Command", nameof (EnableAutoRebase))
                return! processCommand context validations command
            }

    /// Deletes the provided branch.
    let Delete: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: DeleteBranchParameters) = [||]

                let command (parameters: DeleteBranchParameters) = DeleteLogical(parameters.Force, parameters.DeleteReason) |> returnValueTask

                context.Items.Add("Command", nameof (DeleteLogical))
                return! processCommand context validations command
            }

    /// Gets details about the provided branch.
    let Get: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    let validations (parameters: GetBranchParameters) = [||]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get(getCorrelationId context)
                            return branchDto
                        }

                    let! parameters = context |> parse<GetBranchParameters>
                    let! result = processQuery context parameters validations 1 query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId context))
            }

    /// Gets the events handled by this branch.
    let GetEvents: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    let validations (parameters: GetBranchParameters) = [||]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchEvents = actorProxy.GetEvents(getCorrelationId context)
                            return branchEvents.Select(fun branchEvent -> serialize branchEvent)
                        //return List<Events.Branch.BranchEvent>()
                        }

                    let! parameters = context |> parse<GetBranchParameters>
                    let! result = processQuery context parameters validations 1 query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId context))
            }

    /// Gets details about the parent branch of the provided branch.
    let GetParentBranch: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    let validations (parameters: BranchParameters) = [||]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! parentBranchDto = actorProxy.GetParentBranch(getCorrelationId context)
                            return parentBranchDto
                        }

                    let! parameters = context |> parse<BranchParameters>
                    let! result = processQuery context parameters validations 1 query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId context))
            }

    /// Gets details about the reference with the provided ReferenceId.
    let GetReference: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    let validations (parameters: GetReferenceParameters) = [||]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let referenceGuid = Guid.Parse(context.Items["ReferenceId"] :?> string)

                            let referenceActorProxy = Reference.CreateActorProxy referenceGuid (getCorrelationId context)

                            let! referenceDto = referenceActorProxy.Get(getCorrelationId context)
                            return referenceDto
                        }

                    let! parameters = context |> parse<GetReferenceParameters>
                    context.Items["ReferenceId"] <- parameters.ReferenceId
                    let! result = processQuery context parameters validations 1 query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId context))
            }

    /// Gets details about multiple references in one API call.
    let GetReferences: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    let validations (parameters: GetReferencesParameters) = [| Number.isPositiveOrZero parameters.MaxCount ValueMustBePositive |]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get(getCorrelationId context)
                            let! results = getReferences branchDto.BranchId maxCount (getCorrelationId context)
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    let! result = processQuery context parameters validations (parameters.MaxCount) query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId context))
            }

    /// Gets a list of references, given a list of reference IDs.
    let GetLatestReferencesByReferenceTypes: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    let validations (parameters: GetLatestReferencesByReferenceTypeParameters) =
                        [| Input.listIsNonEmpty parameters.ReferenceTypes BranchError.ReferenceTypeMustBeProvided |]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IBranchActor) =
                        task {
                            let referenceTypes = context.Items["ReferenceTypes"] :?> ReferenceType array

                            log.LogDebug(
                                "In Repository.Server.GetLatestReferencesByReferenceTypes: ReferenceTypes: {referenceTypes}.",
                                serialize referenceTypes
                            )

                            return! getLatestReferenceByReferenceTypes referenceTypes (Guid.Parse(graceIds.BranchId))
                        }

                    let! parameters = context |> parse<GetLatestReferencesByReferenceTypeParameters>
                    context.Items.Add("ReferenceTypes", serialize parameters.ReferenceTypes)
                    let! result = processQuery context parameters validations 1 query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId context))
            }

    /// Retrieves the diffs between references in a branch by ReferenceType.
    let GetDiffsForReferenceType: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                try
                    let validations (parameters: GetDiffsForReferenceTypeParameters) =
                        [| Number.isPositiveOrZero parameters.MaxCount ValueMustBePositive
                           String.isNotEmpty parameters.ReferenceType ReferenceTypeMustBeProvided
                           DiscriminatedUnion.isMemberOf<ReferenceType, BranchError> parameters.ReferenceType InvalidReferenceType |]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IBranchActor) =
                        task {
                            let diffDtos = ConcurrentBag<DiffDto>()
                            let! branchDto = actorProxy.Get correlationId

                            let referenceType =
                                (context.Items[nameof (ReferenceType)] :?> String)
                                |> discriminatedUnionFromString<ReferenceType>

                            let! references = getReferencesByType referenceType.Value branchDto.BranchId maxCount (getCorrelationId context)

                            let sortedRefs =
                                references
                                    .OrderByDescending(fun referenceDto -> referenceDto.CreatedAt)
                                    .ToList()

                            // Take pairs of references and get the diffs between them.
                            do!
                                Parallel.ForEachAsync(
                                    [ 0 .. sortedRefs.Count - 2 ],
                                    Constants.ParallelOptions,
                                    (fun i ct ->
                                        ValueTask(
                                            task {
                                                let diffActorProxy =
                                                    Diff.CreateActorProxy sortedRefs[i].DirectoryId sortedRefs[i + 1].DirectoryId correlationId

                                                let! diffDto = diffActorProxy.GetDiff correlationId
                                                diffDtos.Add(diffDto)
                                            }
                                        ))
                                )

                            return
                                (sortedRefs,
                                 diffDtos
                                     .OrderByDescending(fun diffDto ->
                                         Math.Max(diffDto.Directory1CreatedAt.ToUnixTimeMilliseconds(), diffDto.Directory2CreatedAt.ToUnixTimeMilliseconds()))
                                     .ToList())
                        }

                    let! parameters = context |> parse<GetDiffsForReferenceTypeParameters>
                    context.Items.Add(nameof (ReferenceType), parameters.ReferenceType)
                    let! result = processQuery context parameters validations (parameters.MaxCount) query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        correlationId,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        correlationId,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId context))
            }

    /// Gets the promotions in a branch.
    let GetPromotions: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    let validations (parameters: GetReferencesParameters) = [| Number.isPositiveOrZero parameters.MaxCount ValueMustBePositive |]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get(getCorrelationId context)
                            let! results = getPromotions branchDto.BranchId maxCount (getCorrelationId context)
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    let! result = processQuery context parameters validations (parameters.MaxCount) query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId context))
            }

    /// Gets the commits in a branch.
    let GetCommits: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    let validations (parameters: GetReferencesParameters) = [| Number.isPositiveOrZero parameters.MaxCount ValueMustBePositive |]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get(getCorrelationId context)
                            let! results = getCommits branchDto.BranchId maxCount (getCorrelationId context)
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    let! result = processQuery context parameters validations (parameters.MaxCount) query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId context))
            }

    /// Gets the checkpoints in a branch.
    let GetCheckpoints: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    let validations (parameters: GetReferencesParameters) = [| Number.isPositiveOrZero parameters.MaxCount ValueMustBePositive |]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get(getCorrelationId context)
                            let! results = getCheckpoints branchDto.BranchId maxCount (getCorrelationId context)
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    let! result = processQuery context parameters validations (parameters.MaxCount) query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId context))
            }

    /// Gets the saves in a branch.
    let GetSaves: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    let validations (parameters: GetReferencesParameters) = [| Number.isPositiveOrZero parameters.MaxCount ValueMustBePositive |]


                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get(getCorrelationId context)
                            let! results = getSaves branchDto.BranchId maxCount (getCorrelationId context)
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    let! result = processQuery context parameters validations (parameters.MaxCount) query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId context))
            }

    /// Gets the tags in a branch.
    let GetTags: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    let validations (parameters: GetReferencesParameters) = [| Number.isPositiveOrZero parameters.MaxCount ValueMustBePositive |]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get(getCorrelationId context)
                            let! results = getTags branchDto.BranchId maxCount (getCorrelationId context)
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    let! result = processQuery context parameters validations (parameters.MaxCount) query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId context))
            }

    /// Gets the external references in a branch.
    let GetExternals: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    let validations (parameters: GetReferencesParameters) = [| Number.isPositiveOrZero parameters.MaxCount ValueMustBePositive |]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get(getCorrelationId context)
                            let! results = getExternals branchDto.BranchId maxCount (getCorrelationId context)
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    let! result = processQuery context parameters validations (parameters.MaxCount) query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId context))
            }

    let GetRecursiveSize: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                try
                    let validations (parameters: ListContentsParameters) =
                        [| String.isEmptyOrValidSha256Hash parameters.Sha256Hash InvalidSha256Hash
                           Guid.isValidAndNotEmptyGuid parameters.ReferenceId InvalidReferenceId |]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let listContentsParameters = context.Items["ListContentsParameters"] :?> ListContentsParameters

                            if
                                String.IsNullOrEmpty(listContentsParameters.ReferenceId)
                                && String.IsNullOrEmpty(listContentsParameters.Sha256Hash)
                            then
                                // If we don't have a referenceId or sha256Hash, we'll get the contents of the most recent reference in the branch.
                                let! branchDto = actorProxy.Get correlationId
                                let! latestReference = getLatestReference branchDto.BranchId

                                match latestReference with
                                | Some latestReference ->
                                    let directoryActorProxy = DirectoryVersion.CreateActorProxy latestReference.DirectoryId correlationId
                                    let! recursiveSize = directoryActorProxy.GetRecursiveSize(getCorrelationId context)
                                    return recursiveSize
                                | None -> return Constants.InitialDirectorySize
                            elif not <| String.IsNullOrEmpty(listContentsParameters.ReferenceId) then
                                // We have a ReferenceId, so we'll get the DirectoryVersion from that reference.
                                let referenceGuid = Guid.Parse(listContentsParameters.ReferenceId)
                                let referenceActorProxy = Reference.CreateActorProxy referenceGuid correlationId

                                let! referenceDto = referenceActorProxy.Get correlationId

                                let directoryActorProxy = DirectoryVersion.CreateActorProxy referenceDto.DirectoryId correlationId

                                let! recursiveSize = directoryActorProxy.GetRecursiveSize correlationId
                                return recursiveSize
                            else
                                // By process of elimination, we have a Sha256Hash, so we'll retrieve the DirectoryVersion using that..
                                match!
                                    Services.getDirectoryBySha256Hash (Guid.Parse(graceIds.RepositoryId)) listContentsParameters.Sha256Hash correlationId
                                with
                                | Some directoryVersion ->
                                    let directoryActorProxy = DirectoryVersion.CreateActorProxy directoryVersion.DirectoryVersionId correlationId

                                    let! recursiveSize = directoryActorProxy.GetRecursiveSize correlationId
                                    return recursiveSize
                                | None -> return Constants.InitialDirectorySize
                        }

                    let! parameters = context |> parse<ListContentsParameters>
                    context.Items["ListContentsParameters"] <- parameters
                    let! result = processQuery context parameters validations 1 query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        correlationId,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        correlationId,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" correlationId)
            }

    let ListContents: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                try
                    let validations (parameters: ListContentsParameters) =
                        [| String.isEmptyOrValidSha256Hash parameters.Sha256Hash InvalidSha256Hash
                           Guid.isValidAndNotEmptyGuid parameters.ReferenceId InvalidReferenceId |]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let listContentsParameters = context.Items["ListContentsParameters"] :?> ListContentsParameters

                            if
                                String.IsNullOrEmpty(listContentsParameters.ReferenceId)
                                && String.IsNullOrEmpty(listContentsParameters.Sha256Hash)
                            then
                                // If we don't have a referenceId or sha256Hash, we'll get the contents of the most recent reference in the branch.
                                let! branchDto = actorProxy.Get correlationId
                                let! latestReference = getLatestReference branchDto.BranchId

                                match latestReference with
                                | Some latestReference ->
                                    let directoryActorProxy = DirectoryVersion.CreateActorProxy latestReference.DirectoryId correlationId

                                    let! contents = directoryActorProxy.GetRecursiveDirectoryVersions listContentsParameters.ForceRecompute correlationId

                                    return contents
                                | None -> return Array.Empty<DirectoryVersion>()
                            elif not <| String.IsNullOrEmpty(listContentsParameters.ReferenceId) then
                                // We have a ReferenceId, so we'll get the DirectoryVersion from that reference.
                                let referenceGuid = Guid.Parse(listContentsParameters.ReferenceId)

                                let referenceActorProxy = Reference.CreateActorProxy referenceGuid correlationId

                                let! referenceDto = referenceActorProxy.Get correlationId

                                let directoryActorProxy = DirectoryVersion.CreateActorProxy referenceDto.DirectoryId correlationId

                                let! contents = directoryActorProxy.GetRecursiveDirectoryVersions listContentsParameters.ForceRecompute correlationId

                                return contents
                            else
                                // By process of elimination, we have a Sha256Hash, so we'll retrieve the DirectoryVersion using that..
                                match! getRootDirectoryBySha256Hash (Guid.Parse(graceIds.RepositoryId)) listContentsParameters.Sha256Hash correlationId with
                                | Some directoryVersion ->
                                    let directoryActorProxy = DirectoryVersion.CreateActorProxy directoryVersion.DirectoryVersionId correlationId

                                    let! contents = directoryActorProxy.GetRecursiveDirectoryVersions listContentsParameters.ForceRecompute correlationId

                                    return contents
                                | None -> return Array.Empty<DirectoryVersion>()
                        }

                    let! parameters = context |> parse<ListContentsParameters>
                    context.Items["ListContentsParameters"] <- parameters
                    let! result = processQuery context parameters validations 1 query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        correlationId,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        correlationId,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" correlationId)
            }

    let GetVersion: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                try
                    let validations (parameters: GetBranchVersionParameters) =
                        [| String.isEmptyOrValidSha256Hash parameters.Sha256Hash InvalidSha256Hash
                           Guid.isValidAndNotEmptyGuid parameters.ReferenceId InvalidReferenceId |]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let parameters = context.Items["GetVersionParameters"] :?> GetBranchVersionParameters

                            let repositoryId = Guid.Parse(parameters.RepositoryId)

                            let! rootDirectoryVersion =
                                task {
                                    if not <| String.IsNullOrEmpty(parameters.Sha256Hash) then
                                        return! getRootDirectoryBySha256Hash repositoryId parameters.Sha256Hash correlationId
                                    elif not <| String.IsNullOrEmpty(parameters.ReferenceId) then
                                        return! getRootDirectoryByReferenceId repositoryId (Guid.Parse(parameters.ReferenceId)) correlationId
                                    else
                                        let! branchDto = actorProxy.Get(getCorrelationId context)
                                        let! latestReference = getLatestReference branchDto.BranchId

                                        match latestReference with
                                        | Some referenceDto -> return! getRootDirectoryBySha256Hash repositoryId referenceDto.Sha256Hash correlationId
                                        | None -> return None
                                }

                            match rootDirectoryVersion with
                            | Some rootDirectoryVersion ->
                                let directoryVersionActorId = ActorId($"{rootDirectoryVersion.DirectoryVersionId}")

                                let directoryVersionActorProxy = DirectoryVersion.CreateActorProxy rootDirectoryVersion.DirectoryVersionId correlationId

                                let! directoryVersions = directoryVersionActorProxy.GetRecursiveDirectoryVersions false correlationId

                                let directoryIds = directoryVersions.Select(fun dv -> dv.DirectoryVersionId).ToList()
                                return directoryIds
                            | None -> return List<DirectoryVersionId>()
                        }

                    let! parameters = context |> parse<GetBranchVersionParameters>

                    let! repositoryId =
                        resolveRepositoryId
                            parameters.OwnerId
                            parameters.OwnerName
                            parameters.OrganizationId
                            parameters.OrganizationName
                            parameters.RepositoryId
                            parameters.RepositoryName
                            parameters.CorrelationId

                    match repositoryId with
                    | Some repositoryId ->
                        parameters.RepositoryId <- repositoryId

                        if
                            not <| String.IsNullOrEmpty(parameters.BranchId)
                            || not <| String.IsNullOrEmpty(parameters.BranchName)
                        then
                            logToConsole $"In Branch.GetVersion: parameters.BranchId: {parameters.BranchId}; parameters.BranchName: {parameters.BranchName}"

                            match! resolveBranchId repositoryId parameters.BranchId parameters.BranchName parameters.CorrelationId with
                            | Some branchId -> parameters.BranchId <- branchId
                            | None -> () // This should never happen because it would get caught in validations.
                        elif not <| String.IsNullOrEmpty(parameters.ReferenceId) then
                            logToConsole $"In Branch.GetVersion: parameters.ReferenceId: {parameters.ReferenceId}"
                            let referenceGuid = Guid.Parse(parameters.ReferenceId)
                            let referenceActorProxy = Reference.CreateActorProxy referenceGuid correlationId

                            let! referenceDto = referenceActorProxy.Get(getCorrelationId context)
                            logToConsole $"referenceDto.ReferenceId: {referenceDto.ReferenceId}"
                            parameters.BranchId <- $"{referenceDto.BranchId}"
                        elif not <| String.IsNullOrEmpty(parameters.Sha256Hash) then
                            logToConsole $"In Branch.GetVersion: parameters.Sha256Hash: {parameters.Sha256Hash}"

                            match! getReferenceBySha256Hash (BranchId.Parse(graceIds.BranchId)) parameters.Sha256Hash with
                            | Some referenceDto ->
                                logToConsole $"referenceDto.ReferenceId: {referenceDto.ReferenceId}"
                                parameters.BranchId <- $"{referenceDto.BranchId}"
                            | None -> // Reference Id was not found in the database.
                                ()
                        // I really want to return a 404 here, have to figure that out.
                        //return! returnResult HttpStatusCode.NotFound (GraceError.Create "Reference not found." (getCorrelationId context)) context
                        else
                            () // This should never happen because it would get caught in validations.
                    | None -> () // This should never happen because it would get caught in validations.

                    // Now that we've populated BranchId for sure...
                    context.Items.Add("GetVersionParameters", parameters)
                    return! processQuery context parameters validations 1 query
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        correlationId,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" correlationId)
            }
