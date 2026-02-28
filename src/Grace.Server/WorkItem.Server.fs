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
open System.IO
open System.Security.Cryptography
open System.Text
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

        if not <| String.IsNullOrWhiteSpace(value)
           && Guid.TryParse(value, &parsedGuid)
           && parsedGuid <> Guid.Empty then
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
                        | None -> return Error(GraceError.Create (WorkItemError.getErrorMessage WorkItemError.WorkItemDoesNotExist) correlationId)
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
                            .enhance ("Path", context.Request.Path.Value)
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
                                .enhance ("Path", context.Request.Path.Value)
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
                                .enhance ("Path", context.Request.Path.Value)
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
                            .enhance ("Path", context.Request.Path.Value)
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
                                .enhance ("Path", context.Request.Path.Value)

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
        elif
            String.Equals(artifactType, "summary", StringComparison.OrdinalIgnoreCase)
            || String.Equals(artifactType, "agentsummary", StringComparison.OrdinalIgnoreCase)
        then
            Ok ArtifactType.AgentSummary
        elif String.Equals(artifactType, "prompt", StringComparison.OrdinalIgnoreCase) then
            Ok ArtifactType.Prompt
        elif
            String.Equals(artifactType, "notes", StringComparison.OrdinalIgnoreCase)
            || String.Equals(artifactType, "reviewnotes", StringComparison.OrdinalIgnoreCase)
        then
            Ok ArtifactType.ReviewNotes
        else
            Error WorkItemError.InvalidArtifactType

    let internal parseAttachmentType (artifactType: string) = parseRemovableArtifactType artifactType

    let internal validateListWorkItemAttachmentsParameters (parameters: ListWorkItemAttachmentsParameters) =
        [|
            validateWorkItemIdentifier parameters.WorkItemId
        |]

    let internal validateShowWorkItemAttachmentParameters (parameters: ShowWorkItemAttachmentParameters) =
        [|
            validateWorkItemIdentifier parameters.WorkItemId
            String.isNotEmpty parameters.AttachmentType WorkItemError.InvalidArtifactType
        |]

    let internal validateDownloadWorkItemAttachmentParameters (parameters: DownloadWorkItemAttachmentParameters) =
        [|
            validateWorkItemIdentifier parameters.WorkItemId
            Guid.isValidAndNotEmptyGuid parameters.ArtifactId WorkItemError.InvalidArtifactId
        |]

    let internal validateRemoveArtifactTypeLinksParameters (parameters: RemoveArtifactTypeLinksParameters) =
        [|
            validateWorkItemIdentifier parameters.WorkItemId
            String.isNotEmpty parameters.ArtifactType WorkItemError.InvalidArtifactType
        |]

    let internal canonicalAddSummaryContractMessage =
        "Canonical add-summary requests must provide SummaryContent (required), PromptContent (optional), PromptOrigin (optional with PromptContent), and PromotionSetId (optional). Caller-supplied SummaryArtifactId/PromptArtifactId values are not supported."

    let private tryParseNonEmptyGuid (value: string) =
        let mutable parsed = Guid.Empty

        if not <| String.IsNullOrWhiteSpace(value)
           && Guid.TryParse(value, &parsed)
           && parsed <> Guid.Empty then
            Some parsed
        else
            None

    let private resolveScopeId (resolvedId: Guid) (rawValue: string) =
        if resolvedId <> Guid.Empty then
            resolvedId
        else
            tryParseNonEmptyGuid rawValue
            |> Option.defaultValue Guid.Empty

    let private resolveWorkItemScopeIds (graceIds: GraceIds) (parameters: WorkItemParameters) =
        let ownerId = resolveScopeId graceIds.OwnerId parameters.OwnerId
        let organizationId = resolveScopeId graceIds.OrganizationId parameters.OrganizationId
        let repositoryId = resolveScopeId graceIds.RepositoryId parameters.RepositoryId
        ownerId, organizationId, repositoryId

    let internal validateAddSummaryParameters (parameters: AddSummaryParameters) =
        match tryParseWorkItemIdentifier parameters.WorkItemId with
        | Error workItemError -> Error(WorkItemError.getErrorMessage workItemError)
        | Ok _ ->
            if String.IsNullOrWhiteSpace(parameters.SummaryContent) then
                Error($"SummaryContent is required. {canonicalAddSummaryContractMessage}")
            elif not
                 <| String.IsNullOrWhiteSpace(parameters.SummaryArtifactId)
                 || not
                    <| String.IsNullOrWhiteSpace(parameters.PromptArtifactId) then
                Error($"Caller-supplied artifact IDs are not supported by add-summary. {canonicalAddSummaryContractMessage}")
            elif
                not
                <| String.IsNullOrWhiteSpace(parameters.PromptOrigin)
                && String.IsNullOrWhiteSpace(parameters.PromptContent)
            then
                Error($"PromptOrigin can only be provided when PromptContent is provided. {canonicalAddSummaryContractMessage}")
            elif not
                 <| String.IsNullOrWhiteSpace(parameters.PromotionSetId)
                 && (tryParseNonEmptyGuid parameters.PromotionSetId
                     |> Option.isNone) then
                Error "PromotionSetId must be a valid non-empty Guid."
            else
                Ok()

    let private normalizeAddSummaryMimeType (mimeType: string) = if String.IsNullOrWhiteSpace(mimeType) then "text/markdown" else mimeType.Trim()

    let internal buildAddSummaryArtifactSeed (repositoryId: RepositoryId) (workItemId: WorkItemId) (artifactCorrelationId: CorrelationId) =
        let normalizedCorrelationId =
            if String.IsNullOrWhiteSpace(artifactCorrelationId) then
                String.Empty
            else
                artifactCorrelationId.Trim().ToLowerInvariant()

        let repositorySegment = repositoryId.ToString("N")
        let workItemSegment = workItemId.ToString("N")

        $"{repositorySegment}|{workItemSegment}|{normalizedCorrelationId}"

    let private createDeterministicArtifactId (seed: string) =
        let normalizedSeed =
            if String.IsNullOrWhiteSpace(seed) then
                String.Empty
            else
                seed.Trim().ToLowerInvariant()

        let seedBytes = Encoding.UTF8.GetBytes(normalizedSeed)

        use hasher = SHA256.Create()
        let hash = hasher.ComputeHash(seedBytes)
        let guidBytes = hash[0..15]
        guidBytes[6] <- (guidBytes[6] &&& 0x0Fuy) ||| 0x50uy
        guidBytes[8] <- (guidBytes[8] &&& 0x3Fuy) ||| 0x80uy
        Guid(guidBytes)

    let internal buildDeterministicAddSummaryArtifactId (repositoryId: RepositoryId) (workItemId: WorkItemId) (artifactCorrelationId: CorrelationId) =
        buildAddSummaryArtifactSeed repositoryId workItemId artifactCorrelationId
        |> createDeterministicArtifactId

    let internal buildDeterministicAddSummaryBlobPath (artifactId: ArtifactId) = $"grace-artifacts/by-id/{artifactId}"

    let private computeSha256 (contentBytes: byte array) =
        use hasher = SHA256.Create()
        let hash = hasher.ComputeHash(contentBytes)
        Convert.ToHexString(hash).ToLowerInvariant()

    let private isGraceTestingEnabled () =
        match Environment.GetEnvironmentVariable("GRACE_TESTING") with
        | null -> false
        | value ->
            value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)

    let private uploadArtifactContent repositoryDto (blobPath: string) (contentBytes: byte array) (correlationId: CorrelationId) =
        task {
            try
                use stream = new MemoryStream(contentBytes)
                let! containerClient = getContainerClient repositoryDto correlationId
                let! _ = containerClient.CreateIfNotExistsAsync()
                let! containerExists = containerClient.ExistsAsync()

                if not containerExists.Value then
                    return Error(GraceError.Create $"Artifact container '{containerClient.Name}' does not exist for blob path '{blobPath}'." correlationId)
                else
                    let blobClient = containerClient.GetBlobClient(blobPath)
                    let! _ = blobClient.UploadAsync(stream, overwrite = true)
                    return Ok()
            with
            | :? Azure.RequestFailedException as requestEx when
                isGraceTestingEnabled ()
                && String.Equals(requestEx.ErrorCode, "ContainerNotFound", StringComparison.OrdinalIgnoreCase)
                ->
                return Ok()
            | ex -> return Error(GraceError.Create $"Failed to upload artifact content: {ex.Message}" correlationId)
        }

    let private withCorrelationId (metadata: EventMetadata) (correlationId: CorrelationId) = { metadata with CorrelationId = correlationId }

    let private addSummaryCorrelationId (baseCorrelationId: CorrelationId) (segment: string) = $"{baseCorrelationId}:add-summary:{segment}"

    let private isDuplicateCorrelationIdError (graceError: GraceError) =
        String.Equals(graceError.Error, WorkItemError.getErrorMessage WorkItemError.DuplicateCorrelationId, StringComparison.OrdinalIgnoreCase)

    let private isArtifactDuplicateCorrelationIdError (graceError: GraceError) =
        String.Equals(graceError.Error, "Duplicate correlation ID for Artifact command.", StringComparison.OrdinalIgnoreCase)

    let private isArtifactAlreadyExistsError (graceError: GraceError) =
        String.Equals(graceError.Error, "Artifact already exists.", StringComparison.OrdinalIgnoreCase)

    let private isRecoverableArtifactCreateError (graceError: GraceError) =
        isArtifactDuplicateCorrelationIdError graceError
        || isArtifactAlreadyExistsError graceError

    let private handleWorkItemCommandAllowReplay (workItemActorProxy: IWorkItemActor) (command: WorkItemCommand) (metadata: EventMetadata) =
        task {
            match! workItemActorProxy.Handle command metadata with
            | Ok _ -> return Ok()
            | Error graceError when isDuplicateCorrelationIdError graceError -> return Ok()
            | Error graceError -> return Error graceError
        }

    let private createArtifactFromContent
        repositoryDto
        (graceIds: GraceIds)
        (workItemId: WorkItemId)
        (metadata: EventMetadata)
        (artifactType: ArtifactType)
        (mimeType: string)
        (content: string)
        =
        task {
            let artifactId = buildDeterministicAddSummaryArtifactId graceIds.RepositoryId workItemId metadata.CorrelationId

            let contentBytes = Encoding.UTF8.GetBytes(content)
            let createdAt = metadata.Timestamp
            let blobPath = buildDeterministicAddSummaryBlobPath artifactId

            let artifactMetadata: ArtifactMetadata =
                { ArtifactMetadata.Default with
                    ArtifactId = artifactId
                    OwnerId = graceIds.OwnerId
                    OrganizationId = graceIds.OrganizationId
                    RepositoryId = graceIds.RepositoryId
                    ArtifactType = artifactType
                    MimeType = normalizeAddSummaryMimeType mimeType
                    Size = int64 contentBytes.LongLength
                    Sha256 = Some(Sha256Hash(computeSha256 contentBytes))
                    BlobPath = blobPath
                    CreatedAt = createdAt
                    CreatedBy = UserId metadata.Principal
                }

            let artifactActorProxy = Artifact.CreateActorProxy artifactId graceIds.RepositoryId metadata.CorrelationId

            let! persistedArtifactMetadataResult =
                task {
                    match! artifactActorProxy.Handle (ArtifactCommand.Create artifactMetadata) metadata with
                    | Ok _ -> return Ok artifactMetadata
                    | Error graceError when isRecoverableArtifactCreateError graceError ->
                        match! artifactActorProxy.Get metadata.CorrelationId with
                        | Some existingMetadata -> return Ok existingMetadata
                        | None -> return Error graceError
                    | Error graceError -> return Error graceError
                }

            match persistedArtifactMetadataResult with
            | Error graceError -> return Error graceError
            | Ok persistedArtifactMetadata ->
                if persistedArtifactMetadata.ArtifactType
                   <> artifactType then
                    return
                        Error(
                            GraceError.Create
                                $"Artifact '{artifactId}' already exists with type '{getDiscriminatedUnionCaseName persistedArtifactMetadata.ArtifactType}', expected '{getDiscriminatedUnionCaseName artifactType}'."
                                metadata.CorrelationId
                        )
                else
                    match! uploadArtifactContent repositoryDto persistedArtifactMetadata.BlobPath contentBytes metadata.CorrelationId with
                    | Error graceError -> return Error graceError
                    | Ok _ -> return Ok artifactId
        }

    /// Adds summary content (and optional prompt content) to a work item using the canonical add-summary request mode.
    let AddSummary: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            async {
                use activity = activitySource.StartActivity("AddSummary", ActivityKind.Server)
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                let! parameters =
                    context
                    |> parse<AddSummaryParameters>
                    |> Async.AwaitTask

                let parameterDictionary = getParametersAsDictionary parameters

                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let withContext (graceError: GraceError) =
                    graceError
                        .enhance(parameterDictionary)
                        .enhance(nameof OwnerId, graceIds.OwnerId)
                        .enhance(nameof OrganizationId, graceIds.OrganizationId)
                        .enhance(nameof RepositoryId, graceIds.RepositoryId)
                        .enhance("Command", "AddSummary")
                        .enhance ("Path", context.Request.Path.Value)
                    |> ignore

                    graceError

                match validateAddSummaryParameters parameters with
                | Error validationError ->
                    return!
                        context
                        |> result400BadRequest (
                            GraceError.Create validationError correlationId
                            |> withContext
                        )
                        |> Async.AwaitTask
                | Ok _ ->
                    let! workItemIdResult =
                        resolveWorkItemId graceIds.RepositoryId parameters.WorkItemId correlationId
                        |> Async.AwaitTask

                    match workItemIdResult with
                    | Error graceError ->
                        return!
                            context
                            |> result400BadRequest (graceError |> withContext)
                            |> Async.AwaitTask
                    | Ok workItemId ->
                        let requestMetadata = createMetadata context
                        let workItemActorProxy = WorkItem.CreateActorProxy workItemId graceIds.RepositoryId correlationId
                        let repositoryActorProxy = Repository.CreateActorProxy graceIds.OrganizationId graceIds.RepositoryId correlationId

                        let! repositoryDto =
                            repositoryActorProxy.Get correlationId
                            |> Async.AwaitTask

                        let summaryArtifactMetadata = withCorrelationId requestMetadata (addSummaryCorrelationId correlationId "summary-artifact")

                        let summaryLinkMetadata = withCorrelationId requestMetadata (addSummaryCorrelationId correlationId "summary-link")

                        let! summaryArtifactResult =
                            createArtifactFromContent
                                repositoryDto
                                graceIds
                                workItemId
                                summaryArtifactMetadata
                                ArtifactType.AgentSummary
                                parameters.SummaryMimeType
                                parameters.SummaryContent
                            |> Async.AwaitTask

                        match summaryArtifactResult with
                        | Error graceError ->
                            return!
                                context
                                |> result400BadRequest (graceError |> withContext)
                                |> Async.AwaitTask
                        | Ok summaryArtifactId ->
                            let! summaryLinkResult =
                                handleWorkItemCommandAllowReplay workItemActorProxy (WorkItemCommand.LinkArtifact summaryArtifactId) summaryLinkMetadata
                                |> Async.AwaitTask

                            match summaryLinkResult with
                            | Error graceError ->
                                return!
                                    context
                                    |> result400BadRequest (graceError |> withContext)
                                    |> Async.AwaitTask
                            | Ok _ ->
                                let hasPromptContent =
                                    not
                                    <| String.IsNullOrWhiteSpace(parameters.PromptContent)

                                let! promptArtifactResult =
                                    if hasPromptContent then
                                        async {
                                            let promptArtifactMetadata =
                                                withCorrelationId requestMetadata (addSummaryCorrelationId correlationId "prompt-artifact")

                                            let promptLinkMetadata = withCorrelationId requestMetadata (addSummaryCorrelationId correlationId "prompt-link")

                                            let! createdPromptArtifactResult =
                                                createArtifactFromContent
                                                    repositoryDto
                                                    graceIds
                                                    workItemId
                                                    promptArtifactMetadata
                                                    ArtifactType.Prompt
                                                    parameters.PromptMimeType
                                                    parameters.PromptContent
                                                |> Async.AwaitTask

                                            match createdPromptArtifactResult with
                                            | Error graceError -> return Error graceError
                                            | Ok promptArtifactId ->
                                                let! promptLinkResult =
                                                    handleWorkItemCommandAllowReplay
                                                        workItemActorProxy
                                                        (WorkItemCommand.LinkArtifact promptArtifactId)
                                                        promptLinkMetadata
                                                    |> Async.AwaitTask

                                                match promptLinkResult with
                                                | Error graceError -> return Error graceError
                                                | Ok _ -> return Ok(Some promptArtifactId)
                                        }
                                        |> Async.StartAsTask
                                        |> Async.AwaitTask
                                    else
                                        async { return Ok None }

                                match promptArtifactResult with
                                | Error graceError ->
                                    return!
                                        context
                                        |> result400BadRequest (graceError |> withContext)
                                        |> Async.AwaitTask
                                | Ok promptArtifactId ->
                                    let promotionSetIdOption = tryParseNonEmptyGuid parameters.PromotionSetId

                                    let! promotionSetLinkResult =
                                        match promotionSetIdOption with
                                        | Some promotionSetId ->
                                            let promotionSetLinkMetadata =
                                                withCorrelationId requestMetadata (addSummaryCorrelationId correlationId "promotion-set-link")

                                            handleWorkItemCommandAllowReplay
                                                workItemActorProxy
                                                (WorkItemCommand.LinkPromotionSet promotionSetId)
                                                promotionSetLinkMetadata
                                            |> Async.AwaitTask
                                        | None -> async { return Ok() }

                                    match promotionSetLinkResult with
                                    | Error graceError ->
                                        return!
                                            context
                                            |> result400BadRequest (graceError |> withContext)
                                            |> Async.AwaitTask
                                    | Ok _ ->
                                        let response =
                                            AddSummaryResult(
                                                WorkItemId = workItemId.ToString(),
                                                SummaryArtifactId = summaryArtifactId.ToString(),
                                                PromptArtifactId =
                                                    (promptArtifactId
                                                     |> Option.map (fun value -> value.ToString())
                                                     |> Option.defaultValue String.Empty),
                                                PromotionSetId =
                                                    (promotionSetIdOption
                                                     |> Option.map (fun value -> value.ToString())
                                                     |> Option.defaultValue String.Empty)
                                            )

                                        let graceReturnValue =
                                            (GraceReturnValue.Create response correlationId)
                                                .enhance(parameterDictionary)
                                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                                .enhance(nameof WorkItemId, workItemId)
                                                .enhance("Command", "AddSummary")
                                                .enhance ("Path", context.Request.Path.Value)

                                        return!
                                            context
                                            |> result200Ok graceReturnValue
                                            |> Async.AwaitTask
            }
            |> Async.StartAsTask

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
                            .enhance ("Path", context.Request.Path.Value)
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
                            .enhance ("Path", context.Request.Path.Value)
                        |> ignore

                        return! context |> result400BadRequest graceError
                else
                    let! error = validations |> getFirstError
                    let errorMessage = WorkItemError.getErrorMessage error

                    return!
                        context
                        |> result400BadRequest (GraceError.Create errorMessage correlationId)
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

    type private WorkItemAttachment = { ArtifactId: ArtifactId; Metadata: ArtifactMetadata; AttachmentType: string }

    let private getAttachmentTypeName (artifactType: ArtifactType) =
        if isNull (box artifactType) then
            "summary"
        else
            match artifactType with
            | ArtifactType.AgentSummary -> "summary"
            | ArtifactType.Prompt -> "prompt"
            | ArtifactType.ReviewNotes -> "notes"
            | ArtifactType.Other kind when
                kind.Equals("summary", StringComparison.OrdinalIgnoreCase)
                || kind.Equals("agentsummary", StringComparison.OrdinalIgnoreCase)
                ->
                "summary"
            | ArtifactType.Other kind when kind.Equals("prompt", StringComparison.OrdinalIgnoreCase) -> "prompt"
            | ArtifactType.Other kind when
                kind.Equals("notes", StringComparison.OrdinalIgnoreCase)
                || kind.Equals("reviewnotes", StringComparison.OrdinalIgnoreCase)
                ->
                "notes"
            | _ -> "other"

    let private tryGetReviewerAttachmentTypeName (artifactType: ArtifactType) =
        if isNull (box artifactType) then
            Some "summary"
        else
            match artifactType with
            | ArtifactType.AgentSummary -> Some "summary"
            | ArtifactType.Prompt -> Some "prompt"
            | ArtifactType.ReviewNotes -> Some "notes"
            | ArtifactType.Other kind when
                kind.Equals("summary", StringComparison.OrdinalIgnoreCase)
                || kind.Equals("agentsummary", StringComparison.OrdinalIgnoreCase)
                ->
                Some "summary"
            | ArtifactType.Other kind when kind.Equals("prompt", StringComparison.OrdinalIgnoreCase) -> Some "prompt"
            | ArtifactType.Other kind when
                kind.Equals("notes", StringComparison.OrdinalIgnoreCase)
                || kind.Equals("reviewnotes", StringComparison.OrdinalIgnoreCase)
                ->
                Some "notes"
            | _ -> None

    let private isTextMimeType (mimeType: string) =
        if String.IsNullOrWhiteSpace(mimeType) then
            false
        else
            let normalized = mimeType.Trim().ToLowerInvariant()

            normalized.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("+json", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("+xml", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("application/yaml", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("application/x-yaml", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("application/toml", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("application/javascript", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("application/x-javascript", StringComparison.OrdinalIgnoreCase)

    let private toAttachmentDescriptor (attachment: WorkItemAttachment) =
        WorkItemAttachmentDescriptor(
            ArtifactId = attachment.ArtifactId.ToString(),
            AttachmentType = attachment.AttachmentType,
            MimeType = attachment.Metadata.MimeType,
            Size = attachment.Metadata.Size,
            CreatedAt = attachment.Metadata.CreatedAt.ToString()
        )

    let private selectAttachmentDeterministically (attachments: WorkItemAttachment list) (latest: bool) =
        if List.isEmpty attachments then
            None
        else
            let ordered = attachments |> List.toArray

            if latest then Some ordered[ordered.Length - 1] else Some ordered[0]

    let private fetchLinkedReviewerAttachments (repositoryId: RepositoryId) (correlationId: CorrelationId) (workItemDto: WorkItemDto) =
        task {
            let attachments = ResizeArray<WorkItemAttachment>()
            let artifactIds = workItemDto.ArtifactIds |> List.toArray
            let mutable index = 0

            while index < artifactIds.Length do
                let artifactId = artifactIds[index]
                let artifactActorProxy = Artifact.CreateActorProxy artifactId repositoryId correlationId
                let! artifactMetadata = artifactActorProxy.Get correlationId

                match artifactMetadata with
                | Some metadata ->
                    match tryGetReviewerAttachmentTypeName metadata.ArtifactType with
                    | Some attachmentType -> attachments.Add({ ArtifactId = artifactId; Metadata = metadata; AttachmentType = attachmentType })
                    | None -> ()
                | None -> ()

                index <- index + 1

            return
                attachments
                |> Seq.sortBy (fun attachment -> attachment.Metadata.CreatedAt, attachment.ArtifactId.ToString("N"))
                |> Seq.toList
        }

    let private downloadArtifactContentBytes repositoryDto (blobPath: string) (correlationId: CorrelationId) =
        task {
            try
                let! containerClient = getContainerClient repositoryDto correlationId
                let blobClient = containerClient.GetBlobClient(blobPath)
                let! exists = blobClient.ExistsAsync()

                if not exists.Value then
                    if isGraceTestingEnabled () then
                        return Ok(Array.empty<byte>)
                    else
                        return Error(GraceError.Create $"Artifact content was not found at blob path '{blobPath}'." correlationId)
                else
                    let! downloadResult = blobClient.DownloadContentAsync()
                    return Ok(downloadResult.Value.Content.ToArray())
            with
            | :? Azure.RequestFailedException as requestEx when
                isGraceTestingEnabled ()
                && (String.Equals(requestEx.ErrorCode, "BlobNotFound", StringComparison.OrdinalIgnoreCase)
                    || String.Equals(requestEx.ErrorCode, "ContainerNotFound", StringComparison.OrdinalIgnoreCase))
                ->
                return Ok(Array.empty<byte>)
            | ex -> return Error(GraceError.Create $"Failed to download artifact content: {ex.Message}" correlationId)
        }

    let private tryReadAttachmentContent
        (organizationId: OrganizationId)
        (repositoryId: RepositoryId)
        (correlationId: CorrelationId)
        (attachment: WorkItemAttachment)
        =
        task {
            if isTextMimeType attachment.Metadata.MimeType then
                let repositoryActorProxy = Repository.CreateActorProxy organizationId repositoryId correlationId
                let! repositoryDto = repositoryActorProxy.Get correlationId
                let! bytesResult = downloadArtifactContentBytes repositoryDto attachment.Metadata.BlobPath correlationId

                match bytesResult with
                | Ok bytes -> return Ok(Encoding.UTF8.GetString(bytes))
                | Error graceError -> return Error graceError
            else
                return Ok String.Empty
        }

    let private completeShowAttachmentRequest
        (context: HttpContext)
        (ownerId: OwnerId)
        (organizationId: OrganizationId)
        (repositoryId: RepositoryId)
        (correlationId: CorrelationId)
        (parameterDictionary: IReadOnlyDictionary<string, obj>)
        (withContext: GraceError -> GraceError)
        (workItemId: WorkItemId)
        (workItemNumber: WorkItemNumber)
        (selectedAttachment: WorkItemAttachment)
        (availableAttachmentCount: int)
        (selectedUsingLatest: bool)
        =
        task {
            let isTextContent = isTextMimeType selectedAttachment.Metadata.MimeType
            let! contentResult = tryReadAttachmentContent organizationId repositoryId correlationId selectedAttachment

            match contentResult with
            | Error graceError ->
                return!
                    context
                    |> result400BadRequest (graceError |> withContext)
            | Ok content ->
                let response =
                    ShowWorkItemAttachmentResult(
                        WorkItemId = workItemId.ToString(),
                        WorkItemNumber = workItemNumber,
                        AttachmentType = selectedAttachment.AttachmentType,
                        ArtifactId = selectedAttachment.ArtifactId.ToString(),
                        MimeType = selectedAttachment.Metadata.MimeType,
                        Size = selectedAttachment.Metadata.Size,
                        CreatedAt = selectedAttachment.Metadata.CreatedAt.ToString(),
                        IsTextContent = isTextContent,
                        Content = content,
                        AvailableAttachmentCount = availableAttachmentCount,
                        SelectedUsingLatest = selectedUsingLatest
                    )

                let graceReturnValue =
                    (GraceReturnValue.Create response correlationId)
                        .enhance(parameterDictionary)
                        .enhance(nameof OwnerId, ownerId)
                        .enhance(nameof OrganizationId, organizationId)
                        .enhance(nameof RepositoryId, repositoryId)
                        .enhance(nameof WorkItemId, workItemId)
                        .enhance("Command", "ShowAttachment")
                        .enhance ("Path", context.Request.Path.Value)

                return! context |> result200Ok graceReturnValue
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
                                .enhance ("Path", context.Request.Path.Value)

                        return! context |> result200Ok graceReturnValue
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = WorkItemError.getErrorMessage error

                    return!
                        context
                        |> result400BadRequest (GraceError.Create errorMessage correlationId)
            }

    /// Lists reviewer attachments linked to a work item.
    let ListAttachments: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                use activity = activitySource.StartActivity("ListAttachments", ActivityKind.Server)
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                let! parameters =
                    context
                    |> parse<ListWorkItemAttachmentsParameters>

                let parameterDictionary = getParametersAsDictionary parameters
                let ownerId, organizationId, repositoryId = resolveWorkItemScopeIds graceIds parameters

                parameters.OwnerId <- $"{ownerId}"
                parameters.OrganizationId <- $"{organizationId}"
                parameters.RepositoryId <- $"{repositoryId}"

                let withContext (graceError: GraceError) =
                    graceError
                        .enhance(parameterDictionary)
                        .enhance(nameof OwnerId, ownerId)
                        .enhance(nameof OrganizationId, organizationId)
                        .enhance(nameof RepositoryId, repositoryId)
                        .enhance("Command", "ListAttachments")
                        .enhance ("Path", context.Request.Path.Value)
                    |> ignore

                    graceError

                try
                    let validationResults = validateListWorkItemAttachmentsParameters parameters
                    let! validationsPassed = validationResults |> allPass

                    if validationsPassed then
                        match! resolveWorkItemId repositoryId parameters.WorkItemId correlationId with
                        | Error graceError ->
                            return!
                                context
                                |> result400BadRequest (graceError |> withContext)
                        | Ok workItemId ->
                            let workItemActorProxy = WorkItem.CreateActorProxy workItemId repositoryId correlationId
                            let! workItemDto = workItemActorProxy.Get correlationId
                            do! cacheWorkItemNumber repositoryId workItemDto.WorkItemNumber workItemDto.WorkItemId correlationId
                            let! attachments = fetchLinkedReviewerAttachments repositoryId correlationId workItemDto

                            let response =
                                ListWorkItemAttachmentsResult(
                                    WorkItemId = workItemId.ToString(),
                                    WorkItemNumber = workItemDto.WorkItemNumber,
                                    Attachments = List<WorkItemAttachmentDescriptor>(attachments |> Seq.map toAttachmentDescriptor)
                                )

                            let graceReturnValue =
                                (GraceReturnValue.Create response correlationId)
                                    .enhance(parameterDictionary)
                                    .enhance(nameof OwnerId, ownerId)
                                    .enhance(nameof OrganizationId, organizationId)
                                    .enhance(nameof RepositoryId, repositoryId)
                                    .enhance(nameof WorkItemId, workItemId)
                                    .enhance("Command", "ListAttachments")
                                    .enhance ("Path", context.Request.Path.Value)

                            return! context |> result200Ok graceReturnValue
                    else
                        let! error = validationResults |> getFirstError
                        let errorMessage = WorkItemError.getErrorMessage error

                        return!
                            context
                            |> result400BadRequest (
                                GraceError.Create errorMessage correlationId
                                |> withContext
                            )
                with
                | ex ->
                    return!
                        context
                        |> result500ServerError (
                            GraceError.CreateWithException ex String.Empty correlationId
                            |> withContext
                        )
            }

    /// Shows a reviewer attachment for a work item using deterministic type-filtered selection.
    let ShowAttachment: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                use activity = activitySource.StartActivity("ShowAttachment", ActivityKind.Server)
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context
                let! parameters = context |> parse<ShowWorkItemAttachmentParameters>
                let parameterDictionary = getParametersAsDictionary parameters
                let ownerId, organizationId, repositoryId = resolveWorkItemScopeIds graceIds parameters

                parameters.OwnerId <- $"{ownerId}"
                parameters.OrganizationId <- $"{organizationId}"
                parameters.RepositoryId <- $"{repositoryId}"

                let withContext (graceError: GraceError) =
                    graceError
                        .enhance(parameterDictionary)
                        .enhance(nameof OwnerId, ownerId)
                        .enhance(nameof OrganizationId, organizationId)
                        .enhance(nameof RepositoryId, repositoryId)
                        .enhance("Command", "ShowAttachment")
                        .enhance ("Path", context.Request.Path.Value)
                    |> ignore

                    graceError

                let validationResults = validateShowWorkItemAttachmentParameters parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    match parseAttachmentType parameters.AttachmentType with
                    | Error error ->
                        let errorMessage = WorkItemError.getErrorMessage error

                        return!
                            context
                            |> result400BadRequest (
                                GraceError.Create errorMessage correlationId
                                |> withContext
                            )
                    | Ok artifactType ->
                        let requestedAttachmentType = getAttachmentTypeName artifactType

                        match! resolveWorkItemId repositoryId parameters.WorkItemId correlationId with
                        | Error graceError ->
                            return!
                                context
                                |> result400BadRequest (graceError |> withContext)
                        | Ok workItemId ->
                            let workItemActorProxy = WorkItem.CreateActorProxy workItemId repositoryId correlationId
                            let! workItemDto = workItemActorProxy.Get correlationId
                            do! cacheWorkItemNumber repositoryId workItemDto.WorkItemNumber workItemDto.WorkItemId correlationId
                            let! attachments = fetchLinkedReviewerAttachments repositoryId correlationId workItemDto

                            let filteredAttachments =
                                attachments
                                |> List.filter (fun attachment -> attachment.AttachmentType.Equals(requestedAttachmentType, StringComparison.OrdinalIgnoreCase))

                            match selectAttachmentDeterministically filteredAttachments parameters.Latest with
                            | None ->
                                return!
                                    context
                                    |> result400BadRequest (
                                        GraceError.Create $"No '{requestedAttachmentType}' attachments are linked to this work item." correlationId
                                        |> withContext
                                    )
                            | Some selectedAttachment ->
                                return!
                                    completeShowAttachmentRequest
                                        context
                                        ownerId
                                        organizationId
                                        repositoryId
                                        correlationId
                                        parameterDictionary
                                        withContext
                                        workItemId
                                        workItemDto.WorkItemNumber
                                        selectedAttachment
                                        filteredAttachments.Length
                                        parameters.Latest
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = WorkItemError.getErrorMessage error

                    return!
                        context
                        |> result400BadRequest (
                            GraceError.Create errorMessage correlationId
                            |> withContext
                        )
            }

    /// Gets download metadata for a linked reviewer attachment.
    let DownloadAttachment: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                use activity = activitySource.StartActivity("DownloadAttachment", ActivityKind.Server)
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                let! parameters =
                    context
                    |> parse<DownloadWorkItemAttachmentParameters>

                let parameterDictionary = getParametersAsDictionary parameters
                let ownerId, organizationId, repositoryId = resolveWorkItemScopeIds graceIds parameters

                parameters.OwnerId <- $"{ownerId}"
                parameters.OrganizationId <- $"{organizationId}"
                parameters.RepositoryId <- $"{repositoryId}"

                let withContext (graceError: GraceError) =
                    graceError
                        .enhance(parameterDictionary)
                        .enhance(nameof OwnerId, ownerId)
                        .enhance(nameof OrganizationId, organizationId)
                        .enhance(nameof RepositoryId, repositoryId)
                        .enhance("Command", "DownloadAttachment")
                        .enhance ("Path", context.Request.Path.Value)
                    |> ignore

                    graceError

                let validationResults = validateDownloadWorkItemAttachmentParameters parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    match! resolveWorkItemId repositoryId parameters.WorkItemId correlationId with
                    | Error graceError ->
                        return!
                            context
                            |> result400BadRequest (graceError |> withContext)
                    | Ok workItemId ->
                        let workItemActorProxy = WorkItem.CreateActorProxy workItemId repositoryId correlationId
                        let! workItemDto = workItemActorProxy.Get correlationId
                        do! cacheWorkItemNumber repositoryId workItemDto.WorkItemNumber workItemDto.WorkItemId correlationId
                        let artifactId = Guid.Parse(parameters.ArtifactId)

                        let isLinked =
                            workItemDto.ArtifactIds
                            |> List.contains artifactId

                        if not isLinked then
                            return!
                                context
                                |> result400BadRequest (
                                    GraceError.Create "The specified artifact is not linked to the work item." correlationId
                                    |> withContext
                                )
                        else
                            let artifactActorProxy = Artifact.CreateActorProxy artifactId repositoryId correlationId
                            let! artifactMetadata = artifactActorProxy.Get correlationId

                            match artifactMetadata with
                            | None ->
                                return!
                                    context
                                    |> result400BadRequest (
                                        GraceError.Create (ArtifactError.getErrorMessage ArtifactError.ArtifactDoesNotExist) correlationId
                                        |> withContext
                                    )
                            | Some metadata ->
                                match tryGetReviewerAttachmentTypeName metadata.ArtifactType with
                                | None ->
                                    return!
                                        context
                                        |> result400BadRequest (
                                            GraceError.Create (WorkItemError.getErrorMessage WorkItemError.InvalidArtifactType) correlationId
                                            |> withContext
                                        )
                                | Some attachmentType ->
                                    let repositoryActorProxy = Repository.CreateActorProxy organizationId repositoryId correlationId

                                    let! repositoryDto = repositoryActorProxy.Get correlationId
                                    let! downloadUri = getUriWithReadSharedAccessSignature repositoryDto metadata.BlobPath correlationId

                                    let response =
                                        DownloadWorkItemAttachmentResult(
                                            WorkItemId = workItemId.ToString(),
                                            WorkItemNumber = workItemDto.WorkItemNumber,
                                            AttachmentType = attachmentType,
                                            ArtifactId = artifactId.ToString(),
                                            MimeType = metadata.MimeType,
                                            Size = metadata.Size,
                                            CreatedAt = metadata.CreatedAt.ToString(),
                                            DownloadUri = $"{downloadUri}"
                                        )

                                    let graceReturnValue =
                                        (GraceReturnValue.Create response correlationId)
                                            .enhance(parameterDictionary)
                                            .enhance(nameof OwnerId, ownerId)
                                            .enhance(nameof OrganizationId, organizationId)
                                            .enhance(nameof RepositoryId, repositoryId)
                                            .enhance(nameof WorkItemId, workItemId)
                                            .enhance(nameof ArtifactId, artifactId)
                                            .enhance("Command", "DownloadAttachment")
                                            .enhance ("Path", context.Request.Path.Value)

                                    return! context |> result200Ok graceReturnValue
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = WorkItemError.getErrorMessage error

                    return!
                        context
                        |> result400BadRequest (
                            GraceError.Create errorMessage correlationId
                            |> withContext
                        )
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
                                    .enhance ("Path", context.Request.Path.Value)

                            return! context |> result400BadRequest graceError
                        else
                            let commandsArray = commands |> List.toArray
                            let mutable result: GraceResult<string> option = None
                            let mutable i = 0

                            while i < commandsArray.Length do
                                match result with
                                | Some (Error _) -> i <- commandsArray.Length
                                | _ ->
                                    let! handleResult = actorProxy.Handle commandsArray[i] metadata
                                    result <- Some handleResult
                                    i <- i + 1

                            match result with
                            | Some (Ok graceReturnValue) ->
                                graceReturnValue
                                    .enhance(parameterDictionary)
                                    .enhance(nameof OwnerId, graceIds.OwnerId)
                                    .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                    .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                    .enhance(nameof WorkItemId, workItemId)
                                    .enhance("Command", "Update")
                                    .enhance ("Path", context.Request.Path.Value)
                                |> ignore

                                return! context |> result200Ok graceReturnValue
                            | Some (Error graceError) ->
                                graceError
                                    .enhance(parameterDictionary)
                                    .enhance(nameof OwnerId, graceIds.OwnerId)
                                    .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                    .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                    .enhance(nameof WorkItemId, workItemId)
                                    .enhance("Command", "Update")
                                    .enhance ("Path", context.Request.Path.Value)
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

                let! parameters =
                    context
                    |> parse<RemoveArtifactTypeLinksParameters>

                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let validationResults = validateRemoveArtifactTypeLinksParameters parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    match parseRemovableArtifactType parameters.ArtifactType with
                    | Error error ->
                        let errorMessage = WorkItemError.getErrorMessage error

                        return!
                            context
                            |> result400BadRequest (GraceError.Create errorMessage correlationId)
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

                            while j < removableArtifactIdsArray.Length
                                  && removeError.IsNone do
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
                                let resultMessage = $"Removed {removedCount} artifact link(s) of type {getDiscriminatedUnionCaseName artifactType}."

                                let graceReturnValue = GraceReturnValue.Create resultMessage correlationId
                                return! context |> result200Ok graceReturnValue
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = WorkItemError.getErrorMessage error

                    return!
                        context
                        |> result400BadRequest (GraceError.Create errorMessage correlationId)
            }
