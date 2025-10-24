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
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Linq
open System.Threading.Tasks
open System.Reflection.Metadata
open MessagePack

module DirectoryVersion =

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
                directoryVersionDto.DirectoryVersion |> returnTask

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
                                    let! json = MessagePackSerializer.DeserializeAsync<DirectoryVersion array>(blobStream, messagePackSerializerOptions)
                                    //use memoryStream = new MemoryStream()
                                    //let! blah = blobClient.DownloadToAsync(memoryStream)
                                    //memoryStream.Position <- 0
                                    //use decompressedStream = new GZipStream(memoryStream, CompressionMode.Decompress)
                                    //use reader = new StreamReader(decompressedStream)
                                    //let json = reader.ReadToEnd()
                                    return Some json
                                else
                                    return None
                            }

                        // If they have already been generated, return them.
                        match! cachedSubdirectoryVersions with
                        | Some subdirectoryVersions ->
                            log.LogDebug(
                                "In DirectoryVersionActor.GetRecursiveDirectoryVersions({id}). Retrieved SubdirectoryVersions from cache.",
                                this.IdentityString
                            )

                            return subdirectoryVersions
                        // If they haven't, generate them by calling each subdirectory in parallel.
                        | None ->
                            log.LogDebug(
                                "In DirectoryVersionActor.GetRecursiveDirectoryVersions({id}). SubdirectoryVersions will be generated. forceRegenerate: {forceRegenerate}",
                                this.IdentityString,
                                forceRegenerate
                            )

                            let subdirectoryVersions = ConcurrentQueue<DirectoryVersion>()

                            // First, add the current directory version to the queue.
                            subdirectoryVersions.Enqueue(directoryVersionDto.DirectoryVersion)

                            // Then, get the subdirectory versions in parallel and add them to the queue.
                            do!
                                Parallel.ForEachAsync(
                                    directoryVersionDto.DirectoryVersion.Directories,
                                    Constants.ParallelOptions,
                                    (fun directoryId ct ->
                                        ValueTask(
                                            task {
                                                try
                                                    let subdirectoryActor =
                                                        DirectoryVersion.CreateActorProxy
                                                            directoryId
                                                            directoryVersionDto.DirectoryVersion.RepositoryId
                                                            correlationId

                                                    let! subdirectoryContents = subdirectoryActor.GetRecursiveDirectoryVersions forceRegenerate correlationId

                                                    for directoryVersion in subdirectoryContents do
                                                        subdirectoryVersions.Enqueue(directoryVersion)
                                                with ex ->
                                                    log.LogError(
                                                        "{CurrentInstant}: Error in {methodName}; DirectoryId: {directoryId}; Exception: {exception}",
                                                        getCurrentInstantExtended (),
                                                        "GetRecursiveDirectoryVersions",
                                                        directoryId,
                                                        ExceptionResponse.Create ex
                                                    )
                                            }
                                        ))
                                )

                            // Sort the subdirectory versions by their relative path.
                            let subdirectoryVersionsList =
                                subdirectoryVersions.ToArray()
                                |> Array.sortBy (fun directoryVersion -> directoryVersion.RelativePath)

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

                        return Array.Empty<DirectoryVersion>()
                }

            member this.Handle command metadata =
                let isValid command (metadata: EventMetadata) =
                    task {
                        match command with
                        | DirectoryVersionCommand.Create directoryVersion ->
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
                            let! eventResult =
                                task {
                                    match command with
                                    | Create directoryVersion -> return Ok(Created directoryVersion)
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

                            match eventResult with
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

                        let recursiveSize = directoryVersions |> Seq.sumBy (fun directoryVersion -> directoryVersion.Size)

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
