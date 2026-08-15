namespace Grace.Server

open Giraffe
open Grace.Actors.Constants
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Server.ApplicationContext
open Grace.Server.Services
open Grace.Server.WorkItemAttachments
open Grace.Shared
open Grace.Shared.Extensions
open Grace.Shared.Parameters.WorkItem
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors
open Grace.Shared.Validation.Utilities
open Grace.Types.Artifact
open Grace.Types.WorkItem
open Grace.Types.Common
open Grace.Types.TextContent
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net.Sockets
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks

/// Contains Grace Server work item behavior and supporting helpers.
module WorkItem =
    /// Represents validations used by Grace Server APIs and background services.
    type Validations<'T when 'T :> WorkItemParameters> = 'T -> ValueTask<Result<unit, WorkItemError>> array

    /// Represents work item identifier used by Grace Server APIs and background services.
    type WorkItemIdentifier =
        | Id of WorkItemId
        | Number of WorkItemNumber

    let log = ApplicationContext.loggerFactory.CreateLogger("WorkItem.Server")

    let activitySource = new ActivitySource("WorkItem")

    /// Parses try parse work item identifier input into the server model.
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

    /// Validates validate work item identifier inputs before server processing continues.
    let internal validateWorkItemIdentifier (value: string) =
        match tryParseWorkItemIdentifier value with
        | Ok _ -> Ok() |> returnValueTask
        | Error error -> Error error |> returnValueTask

    /// Resolves resolve work item id data from request or repository state.
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

    /// Coordinates cache work item number processing for Grace Server.
    let private cacheWorkItemNumber (repositoryId: RepositoryId) (workItemNumber: WorkItemNumber) (workItemId: WorkItemId) (correlationId: CorrelationId) =
        task {
            let workItemNumberActorProxy = WorkItemNumber.CreateActorProxy repositoryId correlationId
            do! workItemNumberActorProxy.SetWorkItemId workItemNumber workItemId correlationId
        }

    /// Adds work item number lock to the server request model.
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

    /// Coordinates process command processing for Grace Server.
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

    /// Coordinates process query processing for Grace Server.
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

    /// Converts non-empty update parameters into the ordered work-item actor commands for a PATCH request.
    let internal buildUpdateCommands (parameters: UpdateWorkItemParameters) =
        [
            if not <| String.IsNullOrEmpty(parameters.Title) then
                WorkItemCommand.SetTitle parameters.Title
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

    /// Validates validate link reference parameters inputs before server processing continues.
    let internal validateLinkReferenceParameters (parameters: LinkReferenceParameters) =
        [|
            validateWorkItemIdentifier parameters.WorkItemId
            Guid.isValidAndNotEmptyGuid parameters.ReferenceId WorkItemError.InvalidReferenceId
        |]

    /// Validates validate link artifact parameters inputs before server processing continues.
    let internal validateLinkArtifactParameters (parameters: LinkArtifactParameters) =
        [|
            validateWorkItemIdentifier parameters.WorkItemId
            Guid.isValidAndNotEmptyGuid parameters.ArtifactId WorkItemError.InvalidArtifactId
        |]

    /// Validates validate link promotion set parameters inputs before server processing continues.
    let internal validateLinkPromotionSetParameters (parameters: LinkPromotionSetParameters) =
        [|
            validateWorkItemIdentifier parameters.WorkItemId
            Guid.isValidAndNotEmptyGuid parameters.PromotionSetId WorkItemError.InvalidPromotionSetId
        |]

    /// Maps artifact-type filters that are allowed when removing work-item artifacts.
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

    /// Maps attachment-type filters that classify work-item artifact links.
    let internal parseAttachmentType (artifactType: string) = parseRemovableArtifactType artifactType

    /// Validates validate list work item attachments parameters inputs before server processing continues.
    let internal validateListWorkItemAttachmentsParameters (parameters: ListWorkItemAttachmentsParameters) =
        [|
            validateWorkItemIdentifier parameters.WorkItemId
        |]

    /// Validates validate show work item attachment parameters inputs before server processing continues.
    let internal validateShowWorkItemAttachmentParameters (parameters: ShowWorkItemAttachmentParameters) =
        [|
            validateWorkItemIdentifier parameters.WorkItemId
            String.isNotEmpty parameters.AttachmentType WorkItemError.InvalidArtifactType
        |]

    /// Validates validate download work item attachment parameters inputs before server processing continues.
    let internal validateDownloadWorkItemAttachmentParameters (parameters: DownloadWorkItemAttachmentParameters) =
        [|
            validateWorkItemIdentifier parameters.WorkItemId
            String.isNotEmpty parameters.ArtifactId WorkItemError.InvalidArtifactId
            Guid.isValidAndNotEmptyGuid parameters.ArtifactId WorkItemError.InvalidArtifactId
        |]

    /// Validates validate remove artifact type links parameters inputs before server processing continues.
    let internal validateRemoveArtifactTypeLinksParameters (parameters: RemoveArtifactTypeLinksParameters) =
        [|
            validateWorkItemIdentifier parameters.WorkItemId
            String.isNotEmpty parameters.ArtifactType WorkItemError.InvalidArtifactType
        |]

    let internal canonicalAddSummaryContractMessage =
        "Canonical add-summary requests must provide SummaryContent (required), PromptContent (optional), PromptOrigin (optional with PromptContent), and PromotionSetId (optional). Caller-supplied SummaryArtifactId/PromptArtifactId values are not supported."

    /// Parses try parse non empty guid input into the server model.
    let private tryParseNonEmptyGuid (value: string) =
        let mutable parsed = Guid.Empty

        if not <| String.IsNullOrWhiteSpace(value)
           && Guid.TryParse(value, &parsed)
           && parsed <> Guid.Empty then
            Some parsed
        else
            None

    /// Resolves resolve scope id data from request or repository state.
    let private resolveScopeId (resolvedId: Guid) (rawValue: string) =
        if resolvedId <> Guid.Empty then
            resolvedId
        else
            tryParseNonEmptyGuid rawValue
            |> Option.defaultValue Guid.Empty

    /// Resolves resolve work item scope ids data from request or repository state.
    let private resolveWorkItemScopeIds (graceIds: GraceIds) (parameters: WorkItemParameters) =
        let ownerId = resolveScopeId graceIds.OwnerId parameters.OwnerId
        let organizationId = resolveScopeId graceIds.OrganizationId parameters.OrganizationId
        let repositoryId = resolveScopeId graceIds.RepositoryId parameters.RepositoryId
        ownerId, organizationId, repositoryId

    /// Validates validate add summary parameters inputs before server processing continues.
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

    /// Normalizes normalize add summary mime type data for stable server comparisons.
    let internal normalizeAddSummaryMimeType (mimeType: string) = if String.IsNullOrWhiteSpace(mimeType) then "text/markdown" else mimeType.Trim()

    /// Combines repository, work item, and correlation identifiers into the seed for idempotent summary artifacts.
    let internal buildAddSummaryArtifactSeed (repositoryId: RepositoryId) (workItemId: WorkItemId) (artifactCorrelationId: CorrelationId) =
        let normalizedCorrelationId =
            if String.IsNullOrWhiteSpace(artifactCorrelationId) then
                String.Empty
            else
                artifactCorrelationId.Trim().ToLowerInvariant()

        let repositorySegment = repositoryId.ToString("N")
        let workItemSegment = workItemId.ToString("N")

        $"{repositorySegment}|{workItemSegment}|{normalizedCorrelationId}"

    /// Hashes a normalized artifact seed into the deterministic identifier used by summary artifact creation.
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

    /// Derives the repeatable summary artifact id for a work item and request correlation id.
    let internal buildDeterministicAddSummaryArtifactId (repositoryId: RepositoryId) (workItemId: WorkItemId) (artifactCorrelationId: CorrelationId) =
        buildAddSummaryArtifactSeed repositoryId workItemId artifactCorrelationId
        |> createDeterministicArtifactId

    /// Uses the deterministic summary artifact id as the stable blob key for replay-safe summary uploads.
    let internal buildDeterministicAddSummaryBlobPath (artifactId: ArtifactId) = $"grace-artifacts/by-id/{artifactId}"

    /// Implements compute sha256 for the server request pipeline.
    let internal computeSha256 (contentBytes: byte array) =
        use hasher = SHA256.Create()
        let hash = hasher.ComputeHash(contentBytes)
        Convert.ToHexString(hash).ToLowerInvariant()

    /// Determines whether grace testing enabled.
    let private isGraceTestingEnabled () =
        match Environment.GetEnvironmentVariable("GRACE_TESTING") with
        | null -> false
        | value ->
            value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)

    /// Holds a test-hosted clear request after its final fresh replay classification.
    type private DescriptionClearPreAppendTestGate = { Client: TcpClient; Reader: StreamReader; Writer: StreamWriter }

    /// Reads the private ephemeral loopback port only when the hosted-race request explicitly selects it.
    let private tryGetDescriptionClearPreAppendTestGatePort (context: HttpContext) =
        if isGraceTestingEnabled () then
            match Environment.GetEnvironmentVariable("GRACE_TEST_DESCRIPTION_CLEAR_PRE_APPEND_PORT"),
                  context.Request.Headers.TryGetValue("X-Grace-Test-Description-Clear-Gate-Port")
                with
            | configuredPort, (true, requestedPort) when
                not (String.IsNullOrWhiteSpace configuredPort)
                && String.Equals(configuredPort, string requestedPort, StringComparison.Ordinal)
                ->
                match Int32.TryParse configuredPort with
                | true, port when port > 0 && port <= 65535 -> Some port
                | _ -> None
            | _ -> None
        else
            None

    /// Waits at the inert test-host gate only for the selected request and injected loopback port.
    let private tryEnterDescriptionClearPreAppendTestGate (context: HttpContext) =
        task {
            match tryGetDescriptionClearPreAppendTestGatePort context with
            | None -> return None
            | Some port ->
                let client = new TcpClient(AddressFamily.InterNetwork)
                let mutable gate: DescriptionClearPreAppendTestGate option = None
                let mutable stream: NetworkStream option = None
                let mutable reader: StreamReader option = None
                let mutable writer: StreamWriter option = None

                try
                    try
                        use gateTimeout = new Threading.CancellationTokenSource(TimeSpan.FromSeconds(20.0))
                        do! client.ConnectAsync("127.0.0.1", port, gateTimeout.Token)

                        let connectedStream = client.GetStream()
                        stream <- Some connectedStream
                        let connectedReader = new StreamReader(connectedStream, Encoding.UTF8, false, 1024, true)
                        reader <- Some connectedReader
                        let connectedWriter = new StreamWriter(connectedStream, Encoding.UTF8, 1024, true)
                        writer <- Some connectedWriter

                        do! connectedWriter.WriteLineAsync("fresh-description-operation".AsMemory(), gateTimeout.Token)

                        do! connectedWriter.FlushAsync(gateTimeout.Token)

                        let! release =
                            connectedReader
                                .ReadLineAsync(gateTimeout.Token)
                                .AsTask()

                        if String.Equals(release, "release", StringComparison.Ordinal) then
                            let acquiredGate = { Client = client; Reader = connectedReader; Writer = connectedWriter }
                            gate <- Some acquiredGate
                            return Some acquiredGate
                        else
                            return None
                    with
                    | :? TimeoutException -> return None
                    | :? SocketException -> return None
                    | :? OperationCanceledException -> return None
                finally
                    match gate with
                    | Some _ -> ()
                    | None ->
                        writer
                        |> Option.iter (fun activeWriter -> activeWriter.Dispose())

                        reader
                        |> Option.iter (fun activeReader -> activeReader.Dispose())

                        stream
                        |> Option.iter (fun activeStream -> activeStream.Dispose())

                        client.Dispose()
        }

    /// Writes one bounded diagnostic outcome to a selected test-only description-clear gate.
    let private writeDescriptionClearPreAppendTestGateOutcome (gate: DescriptionClearPreAppendTestGate) (outcome: string) =
        task {
            use gateTimeout = new Threading.CancellationTokenSource(TimeSpan.FromSeconds(20.0))

            try
                do! gate.Writer.WriteLineAsync(outcome.AsMemory(), gateTimeout.Token)

                do! gate.Writer.FlushAsync(gateTimeout.Token)
            with
            | :? TimeoutException -> ()
            | :? SocketException -> ()
            | :? OperationCanceledException -> ()
        }

    /// Records that a gated request entered duplicate-result reclassification before returning its HTTP result.
    let private observeDescriptionClearDuplicateResultReclassification (testGate: DescriptionClearPreAppendTestGate option) =
        task {
            match testGate with
            | None -> ()
            | Some gate -> do! writeDescriptionClearPreAppendTestGateOutcome gate "duplicate-result-reclassified"
        }

    /// Records that a gated request appended its description-clear event without a duplicate result.
    let private observeDescriptionClearAppendSucceeded (testGate: DescriptionClearPreAppendTestGate option) =
        task {
            match testGate with
            | None -> ()
            | Some gate -> do! writeDescriptionClearPreAppendTestGateOutcome gate "append-succeeded"
        }

    /// Releases the loopback resources held by one test-hosted clear request.
    let private disposeDescriptionClearPreAppendTestGate (testGate: DescriptionClearPreAppendTestGate option) =
        match testGate with
        | None -> ()
        | Some gate ->
            gate.Writer.Dispose()
            gate.Reader.Dispose()
            gate.Client.Dispose()

    /// Implements upload artifact content for the server request pipeline.
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

    /// Adds correlation id to the server request model.
    let private withCorrelationId (metadata: EventMetadata) (correlationId: CorrelationId) = { metadata with CorrelationId = correlationId }

    /// Coordinates add summary correlation id processing for Grace Server.
    let private addSummaryCorrelationId (baseCorrelationId: CorrelationId) (segment: string) = $"{baseCorrelationId}:add-summary:{segment}"

    /// Determines whether duplicate correlation id error.
    let private isDuplicateCorrelationIdError (graceError: GraceError) =
        String.Equals(graceError.Error, WorkItemError.getErrorMessage WorkItemError.DuplicateCorrelationId, StringComparison.OrdinalIgnoreCase)

    /// Determines whether artifact duplicate correlation id error.
    let private isArtifactDuplicateCorrelationIdError (graceError: GraceError) =
        String.Equals(graceError.Error, "Duplicate correlation ID for Artifact command.", StringComparison.OrdinalIgnoreCase)

    /// Determines whether artifact already exists error.
    let private isArtifactAlreadyExistsError (graceError: GraceError) =
        String.Equals(graceError.Error, "Artifact already exists.", StringComparison.OrdinalIgnoreCase)

    /// Determines whether recoverable artifact create error.
    let internal isRecoverableArtifactCreateError (graceError: GraceError) =
        isArtifactDuplicateCorrelationIdError graceError
        || isArtifactAlreadyExistsError graceError

    /// Coordinates handle work item command allow replay processing for Grace Server.
    let private handleWorkItemCommandAllowReplay (workItemActorProxy: IWorkItemActor) (command: WorkItemCommand) (metadata: EventMetadata) =
        task {
            match! workItemActorProxy.Handle command metadata with
            | Ok _ -> return Ok()
            | Error graceError when isDuplicateCorrelationIdError graceError -> return Ok()
            | Error graceError -> return Error graceError
        }

    /// Identifies the description operation whose durable event can make a retry successful.
    type internal DescriptionOperation =
        | CreateDescription
        | SetDescription
        | ClearDescription

    /// Classifies whether persisted work-item evidence proves, rejects, or has not yet seen a description operation.
    type internal DescriptionReplay =
        | FreshDescriptionOperation
        | ExactDescriptionReplay
        | ConflictingDescriptionCorrelation

    /// Classifies whether a newly written text object can be deleted without discarding ambiguous retry evidence.
    type internal DescriptionAppendFailure =
        | ProvenPreAppendRejection
        | AmbiguousAppendOutcome

    /// Rejects a GUID that resolves to a work item stored under a different repository without disclosing its state.
    let internal isWorkItemBoundToRepository (repositoryId: RepositoryId) (state: WorkItemState) =
        state.WorkItem.WorkItemId <> WorkItemId.Empty
        && state.WorkItem.RepositoryId = repositoryId

    /// Determines whether a persisted event proves the same description operation and immutable reference as a retry.
    let private isMatchingDescriptionEvent operation workItemId repositoryId description workItemEvent =
        match operation, workItemEvent.Event with
        | CreateDescription, Created (eventWorkItemId, _, _, _, eventRepositoryId, _, eventDescription) ->
            eventWorkItemId = workItemId
            && eventRepositoryId = repositoryId
            && eventDescription = description
        | SetDescription, DescriptionSet eventDescription -> Some eventDescription = description
        | ClearDescription, DescriptionCleared eventDescription -> Some eventDescription = description
        | _ -> false

    /// Uses the persisted event stream and current state to distinguish an exact retry from correlation reuse or a new operation.
    let internal classifyDescriptionReplay
        operation
        (workItemId: WorkItemId)
        (repositoryId: RepositoryId)
        (description: Description option)
        (state: WorkItemState)
        (events: IReadOnlyList<WorkItemEvent>)
        (correlationId: CorrelationId)
        =
        match events
              |> Seq.tryFind (fun workItemEvent -> workItemEvent.Metadata.CorrelationId = correlationId)
            with
        | None -> FreshDescriptionOperation
        | Some workItemEvent when
            isMatchingDescriptionEvent operation workItemId repositoryId description workItemEvent
            && state.Description = description
            ->
            ExactDescriptionReplay
        | Some _ -> ConflictingDescriptionCorrelation

    /// Returns a user-safe error when a correlation belongs to another description operation or an obsolete append.
    let internal conflictingDescriptionCorrelationError correlationId =
        GraceError.Create "The correlation ID cannot be reused for a different or superseded work-item description operation." correlationId

    /// Verifies an exact replay object's immutable bytes, recreating only a missing deterministic object before replay success.
    let private ensureExactDescriptionStorage repositoryDto repositoryId workItemId correlationId text expectedDescription =
        task {
            match! TextContentStorage.write repositoryDto repositoryId workItemId correlationId text with
            | Ok (actualDescription, _) when actualDescription = expectedDescription -> return Ok()
            | Ok _ -> return Error(GraceError.Create "Text-content replay identity did not reproduce the expected immutable description." correlationId)
            | Error error -> return Error error
        }

    /// Verifies immutable content before reporting an exact create replay, while keeping description-free creates free of text storage work.
    let private ensureExactCreateDescriptionStorage organizationId repositoryId workItemId correlationId text expectedDescription =
        match expectedDescription with
        | None -> Task.FromResult(Ok())
        | Some expectedDescription ->
            task {
                let repositoryActorProxy = Repository.CreateActorProxy organizationId repositoryId correlationId
                let! repositoryDto = repositoryActorProxy.Get correlationId

                return! ensureExactDescriptionStorage repositoryDto repositoryId workItemId correlationId text expectedDescription
            }

    /// Classifies actor validation failures that prove a create request did not append its event before rejection.
    let internal classifyDescriptionAppendFailure (graceError: GraceError) =
        if
            isDuplicateCorrelationIdError graceError
            || String.Equals(graceError.Error, WorkItemError.getErrorMessage WorkItemError.WorkItemAlreadyExists, StringComparison.OrdinalIgnoreCase)
            || String.Equals(graceError.Error, WorkItemError.getErrorMessage WorkItemError.WorkItemDoesNotExist, StringComparison.OrdinalIgnoreCase)
        then
            ProvenPreAppendRejection
        else
            AmbiguousAppendOutcome

    /// Removes a create-only immutable object after a proven rejection before any WorkItem can reference that object identity.
    let private cleanupProvenDescriptionRejection repositoryDto wasCreated description correlationId =
        task {
            match description.TextContent, wasCreated with
            | Some reference, true ->
                let! _ = TextContentStorage.deleteIfNewlyCreated repositoryDto reference correlationId
                return ()
            | _ -> return ()
        }

    /// Reads the latest work-item state and prevents a request repository from acting on another repository's GUID.
    let private getRepositoryBoundWorkItemState (actorProxy: IWorkItemActor) repositoryId correlationId =
        task {
            let! state = actorProxy.GetState correlationId

            if isWorkItemBoundToRepository repositoryId state then
                return Ok state
            else
                return Error(GraceError.Create (WorkItemError.getErrorMessage WorkItemError.WorkItemDoesNotExist) correlationId)
        }

    /// Uploads generated work-item content as a deterministic artifact and links its metadata to the work item.
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
                    WorkItemId = Some workItemId
                }

            let artifactActorProxy = Artifact.CreateActorProxy artifactId graceIds.RepositoryId metadata.CorrelationId

            let! persistedArtifactMetadataResult =
                task {
                    match! artifactActorProxy.Handle (ArtifactCommand.Create(ArtifactCreated.FromMetadata artifactMetadata)) metadata with
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

                /// Adds context to the server request model.
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

                    let descriptionValidation =
                        if String.IsNullOrWhiteSpace(parameters.Description) then
                            Ok()
                        else
                            TextContentStorage.validateText parameters.Description

                    match descriptionValidation with
                    | Error error ->
                        return!
                            context
                            |> result400BadRequest (GraceError.Create error correlationId)
                    | Ok () ->
                        let expectedDescription =
                            if String.IsNullOrWhiteSpace(parameters.Description) then
                                None
                            else
                                Some(TextContentStorage.createDescription graceIds.RepositoryId workItemId correlationId parameters.Description)

                        let actorProxy = WorkItem.CreateActorProxy workItemId graceIds.RepositoryId correlationId
                        let! existingState = actorProxy.GetState correlationId
                        let! existingEvents = actorProxy.GetEvents correlationId

                        let existingReplay =
                            classifyDescriptionReplay
                                CreateDescription
                                workItemId
                                graceIds.RepositoryId
                                expectedDescription
                                existingState
                                existingEvents
                                correlationId

                        let! createResult =
                            match existingReplay with
                            | ExactDescriptionReplay ->
                                task {
                                    match!
                                        ensureExactCreateDescriptionStorage
                                            graceIds.OrganizationId
                                            graceIds.RepositoryId
                                            workItemId
                                            correlationId
                                            parameters.Description
                                            expectedDescription
                                        with
                                    | Ok () -> return Ok(GraceReturnValue.Create "Work item command succeeded." correlationId)
                                    | Error error -> return Error error
                                }
                            | ConflictingDescriptionCorrelation -> Task.FromResult(Error(conflictingDescriptionCorrelationError correlationId))
                            | FreshDescriptionOperation when
                                existingState.WorkItem.WorkItemId
                                <> WorkItemId.Empty
                                && existingState.WorkItem.RepositoryId
                                   <> graceIds.RepositoryId
                                ->
                                Task.FromResult(Error(GraceError.Create (WorkItemError.getErrorMessage WorkItemError.WorkItemDoesNotExist) correlationId))
                            | FreshDescriptionOperation when
                                existingState.WorkItem.WorkItemId
                                <> WorkItemId.Empty
                                ->
                                Task.FromResult(Error(GraceError.Create (WorkItemError.getErrorMessage WorkItemError.WorkItemAlreadyExists) correlationId))
                            | FreshDescriptionOperation ->
                                task {
                                    let repositoryActorProxy = Repository.CreateActorProxy graceIds.OrganizationId graceIds.RepositoryId correlationId

                                    let! repositoryDto = repositoryActorProxy.Get correlationId

                                    return!
                                        withWorkItemNumberLock graceIds.RepositoryId correlationId (fun () ->
                                            task {
                                                let workItemNumberCounterActorProxy = WorkItemNumberCounter.CreateActorProxy graceIds.RepositoryId correlationId

                                                let! workItemNumber = workItemNumberCounterActorProxy.AllocateNext correlationId
                                                let! stateBeforeWrite = actorProxy.GetState correlationId
                                                let! eventsBeforeWrite = actorProxy.GetEvents correlationId

                                                match
                                                    classifyDescriptionReplay
                                                        CreateDescription
                                                        workItemId
                                                        graceIds.RepositoryId
                                                        expectedDescription
                                                        stateBeforeWrite
                                                        eventsBeforeWrite
                                                        correlationId
                                                    with
                                                | ExactDescriptionReplay ->
                                                    match!
                                                        ensureExactCreateDescriptionStorage
                                                            graceIds.OrganizationId
                                                            graceIds.RepositoryId
                                                            workItemId
                                                            correlationId
                                                            parameters.Description
                                                            expectedDescription
                                                        with
                                                    | Ok () -> return Ok(GraceReturnValue.Create "Work item command succeeded." correlationId)
                                                    | Error error -> return Error error
                                                | ConflictingDescriptionCorrelation -> return Error(conflictingDescriptionCorrelationError correlationId)
                                                | FreshDescriptionOperation when
                                                    stateBeforeWrite.WorkItem.WorkItemId
                                                    <> WorkItemId.Empty
                                                    && stateBeforeWrite.WorkItem.RepositoryId
                                                       <> graceIds.RepositoryId
                                                    ->
                                                    return
                                                        Error(
                                                            GraceError.Create (WorkItemError.getErrorMessage WorkItemError.WorkItemDoesNotExist) correlationId
                                                        )
                                                | FreshDescriptionOperation when
                                                    stateBeforeWrite.WorkItem.WorkItemId
                                                    <> WorkItemId.Empty
                                                    ->
                                                    return
                                                        Error(
                                                            GraceError.Create (WorkItemError.getErrorMessage WorkItemError.WorkItemAlreadyExists) correlationId
                                                        )
                                                | FreshDescriptionOperation ->
                                                    let! writeResult =
                                                        match expectedDescription with
                                                        | None -> Task.FromResult(Ok(None, false))
                                                        | Some _ ->
                                                            task {
                                                                match!
                                                                    TextContentStorage.write
                                                                        repositoryDto
                                                                        graceIds.RepositoryId
                                                                        workItemId
                                                                        correlationId
                                                                        parameters.Description
                                                                    with
                                                                | Ok (storedDescription, wasCreated) -> return Ok(Some storedDescription, wasCreated)
                                                                | Error error -> return Error error
                                                            }

                                                    match writeResult with
                                                    | Error error -> return Error error
                                                    | Ok (storedDescription, wasCreated) ->
                                                        let! stateBeforeAppend = actorProxy.GetState correlationId
                                                        let! eventsBeforeAppend = actorProxy.GetEvents correlationId

                                                        match
                                                            classifyDescriptionReplay
                                                                CreateDescription
                                                                workItemId
                                                                graceIds.RepositoryId
                                                                expectedDescription
                                                                stateBeforeAppend
                                                                eventsBeforeAppend
                                                                correlationId
                                                            with
                                                        | ExactDescriptionReplay ->
                                                            return Ok(GraceReturnValue.Create "Work item command succeeded." correlationId)
                                                        | ConflictingDescriptionCorrelation ->
                                                            match storedDescription with
                                                            | Some description ->
                                                                do! cleanupProvenDescriptionRejection repositoryDto wasCreated description correlationId
                                                            | None -> ()

                                                            return Error(conflictingDescriptionCorrelationError correlationId)
                                                        | FreshDescriptionOperation when
                                                            stateBeforeAppend.WorkItem.WorkItemId
                                                            <> WorkItemId.Empty
                                                            && stateBeforeAppend.WorkItem.RepositoryId
                                                               <> graceIds.RepositoryId
                                                            ->
                                                            match storedDescription with
                                                            | Some description ->
                                                                do! cleanupProvenDescriptionRejection repositoryDto wasCreated description correlationId
                                                            | None -> ()

                                                            return
                                                                Error(
                                                                    GraceError.Create
                                                                        (WorkItemError.getErrorMessage WorkItemError.WorkItemDoesNotExist)
                                                                        correlationId
                                                                )
                                                        | FreshDescriptionOperation when
                                                            stateBeforeAppend.WorkItem.WorkItemId
                                                            <> WorkItemId.Empty
                                                            ->
                                                            match storedDescription with
                                                            | Some description ->
                                                                do! cleanupProvenDescriptionRejection repositoryDto wasCreated description correlationId
                                                            | None -> ()

                                                            return
                                                                Error(
                                                                    GraceError.Create
                                                                        (WorkItemError.getErrorMessage WorkItemError.WorkItemAlreadyExists)
                                                                        correlationId
                                                                )
                                                        | FreshDescriptionOperation ->
                                                            let command =
                                                                WorkItemCommand.Create(
                                                                    workItemId,
                                                                    workItemNumber,
                                                                    Guid.Parse(parameters.OwnerId),
                                                                    Guid.Parse(parameters.OrganizationId),
                                                                    Guid.Parse(parameters.RepositoryId),
                                                                    parameters.Title,
                                                                    storedDescription
                                                                )

                                                            match! actorProxy.Handle command metadata with
                                                            | Ok graceReturnValue ->
                                                                do! cacheWorkItemNumber graceIds.RepositoryId workItemNumber workItemId correlationId

                                                                return Ok graceReturnValue
                                                            | Error graceError ->
                                                                if classifyDescriptionAppendFailure graceError = ProvenPreAppendRejection then
                                                                    match storedDescription with
                                                                    | Some description ->
                                                                        do! cleanupProvenDescriptionRejection repositoryDto wasCreated description correlationId
                                                                    | None -> ()

                                                                return Error graceError
                                            })
                                }

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
                let correlationId = getCorrelationId context
                let! parameters = context |> parse<GetWorkItemParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                match! resolveWorkItemId graceIds.RepositoryId parameters.WorkItemId correlationId with
                | Error error -> return! context |> result400BadRequest error
                | Ok workItemId ->
                    let actorProxy = WorkItem.CreateActorProxy workItemId graceIds.RepositoryId correlationId
                    let repositoryActorProxy = Repository.CreateActorProxy graceIds.OrganizationId graceIds.RepositoryId correlationId

                    let! repositoryDto = repositoryActorProxy.Get correlationId
                    let! stateResult = getRepositoryBoundWorkItemState actorProxy graceIds.RepositoryId correlationId

                    match stateResult with
                    | Error error -> return! context |> result400BadRequest error
                    | Ok state ->

                        let! hydratedDescription =
                            match state.Description with
                            | None -> Task.FromResult(Ok String.Empty)
                            | Some description ->
                                match description.TextContent with
                                | None -> Task.FromResult(Ok String.Empty)
                                | Some reference -> TextContentStorage.read repositoryDto reference correlationId

                        match hydratedDescription with
                        | Error error -> return! context |> result400BadRequest error
                        | Ok description ->
                            let hydrated = { state.WorkItem with Description = description }

                            return!
                                context
                                |> result200Ok (GraceReturnValue.Create hydrated correlationId)
            }

    /// Sets the current work-item description after first writing its immutable text object.
    let SetDescription: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context
                let! parameters = context |> parse<SetWorkItemDescriptionParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let! workItemValidation = validateWorkItemIdentifier parameters.WorkItemId

                match workItemValidation, TextContentStorage.validateText parameters.Text with
                | Error validationError, _ ->
                    return!
                        context
                        |> result400BadRequest (GraceError.Create (WorkItemError.getErrorMessage validationError) correlationId)
                | _, Error validationError ->
                    return!
                        context
                        |> result400BadRequest (GraceError.Create validationError correlationId)
                | Ok (), Ok () ->
                    match! resolveWorkItemId graceIds.RepositoryId parameters.WorkItemId correlationId with
                    | Error error -> return! context |> result400BadRequest error
                    | Ok workItemId ->
                        let expectedDescription = TextContentStorage.createDescription graceIds.RepositoryId workItemId correlationId parameters.Text

                        let actorProxy = WorkItem.CreateActorProxy workItemId graceIds.RepositoryId correlationId
                        let! initialStateResult = getRepositoryBoundWorkItemState actorProxy graceIds.RepositoryId correlationId

                        match initialStateResult with
                        | Error error -> return! context |> result400BadRequest error
                        | Ok initialState ->
                            let! initialEvents = actorProxy.GetEvents correlationId

                            match
                                classifyDescriptionReplay
                                    SetDescription
                                    workItemId
                                    graceIds.RepositoryId
                                    (Some expectedDescription)
                                    initialState
                                    initialEvents
                                    correlationId
                                with
                            | ExactDescriptionReplay ->
                                let repositoryActorProxy = Repository.CreateActorProxy graceIds.OrganizationId graceIds.RepositoryId correlationId
                                let! repositoryDto = repositoryActorProxy.Get correlationId

                                match!
                                    ensureExactDescriptionStorage
                                        repositoryDto
                                        graceIds.RepositoryId
                                        workItemId
                                        correlationId
                                        parameters.Text
                                        expectedDescription
                                    with
                                | Ok () ->
                                    return!
                                        context
                                        |> result200Ok (GraceReturnValue.Create "Work item description set." correlationId)
                                | Error error -> return! context |> result400BadRequest error
                            | ConflictingDescriptionCorrelation ->
                                return!
                                    context
                                    |> result400BadRequest (conflictingDescriptionCorrelationError correlationId)
                            | FreshDescriptionOperation ->
                                let repositoryActorProxy = Repository.CreateActorProxy graceIds.OrganizationId graceIds.RepositoryId correlationId

                                let! repositoryDto = repositoryActorProxy.Get correlationId
                                let! stateBeforeWriteResult = getRepositoryBoundWorkItemState actorProxy graceIds.RepositoryId correlationId

                                match stateBeforeWriteResult with
                                | Error error -> return! context |> result400BadRequest error
                                | Ok stateBeforeWrite ->
                                    let! eventsBeforeWrite = actorProxy.GetEvents correlationId

                                    match
                                        classifyDescriptionReplay
                                            SetDescription
                                            workItemId
                                            graceIds.RepositoryId
                                            (Some expectedDescription)
                                            stateBeforeWrite
                                            eventsBeforeWrite
                                            correlationId
                                        with
                                    | ExactDescriptionReplay ->
                                        match!
                                            ensureExactDescriptionStorage
                                                repositoryDto
                                                graceIds.RepositoryId
                                                workItemId
                                                correlationId
                                                parameters.Text
                                                expectedDescription
                                            with
                                        | Ok () ->
                                            return!
                                                context
                                                |> result200Ok (GraceReturnValue.Create "Work item description set." correlationId)
                                        | Error error -> return! context |> result400BadRequest error
                                    | ConflictingDescriptionCorrelation ->
                                        return!
                                            context
                                            |> result400BadRequest (conflictingDescriptionCorrelationError correlationId)
                                    | FreshDescriptionOperation ->
                                        let! writeResult = TextContentStorage.write repositoryDto graceIds.RepositoryId workItemId correlationId parameters.Text

                                        match writeResult with
                                        | Error error -> return! context |> result400BadRequest error
                                        | Ok (description, wasCreated) ->
                                            let! testGate = tryEnterDescriptionClearPreAppendTestGate context
                                            disposeDescriptionClearPreAppendTestGate testGate
                                            let! stateBeforeAppendResult = getRepositoryBoundWorkItemState actorProxy graceIds.RepositoryId correlationId

                                            match stateBeforeAppendResult with
                                            | Error error -> return! context |> result400BadRequest error
                                            | Ok stateBeforeAppend ->
                                                let! eventsBeforeAppend = actorProxy.GetEvents correlationId

                                                match
                                                    classifyDescriptionReplay
                                                        SetDescription
                                                        workItemId
                                                        graceIds.RepositoryId
                                                        (Some expectedDescription)
                                                        stateBeforeAppend
                                                        eventsBeforeAppend
                                                        correlationId
                                                    with
                                                | ExactDescriptionReplay ->
                                                    return!
                                                        context
                                                        |> result200Ok (GraceReturnValue.Create "Work item description set." correlationId)
                                                | ConflictingDescriptionCorrelation ->
                                                    return!
                                                        context
                                                        |> result400BadRequest (conflictingDescriptionCorrelationError correlationId)
                                                | FreshDescriptionOperation ->
                                                    let metadata = createMetadata context

                                                    match! actorProxy.Handle (WorkItemCommand.SetDescription description) metadata with
                                                    | Ok _ ->
                                                        return!
                                                            context
                                                            |> result200Ok (GraceReturnValue.Create "Work item description set." correlationId)
                                                    | Error error ->
                                                        let! stateAfterAppendResult =
                                                            getRepositoryBoundWorkItemState actorProxy graceIds.RepositoryId correlationId

                                                        match stateAfterAppendResult with
                                                        | Error _ -> return! context |> result400BadRequest error
                                                        | Ok stateAfterAppend ->
                                                            let! eventsAfterAppend = actorProxy.GetEvents correlationId

                                                            match
                                                                classifyDescriptionReplay
                                                                    SetDescription
                                                                    workItemId
                                                                    graceIds.RepositoryId
                                                                    (Some expectedDescription)
                                                                    stateAfterAppend
                                                                    eventsAfterAppend
                                                                    correlationId
                                                                with
                                                            | ExactDescriptionReplay ->
                                                                match!
                                                                    ensureExactDescriptionStorage
                                                                        repositoryDto
                                                                        graceIds.RepositoryId
                                                                        workItemId
                                                                        correlationId
                                                                        parameters.Text
                                                                        expectedDescription
                                                                    with
                                                                | Ok () ->
                                                                    return!
                                                                        context
                                                                        |> result200Ok (GraceReturnValue.Create "Work item description set." correlationId)
                                                                | Error storageError -> return! context |> result400BadRequest storageError
                                                            | ConflictingDescriptionCorrelation ->
                                                                return!
                                                                    context
                                                                    |> result400BadRequest (conflictingDescriptionCorrelationError correlationId)
                                                            | FreshDescriptionOperation -> return! context |> result400BadRequest error
            }

    /// Appends an immutable empty description without reading, writing, or deleting text-content objects.
    let ClearDescription: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                let! parameters =
                    context
                    |> parse<ClearWorkItemDescriptionParameters>

                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                match! validateWorkItemIdentifier parameters.WorkItemId with
                | Error validationError ->
                    return!
                        context
                        |> result400BadRequest (GraceError.Create (WorkItemError.getErrorMessage validationError) correlationId)
                | Ok () ->
                    match! resolveWorkItemId graceIds.RepositoryId parameters.WorkItemId correlationId with
                    | Error error -> return! context |> result400BadRequest error
                    | Ok workItemId ->
                        let descriptionId, _ = TextContentStorage.createIds graceIds.RepositoryId workItemId correlationId
                        let expectedDescription = { DescriptionId = descriptionId; TextContent = None }
                        let actorProxy = WorkItem.CreateActorProxy workItemId graceIds.RepositoryId correlationId
                        let! initialStateResult = getRepositoryBoundWorkItemState actorProxy graceIds.RepositoryId correlationId

                        match initialStateResult with
                        | Error error -> return! context |> result400BadRequest error
                        | Ok initialState ->
                            let! initialEvents = actorProxy.GetEvents correlationId

                            match
                                classifyDescriptionReplay
                                    ClearDescription
                                    workItemId
                                    graceIds.RepositoryId
                                    (Some expectedDescription)
                                    initialState
                                    initialEvents
                                    correlationId
                                with
                            | ExactDescriptionReplay ->
                                return!
                                    context
                                    |> result200Ok (GraceReturnValue.Create "Work item description cleared." correlationId)
                            | ConflictingDescriptionCorrelation ->
                                return!
                                    context
                                    |> result400BadRequest (conflictingDescriptionCorrelationError correlationId)
                            | FreshDescriptionOperation ->
                                let! stateBeforeAppendResult = getRepositoryBoundWorkItemState actorProxy graceIds.RepositoryId correlationId

                                match stateBeforeAppendResult with
                                | Error error -> return! context |> result400BadRequest error
                                | Ok stateBeforeAppend ->
                                    let! eventsBeforeAppend = actorProxy.GetEvents correlationId

                                    match
                                        classifyDescriptionReplay
                                            ClearDescription
                                            workItemId
                                            graceIds.RepositoryId
                                            (Some expectedDescription)
                                            stateBeforeAppend
                                            eventsBeforeAppend
                                            correlationId
                                        with
                                    | ExactDescriptionReplay ->
                                        return!
                                            context
                                            |> result200Ok (GraceReturnValue.Create "Work item description cleared." correlationId)
                                    | ConflictingDescriptionCorrelation ->
                                        return!
                                            context
                                            |> result400BadRequest (conflictingDescriptionCorrelationError correlationId)
                                    | FreshDescriptionOperation ->
                                        let metadata = createMetadata context
                                        let! testGate = tryEnterDescriptionClearPreAppendTestGate context

                                        try
                                            match! actorProxy.Handle (WorkItemCommand.ClearDescription expectedDescription) metadata with
                                            | Ok _ ->
                                                do! observeDescriptionClearAppendSucceeded testGate

                                                return!
                                                    context
                                                    |> result200Ok (GraceReturnValue.Create "Work item description cleared." correlationId)
                                            | Error error when isDuplicateCorrelationIdError error ->
                                                do! observeDescriptionClearDuplicateResultReclassification testGate
                                                let! stateAfterDuplicateResult = getRepositoryBoundWorkItemState actorProxy graceIds.RepositoryId correlationId

                                                match stateAfterDuplicateResult with
                                                | Error repositoryError -> return! context |> result400BadRequest repositoryError
                                                | Ok stateAfterDuplicate ->
                                                    let! eventsAfterDuplicate = actorProxy.GetEvents correlationId

                                                    match
                                                        classifyDescriptionReplay
                                                            ClearDescription
                                                            workItemId
                                                            graceIds.RepositoryId
                                                            (Some expectedDescription)
                                                            stateAfterDuplicate
                                                            eventsAfterDuplicate
                                                            correlationId
                                                        with
                                                    | ExactDescriptionReplay ->
                                                        return!
                                                            context
                                                            |> result200Ok (GraceReturnValue.Create "Work item description cleared." correlationId)
                                                    | ConflictingDescriptionCorrelation ->
                                                        return!
                                                            context
                                                            |> result400BadRequest (conflictingDescriptionCorrelationError correlationId)
                                                    | FreshDescriptionOperation -> return! context |> result400BadRequest error
                                            | Error error -> return! context |> result400BadRequest error
                                        finally
                                            disposeDescriptionClearPreAppendTestGate testGate
            }

    /// Implements fetch linked reviewer attachments for the server request pipeline.
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
                | Some metadata when
                    metadata.IsDeleted
                    || metadata.WorkItemId <> Some workItemDto.WorkItemId
                    ->
                    ()
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

    /// Implements download artifact content bytes for the server request pipeline.
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

    /// Attempts to read attachment content and returns an option or result instead of throwing.
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

    /// Implements complete show attachment request for the server request pipeline.
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

                /// Adds context to the server request model.
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

                /// Adds context to the server request model.
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

                /// Adds context to the server request model.
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
                                    GraceError.Create "Attachment is unavailable for this work item." correlationId
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
                                        GraceError.Create "Attachment is unavailable for this work item." correlationId
                                        |> withContext
                                    )
                            | Some metadata when
                                metadata.OwnerId <> ownerId
                                || metadata.OrganizationId <> organizationId
                                || metadata.RepositoryId <> repositoryId
                                || metadata.WorkItemId <> Some workItemId
                                || metadata.IsDeleted
                                ->
                                return!
                                    context
                                    |> result400BadRequest (
                                        GraceError.Create "Attachment is unavailable for this work item." correlationId
                                        |> withContext
                                    )
                            | Some metadata ->
                                match tryGetReviewerAttachmentTypeName metadata.ArtifactType with
                                | None ->
                                    return!
                                        context
                                        |> result400BadRequest (
                                            GraceError.Create "Attachment is unavailable for this work item." correlationId
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

                /// Implements validations for the server request pipeline.
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
                /// Implements validations for the server request pipeline.
                let validations (parameters: LinkReferenceParameters) = validateLinkReferenceParameters parameters

                /// Implements command for the server request pipeline.
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
                /// Implements validations for the server request pipeline.
                let validations (parameters: RemoveReferenceLinkParameters) =
                    [|
                        validateWorkItemIdentifier parameters.WorkItemId
                        Guid.isValidAndNotEmptyGuid parameters.ReferenceId WorkItemError.InvalidReferenceId
                    |]

                /// Implements command for the server request pipeline.
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
                /// Implements validations for the server request pipeline.
                let validations (parameters: LinkPromotionSetParameters) = validateLinkPromotionSetParameters parameters

                /// Implements command for the server request pipeline.
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
                /// Implements validations for the server request pipeline.
                let validations (parameters: RemovePromotionSetLinkParameters) =
                    [|
                        validateWorkItemIdentifier parameters.WorkItemId
                        Guid.isValidAndNotEmptyGuid parameters.PromotionSetId WorkItemError.InvalidPromotionSetId
                    |]

                /// Implements command for the server request pipeline.
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
                /// Implements validations for the server request pipeline.
                let validations (parameters: LinkArtifactParameters) =
                    let graceIds = getGraceIds context
                    let correlationId = getCorrelationId context

                    Array.append
                        (validateLinkArtifactParameters parameters)
                        [|
                            ValueTask<Result<unit, WorkItemError>>(
                                task {
                                    match! resolveWorkItemId graceIds.RepositoryId parameters.WorkItemId correlationId with
                                    | Error _ -> return Error WorkItemError.InvalidArtifactId
                                    | Ok workItemId ->
                                        let artifactId = Guid.Parse parameters.ArtifactId
                                        let artifactActorProxy = Artifact.CreateActorProxy artifactId graceIds.RepositoryId correlationId

                                        match! artifactActorProxy.Get correlationId with
                                        | Some artifact when
                                            artifact.RepositoryId = graceIds.RepositoryId
                                            && (artifact.WorkItemId.IsNone
                                                || artifact.WorkItemId = Some workItemId)
                                            ->
                                            return Ok()
                                        | _ -> return Error WorkItemError.InvalidArtifactId
                                }
                            )
                        |]

                /// Implements command for the server request pipeline.
                let command (parameters: LinkArtifactParameters) =
                    WorkItemCommand.LinkArtifact(Guid.Parse(parameters.ArtifactId))
                    |> returnValueTask

                context.Items[ "Command" ] <- nameof LinkArtifact
                return! processCommand context validations command
            }

    /// Logically deletes one exact owned attachment while retaining bytes and the work-item link for recovery.
    let DeleteAttachment: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                let! parameters =
                    context
                    |> parse<DeleteWorkItemAttachmentParameters>

                if String.IsNullOrWhiteSpace parameters.DeleteReason then
                    return!
                        context
                        |> result400BadRequest (GraceError.Create "DeleteReason is required." correlationId)
                else
                    match tryParseNonEmptyGuid parameters.ArtifactId with
                    | None ->
                        return!
                            context
                            |> result400BadRequest (GraceError.Create "Attachment is unavailable for this work item." correlationId)
                    | Some artifactId ->
                        match! resolveWorkItemId graceIds.RepositoryId parameters.WorkItemId correlationId with
                        | Error _ ->
                            return!
                                context
                                |> result400BadRequest (GraceError.Create "Attachment is unavailable for this work item." correlationId)
                        | Ok workItemId ->
                            let workItemActorProxy = WorkItem.CreateActorProxy workItemId graceIds.RepositoryId correlationId
                            let artifactActorProxy = Artifact.CreateActorProxy artifactId graceIds.RepositoryId correlationId
                            let! workItemDto = workItemActorProxy.Get correlationId
                            let! artifactMetadata = artifactActorProxy.Get correlationId

                            match artifactMetadata with
                            | Some artifact when
                                artifact.OwnerId = graceIds.OwnerId
                                && artifact.OrganizationId = graceIds.OrganizationId
                                && artifact.RepositoryId = graceIds.RepositoryId
                                && artifact.WorkItemId = Some workItemId
                                && (workItemDto.ArtifactIds
                                    |> List.contains artifactId)
                                && tryGetReviewerAttachmentTypeName artifact.ArtifactType
                                   |> Option.isSome
                                ->
                                let repositoryActorProxy = Repository.CreateActorProxy graceIds.OrganizationId graceIds.RepositoryId correlationId
                                let! repositoryDto = repositoryActorProxy.Get correlationId
                                let deletedAt = getCurrentInstant ()

                                let physicalDeletionAt =
                                    deletedAt
                                    + Duration.FromDays(float repositoryDto.LogicalDeleteDays)

                                let deletionGeneration = Guid.NewGuid()

                                let command =
                                    ArtifactCommand.DeleteLogical(workItemId, parameters.DeleteReason.Trim(), deletionGeneration, deletedAt, physicalDeletionAt)

                                match! artifactActorProxy.Handle command (createMetadata context) with
                                | Error graceError -> return! context |> result400BadRequest graceError
                                | Ok _ ->
                                    let! current = artifactActorProxy.Get correlationId

                                    match current with
                                    | Some deletedArtifact when
                                        deletedArtifact.DeletedAt.IsSome
                                        && deletedArtifact.PhysicalDeletionAt.IsSome
                                        && deletedArtifact.WorkItemId.IsSome
                                        ->
                                        let response: ArtifactDeletionResult =
                                            {
                                                ArtifactId = deletedArtifact.ArtifactId
                                                WorkItemId = deletedArtifact.WorkItemId.Value
                                                DeletionGeneration = deletedArtifact.DeletionGeneration
                                                DeletedAt = deletedArtifact.DeletedAt.Value
                                                PhysicalDeletionAt = deletedArtifact.PhysicalDeletionAt.Value
                                                DeleteReason = deletedArtifact.DeleteReason
                                            }

                                        return!
                                            context
                                            |> result200Ok (GraceReturnValue.Create response correlationId)
                                    | _ ->
                                        return!
                                            context
                                            |> result500ServerError (GraceError.Create "Attachment deletion state was not persisted." correlationId)
                            | _ ->
                                return!
                                    context
                                    |> result400BadRequest (GraceError.Create "Attachment is unavailable for this work item." correlationId)
            }

    /// Recovers one exact logically deleted attachment before its stored retention deadline.
    let UndeleteAttachment: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                let! parameters =
                    context
                    |> parse<UndeleteWorkItemAttachmentParameters>

                match tryParseNonEmptyGuid parameters.ArtifactId with
                | None ->
                    return!
                        context
                        |> result400BadRequest (GraceError.Create "Attachment is unavailable for this work item." correlationId)
                | Some artifactId ->
                    match! resolveWorkItemId graceIds.RepositoryId parameters.WorkItemId correlationId with
                    | Error _ ->
                        return!
                            context
                            |> result400BadRequest (GraceError.Create "Attachment is unavailable for this work item." correlationId)
                    | Ok workItemId ->
                        let workItemActorProxy = WorkItem.CreateActorProxy workItemId graceIds.RepositoryId correlationId
                        let artifactActorProxy = Artifact.CreateActorProxy artifactId graceIds.RepositoryId correlationId
                        let! workItemDto = workItemActorProxy.Get correlationId
                        let! artifactMetadata = artifactActorProxy.Get correlationId

                        match artifactMetadata with
                        | Some artifact when
                            artifact.OwnerId = graceIds.OwnerId
                            && artifact.OrganizationId = graceIds.OrganizationId
                            && artifact.RepositoryId = graceIds.RepositoryId
                            && artifact.WorkItemId = Some workItemId
                            && artifact.IsDeleted
                            && (workItemDto.ArtifactIds
                                |> List.contains artifactId)
                            && tryGetReviewerAttachmentTypeName artifact.ArtifactType
                               |> Option.isSome
                            ->
                            match! artifactActorProxy.Handle (ArtifactCommand.Undelete workItemId) (createMetadata context) with
                            | Error graceError -> return! context |> result400BadRequest graceError
                            | Ok graceReturnValue -> return! context |> result200Ok graceReturnValue
                        | _ ->
                            return!
                                context
                                |> result400BadRequest (GraceError.Create "Attachment is unavailable for this work item." correlationId)
            }

    /// Removes an artifact link from a work item.
    let RemoveArtifactLink: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context
                context.Items[ "Command" ] <- "RemoveArtifactLink"

                let! parameters = context |> parse<RemoveArtifactLinkParameters>

                let validations =
                    [|
                        validateWorkItemIdentifier parameters.WorkItemId
                        Guid.isValidAndNotEmptyGuid parameters.ArtifactId WorkItemError.InvalidArtifactId
                    |]

                let! validationsPassed = validations |> allPass

                if not validationsPassed then
                    let! error = validations |> getFirstError

                    return!
                        context
                        |> result400BadRequest (GraceError.Create (WorkItemError.getErrorMessage error) correlationId)
                else
                    match! resolveWorkItemId graceIds.RepositoryId parameters.WorkItemId correlationId with
                    | Error graceError -> return! context |> result400BadRequest graceError
                    | Ok workItemId ->
                        let artifactId = Guid.Parse parameters.ArtifactId
                        let artifactActorProxy = Artifact.CreateActorProxy artifactId graceIds.RepositoryId correlationId

                        match! artifactActorProxy.UnlinkFromWorkItem workItemId graceIds.RepositoryId (createMetadata context) with
                        | Ok graceReturnValue -> return! context |> result200Ok graceReturnValue
                        | Error graceError -> return! context |> result400BadRequest graceError
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
                            let mutable ownedAttachmentFound = false
                            let mutable i = 0

                            while i < artifactIds.Length do
                                let artifactId = artifactIds[i]
                                let artifactActorProxy = Artifact.CreateActorProxy artifactId graceIds.RepositoryId correlationId
                                let! artifactMetadata = artifactActorProxy.Get correlationId

                                match artifactMetadata with
                                | Some metadata when
                                    metadata.ArtifactType = artifactType
                                    && Grace.Actors.Artifact.isOwnedReviewerAttachment metadata
                                    ->
                                    ownedAttachmentFound <- true
                                | Some metadata when metadata.ArtifactType = artifactType -> removableArtifactIds.Add(artifactId)
                                | _ -> ()

                                i <- i + 1

                            if ownedAttachmentFound then
                                return!
                                    context
                                    |> result400BadRequest (
                                        GraceError.Create "Owned reviewer attachments must be deleted through the attachment lifecycle." correlationId
                                    )
                            else
                                let metadata = createMetadata context
                                let removableArtifactIdsArray = removableArtifactIds |> Seq.toArray
                                let mutable removedCount = 0
                                let mutable removeError: GraceError option = None
                                let mutable j = 0

                                while j < removableArtifactIdsArray.Length
                                      && removeError.IsNone do
                                    let artifactId = removableArtifactIdsArray[j]
                                    let artifactActorProxy = Artifact.CreateActorProxy artifactId graceIds.RepositoryId correlationId

                                    let unlinkMetadata = { metadata with CorrelationId = $"{metadata.CorrelationId}:generic-artifact-unlink:{artifactId:N}" }

                                    let! removeResult = artifactActorProxy.UnlinkFromWorkItem workItemId graceIds.RepositoryId unlinkMetadata

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
