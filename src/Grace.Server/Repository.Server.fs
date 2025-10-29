namespace Grace.Server

open FSharpPlus
open Giraffe
open Grace.Actors
open Grace.Actors.Constants
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Server.Services
open Grace.Server.Validations
open Grace.Shared
open Grace.Shared.Extensions
open Grace.Shared.Parameters.Repository
open Grace.Types.Repository
open Grace.Types.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors
open Grace.Shared.Validation
open Grace.Shared.Validation.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open Microsoft.Extensions.Logging
open NodaTime
open OpenTelemetry.Trace
open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.Linq
open System.Threading.Tasks
open System.Text
open System.Text.Json

module Repository =

    type Validations<'T when 'T :> RepositoryParameters> = 'T -> ValueTask<Result<unit, RepositoryError>> array

    let activitySource = new ActivitySource("Repository")

    let log = ApplicationContext.loggerFactory.CreateLogger("Repository.Server")

    let processCommand<'T when 'T :> RepositoryParameters> (context: HttpContext) (validations: Validations<'T>) (command: 'T -> ValueTask<RepositoryCommand>) =
        task {
            use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
            let graceIds = getGraceIds context
            let parameterDictionary = Dictionary<string, obj>()

            try
                let commandName = context.Items["Command"] :?> string
                let correlationId = getCorrelationId context
                let! parameters = context |> parse<'T>
                parameterDictionary.AddRange(getParametersAsDictionary parameters)

                // We know these Id's from ValidateIds.Middleware, so let's set them so we never have to resolve them again.
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let handleCommand organizationId repositoryId cmd =
                    task {
                        let actorProxy = Repository.CreateActorProxy organizationId repositoryId correlationId

                        match! actorProxy.Handle cmd (createMetadata context) with
                        | Ok graceReturnValue ->
                            graceReturnValue
                                .enhance(parameterDictionary)
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
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
                                .enhance("Command", commandName)
                                .enhance ("Path", context.Request.Path.Value)
                            |> ignore

                            log.LogDebug(
                                "{CurrentInstant}: In Branch.Server.handleCommand: error from actorProxy.Handle: {error}",
                                getCurrentInstantExtended (),
                                (graceError.ToString())
                            )

                            return! context |> result400BadRequest graceError
                    }

                log.LogDebug(
                    "{CurrentInstant}: ****In Repository.Server.processCommand; about to run validations: CorrelationId: {correlationId}; RepositoryId: {repositoryId}.",
                    getCurrentInstantExtended (),
                    correlationId,
                    graceIds.RepositoryIdString
                )

                let validationResults = validations parameters

                let! validationsPassed = validationResults |> allPass

                log.LogDebug(
                    "{CurrentInstant}: ****In Repository.Server.processCommand: CorrelationId: {correlationId}; RepositoryId: {repositoryId}; validationsPassed: {validationsPassed}.",
                    getCurrentInstantExtended (),
                    correlationId,
                    graceIds.RepositoryIdString,
                    validationsPassed
                )

                if validationsPassed then
                    let! cmd = command parameters
                    let! result = handleCommand graceIds.OrganizationId graceIds.RepositoryId cmd

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Finished {path}; Status code: {statusCode}; OwnerId: {ownerId}; OrganizationId: {organizationId}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId,
                        context.Request.Path,
                        context.Response.StatusCode,
                        graceIds.OwnerIdString,
                        graceIds.OrganizationIdString,
                        graceIds.RepositoryIdString
                    )

                    return result
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = getErrorOptionMessage error
                    log.LogDebug("{CurrentInstant}: error: {error}", getCurrentInstantExtended (), errorMessage)

                    let graceError =
                        (GraceError.Create errorMessage correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance("Command", commandName)
                            .enhance("Path", context.Request.Path.Value)
                            .enhance ("Error", errorMessage)

                    return! context |> result400BadRequest graceError
            with ex ->
                log.LogError(
                    ex,
                    "{CurrentInstant}: Exception in Repository.Server.processCommand; Path: {path}; CorrelationId: {correlationId}.",
                    getCurrentInstantExtended (),
                    context.Request.Path,
                    (getCorrelationId context)
                )

                let graceError =
                    (GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (getCorrelationId context))
                        .enhance(parameterDictionary)
                        .enhance(nameof OwnerId, graceIds.OwnerId)
                        .enhance(nameof OrganizationId, graceIds.OrganizationId)
                        .enhance(nameof RepositoryId, graceIds.RepositoryId)
                        .enhance ("Path", context.Request.Path.Value)

                return! context |> result500ServerError graceError
        }

    let processQuery<'T, 'U when 'T :> RepositoryParameters>
        (context: HttpContext)
        (parameters: 'T)
        (validations: Validations<'T>)
        (maxCount: int)
        (query: QueryResult<IRepositoryActor, 'U>)
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
                    // Get the actor proxy for the repository.
                    let actorProxy = Repository.CreateActorProxy graceIds.OrganizationId graceIds.RepositoryId correlationId

                    // Execute the query.
                    let! queryResult = query context maxCount actorProxy

                    // Wrap the query result in a GraceReturnValue.
                    let graceReturnValue =
                        (GraceReturnValue.Create queryResult correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result200Ok graceReturnValue
                else
                    let! error = validationResults |> getFirstError

                    let graceError =
                        (GraceError.Create (getErrorOptionMessage error) correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result400BadRequest graceError
            with ex ->
                log.LogError(
                    ex,
                    "{CurrentInstant}: Exception in Repository.Server.processQuery; Path: {path}; CorrelationId: {correlationId}.",
                    getCurrentInstantExtended (),
                    context.Request.Path,
                    correlationId
                )

                let graceError =
                    (GraceError.Create $"{ExceptionResponse.Create ex}" correlationId)
                        .enhance(parameterDictionary)
                        .enhance(nameof OwnerId, graceIds.OwnerId)
                        .enhance(nameof OrganizationId, graceIds.OrganizationId)
                        .enhance(nameof RepositoryId, graceIds.RepositoryId)
                        .enhance ("Path", context.Request.Path.Value)

                return! context |> result500ServerError graceError
        }

    /// Create a new repository.
    let Create: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                context.Items.Add("Command", nameof Create)
                let graceIds = getGraceIds context

                //let! parameters = context |> parse<CreateParameters>
                let validations (parameters: CreateRepositoryParameters) =
                    [| Repository.repositoryIdDoesNotExist graceIds.OrganizationId parameters.RepositoryId parameters.CorrelationId RepositoryIdAlreadyExists
                       Repository.repositoryNameIsUnique
                           parameters.OwnerId
                           parameters.OrganizationId
                           parameters.RepositoryName
                           parameters.CorrelationId
                           RepositoryNameAlreadyExists |]

                let command (parameters: CreateRepositoryParameters) =
                    task {
                        return
                            Create(
                                RepositoryName parameters.RepositoryName,
                                (Guid.Parse(parameters.RepositoryId)),
                                graceIds.OwnerId,
                                graceIds.OrganizationId,
                                parameters.ObjectStorageProvider
                            )
                    }
                    |> ValueTask<RepositoryCommand>

                return! processCommand context validations command
            }

    /// Sets the search visibility of the repository.
    let SetVisibility: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetRepositoryVisibilityParameters) =
                    [| Repository.visibilityIsValid parameters.Visibility InvalidVisibilityValue
                       Repository.repositoryIsNotDeleted context parameters.CorrelationId RepositoryIsDeleted |]

                let command (parameters: SetRepositoryVisibilityParameters) =
                    SetRepositoryType(discriminatedUnionFromString<RepositoryType>(parameters.Visibility).Value)
                    |> returnValueTask

                context.Items.Add("Command", nameof SetRepositoryType)
                return! processCommand context validations command
            }

    /// Sets the number of days to keep an entity that has been logically deleted. After this time expires, the entity will be physically deleted.
    let SetLogicalDeleteDays: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetLogicalDeleteDaysParameters) =
                    [| Repository.daysIsValid parameters.LogicalDeleteDays InvalidLogicalDeleteDaysValue
                       Repository.repositoryIsNotDeleted context parameters.CorrelationId RepositoryIsDeleted |]

                let command (parameters: SetLogicalDeleteDaysParameters) = SetLogicalDeleteDays(parameters.LogicalDeleteDays) |> returnValueTask

                context.Items.Add("Command", nameof SetLogicalDeleteDays)
                return! processCommand context validations command
            }

    /// Sets the number of days to keep saves in the repository.
    let SetSaveDays: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetSaveDaysParameters) =
                    [| Repository.daysIsValid parameters.SaveDays InvalidSaveDaysValue
                       Repository.repositoryIsNotDeleted context parameters.CorrelationId RepositoryIsDeleted |]

                let command (parameters: SetSaveDaysParameters) = SetSaveDays(parameters.SaveDays) |> returnValueTask

                context.Items.Add("Command", nameof SetSaveDays)
                return! processCommand context validations command
            }

    /// Sets the number of days to keep checkpoints in the repository.
    let SetCheckpointDays: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetCheckpointDaysParameters) =
                    [| Repository.daysIsValid parameters.CheckpointDays InvalidCheckpointDaysValue
                       Repository.repositoryIsNotDeleted context parameters.CorrelationId RepositoryIsDeleted |]

                let command (parameters: SetCheckpointDaysParameters) = SetCheckpointDays(parameters.CheckpointDays) |> returnValueTask

                context.Items.Add("Command", nameof SetCheckpointDays)
                return! processCommand context validations command
            }

    /// Sets the number of days to keep diff contents in the database.
    let SetDiffCacheDays: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetDiffCacheDaysParameters) =
                    [| Repository.daysIsValid parameters.DiffCacheDays InvalidDiffCacheDaysValue
                       Repository.repositoryIsNotDeleted context parameters.CorrelationId RepositoryIsDeleted |]

                let command (parameters: SetDiffCacheDaysParameters) = SetDiffCacheDays(parameters.DiffCacheDays) |> returnValueTask

                context.Items.Add("Command", nameof SetDiffCacheDays)
                return! processCommand context validations command
            }

    /// Sets the number of days to keep recursive directory version contents in the database.
    let SetDirectoryVersionCacheDays: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetDirectoryVersionCacheDaysParameters) =
                    [| Repository.daysIsValid parameters.DirectoryVersionCacheDays InvalidDirectoryVersionCacheDaysValue
                       Repository.repositoryIsNotDeleted context parameters.CorrelationId RepositoryIsDeleted |]

                let command (parameters: SetDirectoryVersionCacheDaysParameters) =
                    SetDirectoryVersionCacheDays(parameters.DirectoryVersionCacheDays)
                    |> returnValueTask

                context.Items.Add("Command", nameof SetDirectoryVersionCacheDays)
                return! processCommand context validations command
            }

    /// Sets the status of the repository (Public, Private).
    let SetStatus: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetRepositoryStatusParameters) =
                    [| String.isNotEmpty parameters.Status InvalidRepositoryStatus
                       DiscriminatedUnion.isMemberOf<RepositoryStatus, RepositoryError> parameters.Status InvalidRepositoryStatus
                       Repository.repositoryIsNotDeleted context parameters.CorrelationId RepositoryIsDeleted |]

                let command (parameters: SetRepositoryStatusParameters) =
                    SetRepositoryStatus(discriminatedUnionFromString<RepositoryStatus>(parameters.Status).Value)
                    |> returnValueTask

                context.Items.Add("Command", nameof SetRepositoryStatus)
                return! processCommand context validations command
            }

    /// Sets whether the repository allows large files.
    let SetAllowsLargeFiles: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetAllowsLargeFilesParameters) =
                    [| Repository.repositoryIsNotDeleted context parameters.CorrelationId RepositoryIsDeleted |]

                let command (parameters: SetAllowsLargeFilesParameters) = SetAllowsLargeFiles(parameters.AllowsLargeFiles) |> returnValueTask

                context.Items.Add("Command", nameof SetAllowsLargeFiles)
                return! processCommand context validations command
            }

    /// Sets whether the repository allows anonymous access.
    let SetAnonymousAccess: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetAnonymousAccessParameters) =
                    [| Repository.repositoryIsNotDeleted context parameters.CorrelationId RepositoryIsDeleted |]

                let command (parameters: SetAnonymousAccessParameters) = SetAnonymousAccess(parameters.AnonymousAccess) |> returnValueTask

                context.Items.Add("Command", nameof SetAnonymousAccess)
                return! processCommand context validations command
            }

    /// Sets the default server API version for the repository.
    let SetDefaultServerApiVersion: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetDefaultServerApiVersionParameters) =
                    [| String.isNotEmpty parameters.DefaultServerApiVersion InvalidServerApiVersion
                       Repository.repositoryIsNotDeleted context parameters.CorrelationId RepositoryIsDeleted |]

                let command (parameters: SetDefaultServerApiVersionParameters) =
                    SetDefaultServerApiVersion(parameters.DefaultServerApiVersion)
                    |> returnValueTask

                context.Items.Add("Command", nameof SetDefaultServerApiVersion)
                return! processCommand context validations command
            }

    /// Sets whether or not to keep saves in the repository.
    let SetRecordSaves: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: RecordSavesParameters) =
                    [| Repository.repositoryIsNotDeleted context parameters.CorrelationId RepositoryIsDeleted |]

                let command (parameters: RecordSavesParameters) = SetRecordSaves(parameters.RecordSaves) |> returnValueTask

                context.Items.Add("Command", nameof SetRecordSaves)
                return! processCommand context validations command
            }

    /// Sets the description of the repository.
    let SetDescription: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetRepositoryDescriptionParameters) =
                    [| String.isNotEmpty parameters.Description DescriptionIsRequired
                       String.maxLength parameters.Description 2048 DescriptionIsTooLong
                       Repository.repositoryIsNotDeleted context parameters.CorrelationId RepositoryIsDeleted |]

                let command (parameters: SetRepositoryDescriptionParameters) = SetDescription(parameters.Description) |> returnValueTask

                context.Items.Add("Command", nameof SetDescription)
                return! processCommand context validations command
            }

    /// Sets the name of the repository.
    let SetName: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetRepositoryNameParameters) =
                    [| String.isNotEmpty parameters.NewName RepositoryNameIsRequired
                       String.isValidGraceName parameters.NewName InvalidRepositoryName
                       Repository.repositoryIsNotDeleted context parameters.CorrelationId RepositoryIsDeleted
                       Repository.repositoryNameIsUnique
                           parameters.OwnerId
                           parameters.OrganizationId
                           parameters.NewName
                           parameters.CorrelationId
                           RepositoryNameAlreadyExists |]

                let command (parameters: SetRepositoryNameParameters) = SetName(parameters.NewName) |> returnValueTask

                context.Items.Add("Command", nameof SetName)
                return! processCommand context validations command
            }

    /// Deletes the repository.
    let Delete: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: DeleteRepositoryParameters) =
                    [| String.isNotEmpty parameters.DeleteReason DeleteReasonIsRequired
                       Repository.repositoryIsNotDeleted context parameters.CorrelationId RepositoryIsDeleted |]

                let command (parameters: DeleteRepositoryParameters) = DeleteLogical(parameters.Force, parameters.DeleteReason) |> returnValueTask

                context.Items.Add("Command", nameof DeleteLogical)
                return! processCommand context validations command
            }

    /// Undeletes a previously-deleted repository.
    let Undelete: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: RepositoryParameters) =
                    [| Repository.repositoryIsDeleted context parameters.CorrelationId RepositoryIsNotDeleted |]

                let command (parameters: RepositoryParameters) = Undelete |> returnValueTask

                context.Items.Add("Command", nameof Undelete)
                return! processCommand context validations command
            }

    /// Checks if a repository exists with the given parameters.
    let Exists: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = context.Items[nameof GraceIds] :?> GraceIds

                try
                    let validations (parameters: RepositoryParameters) = [||]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IRepositoryActor) =
                        task { return! actorProxy.Exists(getCorrelationId context) }

                    let! parameters = context |> parse<RepositoryParameters>
                    let! result = processQuery context parameters validations 1 query

                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryIdString
                    )

                    return result
                with ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryIdString
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId context))
            }

    /// Checks if a repository is empty - that is, just created - or not.
    let IsEmpty: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = context.Items[nameof GraceIds] :?> GraceIds

                try
                    let validations (parameters: RepositoryParameters) = [||]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IRepositoryActor) =
                        task { return! actorProxy.IsEmpty(getCorrelationId context) }

                    let! parameters = context |> parse<RepositoryParameters>
                    let! result = processQuery context parameters validations 1 query

                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryIdString
                    )

                    return result
                with ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryIdString
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId context))
            }

    /// Gets a repository.
    let Get: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    let validations (parameters: RepositoryParameters) = [||]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IRepositoryActor) = actorProxy.Get(getCorrelationId context)

                    let! parameters = context |> parse<RepositoryParameters>
                    let! result = processQuery context parameters validations 1 query

                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryIdString
                    )

                    return result
                with ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryIdString
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId context))
            }

    /// Gets a repository's branches.
    let GetBranches: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = context.Items[nameof GraceIds] :?> GraceIds

                try
                    let validations (parameters: GetBranchesParameters) = [| Number.isWithinRange parameters.MaxCount 1 1000 InvalidMaxCountValue |]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IRepositoryActor) =
                        task {
                            let graceIds = context.Items[nameof GraceIds] :?> GraceIds
                            let includeDeleted = context.Items["IncludeDeleted"] :?> bool

                            return!
                                getBranches graceIds.OwnerId graceIds.OrganizationId graceIds.RepositoryId maxCount includeDeleted (getCorrelationId context)
                        }

                    let! parameters = context |> parse<GetBranchesParameters>
                    context.Items.Add("IncludeDeleted", parameters.IncludeDeleted)
                    let! result = processQuery context parameters validations 1000 query

                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryIdString
                    )

                    return result
                with ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryIdString
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId context))
            }

    /// Gets a list of references, given a list of reference IDs.
    let GetReferencesByReferenceId: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    let validations (parameters: GetReferencesByReferenceIdParameters) =
                        [| Input.listIsNonEmpty parameters.ReferenceIds ReferenceIdsAreRequired
                           Number.isWithinRange parameters.MaxCount 1 1000 InvalidMaxCountValue |]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IRepositoryActor) =
                        task {
                            let referenceIds = context.Items["ReferenceIds"] :?> IEnumerable<ReferenceId>

                            log.LogDebug("In Repository.Server.GetReferencesByReferenceId: ReferenceIds: {referenceIds}", serialize referenceIds)

                            return! getReferencesByReferenceId graceIds.RepositoryId referenceIds maxCount (getCorrelationId context)
                        }

                    let! parameters = context |> parse<GetReferencesByReferenceIdParameters>
                    context.Items.Add("ReferenceIds", parameters.ReferenceIds)
                    let! result = processQuery context parameters validations parameters.MaxCount query

                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryIdString
                    )

                    return result
                with ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryIdString
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId context))
            }

    /// Gets a list of branches, given a list of branch IDs.
    let GetBranchesByBranchId: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = context.Items[nameof GraceIds] :?> GraceIds

                try
                    let validations (parameters: GetBranchesByBranchIdParameters) =
                        [| Input.listIsNonEmpty parameters.BranchIds BranchIdsAreRequired
                           Number.isWithinRange parameters.MaxCount 1 1000 InvalidMaxCountValue |]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IRepositoryActor) =
                        task {
                            let repositoryId = Guid.Parse(graceIds.RepositoryIdString)
                            let branchIdsFromContext = (context.Items["BranchIds"] :?> string)

                            let branchIds =
                                branchIdsFromContext
                                    .Split(',', StringSplitOptions.TrimEntries)
                                    .Select(fun branchId -> Guid.Parse(branchId))

                            let includeDeleted = context.Items["IncludeDeleted"] :?> bool
                            return! getBranchesByBranchId repositoryId branchIds maxCount includeDeleted
                        }

                    let! parameters = context |> parse<GetBranchesByBranchIdParameters>

                    let branchIdList = parameters.BranchIds.ToList() // We need .Count below, so may as well materialize it once here.
                    let sb = stringBuilderPool.Get()

                    try
                        let branchIds = branchIdList.Aggregate(sb, (fun sb branchId -> sb.Append($"{branchId},")))

                        context.Items.Add("BranchIds", (branchIds.ToString())[0..^1])
                        context.Items.Add("IncludeDeleted", parameters.IncludeDeleted)

                        let! result = processQuery context parameters validations (branchIdList.Count) query

                        let duration_ms = getDurationRightAligned_ms startTime

                        log.LogInformation(
                            "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; RepositoryId: {repositoryId}.",
                            getCurrentInstantExtended (),
                            getMachineName,
                            duration_ms,
                            (getCorrelationId context),
                            context.Request.Path,
                            graceIds.RepositoryIdString
                        )

                        return result
                    finally
                        stringBuilderPool.Return(sb)
                with ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryIdString
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId context))
            }
