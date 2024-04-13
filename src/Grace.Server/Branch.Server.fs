namespace Grace.Server

open Dapr.Actors
open Dapr.Actors.Client
open Giraffe
open Grace.Actors
open Grace.Actors.Commands.Branch
open Grace.Actors.Constants
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

    let actorProxyFactory = ApplicationContext.actorProxyFactory

    let getActorProxy (context: HttpContext) (branchId: string) =
        let actorId = ActorId($"{branchId}")
        actorProxyFactory.CreateActorProxy<IBranchActor>(actorId, ActorName.Branch)

    let commonValidations<'T when 'T :> BranchParameters> (parameters: 'T) =
        [| Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
           String.isValidGraceName parameters.OwnerName InvalidOwnerName
           Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
           Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
           String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
           Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
           Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
           String.isValidGraceName parameters.RepositoryName InvalidRepositoryName
           Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired
           Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
           String.isValidGraceName parameters.BranchName InvalidBranchName
           Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired |]

    let processCommand<'T when 'T :> BranchParameters> (context: HttpContext) (validations: Validations<'T>) (command: 'T -> ValueTask<BranchCommand>) =
        task {
            try
                let commandName = context.Items["Command"] :?> string
                let graceIds = context.Items[nameof (GraceIds)] :?> GraceIds
                use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
                let! parameters = context |> parse<'T>

                let handleCommand branchId cmd =
                    task {
                        let actorProxy = getActorProxy context branchId

                        match! actorProxy.Handle cmd (createMetadata context) with
                        | Ok graceReturnValue ->
                            let graceIds = getGraceIds context
                            graceReturnValue.Properties[nameof (OwnerId)] <- graceIds.OwnerId
                            graceReturnValue.Properties[nameof (OrganizationId)] <- graceIds.OrganizationId
                            graceReturnValue.Properties[nameof (RepositoryId)] <- graceIds.RepositoryId
                            graceReturnValue.Properties[nameof (BranchId)] <- graceIds.BranchId

                            return! context |> result200Ok graceReturnValue
                        | Error graceError ->
                            log.LogDebug(
                                "{currentInstant}: In Branch.Server.handleCommand: error from actorProxy.Handle: {error}",
                                getCurrentInstantExtended (),
                                (graceError.ToString())
                            )

                            return!
                                context
                                |> result400BadRequest { graceError with Properties = getPropertiesAsDictionary parameters }
                    }

                let combinedValidations = Array.append (commonValidations parameters) (validations parameters)

                let! validationsPassed = combinedValidations |> allPass

                log.LogDebug(
                    "{currentInstant}: In Branch.Server.processCommand: validationsPassed: {validationsPassed}.",
                    getCurrentInstantExtended (),
                    validationsPassed
                )

                if validationsPassed then
                    let! cmd = command parameters

                    let! branchId = resolveBranchId graceIds.RepositoryId parameters.BranchId parameters.BranchName parameters.CorrelationId

                    match branchId, commandName = nameof (Create) with
                    | Some branchId, _ ->
                        // If Id is Some, then we know we have a valid Id.
                        if String.IsNullOrEmpty(parameters.BranchId) then
                            parameters.BranchId <- branchId

                        return! handleCommand branchId cmd
                    | None, true ->
                        // If it's None, but this is a Create command, still valid, just use the Id from the parameters.
                        return! handleCommand parameters.BranchId cmd
                    | None, false ->
                        // If it's None, and this is not a Create command, then we have a bad request.
                        log.LogDebug(
                            "{currentInstant}: In Branch.Server.processCommand: resolveBranchId failed. Branch does not exist. repositoryId: {repositoryId}; repositoryName: {repositoryName}.",
                            getCurrentInstantExtended (),
                            parameters.RepositoryId,
                            parameters.RepositoryName
                        )

                        return!
                            context
                            |> result400BadRequest (GraceError.Create (BranchError.getErrorMessage RepositoryDoesNotExist) (getCorrelationId context))
                else
                    let! error = combinedValidations |> getFirstError
                    let errorMessage = BranchError.getErrorMessage error
                    log.LogDebug("{currentInstant}: error: {error}", getCurrentInstantExtended (), errorMessage)

                    let graceError = GraceError.CreateWithMetadata errorMessage (getCorrelationId context) (getPropertiesAsDictionary parameters)

                    graceError.Properties.Add("Path", context.Request.Path)
                    graceError.Properties.Add("Error", errorMessage)
                    return! context |> result400BadRequest graceError
            with ex ->
                let graceError = GraceError.Create $"{Utilities.createExceptionResponse ex}" (getCorrelationId context)

                graceError.Properties.Add("Path", context.Request.Path)
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

            try
                let validationResults = Array.append (commonValidations parameters) (validations parameters)

                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    match! resolveBranchId parameters.RepositoryId parameters.BranchId parameters.BranchName parameters.CorrelationId with
                    | Some branchId ->
                        let actorProxy = getActorProxy context branchId
                        let! queryResult = query context maxCount actorProxy

                        let graceReturnValue = GraceReturnValue.Create queryResult (getCorrelationId context)

                        let graceIds = getGraceIds context
                        graceReturnValue.Properties[nameof (OwnerId)] <- graceIds.OwnerId
                        graceReturnValue.Properties[nameof (OrganizationId)] <- graceIds.OrganizationId
                        graceReturnValue.Properties[nameof (RepositoryId)] <- graceIds.RepositoryId
                        graceReturnValue.Properties[nameof (BranchId)] <- graceIds.BranchId

                        return! context |> result200Ok graceReturnValue
                    | None ->
                        return!
                            context
                            |> result400BadRequest (GraceError.Create (BranchError.getErrorMessage BranchDoesNotExist) (getCorrelationId context))
                else
                    let! error = validationResults |> getFirstError

                    let graceError = GraceError.Create (BranchError.getErrorMessage error) (getCorrelationId context)

                    graceError.Properties.Add("Path", context.Request.Path)
                    return! context |> result400BadRequest graceError
            with ex ->
                return!
                    context
                    |> result500ServerError (GraceError.Create $"{Utilities.createExceptionResponse ex}" (getCorrelationId context))
        }

    /// Creates a new branch.
    let Create: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: CreateBranchParameters) =
                    [| String.isNotEmpty parameters.BranchId BranchIdIsRequired
                       Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                       String.isNotEmpty parameters.BranchName BranchNameIsRequired
                       String.isValidGraceName parameters.BranchName InvalidBranchName
                       Guid.isValidAndNotEmpty parameters.ParentBranchId InvalidBranchId
                       String.isValidGraceName parameters.ParentBranchName InvalidBranchName
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       Organization.organizationExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationDoesNotExist
                       Repository.repositoryExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryDoesNotExist
                       Branch.branchExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.ParentBranchId
                           parameters.ParentBranchName
                           parameters.CorrelationId
                           ParentBranchDoesNotExist
                       Branch.branchNameDoesNotExist
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.BranchName
                           parameters.CorrelationId
                           BranchNameAlreadyExists |]

                let command (parameters: CreateBranchParameters) =
                    task {
                        match! (resolveBranchId parameters.RepositoryId parameters.ParentBranchId parameters.ParentBranchName parameters.CorrelationId) with
                        | Some parentBranchId ->
                            let parentBranchActorId = ActorId(parentBranchId)

                            let parentBranchActorProxy = actorProxyFactory.CreateActorProxy<IBranchActor>(parentBranchActorId, ActorName.Branch)

                            let! parentBranch = parentBranchActorProxy.Get parameters.CorrelationId

                            return
                                Create(
                                    (Guid.Parse(parameters.BranchId)),
                                    (BranchName parameters.BranchName),
                                    (Guid.Parse(parentBranchId)),
                                    parentBranch.BasedOn,
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
                    [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                       String.isValidGraceName parameters.BranchName InvalidBranchName
                       Branch.referenceIdExists parameters.BasedOn parameters.CorrelationId ReferenceIdDoesNotExist
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       Organization.organizationExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationDoesNotExist
                       Repository.repositoryExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryDoesNotExist
                       Branch.branchExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.BranchId
                           parameters.BranchName
                           parameters.CorrelationId
                           ParentBranchDoesNotExist
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

                let command (parameters: RebaseParameters) = Rebase(parameters.BasedOn) |> returnValueTask

                context.Items.Add("Command", nameof (Rebase))
                return! processCommand context validations command
            }

    /// Assigns a specific directory version as the next promotion reference for a branch.
    let Assign: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = context.Items[nameof (GraceIds)] :?> GraceIds

                let validations (parameters: AssignParameters) =
                    [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                       String.isValidGraceName parameters.BranchName InvalidBranchName
                       String.isValidSha256Hash parameters.Sha256Hash Sha256HashIsRequired
                       Input.oneOfTheseValuesMustBeProvided
                           [| parameters.DirectoryVersionId; parameters.Sha256Hash |]
                           EitherDirectoryVersionIdOrSha256HashRequired
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       Organization.organizationExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationDoesNotExist
                       Repository.repositoryExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryDoesNotExist
                       Branch.branchExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.BranchId
                           parameters.BranchName
                           parameters.CorrelationId
                           ParentBranchDoesNotExist
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
                            let directoryVersionActorProxy =
                                actorProxyFactory.CreateActorProxy<IDirectoryVersionActor>(
                                    DirectoryVersion.GetActorId(parameters.DirectoryVersionId),
                                    ActorName.DirectoryVersion
                                )

                            let! directoryVersion = directoryVersionActorProxy.Get(parameters.CorrelationId)

                            return Some(Assign(parameters.DirectoryVersionId, directoryVersion.Sha256Hash, ReferenceText parameters.Message))
                        elif not <| String.IsNullOrEmpty(parameters.Sha256Hash) then
                            match! getDirectoryBySha256Hash (Guid.Parse(graceIds.RepositoryId)) parameters.Sha256Hash parameters.CorrelationId with
                            | Some directoryVersion ->
                                return Some(Assign(directoryVersion.DirectoryId, directoryVersion.Sha256Hash, ReferenceText parameters.Message))
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
                    [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                       String.isValidGraceName parameters.BranchName InvalidBranchName
                       String.isNotEmpty parameters.Message MessageIsRequired
                       String.maxLength parameters.Message 2048 StringIsTooLong
                       String.isValidSha256Hash parameters.Sha256Hash Sha256HashIsRequired
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       Organization.organizationExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationDoesNotExist
                       Repository.repositoryExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryDoesNotExist
                       Branch.branchExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.BranchId
                           parameters.BranchName
                           parameters.CorrelationId
                           ParentBranchDoesNotExist
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
                    [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                       String.isValidGraceName parameters.BranchName InvalidBranchName
                       String.isNotEmpty parameters.Message MessageIsRequired
                       String.maxLength parameters.Message 2048 StringIsTooLong
                       String.isValidSha256Hash parameters.Sha256Hash Sha256HashIsRequired
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       Organization.organizationExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationDoesNotExist
                       Repository.repositoryExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryDoesNotExist
                       Branch.branchExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.BranchId
                           parameters.BranchName
                           parameters.CorrelationId
                           BranchDoesNotExist
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
                    [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                       String.isValidGraceName parameters.BranchName InvalidBranchName
                       Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                       String.maxLength parameters.Message 2048 StringIsTooLong
                       String.isValidSha256Hash parameters.Sha256Hash Sha256HashIsRequired
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       Organization.organizationExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationDoesNotExist
                       Repository.repositoryExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryDoesNotExist
                       Branch.branchExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.BranchId
                           parameters.BranchName
                           parameters.CorrelationId
                           BranchDoesNotExist
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
                    [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                       String.isValidGraceName parameters.BranchName InvalidBranchName
                       Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                       String.maxLength parameters.Message 4096 StringIsTooLong
                       String.isValidSha256Hash parameters.Sha256Hash Sha256HashIsRequired
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       Organization.organizationExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationDoesNotExist
                       Repository.repositoryExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryDoesNotExist
                       Branch.branchExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.BranchId
                           parameters.BranchName
                           parameters.CorrelationId
                           BranchDoesNotExist
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
                    [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                       String.isValidGraceName parameters.BranchName InvalidBranchName
                       Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                       String.maxLength parameters.Message 2048 StringIsTooLong
                       String.isValidSha256Hash parameters.Sha256Hash Sha256HashIsRequired
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       Organization.organizationExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationDoesNotExist
                       Repository.repositoryExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryDoesNotExist
                       Branch.branchExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.BranchId
                           parameters.BranchName
                           parameters.CorrelationId
                           BranchDoesNotExist
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
                    [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                       String.isValidGraceName parameters.BranchName InvalidBranchName
                       Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                       String.maxLength parameters.Message 2048 StringIsTooLong
                       String.isValidSha256Hash parameters.Sha256Hash Sha256HashIsRequired
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       Organization.organizationExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationDoesNotExist
                       Repository.repositoryExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryDoesNotExist
                       Branch.branchExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.BranchId
                           parameters.BranchName
                           parameters.CorrelationId
                           BranchDoesNotExist
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
                let validations (parameters: EnableFeatureParameters) =
                    [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                       String.isValidGraceName parameters.BranchName InvalidBranchName
                       Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       Organization.organizationExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationDoesNotExist
                       Repository.repositoryExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryDoesNotExist
                       Branch.branchExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.BranchId
                           parameters.BranchName
                           parameters.CorrelationId
                           BranchDoesNotExist |]

                let command (parameters: EnableFeatureParameters) = EnableAssign(parameters.Enabled) |> returnValueTask

                context.Items.Add("Command", nameof (EnableAssign))
                return! processCommand context validations command
            }

    /// Enables and disables promotion references in the provided branch.
    let EnablePromotion: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: EnableFeatureParameters) =
                    [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                       String.isValidGraceName parameters.BranchName InvalidBranchName
                       Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       Organization.organizationExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationDoesNotExist
                       Repository.repositoryExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryDoesNotExist
                       Branch.branchExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.BranchId
                           parameters.BranchName
                           parameters.CorrelationId
                           BranchDoesNotExist |]

                let command (parameters: EnableFeatureParameters) = EnablePromotion(parameters.Enabled) |> returnValueTask

                context.Items.Add("Command", nameof (EnablePromotion))
                return! processCommand context validations command
            }

    /// Enables and disables commit references in the provided branch.
    let EnableCommit: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: EnableFeatureParameters) =
                    [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                       String.isValidGraceName parameters.BranchName InvalidBranchName
                       Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       Organization.organizationExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationDoesNotExist
                       Repository.repositoryExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryDoesNotExist
                       Branch.branchExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.BranchId
                           parameters.BranchName
                           parameters.CorrelationId
                           BranchDoesNotExist |]

                let command (parameters: EnableFeatureParameters) = EnableCommit(parameters.Enabled) |> returnValueTask

                context.Items.Add("Command", nameof (EnableCommit))
                return! processCommand context validations command
            }

    /// Enables and disables checkpoint references in the provided branch.
    let EnableCheckpoint: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: EnableFeatureParameters) =
                    [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                       String.isValidGraceName parameters.BranchName InvalidBranchName
                       Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       Organization.organizationExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationDoesNotExist
                       Repository.repositoryExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryDoesNotExist
                       Branch.branchExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.BranchId
                           parameters.BranchName
                           parameters.CorrelationId
                           BranchDoesNotExist |]

                let command (parameters: EnableFeatureParameters) = EnableCheckpoint(parameters.Enabled) |> returnValueTask

                context.Items.Add("Command", nameof (EnableCheckpoint))
                return! processCommand context validations command
            }

    /// Enables and disables save references in the provided branch.
    let EnableSave: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: EnableFeatureParameters) =
                    [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                       String.isValidGraceName parameters.BranchName InvalidBranchName
                       Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       Organization.organizationExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationDoesNotExist
                       Repository.repositoryExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryDoesNotExist
                       Branch.branchExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.BranchId
                           parameters.BranchName
                           parameters.CorrelationId
                           ParentBranchDoesNotExist |]

                let command (parameters: EnableFeatureParameters) = EnableSave(parameters.Enabled) |> returnValueTask

                context.Items.Add("Command", nameof (EnableSave))
                return! processCommand context validations command
            }

    /// Enables and disables tag references in the provided branch.
    let EnableTag: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: EnableFeatureParameters) =
                    [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                       String.isValidGraceName parameters.BranchName InvalidBranchName
                       Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       Organization.organizationExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationDoesNotExist
                       Repository.repositoryExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryDoesNotExist
                       Branch.branchExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.BranchId
                           parameters.BranchName
                           parameters.CorrelationId
                           BranchDoesNotExist |]

                let command (parameters: EnableFeatureParameters) = EnableTag(parameters.Enabled) |> returnValueTask

                context.Items.Add("Command", nameof (EnableTag))
                return! processCommand context validations command
            }

    /// Enables and disables external references in the provided branch.
    let EnableExternal: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: EnableFeatureParameters) =
                    [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                       String.isValidGraceName parameters.BranchName InvalidBranchName
                       Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       Organization.organizationExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationDoesNotExist
                       Repository.repositoryExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryDoesNotExist
                       Branch.branchExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.BranchId
                           parameters.BranchName
                           parameters.CorrelationId
                           BranchDoesNotExist |]

                let command (parameters: EnableFeatureParameters) = EnableExternal(parameters.Enabled) |> returnValueTask

                context.Items.Add("Command", nameof (EnableExternal))
                return! processCommand context validations command
            }

    /// Enables and disables auto-rebase for the provided branch.
    let EnableAutoRebase: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: EnableFeatureParameters) =
                    [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                       String.isValidGraceName parameters.BranchName InvalidBranchName
                       Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       Organization.organizationExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationDoesNotExist
                       Repository.repositoryExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryDoesNotExist
                       Branch.branchExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.BranchId
                           parameters.BranchName
                           parameters.CorrelationId
                           BranchDoesNotExist |]

                let command (parameters: EnableFeatureParameters) = EnableAutoRebase(parameters.Enabled) |> returnValueTask

                context.Items.Add("Command", nameof (EnableAutoRebase))
                return! processCommand context validations command
            }

    /// Deletes the provided branch.
    let Delete: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: DeleteBranchParameters) =
                    [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                       String.isValidGraceName parameters.BranchName InvalidBranchName
                       Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       Organization.organizationExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationDoesNotExist
                       Repository.repositoryExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryDoesNotExist
                       Branch.branchExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.RepositoryId
                           parameters.RepositoryName
                           parameters.BranchId
                           parameters.BranchName
                           parameters.CorrelationId
                           BranchDoesNotExist |]

                let command (parameters: DeleteBranchParameters) = DeleteLogical(parameters.Force, parameters.DeleteReason) |> returnValueTask

                context.Items.Add("Command", nameof (DeleteLogical))
                return! processCommand context validations command
            }

    /// Gets details about the provided branch.
    let Get: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = context.Items[nameof (GraceIds)] :?> GraceIds

                try
                    let validations (parameters: GetBranchParameters) =
                        [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                           String.isValidGraceName parameters.BranchName InvalidBranchName
                           Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                           Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                           Organization.organizationExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.CorrelationId
                               OrganizationDoesNotExist
                           Repository.repositoryExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.CorrelationId
                               RepositoryDoesNotExist
                           Branch.branchExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.BranchId
                               parameters.BranchName
                               parameters.CorrelationId
                               BranchDoesNotExist |]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get(getCorrelationId context)
                            return branchDto
                        }

                    let! parameters = context |> parse<GetBranchParameters>
                    let! result = processQuery context parameters validations 1 query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Gets the events handled by this branch.
    let GetEvents: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = context.Items[nameof (GraceIds)] :?> GraceIds

                try
                    let validations (parameters: GetBranchParameters) =
                        [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                           String.isValidGraceName parameters.BranchName InvalidBranchName
                           Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                           Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                           Organization.organizationExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.CorrelationId
                               OrganizationDoesNotExist
                           Repository.repositoryExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.CorrelationId
                               RepositoryDoesNotExist
                           Branch.branchExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.BranchId
                               parameters.BranchName
                               parameters.CorrelationId
                               BranchDoesNotExist |]

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
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Gets details about the parent branch of the provided branch.
    let GetParentBranch: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = context.Items[nameof (GraceIds)] :?> GraceIds

                try
                    let validations (parameters: BranchParameters) =
                        [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                           String.isValidGraceName parameters.BranchName InvalidBranchName
                           Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                           Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                           Organization.organizationExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.CorrelationId
                               OrganizationDoesNotExist
                           Repository.repositoryExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.CorrelationId
                               RepositoryDoesNotExist
                           Branch.branchExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.BranchId
                               parameters.BranchName
                               parameters.CorrelationId
                               ParentBranchDoesNotExist |]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! parentBranchDto = actorProxy.GetParentBranch(getCorrelationId context)
                            return parentBranchDto
                        }

                    let! parameters = context |> parse<BranchParameters>
                    let! result = processQuery context parameters validations 1 query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Gets details about the reference with the provided ReferenceId.
    let GetReference: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = context.Items[nameof (GraceIds)] :?> GraceIds

                try
                    let validations (parameters: GetReferenceParameters) =
                        [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                           String.isValidGraceName parameters.BranchName InvalidBranchName
                           Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                           Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                           Organization.organizationExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.CorrelationId
                               OrganizationDoesNotExist
                           Repository.repositoryExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.CorrelationId
                               RepositoryDoesNotExist
                           Branch.branchExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.BranchId
                               parameters.BranchName
                               parameters.CorrelationId
                               BranchDoesNotExist |]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let referenceActorId = ActorId(context.Items["ReferenceId"] :?> string)

                            let referenceActorProxy =
                                ApplicationContext.actorProxyFactory.CreateActorProxy<IReferenceActor>(referenceActorId, ActorName.Reference)

                            let! referenceDto = referenceActorProxy.Get(getCorrelationId context)
                            return referenceDto
                        }

                    let! parameters = context |> parse<GetReferenceParameters>
                    context.Items.Add("ReferenceId", parameters.ReferenceId)
                    let! result = processQuery context parameters validations 1 query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Gets details about multiple references in one API call.
    let GetReferences: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = context.Items[nameof (GraceIds)] :?> GraceIds

                try
                    let validations (parameters: GetReferencesParameters) =
                        [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                           String.isValidGraceName parameters.BranchName InvalidBranchName
                           Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                           Number.isPositiveOrZero parameters.MaxCount ValueMustBePositive
                           Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                           Organization.organizationExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.CorrelationId
                               OrganizationDoesNotExist
                           Repository.repositoryExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.CorrelationId
                               RepositoryDoesNotExist
                           Branch.branchExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.BranchId
                               parameters.BranchName
                               parameters.CorrelationId
                               BranchDoesNotExist |]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get(getCorrelationId context)
                            let! results = getReferences branchDto.BranchId maxCount
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    let! result = processQuery context parameters validations (parameters.MaxCount) query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Retrieves the diffs between references in a branch by ReferenceType.
    let GetDiffsForReferenceType: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = context.Items[nameof (GraceIds)] :?> GraceIds

                try
                    let validations (parameters: GetDiffsForReferenceTypeParameters) =
                        [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                           String.isValidGraceName parameters.BranchName InvalidBranchName
                           Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                           Number.isPositiveOrZero parameters.MaxCount ValueMustBePositive
                           String.isNotEmpty parameters.ReferenceType ReferenceTypeMustBeProvided
                           DiscriminatedUnion.isMemberOf<ReferenceType, BranchError> parameters.ReferenceType InvalidReferenceType
                           Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                           Organization.organizationExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.CorrelationId
                               OrganizationDoesNotExist
                           Repository.repositoryExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.CorrelationId
                               RepositoryDoesNotExist
                           Branch.branchExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.BranchId
                               parameters.BranchName
                               parameters.CorrelationId
                               BranchDoesNotExist |]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IBranchActor) =
                        task {
                            let diffDtos = ConcurrentBag<DiffDto>()
                            let! branchDto = actorProxy.Get(getCorrelationId context)

                            let referenceType =
                                (context.Items[nameof (ReferenceType)] :?> String)
                                |> discriminatedUnionFromString<ReferenceType>

                            let! references = getReferencesByType referenceType.Value branchDto.BranchId maxCount

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
                                                let diffActorId = Diff.GetActorId sortedRefs[i].DirectoryId sortedRefs[i + 1].DirectoryId

                                                let diffActorProxy =
                                                    ApplicationContext.actorProxyFactory.CreateActorProxy<IDiffActor>(diffActorId, ActorName.Diff)

                                                let! diffDto = diffActorProxy.GetDiff(getCorrelationId context)
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
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Gets the promotions in a branch.
    let GetPromotions: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = context.Items[nameof (GraceIds)] :?> GraceIds

                try
                    let validations (parameters: GetReferencesParameters) =
                        [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                           String.isValidGraceName parameters.BranchName InvalidBranchName
                           Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                           Number.isPositiveOrZero parameters.MaxCount ValueMustBePositive
                           Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                           Organization.organizationExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.CorrelationId
                               OrganizationDoesNotExist
                           Repository.repositoryExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.CorrelationId
                               RepositoryDoesNotExist
                           Branch.branchExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.BranchId
                               parameters.BranchName
                               parameters.CorrelationId
                               BranchDoesNotExist |]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get(getCorrelationId context)
                            let! results = getPromotions branchDto.BranchId maxCount
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    let! result = processQuery context parameters validations (parameters.MaxCount) query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Gets the commits in a branch.
    let GetCommits: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = context.Items[nameof (GraceIds)] :?> GraceIds

                try
                    let validations (parameters: GetReferencesParameters) =
                        [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                           String.isValidGraceName parameters.BranchName InvalidBranchName
                           Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                           Number.isPositiveOrZero parameters.MaxCount ValueMustBePositive
                           Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                           Organization.organizationExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.CorrelationId
                               OrganizationDoesNotExist
                           Repository.repositoryExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.CorrelationId
                               RepositoryDoesNotExist
                           Branch.branchExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.BranchId
                               parameters.BranchName
                               parameters.CorrelationId
                               BranchDoesNotExist |]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get(getCorrelationId context)
                            let! results = getCommits branchDto.BranchId maxCount
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    let! result = processQuery context parameters validations (parameters.MaxCount) query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Gets the checkpoints in a branch.
    let GetCheckpoints: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = context.Items[nameof (GraceIds)] :?> GraceIds

                try
                    let validations (parameters: GetReferencesParameters) =
                        [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                           String.isValidGraceName parameters.BranchName InvalidBranchName
                           Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                           Number.isPositiveOrZero parameters.MaxCount ValueMustBePositive
                           Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                           Organization.organizationExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.CorrelationId
                               OrganizationDoesNotExist
                           Repository.repositoryExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.CorrelationId
                               RepositoryDoesNotExist
                           Branch.branchExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.BranchId
                               parameters.BranchName
                               parameters.CorrelationId
                               BranchDoesNotExist |]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get(getCorrelationId context)
                            let! results = getCheckpoints branchDto.BranchId maxCount
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    let! result = processQuery context parameters validations (parameters.MaxCount) query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Gets the saves in a branch.
    let GetSaves: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = context.Items[nameof (GraceIds)] :?> GraceIds

                try
                    let validations (parameters: GetReferencesParameters) =
                        [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                           String.isValidGraceName parameters.BranchName InvalidBranchName
                           Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                           Number.isPositiveOrZero parameters.MaxCount ValueMustBePositive
                           Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                           Organization.organizationExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.CorrelationId
                               OrganizationDoesNotExist
                           Repository.repositoryExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.CorrelationId
                               RepositoryDoesNotExist
                           Branch.branchExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.BranchId
                               parameters.BranchName
                               parameters.CorrelationId
                               BranchDoesNotExist |]


                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get(getCorrelationId context)
                            let! results = getSaves branchDto.BranchId maxCount
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    let! result = processQuery context parameters validations (parameters.MaxCount) query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Gets the tags in a branch.
    let GetTags: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = context.Items[nameof (GraceIds)] :?> GraceIds

                try
                    let validations (parameters: GetReferencesParameters) =
                        [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                           String.isValidGraceName parameters.BranchName InvalidBranchName
                           Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                           Number.isPositiveOrZero parameters.MaxCount ValueMustBePositive
                           Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                           Organization.organizationExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.CorrelationId
                               OrganizationDoesNotExist
                           Repository.repositoryExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.CorrelationId
                               RepositoryDoesNotExist
                           Branch.branchExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.BranchId
                               parameters.BranchName
                               parameters.CorrelationId
                               BranchDoesNotExist |]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get(getCorrelationId context)
                            let! results = getTags branchDto.BranchId maxCount
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    let! result = processQuery context parameters validations (parameters.MaxCount) query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Gets the external references in a branch.
    let GetExternals: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = context.Items[nameof (GraceIds)] :?> GraceIds

                try
                    let validations (parameters: GetReferencesParameters) =
                        [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                           String.isValidGraceName parameters.BranchName InvalidBranchName
                           Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                           Number.isPositiveOrZero parameters.MaxCount ValueMustBePositive
                           Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                           Organization.organizationExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.CorrelationId
                               OrganizationDoesNotExist
                           Repository.repositoryExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.CorrelationId
                               RepositoryDoesNotExist
                           Branch.branchExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.BranchId
                               parameters.BranchName
                               parameters.CorrelationId
                               BranchDoesNotExist |]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get(getCorrelationId context)
                            let! results = getExternals branchDto.BranchId maxCount
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    let! result = processQuery context parameters validations (parameters.MaxCount) query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    let GetRecursiveSize: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = context.Items[nameof (GraceIds)] :?> GraceIds

                try
                    let validations (parameters: ListContentsParameters) =
                        [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                           String.isValidGraceName parameters.BranchName InvalidBranchName
                           Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                           String.isEmptyOrValidSha256Hash parameters.Sha256Hash InvalidSha256Hash
                           Guid.isValidAndNotEmpty parameters.ReferenceId InvalidReferenceId
                           Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                           Organization.organizationExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.CorrelationId
                               OrganizationDoesNotExist
                           Repository.repositoryExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.CorrelationId
                               RepositoryDoesNotExist
                           Branch.branchExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.BranchId
                               parameters.BranchName
                               parameters.CorrelationId
                               BranchDoesNotExist |]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let listContentsParameters = context.Items["ListContentsParameters"] :?> ListContentsParameters

                            if
                                String.IsNullOrEmpty(listContentsParameters.ReferenceId)
                                && String.IsNullOrEmpty(listContentsParameters.Sha256Hash)
                            then
                                // If we don't have a referenceId or sha256Hash, we'll get the contents of the most recent reference in the branch.
                                let! branchDto = actorProxy.Get(getCorrelationId context)
                                let! latestReference = getLatestReference branchDto.BranchId

                                match latestReference with
                                | Some latestReference ->
                                    let directoryActorId = DirectoryVersion.GetActorId latestReference.DirectoryId

                                    let directoryActorProxy =
                                        actorProxyFactory.CreateActorProxy<IDirectoryVersionActor>(directoryActorId, ActorName.DirectoryVersion)

                                    let! directoryVersion = directoryActorProxy.Get(getCorrelationId context)
                                    let! recursiveSize = directoryActorProxy.GetRecursiveSize(getCorrelationId context)
                                    return recursiveSize
                                | None -> return Constants.InitialDirectorySize
                            elif not <| String.IsNullOrEmpty(listContentsParameters.ReferenceId) then
                                // We have a ReferenceId, so we'll get the DirectoryVersion from that reference.
                                let referenceActorId = ActorId(listContentsParameters.ReferenceId)

                                let referenceActorProxy = actorProxyFactory.CreateActorProxy<IReferenceActor>(referenceActorId, ActorName.Reference)

                                let! referenceDto = referenceActorProxy.Get(getCorrelationId context)
                                let directoryActorId = DirectoryVersion.GetActorId referenceDto.DirectoryId

                                let directoryActorProxy =
                                    actorProxyFactory.CreateActorProxy<IDirectoryVersionActor>(directoryActorId, ActorName.DirectoryVersion)

                                let! recursiveSize = directoryActorProxy.GetRecursiveSize(getCorrelationId context)
                                return recursiveSize
                            else
                                // By process of elimination, we have a Sha256Hash, so we'll retrieve the DirectoryVersion using that..
                                match!
                                    Services.getDirectoryBySha256Hash
                                        (Guid.Parse(graceIds.RepositoryId))
                                        listContentsParameters.Sha256Hash
                                        (getCorrelationId context)
                                with
                                | Some directoryVersion ->
                                    let directoryActorId = DirectoryVersion.GetActorId directoryVersion.DirectoryId

                                    let directoryActorProxy =
                                        actorProxyFactory.CreateActorProxy<IDirectoryVersionActor>(directoryActorId, ActorName.DirectoryVersion)

                                    let! recursiveSize = directoryActorProxy.GetRecursiveSize(getCorrelationId context)
                                    return recursiveSize
                                | None -> return Constants.InitialDirectorySize
                        }

                    let! parameters = context |> parse<ListContentsParameters>
                    context.Items["ListContentsParameters"] <- parameters
                    let! result = processQuery context parameters validations 1 query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    let ListContents: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = context.Items[nameof (GraceIds)] :?> GraceIds

                try
                    let validations (parameters: ListContentsParameters) =
                        [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                           String.isValidGraceName parameters.BranchName InvalidBranchName
                           Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                           String.isEmptyOrValidSha256Hash parameters.Sha256Hash InvalidSha256Hash
                           Guid.isValidAndNotEmpty parameters.ReferenceId InvalidReferenceId
                           Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                           Organization.organizationExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.CorrelationId
                               OrganizationDoesNotExist
                           Repository.repositoryExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.CorrelationId
                               RepositoryDoesNotExist
                           Branch.branchExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.BranchId
                               parameters.BranchName
                               parameters.CorrelationId
                               BranchDoesNotExist |]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let listContentsParameters = context.Items["ListContentsParameters"] :?> ListContentsParameters

                            if
                                String.IsNullOrEmpty(listContentsParameters.ReferenceId)
                                && String.IsNullOrEmpty(listContentsParameters.Sha256Hash)
                            then
                                // If we don't have a referenceId or sha256Hash, we'll get the contents of the most recent reference in the branch.
                                let! branchDto = actorProxy.Get(getCorrelationId context)
                                let! latestReference = getLatestReference branchDto.BranchId

                                match latestReference with
                                | Some latestReference ->
                                    let directoryActorId = DirectoryVersion.GetActorId latestReference.DirectoryId

                                    let directoryActorProxy =
                                        actorProxyFactory.CreateActorProxy<IDirectoryVersionActor>(directoryActorId, ActorName.DirectoryVersion)

                                    let! directoryVersion = directoryActorProxy.Get(getCorrelationId context)

                                    let! contents =
                                        directoryActorProxy.GetDirectoryVersionsRecursive listContentsParameters.ForceRecompute (getCorrelationId context)

                                    return contents
                                | None -> return List<DirectoryVersion>()
                            elif not <| String.IsNullOrEmpty(listContentsParameters.ReferenceId) then
                                // We have a ReferenceId, so we'll get the DirectoryVersion from that reference.
                                let referenceActorId = ActorId(listContentsParameters.ReferenceId)

                                let referenceActorProxy = actorProxyFactory.CreateActorProxy<IReferenceActor>(referenceActorId, ActorName.Reference)

                                let! referenceDto = referenceActorProxy.Get(getCorrelationId context)
                                let directoryActorId = DirectoryVersion.GetActorId referenceDto.DirectoryId

                                let directoryActorProxy =
                                    actorProxyFactory.CreateActorProxy<IDirectoryVersionActor>(directoryActorId, ActorName.DirectoryVersion)

                                let! contents =
                                    directoryActorProxy.GetDirectoryVersionsRecursive listContentsParameters.ForceRecompute (getCorrelationId context)

                                return contents
                            else
                                // By process of elimination, we have a Sha256Hash, so we'll retrieve the DirectoryVersion using that..
                                match!
                                    getRootDirectoryBySha256Hash
                                        (Guid.Parse(graceIds.RepositoryId))
                                        listContentsParameters.Sha256Hash
                                        (getCorrelationId context)
                                with
                                | Some directoryVersion ->
                                    let directoryActorId = DirectoryVersion.GetActorId directoryVersion.DirectoryId

                                    let directoryActorProxy =
                                        actorProxyFactory.CreateActorProxy<IDirectoryVersionActor>(directoryActorId, ActorName.DirectoryVersion)

                                    let! contents =
                                        directoryActorProxy.GetDirectoryVersionsRecursive listContentsParameters.ForceRecompute (getCorrelationId context)

                                    return contents
                                | None -> return List<DirectoryVersion>()
                        }

                    let! parameters = context |> parse<ListContentsParameters>
                    context.Items["ListContentsParameters"] <- parameters
                    let! result = processQuery context parameters validations 1 query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    let GetVersion: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = context.Items[nameof (GraceIds)] :?> GraceIds

                try
                    let validations (parameters: GetBranchVersionParameters) =
                        [| Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                           String.isValidGraceName parameters.BranchName InvalidBranchName
                           Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                           String.isEmptyOrValidSha256Hash parameters.Sha256Hash InvalidSha256Hash
                           Guid.isValidAndNotEmpty parameters.ReferenceId InvalidReferenceId
                           Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                           Organization.organizationExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.CorrelationId
                               OrganizationDoesNotExist
                           Repository.repositoryExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.CorrelationId
                               RepositoryDoesNotExist
                           Branch.branchExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.RepositoryId
                               parameters.RepositoryName
                               parameters.BranchId
                               parameters.BranchName
                               parameters.CorrelationId
                               BranchDoesNotExist |]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let parameters = context.Items["GetVersionParameters"] :?> GetBranchVersionParameters

                            let repositoryId = Guid.Parse(parameters.RepositoryId)

                            let! rootDirectoryVersion =
                                task {
                                    if not <| String.IsNullOrEmpty(parameters.Sha256Hash) then
                                        return! getRootDirectoryBySha256Hash repositoryId parameters.Sha256Hash (getCorrelationId context)
                                    elif not <| String.IsNullOrEmpty(parameters.ReferenceId) then
                                        return! getRootDirectoryByReferenceId repositoryId (Guid.Parse(parameters.ReferenceId)) (getCorrelationId context)
                                    else
                                        let! branchDto = actorProxy.Get(getCorrelationId context)
                                        let! latestReference = getLatestReference branchDto.BranchId

                                        match latestReference with
                                        | Some referenceDto ->
                                            return! getRootDirectoryBySha256Hash repositoryId referenceDto.Sha256Hash (getCorrelationId context)
                                        | None -> return None
                                }

                            match rootDirectoryVersion with
                            | Some rootDirectoryVersion ->
                                let directoryVersionActorId = ActorId($"{rootDirectoryVersion.DirectoryId}")

                                let directoryVersionActorProxy =
                                    ApplicationContext.actorProxyFactory.CreateActorProxy<IDirectoryVersionActor>(
                                        directoryVersionActorId,
                                        ActorName.DirectoryVersion
                                    )

                                let! directoryVersions = directoryVersionActorProxy.GetDirectoryVersionsRecursive false (getCorrelationId context)

                                let directoryIds = directoryVersions.Select(fun dv -> dv.DirectoryId).ToList()
                                return directoryIds
                            | None -> return List<DirectoryId>()
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
                            let referenceActorId = ActorId(parameters.ReferenceId)

                            let referenceActorProxy =
                                ApplicationContext.actorProxyFactory.CreateActorProxy<IReferenceActor>(referenceActorId, ActorName.Reference)

                            let! referenceDto = referenceActorProxy.Get(getCorrelationId context)
                            logToConsole $"referenceDto.ReferenceId: {referenceDto.ReferenceId}"
                            parameters.BranchId <- $"{referenceDto.BranchId}"
                        elif not <| String.IsNullOrEmpty(parameters.Sha256Hash) then
                            logToConsole $"In Branch.GetVersion: parameters.Sha256Hash: {parameters.Sha256Hash}"
                            let! referenceDto = getReferenceBySha256Hash parameters.Sha256Hash
                            logToConsole $"referenceDto.ReferenceId: {referenceDto.ReferenceId}"
                            parameters.BranchId <- $"{referenceDto.BranchId}"
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
                        "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        (getCorrelationId context),
                        duration_ms,
                        context.Request.Path,
                        graceIds.BranchId
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }
