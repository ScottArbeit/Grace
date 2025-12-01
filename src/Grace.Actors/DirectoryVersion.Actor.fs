namespace Grace.Actors

open Azure.Storage.Blobs
open Azure.Storage.Blobs.Models
open Azure.Storage.Blobs.Specialized
open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Services
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.Events
open Grace.Types.Reminder
open Grace.Types.Repository
open Grace.Types.DirectoryVersion
open Grace.Types.Types
open Microsoft.Extensions.Logging
open Microsoft.Extensions.ObjectPool
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Buffers
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Linq
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open System.Reflection.Metadata
open MessagePack
open System.Threading

module DirectoryVersion =

    /// Result of validating a single file's SHA-256 hash.
    type FileValidationResult =
        | Valid of fileVersion: FileVersion * computedHash: Sha256Hash * elapsedMs: float
        | HashMismatch of fileVersion: FileVersion * expectedHash: Sha256Hash * computedHash: Sha256Hash * elapsedMs: float
        | MissingInStorage of fileVersion: FileVersion * elapsedMs: float
        | ValidationError of fileVersion: FileVersion * errorMessage: string * elapsedMs: float

    /// Validates a single file's SHA-256 hash by downloading from storage and computing.
    /// Note: Non-binary files are stored as GZip-compressed streams, so we need to decompress them first.
    let validateFileSha256 (repositoryDto: RepositoryDto) (fileVersion: FileVersion) (correlationId: CorrelationId) =
        task {
            let stopwatch = Stopwatch.StartNew()

            try
                let! blobClient = getAzureBlobClientForFileVersion repositoryDto fileVersion correlationId
                let! existsResponse = blobClient.ExistsAsync()

                if not existsResponse.Value then
                    stopwatch.Stop()
                    return MissingInStorage(fileVersion, stopwatch.Elapsed.TotalMilliseconds)
                else
                    // Download the stream from blob storage
                    use! blobStream = blobClient.OpenReadAsync(position = 0, bufferSize = (64 * 1024))

                    // Compute SHA-256 hash using the server-specific validation function.
                    // Text files are stored as GZip streams and need decompression.
                    let! computedHash =
                        if fileVersion.IsBinary then
                            computeSha256ForFile blobStream fileVersion.RelativePath
                        else
                            task {
                                use gzStream = new GZipStream(stream = blobStream, mode = CompressionMode.Decompress, leaveOpen = false)
                                return! computeSha256ForFile gzStream fileVersion.RelativePath
                            }

                    stopwatch.Stop()

                    if computedHash = fileVersion.Sha256Hash then
                        return Valid(fileVersion, computedHash, stopwatch.Elapsed.TotalMilliseconds)
                    else
                        return HashMismatch(fileVersion, fileVersion.Sha256Hash, computedHash, stopwatch.Elapsed.TotalMilliseconds)
            with ex ->
                stopwatch.Stop()
                return ValidationError(fileVersion, ex.Message, stopwatch.Elapsed.TotalMilliseconds)
        }

    /// Determines which files need validation by comparing with a previously validated DirectoryVersion.
    /// Returns the list of files that need to be validated.
    let getFilesToValidate (newFiles: List<FileVersion>) (previouslyValidatedFiles: List<FileVersion>) : FileVersion array =
        if previouslyValidatedFiles.Count > 0 then
            // Create a set of (RelativePath, Sha256Hash) pairs from the old files
            let previousFilesLookup = Dictionary<RelativePath, Sha256Hash>()
            previouslyValidatedFiles |> Seq.iter (fun previousFile -> previousFilesLookup.Add(previousFile.RelativePath, previousFile.Sha256Hash))

            // Return files that are not in the old set (new or changed)
            newFiles.Where(fun f -> not (previousFilesLookup.Contains(KeyValuePair(f.RelativePath, f.Sha256Hash)))).ToArray()
        else
            newFiles.ToArray()

    type DirectoryVersionActor
        ([<PersistentState(StateName.DirectoryVersion, Constants.GraceActorStorage)>] state: IPersistentState<List<DirectoryVersionEvent>>) =
        inherit Grain()

        static let actorName = ActorName.DirectoryVersion

        let log = loggerFactory.CreateLogger("DirectoryVersion.Actor")

        let mutable directoryVersionDto = DirectoryVersionDto.Default
        let mutable currentCommand = String.Empty

        let recursiveDirectoryVersionsCacheFileName directoryVersionId = $"{directoryVersionId}.msgpack"

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            // Apply the events to build the current Dto.
            directoryVersionDto <-
                state.State
                |> Seq.fold
                    (fun directoryVersionDto directoryVersionEvent -> directoryVersionDto |> DirectoryVersionDto.UpdateDto directoryVersionEvent)
                    DirectoryVersionDto.Default

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            Task.CompletedTask

        interface IGraceReminderWithGuidKey with
            /// Schedules a Grace reminder.
            member this.ScheduleReminderAsync reminderType delay state correlationId =
                task {
                    let reminder =
                        ReminderDto.Create
                            actorName
                            $"{this.IdentityString}"
                            directoryVersionDto.DirectoryVersion.OwnerId
                            directoryVersionDto.DirectoryVersion.OrganizationId
                            directoryVersionDto.DirectoryVersion.RepositoryId
                            reminderType
                            (getFutureInstant delay)
                            state
                            correlationId

                    do! createReminder reminder
                }
                :> Task

            /// Receives a Grace reminder.
            member this.ReceiveReminderAsync(reminder: ReminderDto) : Task<Result<unit, GraceError>> =
                task {
                    this.correlationId <- reminder.CorrelationId

                    match reminder.ReminderType, reminder.State with
                    | ReminderTypes.DeleteCachedState, ReminderState.DirectoryVersionDeleteCachedState reminderState ->
                        this.correlationId <- reminderState.CorrelationId

                        // Delete cached state for this actor.
                        let directoryVersionBlobClient =
                            directoryVersionContainerClient.GetBlobClient(
                                recursiveDirectoryVersionsCacheFileName directoryVersionDto.DirectoryVersion.DirectoryVersionId
                            )

                        let! deleted = directoryVersionBlobClient.DeleteIfExistsAsync()

                        if deleted.HasValue && deleted.Value then
                            log.LogInformation(
                                "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Deleted cached state for directory version; RepositoryId: {RepositoryId}; DirectoryVersionId: {DirectoryVersionId}; deleteReason: {deleteReason}.",
                                getCurrentInstantExtended (),
                                getMachineName,
                                reminderState.CorrelationId,
                                directoryVersionDto.DirectoryVersion.RepositoryId,
                                directoryVersionDto.DirectoryVersion.DirectoryVersionId,
                                reminderState.DeleteReason
                            )
                        else
                            log.LogWarning(
                                "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Failed to delete cached state for directory version (it may have already been deleted); RepositoryId: {RepositoryId}; DirectoryVersionId: {DirectoryVersionId}; deleteReason: {deleteReason}.",
                                getCurrentInstantExtended (),
                                getMachineName,
                                reminderState.CorrelationId,
                                directoryVersionDto.DirectoryVersion.RepositoryId,
                                directoryVersionDto.DirectoryVersion.DirectoryVersionId,
                                reminderState.DeleteReason
                            )

                        return Ok()
                    | ReminderTypes.DeleteZipFile, ReminderState.DirectoryVersionDeleteZipFile reminderState ->
                        this.correlationId <- reminderState.CorrelationId

                        let directoryVersion = directoryVersionDto.DirectoryVersion

                        let repositoryActorProxy =
                            Repository.CreateActorProxy directoryVersion.OrganizationId directoryVersion.RepositoryId reminderState.CorrelationId

                        let! repositoryDto = repositoryActorProxy.Get reminderState.CorrelationId

                        // Delete cached directory version contents for this actor.
                        let zipFileBlobClient = zipFileContainerClient.GetBlobClient $"{directoryVersion.DirectoryVersionId}.zip"

                        let! deleted = zipFileBlobClient.DeleteIfExistsAsync()

                        if deleted.HasValue && deleted.Value then
                            log.LogInformation(
                                "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Deleted cache for directory version; RepositoryId: {RepositoryId}; DirectoryVersionId: {DirectoryVersionId}; deleteReason: {deleteReason}.",
                                getCurrentInstantExtended (),
                                getMachineName,
                                reminderState.CorrelationId,
                                directoryVersionDto.DirectoryVersion.RepositoryId,
                                directoryVersionDto.DirectoryVersion.DirectoryVersionId,
                                reminderState.DeleteReason
                            )
                        else
                            log.LogWarning(
                                "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Failed to delete cache for directory version (it may have already been deleted); RepositoryId: {RepositoryId}; DirectoryVersionId: {DirectoryVersionId}; deleteReason: {deleteReason}.",
                                getCurrentInstantExtended (),
                                getMachineName,
                                reminderState.CorrelationId,
                                directoryVersionDto.DirectoryVersion.RepositoryId,
                                directoryVersionDto.DirectoryVersion.DirectoryVersionId,
                                reminderState.DeleteReason
                            )

                        return Ok()
                    | ReminderTypes.PhysicalDeletion, ReminderState.DirectoryVersionPhysicalDeletion reminderState ->
                        this.correlationId <- reminderState.CorrelationId

                        // Delete saved state for this actor.
                        do! state.ClearStateAsync()

                        log.LogInformation(
                            "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Deleted state for directory version; RepositoryId: {RepositoryId}; DirectoryVersionId: {DirectoryVersionId}; deleteReason: {deleteReason}.",
                            getCurrentInstantExtended (),
                            getMachineName,
                            reminderState.CorrelationId,
                            directoryVersionDto.DirectoryVersion.RepositoryId,
                            directoryVersionDto.DirectoryVersion.DirectoryVersionId,
                            reminderState.DeleteReason
                        )

                        this.DeactivateOnIdle()
                        return Ok()
                    | reminderType, state ->
                        return
                            Error(
                                (GraceError.Create
                                    $"{actorName} does not process reminder type {getDiscriminatedUnionCaseName reminderType} with state {getDiscriminatedUnionCaseName state}."
                                    this.correlationId)
                                    .enhance ("IsRetryable", "false")
                            )
                }

        member private this.ApplyEvent directoryVersionEvent =
            task {
                try
                    // Add the event to the list of events, and save it to actor state.
                    state.State.Add(directoryVersionEvent)
                    do! state.WriteStateAsync()

                    // Update the Dto with the event.
                    directoryVersionDto <- directoryVersionDto |> DirectoryVersionDto.UpdateDto directoryVersionEvent

                    //logToConsole
                    //    $"In ApplyEvent(): directoryVersion.DirectoryVersionId: {directoryVersionDto.DirectoryVersion.DirectoryVersionId}; directoryVersion.RelativePath: {directoryVersionDto.DirectoryVersion.RelativePath}."

                    // Publish the event to the rest of the world.
                    let graceEvent = GraceEvent.DirectoryVersionEvent directoryVersionEvent

                    let streamProvider = this.GetStreamProvider GraceEventStreamProvider

                    let stream =
                        streamProvider.GetStream<GraceEvent>(
                            StreamId.Create(Constants.GraceEventStreamTopic, directoryVersionDto.DirectoryVersion.DirectoryVersionId)
                        )

                    do! stream.OnNextAsync(graceEvent)

                    let returnValue = GraceReturnValue.Create "Directory version command succeeded." directoryVersionEvent.Metadata.CorrelationId

                    returnValue
                        .enhance(nameof RepositoryId, directoryVersionDto.DirectoryVersion.RepositoryId)
                        .enhance(nameof DirectoryVersionId, directoryVersionDto.DirectoryVersion.DirectoryVersionId)
                        .enhance(nameof Sha256Hash, directoryVersionDto.DirectoryVersion.Sha256Hash)
                        .enhance (nameof DirectoryVersionEventType, getDiscriminatedUnionFullName directoryVersionEvent.Event)
                    |> ignore

                    return Ok returnValue
                with ex ->
                    let graceError =
                        GraceError.CreateWithException
                            ex
                            (getErrorMessage DirectoryVersionError.FailedWhileApplyingEvent)
                            directoryVersionEvent.Metadata.CorrelationId

                    graceError
                        .enhance(nameof RepositoryId, directoryVersionDto.DirectoryVersion.RepositoryId)
                        .enhance(nameof DirectoryVersionId, directoryVersionDto.DirectoryVersion.DirectoryVersionId)
                        .enhance(nameof Sha256Hash, directoryVersionDto.DirectoryVersion.Sha256Hash)
                        .enhance (nameof DirectoryVersionEventType, getDiscriminatedUnionFullName directoryVersionEvent.Event)
                    |> ignore

                    return Error graceError
            }

        interface IHasRepositoryId with
            member this.GetRepositoryId correlationId = directoryVersionDto.DirectoryVersion.RepositoryId |> returnTask

        interface IDirectoryVersionActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId

                (directoryVersionDto.DirectoryVersion.DirectoryVersionId
                 <> DirectoryVersion.Default.DirectoryVersionId)
                |> returnTask

            member this.Delete correlationId =
                this.correlationId <- correlationId

                GraceResult.Error(GraceError.Create "Not implemented" correlationId)
                |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId
                directoryVersionDto |> returnTask

            member this.GetCreatedAt correlationId =
                this.correlationId <- correlationId
                directoryVersionDto.DirectoryVersion.CreatedAt |> returnTask

            member this.GetDirectories correlationId =
                this.correlationId <- correlationId
                directoryVersionDto.DirectoryVersion.Directories |> returnTask

            member this.GetFiles correlationId =
                this.correlationId <- correlationId
                directoryVersionDto.DirectoryVersion.Files |> returnTask

            member this.GetSha256Hash correlationId =
                this.correlationId <- correlationId
                directoryVersionDto.DirectoryVersion.Sha256Hash |> returnTask

            member this.GetSize correlationId =
                this.correlationId <- correlationId
                directoryVersionDto.DirectoryVersion.Size |> returnTask

            member this.GetRecursiveDirectoryVersions (forceRegenerate: bool) correlationId =
                this.correlationId <- correlationId

                task {
                    try
                        let directoryVersionBlobClient =
                            directoryVersionContainerClient.GetBlockBlobClient(
                                recursiveDirectoryVersionsCacheFileName directoryVersionDto.DirectoryVersion.DirectoryVersionId
                            )

                        // Check if the subdirectory versions have already been generated and cached.
                        let cachedSubdirectoryVersions =
                            task {
                                if not forceRegenerate && directoryVersionBlobClient.Exists() then
                                    use! blobStream = directoryVersionBlobClient.OpenReadAsync()

                                    let! directoryVersions =
                                        MessagePackSerializer.DeserializeAsync<DirectoryVersionDto array>(blobStream, messagePackSerializerOptions)

                                    return Some directoryVersions
                                else
                                    return None
                            }

                        // If they have already been generated, return them.
                        match! cachedSubdirectoryVersions with
                        | Some subdirectoryVersionDtos ->
                            log.LogTrace(
                                "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; In DirectoryVersionActor.GetRecursiveDirectoryVersions({id}): Retrieved SubdirectoryVersions from cache.",
                                getCurrentInstantExtended (),
                                getMachineName,
                                correlationId,
                                this.IdentityString
                            )

                            return subdirectoryVersionDtos
                        // If they haven't, generate them by calling each subdirectory in parallel.
                        | None ->
                            log.LogTrace(
                                "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; In DirectoryVersionActor.GetRecursiveDirectoryVersions({id}): subdirectoryVersions will be generated. forceRegenerate: {forceRegenerate}",
                                getCurrentInstantExtended (),
                                getMachineName,
                                correlationId,
                                directoryVersionDto.DirectoryVersion.DirectoryVersionId,
                                forceRegenerate
                            )

                            let subdirectoryVersionDtos = ConcurrentDictionary<DirectoryVersionId, DirectoryVersionDto>()

                            // First, add the current directory version to the queue.
                            log.LogTrace(
                                "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; In DirectoryVersionActor.GetRecursiveDirectoryVersions({id}): Adding current directory version. RelativePath: {relativePath}",
                                getCurrentInstantExtended (),
                                getMachineName,
                                correlationId,
                                this.GetPrimaryKey(),
                                directoryVersionDto.DirectoryVersion.RelativePath
                            )

                            subdirectoryVersionDtos.TryAdd(directoryVersionDto.DirectoryVersion.DirectoryVersionId, directoryVersionDto)
                            |> ignore

                            // Then, get the subdirectory versions in parallel and add them to the dictionary.
                            do!
                                Parallel.ForEachAsync(
                                    directoryVersionDto.DirectoryVersion.Directories,
                                    Constants.ParallelOptions,
                                    (fun subdirectoryVersionId ct ->
                                        ValueTask(
                                            task {
                                                try
                                                    let subdirectoryActor =
                                                        DirectoryVersion.CreateActorProxy
                                                            subdirectoryVersionId
                                                            directoryVersionDto.DirectoryVersion.RepositoryId
                                                            correlationId

                                                    let! subdirectoryVersion = subdirectoryActor.Get correlationId

                                                    log.LogTrace(
                                                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; In DirectoryVersionActor.GetRecursiveDirectoryVersions({id}): subdirectoryVersionId: {subdirectoryVersionId}. RelativePath: {relativePath}\n{directoryVersion}",
                                                        getCurrentInstantExtended (),
                                                        getMachineName,
                                                        correlationId,
                                                        directoryVersionDto.DirectoryVersion.DirectoryVersionId,
                                                        subdirectoryVersionId,
                                                        subdirectoryVersion.DirectoryVersion.RelativePath,
                                                        serialize subdirectoryVersion
                                                    )

                                                    let! subdirectoryContents = subdirectoryActor.GetRecursiveDirectoryVersions forceRegenerate correlationId

                                                    log.LogTrace(
                                                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; In DirectoryVersionActor.GetRecursiveDirectoryVersions({id}): subdirectoryVersionId: {subdirectoryVersionId}; Retrieved {count} subdirectory versions for RelativePath: {relativePath}\n{subdirectoryContents}",
                                                        getCurrentInstantExtended (),
                                                        getMachineName,
                                                        correlationId,
                                                        directoryVersionDto.DirectoryVersion.DirectoryVersionId,
                                                        subdirectoryVersionId,
                                                        subdirectoryContents.Length,
                                                        subdirectoryVersion.DirectoryVersion.RelativePath,
                                                        serialize subdirectoryContents
                                                    )

                                                    for directoryVersionDto in subdirectoryContents do
                                                        let directoryVersion = directoryVersionDto.DirectoryVersion

                                                        log.LogTrace(
                                                            "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; In DirectoryVersionActor.GetRecursiveDirectoryVersions({id}): subdirectoryVersionId: {subdirectoryVersionId}; Adding subdirectories. RelativePath: {relativePath}",
                                                            getCurrentInstantExtended (),
                                                            getMachineName,
                                                            correlationId,
                                                            directoryVersion.DirectoryVersionId,
                                                            subdirectoryVersionId,
                                                            directoryVersion.RelativePath
                                                        )

                                                        if not (subdirectoryVersionDtos.TryAdd(directoryVersion.DirectoryVersionId, directoryVersionDto)) then
                                                            logToConsole
                                                                $"Warning: In DirectoryVersionActor.GetRecursiveDirectoryVersions({this.GetPrimaryKey()}); Failed to add subdirectory version {directoryVersion.DirectoryVersionId} with relative path {directoryVersion.RelativePath} to the dictionary because it already exists."

                                                            logToConsole $"Current dictionary contents: {serialize subdirectoryVersionDtos}"

                                                            logToConsole $"Attempted to add: {serialize directoryVersionDto}"

                                                            Environment.Exit(-99)
                                                with ex ->
                                                    log.LogError(
                                                        "{CurrentInstant}: Error in {methodName}; DirectoryId: {directoryId}; Exception: {exception}",
                                                        getCurrentInstantExtended (),
                                                        "GetRecursiveDirectoryVersions",
                                                        subdirectoryVersionId,
                                                        ExceptionResponse.Create ex
                                                    )
                                            }
                                        ))
                                )

                            // Sort the subdirectory versions by their relative path.
                            let subdirectoryVersionsList =
                                subdirectoryVersionDtos.Values.OrderBy(fun directoryVersionDto -> directoryVersionDto.DirectoryVersion.RelativePath).ToArray()

                            // Save the recursive results to Azure Blob Storage.
                            //use memoryStream = new MemoryStream()
                            //use compressedStream = new GZipStream(memoryStream, CompressionMode.Compress)
                            //use writer = new StreamWriter(compressedStream)
                            //let json = serialize subdirectoryVersionsList
                            //writer.Write(json)
                            //writer.Flush()
                            //compressedStream.Flush()
                            //memoryStream.Position <- 0
                            //let! uploadResponse = blobClient.UploadAsync(memoryStream, overwrite = true)
                            let repositoryActorProxy =
                                Repository.CreateActorProxy
                                    directoryVersionDto.DirectoryVersion.OrganizationId
                                    directoryVersionDto.DirectoryVersion.RepositoryId
                                    correlationId

                            let! repositoryDto = repositoryActorProxy.Get correlationId

                            let tags = Dictionary<string, string>()
                            tags.Add(nameof DirectoryVersionId, $"{directoryVersionDto.DirectoryVersion.DirectoryVersionId}")
                            tags.Add(nameof RepositoryId, $"{directoryVersionDto.DirectoryVersion.RepositoryId}")
                            tags.Add(nameof RelativePath, $"{directoryVersionDto.DirectoryVersion.RelativePath}")
                            tags.Add(nameof Sha256Hash, $"{directoryVersionDto.DirectoryVersion.Sha256Hash}")
                            tags.Add(nameof OwnerId, $"{repositoryDto.OwnerId}")
                            tags.Add(nameof OrganizationId, $"{repositoryDto.OrganizationId}")

                            // Write the JSON using MessagePack serialization for efficiency.
                            use! blobStream = directoryVersionBlobClient.OpenWriteAsync(overwrite = true)
                            do! MessagePackSerializer.SerializeAsync(blobStream, subdirectoryVersionsList, messagePackSerializerOptions)
                            do! blobStream.DisposeAsync()

                            // Set the tags for the blob.
                            let! azureResponse = directoryVersionBlobClient.SetTagsAsync(tags)

                            log.LogDebug(
                                "In DirectoryVersionActor.GetRecursiveDirectoryVersions({id}); Saving cached list of directory versions. RelativePath: {relativePath}.",
                                this.GetPrimaryKey(),
                                directoryVersionDto.DirectoryVersion.RelativePath
                            )

                            // Create a reminder to delete the cached state after the configured number of cache days.
                            let deletionReminderState: PhysicalDeletionReminderState =
                                { DeleteReason = getDiscriminatedUnionCaseName ReminderTypes.DeleteCachedState; CorrelationId = correlationId }

                            do!
                                (this :> IGraceReminderWithGuidKey).ScheduleReminderAsync
                                    ReminderTypes.DeleteCachedState
                                    (Duration.FromDays(float repositoryDto.DirectoryVersionCacheDays))
                                    (ReminderState.DirectoryVersionDeleteCachedState deletionReminderState)
                                    correlationId

                            log.LogDebug(
                                "In DirectoryVersionActor.GetRecursiveDirectoryVersions({id}); Delete cached state reminder was set.",
                                this.GetPrimaryKey()
                            )

                            return subdirectoryVersionsList
                    with ex ->
                        log.LogError(
                            "{CurrentInstant}: Error in {methodName}. Exception: {exception}",
                            getCurrentInstantExtended (),
                            "GetRecursiveDirectoryVersions",
                            ExceptionResponse.Create ex
                        )

                        return Array.Empty<DirectoryVersionDto>()
                }

            member this.Handle command metadata =
                let isValid command (metadata: EventMetadata) =
                    task {
                        match command with
                        | DirectoryVersionCommand.Create(directoryVersion, repositoryDto) ->
                            if
                                state.State.Any(fun e ->
                                    match e.Event with
                                    | DirectoryVersionEventType.Created _ -> true
                                    | _ -> false)
                            then
                                return
                                    Error(
                                        GraceError.Create
                                            (DirectoryVersionError.getErrorMessage DirectoryVersionError.DirectoryAlreadyExists)
                                            metadata.CorrelationId
                                    )
                            else
                                return Ok command

                        | _ ->
                            if directoryVersionDto.DirectoryVersion.CreatedAt = DirectoryVersion.Default.CreatedAt then
                                return Error(GraceError.Create (DirectoryVersionError.getErrorMessage DirectoryDoesNotExist) metadata.CorrelationId)
                            else
                                return Ok command
                    }

                let processCommand (command: DirectoryVersionCommand) (metadata: EventMetadata) =
                    task {
                        try
                            let! event =
                                task {
                                    match command with
                                    | Create(directoryVersion, repositoryDto) ->
                                        // Determine which files need validation using incremental validation logic.
                                        let! mostRecentDirectoryVersion = getMostRecentDirectoryVersionByRelativePath repositoryDto.RepositoryId directoryVersion.RelativePath metadata.CorrelationId

                                        let filesToValidate =
                                            match mostRecentDirectoryVersion with
                                            | Some previousDirectoryVersion -> getFilesToValidate directoryVersion.Files previousDirectoryVersion.Files
                                            | None -> getFilesToValidate directoryVersion.Files (List<FileVersion>())

                                        log.LogDebug(
                                            "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Starting SHA-256 validation for DirectoryVersion; DirectoryVersionId: {DirectoryVersionId}; RelativePath: {RelativePath}; FileCount: {FileCount}; FilesToValidate: {FilesToValidate}.",
                                            getCurrentInstantExtended (),
                                            getMachineName,
                                            metadata.CorrelationId,
                                            directoryVersion.DirectoryVersionId,
                                            directoryVersion.RelativePath,
                                            directoryVersion.Files.Count,
                                            filesToValidate.Length
                                        )

                                        let validationResults = ConcurrentQueue<FileValidationResult>()

                                        // Validate files in parallel
                                        do! Parallel.ForEachAsync(
                                                filesToValidate,
                                                Constants.ParallelOptions,
                                                (fun fileVersion ct ->
                                                    ValueTask(
                                                        task {
                                                            let! result = validateFileSha256 repositoryDto fileVersion metadata.CorrelationId
                                                            validationResults.Enqueue result
                                                        }
                                                    )
                                                )
                                            )

                                        let validationResults = validationResults.ToArray()

                                        // Collect failures
                                        let failures =
                                            validationResults
                                            |> Array.filter (fun result ->
                                                match result with
                                                | Valid _ -> false
                                                | _ -> true)
                                            |> Array.toList

                                        let validCount =
                                            validationResults
                                            |> Array.filter (fun result ->
                                                match result with
                                                | Valid _ -> true
                                                | _ -> false)
                                            |> Array.length

                                        let totalElapsedMs =
                                            validationResults
                                            |> Array.sumBy (fun result ->
                                                match result with
                                                | Valid(_, _, ms) -> ms
                                                | HashMismatch(_, _, _, ms) -> ms
                                                | MissingInStorage(_, ms) -> ms
                                                | ValidationError(_, _, ms) -> ms)

                                        // Log validation results
                                        for result in validationResults do
                                            match result with
                                            | Valid(fv, computedHash, elapsedMs) ->
                                                log.LogDebug(
                                                    "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; SHA-256 validation passed; File: {RelativePath}; Hash: {Hash}; ElapsedMs: {ElapsedMs}.",
                                                    getCurrentInstantExtended (),
                                                    getMachineName,
                                                    metadata.CorrelationId,
                                                    fv.RelativePath,
                                                    computedHash,
                                                    elapsedMs
                                                )
                                            | HashMismatch(fv, expectedHash, computedHash, elapsedMs) ->
                                                log.LogWarning(
                                                    "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; SHA-256 hash mismatch; File: {RelativePath}; ExpectedHash: {ExpectedHash}; ComputedHash: {ComputedHash}; ElapsedMs: {ElapsedMs}.",
                                                    getCurrentInstantExtended (),
                                                    getMachineName,
                                                    metadata.CorrelationId,
                                                    fv.RelativePath,
                                                    expectedHash,
                                                    computedHash,
                                                    elapsedMs
                                                )
                                            | MissingInStorage(fv, elapsedMs) ->
                                                log.LogWarning(
                                                    "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; File not found in object storage; File: {RelativePath}; ExpectedHash: {ExpectedHash}; ElapsedMs: {ElapsedMs}.",
                                                    getCurrentInstantExtended (),
                                                    getMachineName,
                                                    metadata.CorrelationId,
                                                    fv.RelativePath,
                                                    fv.Sha256Hash,
                                                    elapsedMs
                                                )
                                            | ValidationError(fv, errorMessage, elapsedMs) ->
                                                log.LogError(
                                                    "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; SHA-256 validation error; File: {RelativePath}; Error: {ErrorMessage}; ElapsedMs: {ElapsedMs}.",
                                                    getCurrentInstantExtended (),
                                                    getMachineName,
                                                    metadata.CorrelationId,
                                                    fv.RelativePath,
                                                    errorMessage,
                                                    elapsedMs
                                                )

                                        // Check if any validation failed
                                        if failures.Length > 0 then
                                            // Build error message with details about failures
                                            let errorDetails =
                                                failures
                                                |> List.map (fun failure ->
                                                    match failure with
                                                    | HashMismatch(fv, expected, computed, _) ->
                                                        $"File '{fv.RelativePath}': hash mismatch (expected: {expected}, computed: {computed})"
                                                    | MissingInStorage(fv, _) -> $"File '{fv.RelativePath}': not found in object storage"
                                                    | ValidationError(fv, msg, _) -> $"File '{fv.RelativePath}': validation error ({msg})"
                                                    | _ -> "Unknown error")
                                                |> String.concat "; "

                                            log.LogWarning(
                                                "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; SHA-256 validation failed for DirectoryVersion; DirectoryVersionId: {DirectoryVersionId}; RelativePath: {RelativePath}; FailedCount: {FailedCount}; ValidCount: {ValidCount}; TotalElapsedMs: {TotalElapsedMs}.",
                                                getCurrentInstantExtended (),
                                                getMachineName,
                                                metadata.CorrelationId,
                                                directoryVersion.DirectoryVersionId,
                                                directoryVersion.RelativePath,
                                                failures.Length,
                                                validCount,
                                                totalElapsedMs
                                            )

                                            // Determine the appropriate error type
                                            let hasHashMismatch =
                                                failures
                                                |> List.exists (fun f ->
                                                    match f with
                                                    | HashMismatch _ -> true
                                                    | _ -> false)

                                            let hasMissing =
                                                failures
                                                |> List.exists (fun f ->
                                                    match f with
                                                    | MissingInStorage _ -> true
                                                    | _ -> false)

                                            let errorMessage =
                                                if hasMissing then
                                                    DirectoryVersionError.getErrorMessage DirectoryVersionError.FileNotFoundInObjectStorage
                                                    + " "
                                                    + errorDetails
                                                elif hasHashMismatch then
                                                    DirectoryVersionError.getErrorMessage DirectoryVersionError.FileSha256HashDoesNotMatch
                                                    + " "
                                                    + errorDetails
                                                else
                                                    $"File integrity check failed: {errorDetails}"

                                            return Error(GraceError.Create errorMessage metadata.CorrelationId)
                                        else
                                            log.LogInformation(
                                                "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; SHA-256 validation succeeded for DirectoryVersion; DirectoryVersionId: {DirectoryVersionId}; RelativePath: {RelativePath}; ValidatedCount: {ValidatedCount}; TotalElapsedMs: {TotalElapsedMs}.",
                                                getCurrentInstantExtended (),
                                                getMachineName,
                                                metadata.CorrelationId,
                                                directoryVersion.DirectoryVersionId,
                                                directoryVersion.RelativePath,
                                                validCount,
                                                totalElapsedMs
                                            )

                                            let newDirectoryVersion = { directoryVersion with HashesValidated = true }
                                            return Ok(Created newDirectoryVersion)
                                    | SetRecursiveSize recursiveSize -> return Ok(RecursiveSizeSet recursiveSize)
                                    | DeleteLogical deleteReason ->
                                        let repositoryActorProxy =
                                            Repository.CreateActorProxy
                                                directoryVersionDto.DirectoryVersion.OrganizationId
                                                directoryVersionDto.DirectoryVersion.RepositoryId
                                                metadata.CorrelationId

                                        let! repositoryDto = repositoryActorProxy.Get metadata.CorrelationId

                                        let physicalDeletionReminderState =
                                            { DeleteReason = getDiscriminatedUnionCaseName deleteReason; CorrelationId = metadata.CorrelationId }

                                        do!
                                            (this :> IGraceReminderWithGuidKey).ScheduleReminderAsync
                                                ReminderTypes.PhysicalDeletion
                                                (Duration.FromDays(float repositoryDto.LogicalDeleteDays))
                                                (ReminderState.DirectoryVersionPhysicalDeletion physicalDeletionReminderState)
                                                metadata.CorrelationId

                                        return Ok(LogicalDeleted deleteReason)
                                    | DeletePhysical ->
                                        do! state.ClearStateAsync()
                                        this.DeactivateOnIdle()
                                        return Ok(PhysicalDeleted)
                                    | Undelete -> return Ok(Undeleted)
                                }

                            match event with
                            | Ok event -> return! this.ApplyEvent { Event = event; Metadata = metadata }
                            | Error error -> return Error error
                        with ex ->
                            return Error(GraceError.CreateWithMetadata ex String.Empty metadata.CorrelationId metadata.Properties)
                    }

                task {
                    try
                        this.correlationId <- metadata.CorrelationId
                        currentCommand <- getDiscriminatedUnionCaseName command

                        match! isValid command metadata with
                        | Ok command -> return! processCommand command metadata
                        | Error error -> return Error error
                    with ex ->
                        logToConsole $"Exception in DirectoryVersionActor.Handle(): {ExceptionResponse.Create ex}"
                        return Error(GraceError.CreateWithException ex "Exception in DirectoryVersionActor.Handle()" metadata.CorrelationId)
                }

            member this.GetRecursiveSize correlationId =
                this.correlationId <- correlationId

                task {
                    if directoryVersionDto.RecursiveSize = Constants.InitialDirectorySize then
                        let! directoryVersions = (this :> IDirectoryVersionActor).GetRecursiveDirectoryVersions false correlationId

                        let recursiveSize =
                            directoryVersions
                            |> Seq.sumBy (fun directoryVersionDto -> directoryVersionDto.DirectoryVersion.Size)

                        match! (this :> IDirectoryVersionActor).Handle (SetRecursiveSize recursiveSize) (EventMetadata.New correlationId "GraceSystem") with
                        | Ok returnValue -> return recursiveSize
                        | Error error -> return Constants.InitialDirectorySize
                    else
                        return directoryVersionDto.RecursiveSize
                }

            member this.GetZipFileUri(correlationId: CorrelationId) : Task<UriWithSharedAccessSignature> =
                this.correlationId <- correlationId
                let directoryVersion = directoryVersionDto.DirectoryVersion

                /// Creates a .zip file containing the file contents of the directory version.
                let createDirectoryVersionZipFile
                    (repositoryDto: RepositoryDto)
                    (zipFileBlobName: string)
                    (directoryVersionId: DirectoryVersionId)
                    (subdirectoryVersionIds: List<DirectoryVersionId>)
                    (fileVersions: IEnumerable<FileVersion>)
                    =

                    task {
                        let zipFileName = $"{directoryVersionId}.zip"
                        let tempZipPath = Path.Combine(Path.GetTempPath(), zipFileName)

                        //logToConsole $"In createDirectoryZipAsync: directoryVersionId: {directoryVersionId}; relativePath: {directoryVersion.RelativePath}; zipFileName: {zipFileName}; tempZipPath: {tempZipPath}."

                        try
                            // Step 1: Create the ZIP archive.
                            use zipToCreate = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, (64 * 1024))
                            use archive = new ZipArchive(zipToCreate, ZipArchiveMode.Create)

                            let zipFileUris = new ConcurrentDictionary<DirectoryVersionId, UriWithSharedAccessSignature>()

                            // Step 2: Ensure that .zip files exist for all subdirectories, in parallel.
                            do!
                                Parallel.ForEachAsync(
                                    subdirectoryVersionIds,
                                    Constants.ParallelOptions,
                                    fun subdirectoryVersionId ct ->
                                        ValueTask(
                                            task {
                                                // Call the subdirectory actor to get the .zip file URI, which will create the .zip file if it doesn't already exist.
                                                let subdirectoryActorProxy =
                                                    DirectoryVersion.CreateActorProxy
                                                        subdirectoryVersionId
                                                        directoryVersionDto.DirectoryVersion.RepositoryId
                                                        correlationId

                                                let! subdirectoryZipFileUri = subdirectoryActorProxy.GetZipFileUri correlationId
                                                zipFileUris[subdirectoryVersionId] <- subdirectoryZipFileUri
                                            }
                                        )
                                )

                            // Step 3: Process the subdirectories of the current directory one at a time, because we need to add entries to the .zip file one at a time.
                            for subdirectoryVersionId in subdirectoryVersionIds do
                                // Get an Azure Blob Client for the .zip file.
                                let subdirectoryZipFileName = $"{GraceDirectoryVersionStorageFolderName}/{subdirectoryVersionId}.zip"
                                let! subdirectoryZipFileClient = getAzureBlobClient repositoryDto subdirectoryZipFileName correlationId

                                // Copy the contents of the subdirectory's .zip file to the new .zip we're creating.
                                use! subdirectoryZipFileStream = subdirectoryZipFileClient.OpenReadAsync()
                                use subdirectoryZipArchive = new ZipArchive(subdirectoryZipFileStream, ZipArchiveMode.Read)

                                for entry in subdirectoryZipArchive.Entries do
                                    if not (String.IsNullOrEmpty(entry.Name)) then
                                        // Using CompressionLevel.NoCompression because the files are already GZipped.
                                        // We're just using .zip as an archive format for already-compressed files.
                                        let newEntry = archive.CreateEntry(entry.FullName, CompressionLevel.NoCompression)
                                        newEntry.Comment <- entry.Comment
                                        use entryStream = entry.Open()
                                        use newEntryStream = newEntry.Open()
                                        do! entryStream.CopyToAsync(newEntryStream)

                            // Step 4: Process the files in the current directory.
                            for fileVersion in fileVersions do
                                //logToConsole $"In createDirectoryZipAsync: Processing file version: {Path.Combine(directoryVersion.RelativePath, fileVersion.GetObjectFileName)}."

                                let! fileBlobClient = getAzureBlobClientForFileVersion repositoryDto fileVersion correlationId
                                let! existsResult = fileBlobClient.ExistsAsync()

                                if existsResult.Value = true then
                                    use! fileStream = fileBlobClient.OpenReadAsync()
                                    let zipEntry = archive.CreateEntry(fileVersion.RelativePath, CompressionLevel.NoCompression)
                                    zipEntry.Comment <- fileVersion.GetObjectFileName
                                    use zipEntryStream = zipEntry.Open()
                                    do! fileStream.CopyToAsync(zipEntryStream)

                            // Step 5: Upload the new ZIP to Azure Blob Storage
                            archive.Dispose() // Dispose the archive before uploading to ensure it's properly flushed to the disk.
                            let! zipFileBlobClient = getAzureBlobClient repositoryDto zipFileBlobName correlationId
                            use tempZipFileStream = File.OpenRead(tempZipPath)
                            let! response = zipFileBlobClient.UploadAsync(tempZipFileStream, overwrite = true)

                            //logToConsole $"In createDirectoryZipAsync: Successfully uploaded {zipFileName} for relative path {directoryVersion.RelativePath} to Azure Blob Storage."

                            ()
                        finally
                            // Step 5: Delete the local ZIP file
                            if File.Exists(tempZipPath) then File.Delete(tempZipPath)
                    }

                task {
                    logToConsole $"In GetZipFileUri: directoryVersion: {serialize directoryVersion}."
                    let repositoryActorProxy = Repository.CreateActorProxy directoryVersion.OrganizationId directoryVersion.RepositoryId correlationId
                    let! repositoryDto = repositoryActorProxy.Get correlationId

                    let blobName = $"{GraceDirectoryVersionStorageFolderName}/{directoryVersion.DirectoryVersionId}.zip"
                    let! zipFileBlobClient = getAzureBlobClient repositoryDto blobName correlationId

                    let! zipFileExists = zipFileBlobClient.ExistsAsync()

                    if zipFileExists.Value = true then
                        // We already have this .zip file, so just return the URI with SAS.
                        let! uriWithSas = getUriWithReadSharedAccessSignature repositoryDto blobName correlationId
                        return uriWithSas
                    else
                        // We don't have the .zip file saved, so let's create it.
                        do!
                            createDirectoryVersionZipFile
                                repositoryDto
                                blobName
                                directoryVersion.DirectoryVersionId
                                directoryVersion.Directories
                                directoryVersion.Files

                        // Schedule a reminder to delete the .zip file after the cache days have passed.
                        let deletionReminderState: PhysicalDeletionReminderState =
                            { DeleteReason = getDiscriminatedUnionCaseName DeleteZipFile; CorrelationId = correlationId }

                        do!
                            (this :> IGraceReminderWithGuidKey).ScheduleReminderAsync
                                DeleteZipFile
                                (Duration.FromDays(float repositoryDto.DirectoryVersionCacheDays))
                                (ReminderState.DirectoryVersionDeleteZipFile deletionReminderState)
                                correlationId

                        let! uriWithSas = getUriWithReadSharedAccessSignature repositoryDto blobName correlationId
                        return uriWithSas
                }
