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
open Grace.Shared.Parameters.WorkItem
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors
open Grace.Shared.Validation.Utilities
open Grace.Types.Artifact
open Grace.Types.WorkItem
open Grace.Types.Types
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading.Tasks

module WorkItem =
    type Validations<'T when 'T :> WorkItemParameters> = 'T -> ValueTask<Result<unit, WorkItemError>> array

    type WorkItemIdentifier =
        | Id of WorkItemId
        | Number of WorkItemNumber

    let log = ApplicationContext.loggerFactory.CreateLogger("WorkItem.Server")

    let activitySource = new ActivitySource("WorkItem")

    let private tryParseWorkItemIdentifier (value: string) =
        let mutable parsedGuid = Guid.Empty

        if
            not <| String.IsNullOrWhiteSpace(value)
            && Guid.TryParse(value, &parsedGuid)
            && parsedGuid <> Guid.Empty
        then
            Ok(Id parsedGuid)
        else
            let mutable parsedNumber = 0L

            if
                not <| String.IsNullOrWhiteSpace(value)
                && Int64.TryParse(value, &parsedNumber)
            then
                if parsedNumber > 0L then
                    Ok(Number parsedNumber)
                else
                    Error WorkItemError.InvalidWorkItemNumber
            else
                Error WorkItemError.InvalidWorkItemId

    let internal validateWorkItemIdentifier (value: string) =
        match tryParseWorkItemIdentifier value with
        | Ok _ -> Ok() |> returnValueTask
        | Error error -> Error error |> returnValueTask

    let private resolveWorkItemId (repositoryId: RepositoryId) (workItemIdentifier: string) (correlationId: CorrelationId) =
        task {
            match tryParseWorkItemIdentifier workItemIdentifier with
            | Error error -> return Error(GraceError.Create (WorkItemError.getErrorMessage error) correlationId)
            | Ok identifier ->
                match identifier with
                | Id workItemId -> return Ok workItemId
                | Number workItemNumber ->
                    let workItemNumberActorProxy = WorkItemNumber.CreateActorProxy repositoryId correlationId
                    let! cachedWorkItemId = workItemNumberActorProxy.GetWorkItemId workItemNumber correlationId

                    match cachedWorkItemId with
                    | Some workItemId -> return Ok workItemId
                    | None ->
                        let! persistedWorkItemId = getWorkItemIdByNumber repositoryId workItemNumber correlationId

                        match persistedWorkItemId with
                        | Some workItemId ->
                            do! workItemNumberActorProxy.SetWorkItemId workItemNumber workItemId correlationId
                            return Ok workItemId
                        | None ->
                            return
                                Error(
                                    GraceError.Create
                                        (WorkItemError.getErrorMessage WorkItemError.WorkItemDoesNotExist)
                                        correlationId
                                )
        }

    let private cacheWorkItemNumber (repositoryId: RepositoryId) (workItemNumber: WorkItemNumber) (workItemId: WorkItemId) (correlationId: CorrelationId) =
        task {
            let workItemNumberActorProxy = WorkItemNumber.CreateActorProxy repositoryId correlationId
            do! workItemNumberActorProxy.SetWorkItemId workItemNumber workItemId correlationId
        }

    let private withWorkItemNumberLock (repositoryId: RepositoryId) (correlationId: CorrelationId) (work: unit -> Task<GraceResult<string>>) =
        task {
            let lockName = $"workitem-number|{repositoryId}"
            let lockOwner = $"WorkItemCreate:{correlationId}"
            let lockActorProxy = GlobalLock.CreateActorProxy lockName correlationId
            let mutable acquired = false
            let mutable attempt = 0

            while not acquired && attempt < 100 do
                let! acquiredNow = lockActorProxy.AcquireLock lockOwner

                if acquiredNow then
                    acquired <- true
                else
                    attempt <- attempt + 1
                    do! Task.Delay(25)

            if not acquired then
                return Error(GraceError.Create "Could not acquire lock while allocating WorkItemNumber." correlationId)
            else
                let! result =
                    task {
                        try
                            return! work ()
                        with
                        | ex -> return Error(GraceError.CreateWithException ex String.Empty correlationId)
                    }

                let! releaseResult = lockActorProxy.ReleaseLock lockOwner

                match releaseResult with
                | Ok _ -> ()
                | Error releaseError ->
                    log.LogWarning(
                        "{CurrentInstant}: Failed to release lock for WorkItemNumber allocation. CorrelationId: {correlationId}; RepositoryId: {repositoryId}; Error: {releaseError}.",
                        getCurrentInstantExtended (),
                        correlationId,
                        repositoryId,
                        releaseError
                    )

                return result
        }

    let processCommand<'T when 'T :> WorkItemParameters> (context: HttpContext) (validations: Validations<'T>) (command: 'T -> ValueTask<WorkItemCommand>) =
        task {
            let commandName = context.Items["Command"] :?> string
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let parameterDictionary = Dictionary<string, obj>()

            try
                use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
                let! parameters = context |> parse<'T>
                parameterDictionary.AddRange(getParametersAsDictionary parameters)

                // Use IDs from middleware.
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    match! resolveWorkItemId graceIds.RepositoryId parameters.WorkItemId correlationId with
                    | Error graceError ->
                        graceError
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance("Command", commandName)
                            .enhance("Path", context.Request.Path.Value)
                        |> ignore

                        return! context |> result400BadRequest graceError
                    | Ok workItemId ->
                        let! cmd = command parameters
                        let actorProxy = WorkItem.CreateActorProxy workItemId graceIds.RepositoryId correlationId
                        let metadata = createMetadata context

                        match! actorProxy.Handle cmd metadata with
                        | Ok graceReturnValue ->
                            graceReturnValue
                                .enhance(parameterDictionary)
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof WorkItemId, workItemId)
                                .enhance("Command", commandName)
                                .enhance("Path", context.Request.Path.Value)
                            |> ignore

                            return! context |> result200Ok graceReturnValue
                        | Error graceError ->
                            graceError
                                .enhance(parameterDictionary)
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof WorkItemId, workItemId)
                                .enhance("Command", commandName)
                                .enhance("Path", context.Request.Path.Value)
                            |> ignore

                            return! context |> result400BadRequest graceError
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = WorkItemError.getErrorMessage error

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
                    "{CurrentInstant}: Exception in WorkItem.Server.processCommand. CorrelationId: {correlationId}.",
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

    let processQuery<'T, 'U when 'T :> WorkItemParameters>
        (context: HttpContext)
        (parameters: 'T)
        (validations: Validations<'T>)
        (query: QueryResult<IWorkItemActor, 'U>)
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
                    match! resolveWorkItemId graceIds.RepositoryId parameters.WorkItemId correlationId with
                    | Error graceError ->
                        graceError
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance("Path", context.Request.Path.Value)
                        |> ignore

                        return! context |> result400BadRequest graceError
                    | Ok workItemId ->
                        let actorProxy = WorkItem.CreateActorProxy workItemId graceIds.RepositoryId correlationId
                        let! queryResult = query context 0 actorProxy

                        let graceReturnValue =
                            (GraceReturnValue.Create queryResult correlationId)
                                .enhance(parameterDictionary)
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof WorkItemId, workItemId)
                                .enhance("Path", context.Request.Path.Value)

                        return! context |> result200Ok graceReturnValue
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = WorkItemError.getErrorMessage error

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

    let internal buildUpdateCommands (parameters: UpdateWorkItemParameters) =
        [
            if not <| String.IsNullOrEmpty(parameters.Title) then
                WorkItemCommand.SetTitle parameters.Title
            if
                not
                <| String.IsNullOrEmpty(parameters.Description)
            then
                WorkItemCommand.SetDescription parameters.Description
            if not <| String.IsNullOrEmpty(parameters.Status) then
                let status =
                    discriminatedUnionFromString<WorkItemStatus> parameters.Status
                    |> Option.get

                WorkItemCommand.SetStatus status
            if
                not
                <| String.IsNullOrEmpty(parameters.Constraints)
            then
                WorkItemCommand.SetConstraints parameters.Constraints
            if not <| String.IsNullOrEmpty(parameters.Notes) then
                WorkItemCommand.SetNotes parameters.Notes
            if
                not
                <| String.IsNullOrEmpty(parameters.ArchitecturalNotes)
            then
                WorkItemCommand.SetArchitecturalNotes parameters.ArchitecturalNotes
            if
                not
                <| String.IsNullOrEmpty(parameters.MigrationNotes)
            then
                WorkItemCommand.SetMigrationNotes parameters.MigrationNotes
        ]

    let internal validateLinkReferenceParameters (parameters: LinkReferenceParameters) =
        [|
            validateWorkItemIdentifier parameters.WorkItemId
            Guid.isValidAndNotEmptyGuid parameters.ReferenceId WorkItemError.InvalidReferenceId
        |]

    let internal validateLinkArtifactParameters (parameters: LinkArtifactParameters) =
        [|
            validateWorkItemIdentifier parameters.WorkItemId
            Guid.isValidAndNotEmptyGuid parameters.ArtifactId WorkItemError.InvalidArtifactId
        |]

    let internal validateLinkPromotionSetParameters (parameters: LinkPromotionSetParameters) =
        [|
            validateWorkItemIdentifier parameters.WorkItemId
            Guid.isValidAndNotEmptyGuid parameters.PromotionSetId WorkItemError.InvalidPromotionSetId
        |]

    let internal parseRemovableArtifactType (artifactType: string) =
        if String.IsNullOrWhiteSpace(artifactType) then
            Error WorkItemError.InvalidArtifactType
        elif String.Equals(artifactType, "summary", StringComparison.OrdinalIgnoreCase)
             || String.Equals(artifactType, "agentsummary", StringComparison.OrdinalIgnoreCase) then
            Ok ArtifactType.AgentSummary
        elif String.Equals(artifactType, "prompt", StringComparison.OrdinalIgnoreCase) then
            Ok ArtifactType.Prompt
        elif String.Equals(artifactType, "notes", StringComparison.OrdinalIgnoreCase)
             || String.Equals(artifactType, "reviewnotes", StringComparison.OrdinalIgnoreCase) then
            Ok ArtifactType.ReviewNotes
        else
            Error WorkItemError.InvalidArtifactType

    let internal validateRemoveArtifactTypeLinksParameters (parameters: RemoveArtifactTypeLinksParameters) =
        [|
            validateWorkItemIdentifier parameters.WorkItemId
            String.isNotEmpty parameters.ArtifactType WorkItemError.InvalidArtifactType
        |]

    /// Creates a new work item.
    let Create: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                use activity = activitySource.StartActivity("Create", ActivityKind.Server)
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context
                let! parameters = context |> parse<CreateWorkItemParameters>

                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let validations =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.WorkItemId WorkItemError.InvalidWorkItemId
                    |]

                let! validationsPassed = validations |> allPass

                if validationsPassed then
                    let workItemId = Guid.Parse(parameters.WorkItemId)
                    let metadata = createMetadata context
                    let parameterDictionary = getParametersAsDictionary parameters

                    let! createResult =
                        withWorkItemNumberLock graceIds.RepositoryId correlationId (fun () ->
                            task {
                                let workItemNumberCounterActorProxy = WorkItemNumberCounter.CreateActorProxy graceIds.RepositoryId correlationId
                                let! workItemNumber = workItemNumberCounterActorProxy.AllocateNext correlationId
                                let actorProxy = WorkItem.CreateActorProxy workItemId graceIds.RepositoryId correlationId

                                let command =
                                    WorkItemCommand.Create(
                                        workItemId,
                                        workItemNumber,
                                        Guid.Parse(parameters.OwnerId),
                                        Guid.Parse(parameters.OrganizationId),
                                        Guid.Parse(parameters.RepositoryId),
                                        parameters.Title,
                                        parameters.Description
                                    )

                                match! actorProxy.Handle command metadata with
                                | Ok graceReturnValue ->
                                    do! cacheWorkItemNumber graceIds.RepositoryId workItemNumber workItemId correlationId
                                    return Ok graceReturnValue
                                | Error graceError -> return Error graceError
                            })

                    match createResult with
                    | Ok graceReturnValue ->
                        graceReturnValue
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance(nameof WorkItemId, workItemId)
                            .enhance("Command", nameof Create)
                            .enhance("Path", context.Request.Path.Value)
                        |> ignore

                        return! context |> result200Ok graceReturnValue
                    | Error graceError ->
                        graceError
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance(nameof WorkItemId, workItemId)
                            .enhance("Command", nameof Create)
                            .enhance("Path", context.Request.Path.Value)
                        |> ignore

                        return! context |> result400BadRequest graceError
                else
                    let! error = validations |> getFirstError
                    let errorMessage = WorkItemError.getErrorMessage error
                    return! context |> result400BadRequest (GraceError.Create errorMessage correlationId)
            }

    /// Gets a work item.
    let Get: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                let validations (parameters: GetWorkItemParameters) =
                    [|
                        validateWorkItemIdentifier parameters.WorkItemId
                    |]

                let query (context: HttpContext) _ (actorProxy: IWorkItemActor) = actorProxy.Get(getCorrelationId context)

                let! parameters = context |> parse<GetWorkItemParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString
                context.Items[ "Command" ] <- "Get"
                return! processQuery context parameters validations query
            }

    let private buildLinksDto (workItemDto: WorkItemDto) (artifactMetadataById: IReadOnlyDictionary<ArtifactId, ArtifactMetadata option>) =
        let agentSummaryArtifactIds = ResizeArray<ArtifactId>()
        let promptArtifactIds = ResizeArray<ArtifactId>()
        let reviewNotesArtifactIds = ResizeArray<ArtifactId>()
        let otherArtifactIds = ResizeArray<ArtifactId>()

        workItemDto.ArtifactIds
        |> Seq.iter (fun artifactId ->
            match artifactMetadataById[artifactId] with
            | Some artifactMetadata ->
                match artifactMetadata.ArtifactType with
                | ArtifactType.AgentSummary -> agentSummaryArtifactIds.Add(artifactId)
                | ArtifactType.Prompt -> promptArtifactIds.Add(artifactId)
                | ArtifactType.ReviewNotes -> reviewNotesArtifactIds.Add(artifactId)
                | _ -> otherArtifactIds.Add(artifactId)
            | None -> otherArtifactIds.Add(artifactId))

        {
            WorkItemId = workItemDto.WorkItemId
            WorkItemNumber = workItemDto.WorkItemNumber
            ReferenceIds = workItemDto.ReferenceIds
            PromotionSetIds = workItemDto.PromotionSetIds
            ArtifactIds = workItemDto.ArtifactIds
            AgentSummaryArtifactIds = agentSummaryArtifactIds |> Seq.toList
            PromptArtifactIds = promptArtifactIds |> Seq.toList
            ReviewNotesArtifactIds = reviewNotesArtifactIds |> Seq.toList
            OtherArtifactIds = otherArtifactIds |> Seq.toList
        }

    /// Gets work item links grouped by link category.
    let GetLinks: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                use activity = activitySource.StartActivity("GetLinks", ActivityKind.Server)
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context
                let! parameters = context |> parse<GetWorkItemLinksParameters>
                let parameterDictionary = getParametersAsDictionary parameters

                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let validationResults =
                    [|
                        validateWorkItemIdentifier parameters.WorkItemId
                    |]

                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    match! resolveWorkItemId graceIds.RepositoryId parameters.WorkItemId correlationId with
                    | Error graceError -> return! context |> result400BadRequest graceError
                    | Ok workItemId ->
                        let workItemActorProxy = WorkItem.CreateActorProxy workItemId graceIds.RepositoryId correlationId
                        let! workItemDto = workItemActorProxy.Get correlationId
                        do! cacheWorkItemNumber graceIds.RepositoryId workItemDto.WorkItemNumber workItemDto.WorkItemId correlationId

                        let artifactMetadataById = Dictionary<ArtifactId, ArtifactMetadata option>()
                        let artifactIds = workItemDto.ArtifactIds |> List.toArray
                        let mutable i = 0

                        while i < artifactIds.Length do
                            let artifactId = artifactIds[i]
                            let artifactActorProxy = Artifact.CreateActorProxy artifactId graceIds.RepositoryId correlationId
                            let! artifactMetadata = artifactActorProxy.Get correlationId
                            artifactMetadataById[artifactId] <- artifactMetadata
                            i <- i + 1

                        let linksDto = buildLinksDto workItemDto artifactMetadataById

                        let graceReturnValue =
                            (GraceReturnValue.Create linksDto correlationId)
                                .enhance(parameterDictionary)
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof WorkItemId, workItemId)
                                .enhance("Path", context.Request.Path.Value)

                        return! context |> result200Ok graceReturnValue
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = WorkItemError.getErrorMessage error
                    return! context |> result400BadRequest (GraceError.Create errorMessage correlationId)
            }

    /// Updates a work item.
    let Update: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                let validations (parameters: UpdateWorkItemParameters) =
                    [|
                        validateWorkItemIdentifier parameters.WorkItemId
                        (if String.IsNullOrEmpty(parameters.Status) then
                             Ok() |> returnValueTask
                         else
                             DiscriminatedUnion.isMemberOf<WorkItemStatus, WorkItemError> parameters.Status WorkItemError.InvalidStatus)
                    |]

                let! parameters = context |> parse<UpdateWorkItemParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString
                let parameterDictionary = getParametersAsDictionary parameters

                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    match! resolveWorkItemId graceIds.RepositoryId parameters.WorkItemId correlationId with
                    | Error graceError -> return! context |> result400BadRequest graceError
                    | Ok workItemId ->
                        let actorProxy = WorkItem.CreateActorProxy workItemId graceIds.RepositoryId correlationId
                        let metadata = createMetadata context
                        let commands = buildUpdateCommands parameters

                        if commands.IsEmpty then
                            let graceError =
                                (GraceError.Create "No updates were provided." correlationId)
                                    .enhance(nameof OwnerId, graceIds.OwnerId)
                                    .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                    .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                    .enhance(nameof WorkItemId, workItemId)
                                    .enhance("Path", context.Request.Path.Value)

                            return! context |> result400BadRequest graceError
                        else
                            let commandsArray = commands |> List.toArray
                            let mutable result: GraceResult<string> option = None
                            let mutable i = 0

                            while i < commandsArray.Length do
                                match result with
                                | Some(Error _) -> i <- commandsArray.Length
                                | _ ->
                                    let! handleResult = actorProxy.Handle commandsArray[i] metadata
                                    result <- Some handleResult
                                    i <- i + 1

                            match result with
                            | Some(Ok graceReturnValue) ->
                                graceReturnValue
                                    .enhance(parameterDictionary)
                                    .enhance(nameof OwnerId, graceIds.OwnerId)
                                    .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                    .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                    .enhance(nameof WorkItemId, workItemId)
                                    .enhance("Command", "Update")
                                    .enhance("Path", context.Request.Path.Value)
                                |> ignore

                                return! context |> result200Ok graceReturnValue
                            | Some(Error graceError) ->
                                graceError
                                    .enhance(parameterDictionary)
                                    .enhance(nameof OwnerId, graceIds.OwnerId)
                                    .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                    .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                    .enhance(nameof WorkItemId, workItemId)
                                    .enhance("Command", "Update")
                                    .enhance("Path", context.Request.Path.Value)
                                |> ignore

                                return! context |> result400BadRequest graceError
                            | None ->
                                return!
                                    context
                                    |> result400BadRequest (GraceError.Create "No updates were applied." correlationId)
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = WorkItemError.getErrorMessage error

                    let graceError =
                        (GraceError.Create errorMessage correlationId)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result400BadRequest graceError
            }

    /// Links a reference to a work item.
    let LinkReference: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: LinkReferenceParameters) = validateLinkReferenceParameters parameters

                let command (parameters: LinkReferenceParameters) =
                    WorkItemCommand.LinkReference(Guid.Parse(parameters.ReferenceId))
                    |> returnValueTask

                context.Items[ "Command" ] <- nameof LinkReference
                return! processCommand context validations command
            }

    /// Removes a reference link from a work item.
    let RemoveReferenceLink: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: RemoveReferenceLinkParameters) =
                    [|
                        validateWorkItemIdentifier parameters.WorkItemId
                        Guid.isValidAndNotEmptyGuid parameters.ReferenceId WorkItemError.InvalidReferenceId
                    |]

                let command (parameters: RemoveReferenceLinkParameters) =
                    WorkItemCommand.UnlinkReference(Guid.Parse(parameters.ReferenceId))
                    |> returnValueTask

                context.Items[ "Command" ] <- "RemoveReferenceLink"
                return! processCommand context validations command
            }

    /// Links a promotion set to a work item.
    let LinkPromotionSet: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: LinkPromotionSetParameters) = validateLinkPromotionSetParameters parameters

                let command (parameters: LinkPromotionSetParameters) =
                    WorkItemCommand.LinkPromotionSet(Guid.Parse(parameters.PromotionSetId))
                    |> returnValueTask

                context.Items[ "Command" ] <- nameof LinkPromotionSet
                return! processCommand context validations command
            }

    /// Removes a promotion set link from a work item.
    let RemovePromotionSetLink: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: RemovePromotionSetLinkParameters) =
                    [|
                        validateWorkItemIdentifier parameters.WorkItemId
                        Guid.isValidAndNotEmptyGuid parameters.PromotionSetId WorkItemError.InvalidPromotionSetId
                    |]

                let command (parameters: RemovePromotionSetLinkParameters) =
                    WorkItemCommand.UnlinkPromotionSet(Guid.Parse(parameters.PromotionSetId))
                    |> returnValueTask

                context.Items[ "Command" ] <- "RemovePromotionSetLink"
                return! processCommand context validations command
            }

    /// Links an artifact to a work item.
    let LinkArtifact: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: LinkArtifactParameters) = validateLinkArtifactParameters parameters

                let command (parameters: LinkArtifactParameters) =
                    WorkItemCommand.LinkArtifact(Guid.Parse(parameters.ArtifactId))
                    |> returnValueTask

                context.Items[ "Command" ] <- nameof LinkArtifact
                return! processCommand context validations command
            }

    /// Removes an artifact link from a work item.
    let RemoveArtifactLink: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: RemoveArtifactLinkParameters) =
                    [|
                        validateWorkItemIdentifier parameters.WorkItemId
                        Guid.isValidAndNotEmptyGuid parameters.ArtifactId WorkItemError.InvalidArtifactId
                    |]

                let command (parameters: RemoveArtifactLinkParameters) =
                    WorkItemCommand.UnlinkArtifact(Guid.Parse(parameters.ArtifactId))
                    |> returnValueTask

                context.Items[ "Command" ] <- "RemoveArtifactLink"
                return! processCommand context validations command
            }

    /// Removes artifact links from a work item by artifact type.
    let RemoveArtifactTypeLinks: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                use activity = activitySource.StartActivity("RemoveArtifactTypeLinks", ActivityKind.Server)
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context
                let! parameters = context |> parse<RemoveArtifactTypeLinksParameters>

                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let validationResults = validateRemoveArtifactTypeLinksParameters parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    match parseRemovableArtifactType parameters.ArtifactType with
                    | Error error ->
                        let errorMessage = WorkItemError.getErrorMessage error
                        return! context |> result400BadRequest (GraceError.Create errorMessage correlationId)
                    | Ok artifactType ->
                        match! resolveWorkItemId graceIds.RepositoryId parameters.WorkItemId correlationId with
                        | Error graceError -> return! context |> result400BadRequest graceError
                        | Ok workItemId ->
                            let workItemActorProxy = WorkItem.CreateActorProxy workItemId graceIds.RepositoryId correlationId
                            let! workItemDto = workItemActorProxy.Get correlationId
                            let artifactIds = workItemDto.ArtifactIds |> List.toArray
                            let removableArtifactIds = ResizeArray<ArtifactId>()
                            let mutable i = 0

                            while i < artifactIds.Length do
                                let artifactId = artifactIds[i]
                                let artifactActorProxy = Artifact.CreateActorProxy artifactId graceIds.RepositoryId correlationId
                                let! artifactMetadata = artifactActorProxy.Get correlationId

                                match artifactMetadata with
                                | Some metadata when metadata.ArtifactType = artifactType -> removableArtifactIds.Add(artifactId)
                                | _ -> ()

                                i <- i + 1

                            let metadata = createMetadata context
                            let removableArtifactIdsArray = removableArtifactIds |> Seq.toArray
                            let mutable removedCount = 0
                            let mutable removeError: GraceError option = None
                            let mutable j = 0

                            while j < removableArtifactIdsArray.Length && removeError.IsNone do
                                let artifactId = removableArtifactIdsArray[j]
                                let! removeResult = workItemActorProxy.Handle (WorkItemCommand.UnlinkArtifact artifactId) metadata

                                match removeResult with
                                | Ok _ ->
                                    removedCount <- removedCount + 1
                                    j <- j + 1
                                | Error graceError -> removeError <- Some graceError

                            match removeError with
                            | Some graceError -> return! context |> result400BadRequest graceError
                            | None ->
                                let resultMessage =
                                    $"Removed {removedCount} artifact link(s) of type {getDiscriminatedUnionCaseName artifactType}."

                                let graceReturnValue = GraceReturnValue.Create resultMessage correlationId
                                return! context |> result200Ok graceReturnValue
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = WorkItemError.getErrorMessage error
                    return! context |> result400BadRequest (GraceError.Create errorMessage correlationId)
            }
