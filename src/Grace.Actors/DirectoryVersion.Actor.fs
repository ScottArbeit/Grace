namespace Grace.Actors

open Azure.Storage.Blobs
open Azure.Storage.Blobs.Models
open Azure.Storage.Blobs.Specialized
open Dapr.Actors
open Dapr.Actors.Client
open Dapr.Actors.Runtime
open Grace.Actors.Commands.DirectoryVersion
open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Events.DirectoryVersion
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Services
open Grace.Shared.Types
open Grace.Shared.Dto.Repository
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors.DirectoryVersion
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Linq
open System.Threading.Tasks
open System.Reflection.Metadata

module DirectoryVersion =

    /// The data types stored in physical deletion reminders.
    type PhysicalDeletionReminderState = DeleteReason * CorrelationId
    type DeleteCachedStateReminderState = unit

    let GetActorId (directoryId: DirectoryVersionId) = ActorId($"{directoryId}")
    let log = loggerFactory.CreateLogger("DirectoryVersion.Actor")

    type DirectoryVersionDto =
        { DirectoryVersion: DirectoryVersion
          RecursiveSize: int64
          DeletedAt: Instant option
          DeleteReason: DeleteReason }

        static member Default =
            { DirectoryVersion = DirectoryVersion.Default; RecursiveSize = Constants.InitialDirectorySize; DeletedAt = None; DeleteReason = String.Empty }

    type DirectoryVersionActor(host: ActorHost) =
        inherit Actor(host)

        static let actorName = ActorName.DirectoryVersion
        static let eventsStateName = StateName.DirectoryVersion
        static let directoryVersionCacheStateName = StateName.DirectoryVersionCache

        let directoryVersionEvents = List<DirectoryVersionEvent>()

        let mutable directoryVersionDto = DirectoryVersionDto.Default
        let mutable actorStartTime = Instant.MinValue
        let mutable logScope: IDisposable = null
        let mutable currentCommand = String.Empty
        let mutable stateManager = Unchecked.defaultof<IActorStateManager>

        /// Indicates that the actor is in an undefined state, and should be reset.
        let mutable isDisposed = false

        let updateDto directoryVersionEvent currentDirectoryVersionDto =
            match directoryVersionEvent with
            | Created directoryVersion -> { currentDirectoryVersionDto with DirectoryVersion = directoryVersion }
            | RecursiveSizeSet recursiveSize -> { currentDirectoryVersionDto with RecursiveSize = recursiveSize }
            | LogicalDeleted deleteReason -> { currentDirectoryVersionDto with DeletedAt = Some(getCurrentInstant ()); DeleteReason = deleteReason }
            | PhysicalDeleted -> currentDirectoryVersionDto // Do nothing because it's about to be deleted anyway.
            | Undeleted -> { currentDirectoryVersionDto with DeletedAt = None; DeleteReason = String.Empty }

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync() =
            let activateStartTime = getCurrentInstant ()
            stateManager <- this.StateManager

            task {
                let correlationId =
                    match memoryCache.GetCorrelationIdEntry this.Id with
                    | Some correlationId -> correlationId
                    | None -> String.Empty

                try
                    let! retrievedEvents = Storage.RetrieveState<List<DirectoryVersionEvent>> stateManager eventsStateName correlationId

                    match retrievedEvents with
                    | Some retrievedEvents ->
                        // Load the existing events into memory.
                        directoryVersionEvents.AddRange(retrievedEvents)

                        // Apply the events to build the current Dto.
                        directoryVersionDto <-
                            retrievedEvents
                            |> Seq.fold
                                (fun directoryVersionDto directoryVersionEvent -> directoryVersionDto |> updateDto directoryVersionEvent.Event)
                                DirectoryVersionDto.Default

                        logToConsole
                            $"In DirectoryVersionActor.OnActivateAsync: directoryVersion.DirectoryVersionId: {directoryVersionDto.DirectoryVersion.DirectoryVersionId}; directoryVersion.RelativePath: {directoryVersionDto.DirectoryVersion.RelativePath}."
                    | None -> ()

                    logActorActivation log activateStartTime correlationId actorName this.Id (getActorActivationMessage retrievedEvents)
                with ex ->
                    let exc = ExceptionResponse.Create ex
                    log.LogError("{CurrentInstant} Error activating {ActorType} {ActorId}.", getCurrentInstantExtended (), this.GetType().Name, host.Id)
                    log.LogError("{CurrentInstant} {ExceptionDetails}", getCurrentInstantExtended (), exc.ToString())
                    logActorActivation log activateStartTime correlationId actorName this.Id "Exception occurred during activation."
            }
            :> Task

        override this.OnPreActorMethodAsync(context) =
            this.correlationId <- String.Empty

            if context.CallType = ActorCallType.ReminderMethod then
                log.LogInformation(
                    "{CurrentInstant}: Reminder {ActorName}.{MethodName} Id: {Id}.",
                    getCurrentInstantExtended (),
                    actorName,
                    context.MethodName,
                    this.Id
                )

            actorStartTime <- getCurrentInstant ()
            currentCommand <- String.Empty
            logScope <- log.BeginScope("Actor {actorName}", actorName)

            log.LogTrace("{CurrentInstant}: Started {ActorName}.{MethodName} Id: {Id}.", getCurrentInstantExtended (), actorName, context.MethodName, this.Id)

            // This checks if the actor is still active, but in an undefined state, which will _almost_ never happen.
            // isDisposed is set when the actor is deleted, or if an error occurs where we're not sure of the state and want to reload from the database.
            if isDisposed then
                this.OnActivateAsync().Wait()
                isDisposed <- false

            Task.CompletedTask

        override this.OnPostActorMethodAsync(context) =
            let duration_ms = getPaddedDuration_ms actorStartTime

            if String.IsNullOrEmpty(currentCommand) then
                log.LogInformation(
                    "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {ActorName}.{MethodName}; DirectoryVersionId: {DirectoryVersionId}; RelativePath: {RelativePath}; RepositoryId: {RepositoryId}.",
                    getCurrentInstantExtended (),
                    getMachineName,
                    duration_ms,
                    this.correlationId,
                    actorName,
                    context.MethodName,
                    directoryVersionDto.DirectoryVersion.DirectoryVersionId,
                    directoryVersionDto.DirectoryVersion.RelativePath,
                    directoryVersionDto.DirectoryVersion.RepositoryId
                )
            else
                log.LogInformation(
                    "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {ActorName}.{MethodName}; Command: {Command}; DirectoryVersionId: {DirectoryVersionId}; RelativePath: {RelativePath}; RepositoryId: {RepositoryId}.",
                    getCurrentInstantExtended (),
                    getMachineName,
                    duration_ms,
                    this.correlationId,
                    actorName,
                    context.MethodName,
                    currentCommand,
                    directoryVersionDto.DirectoryVersion.DirectoryVersionId,
                    directoryVersionDto.DirectoryVersion.RelativePath,
                    directoryVersionDto.DirectoryVersion.RepositoryId
                )

            logScope.Dispose()
            Task.CompletedTask


        interface IGraceReminder with
            /// Schedules a Grace reminder.
            member this.ScheduleReminderAsync reminderType delay state correlationId =
                task {
                    let reminder = ReminderDto.Create actorName $"{this.Id}" reminderType (getFutureInstant delay) state correlationId
                    do! createReminder reminder
                }
                :> Task

            /// Receives a Grace reminder.
            member this.ReceiveReminderAsync(reminder: ReminderDto) : Task<Result<unit, GraceError>> =
                task {
                    this.correlationId <- reminder.CorrelationId

                    match reminder.ReminderType with
                    | ReminderTypes.DeleteCachedState ->
                        // Get values from state.
                        if not <| String.IsNullOrEmpty reminder.State then
                            let (deleteReason, correlationId) = deserialize<PhysicalDeletionReminderState> reminder.State

                            this.correlationId <- correlationId

                            // Delete saved state for this actor.
                            let! deleted = Storage.DeleteState stateManager directoryVersionCacheStateName

                            if deleted then
                                log.LogInformation(
                                    "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Deleted cache for directory version; RepositoryId: {RepositoryId}; DirectoryVersionId: {DirectoryVersionId}; deleteReason: {deleteReason}.",
                                    getCurrentInstantExtended (),
                                    getMachineName,
                                    correlationId,
                                    directoryVersionDto.DirectoryVersion.RepositoryId,
                                    directoryVersionDto.DirectoryVersion.DirectoryVersionId,
                                    deleteReason
                                )
                            else
                                log.LogWarning(
                                    "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Failed to delete cache for directory version (it may have already been deleted); RepositoryId: {RepositoryId}; DirectoryVersionId: {DirectoryVersionId}; deleteReason: {deleteReason}.",
                                    getCurrentInstantExtended (),
                                    getMachineName,
                                    correlationId,
                                    directoryVersionDto.DirectoryVersion.RepositoryId,
                                    directoryVersionDto.DirectoryVersion.DirectoryVersionId,
                                    deleteReason
                                )

                        return Ok()
                    | ReminderTypes.DeleteZipFile ->
                        // Get values from state.
                        if not <| String.IsNullOrEmpty reminder.State then
                            let (deleteReason, correlationId) = deserialize<PhysicalDeletionReminderState> reminder.State

                            this.correlationId <- correlationId

                            let directoryVersion = directoryVersionDto.DirectoryVersion
                            let repositoryActorProxy = Repository.CreateActorProxy directoryVersion.RepositoryId correlationId
                            let! repositoryDto = repositoryActorProxy.Get correlationId

                            // Delete cached directory version contents for this actor.
                            let blobName = $"{GraceDirectoryVersionStorageFolderName}/{directoryVersion.DirectoryVersionId}.zip"
                            let! zipFileBlobClient = getAzureBlobClient repositoryDto blobName correlationId

                            let! deleted = zipFileBlobClient.DeleteIfExistsAsync()

                            if deleted.Value then
                                log.LogInformation(
                                    "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Deleted cache for directory version; RepositoryId: {RepositoryId}; DirectoryVersionId: {DirectoryVersionId}; deleteReason: {deleteReason}.",
                                    getCurrentInstantExtended (),
                                    getMachineName,
                                    correlationId,
                                    directoryVersionDto.DirectoryVersion.RepositoryId,
                                    directoryVersionDto.DirectoryVersion.DirectoryVersionId,
                                    deleteReason
                                )
                            else
                                log.LogWarning(
                                    "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Failed to delete cache for directory version (it may have already been deleted); RepositoryId: {RepositoryId}; DirectoryVersionId: {DirectoryVersionId}; deleteReason: {deleteReason}.",
                                    getCurrentInstantExtended (),
                                    getMachineName,
                                    correlationId,
                                    directoryVersionDto.DirectoryVersion.RepositoryId,
                                    directoryVersionDto.DirectoryVersion.DirectoryVersionId,
                                    deleteReason
                                )

                        return Ok()
                    | ReminderTypes.PhysicalDeletion ->
                        // Get values from state.
                        let (deleteReason, correlationId) = deserialize<PhysicalDeletionReminderState> reminder.State

                        this.correlationId <- correlationId

                        // Delete saved state for this actor.
                        let! deleted = Storage.DeleteState stateManager eventsStateName

                        if deleted then
                            log.LogInformation(
                                "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Deleted state for directory version; RepositoryId: {RepositoryId}; DirectoryVersionId: {DirectoryVersionId}; deleteReason: {deleteReason}.",
                                getCurrentInstantExtended (),
                                getMachineName,
                                correlationId,
                                directoryVersionDto.DirectoryVersion.RepositoryId,
                                directoryVersionDto.DirectoryVersion.DirectoryVersionId,
                                deleteReason
                            )
                        else
                            log.LogWarning(
                                "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Failed to delete state for directory version (it may have already been deleted); RepositoryId: {RepositoryId}; DirectoryVersionId: {DirectoryVersionId}; deleteReason: {deleteReason}.",
                                getCurrentInstantExtended (),
                                getMachineName,
                                correlationId,
                                directoryVersionDto.DirectoryVersion.RepositoryId,
                                directoryVersionDto.DirectoryVersion.DirectoryVersionId,
                                deleteReason
                            )

                        // Set all values to default.
                        directoryVersionDto <- DirectoryVersionDto.Default
                        directoryVersionEvents.Clear()

                        // Mark the actor as disposed, in case someone tries to use it before Dapr GC's it.
                        isDisposed <- true
                        return Ok()
                    | _ ->
                        return
                            Error(
                                (GraceError.Create
                                    $"{actorName} does not process reminder type {getDiscriminatedUnionCaseName reminder.ReminderType}."
                                    this.correlationId)
                                    .enhance ("IsRetryable", "false")
                            )
                }

        member private this.ApplyEvent directoryVersionEvent =
            task {
                try
                    // Add the event to the list of events, and save it to actor state.
                    directoryVersionEvents.Add(directoryVersionEvent)
                    do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(eventsStateName, directoryVersionEvents))

                    // Update the Dto with the event.
                    directoryVersionDto <- directoryVersionDto |> updateDto directoryVersionEvent.Event

                    logToConsole
                        $"In ApplyEvent(): directoryVersion.DirectoryVersionId: {directoryVersionDto.DirectoryVersion.DirectoryVersionId}; directoryVersion.RelativePath: {directoryVersionDto.DirectoryVersion.RelativePath}."


                    // Publish the event to the rest of the world.
                    let graceEvent = Events.GraceEvent.DirectoryVersionEvent directoryVersionEvent
                    do! daprClient.PublishEventAsync(GracePubSubService, GraceEventStreamTopic, graceEvent)

                    let returnValue = GraceReturnValue.Create "Directory version command succeeded." directoryVersionEvent.Metadata.CorrelationId

                    returnValue
                        .enhance(nameof (RepositoryId), $"{directoryVersionDto.DirectoryVersion.RepositoryId}")
                        .enhance(nameof (DirectoryVersionId), $"{directoryVersionDto.DirectoryVersion.DirectoryVersionId}")
                        .enhance(nameof (Sha256Hash), $"{directoryVersionDto.DirectoryVersion.Sha256Hash}")
                        .enhance (nameof (DirectoryVersionEventType), $"{getDiscriminatedUnionFullName directoryVersionEvent.Event}")
                    |> ignore

                    return Ok returnValue
                with ex ->
                    let exceptionResponse = ExceptionResponse.Create ex

                    let graceError =
                        GraceError.Create (DirectoryVersionError.getErrorMessage FailedWhileApplyingEvent) directoryVersionEvent.Metadata.CorrelationId

                    graceError
                        .enhance("Exception details", exceptionResponse.``exception`` + exceptionResponse.innerException)
                        .enhance(nameof (RepositoryId), $"{directoryVersionDto.DirectoryVersion.RepositoryId}")
                        .enhance(nameof (DirectoryVersionId), $"{directoryVersionDto.DirectoryVersion.DirectoryVersionId}")
                        .enhance(nameof (Sha256Hash), $"{directoryVersionDto.DirectoryVersion.Sha256Hash}")
                        .enhance (nameof (DirectoryVersionEventType), $"{getDiscriminatedUnionFullName directoryVersionEvent.Event}")
                    |> ignore

                    return Error graceError
            }

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
                        // Check if the subdirectory versions have already been generated and cached.
                        let cachedSubdirectoryVersions =
                            task {
                                if not <| forceRegenerate then
                                    return! Storage.RetrieveState<DirectoryVersion array> stateManager directoryVersionCacheStateName correlationId
                                else
                                    return None
                            }

                        // If they have already been generated, return them.
                        match! cachedSubdirectoryVersions with
                        | Some subdirectoryVersions ->
                            log.LogDebug("In DirectoryVersionActor.GetDirectoryVersionsRecursive({id}). Retrieved SubdirectoryVersions from cache.", this.Id)

                            return subdirectoryVersions.ToArray()
                        // If they haven't, generate them by calling each subdirectory in parallel.
                        | None ->
                            log.LogDebug(
                                "In DirectoryVersionActor.GetDirectoryVersionsRecursive({id}). SubdirectoryVersions will be generated. forceRegenerate: {forceRegenerate}",
                                this.Id,
                                forceRegenerate
                            )

                            let subdirectoryVersions = ConcurrentQueue<DirectoryVersion>()
                            subdirectoryVersions.Enqueue(directoryVersionDto.DirectoryVersion)

                            do!
                                Parallel.ForEachAsync(
                                    directoryVersionDto.DirectoryVersion.Directories,
                                    Constants.ParallelOptions,
                                    (fun directoryId ct ->
                                        ValueTask(
                                            task {
                                                try
                                                    let subdirectoryActor = DirectoryVersion.CreateActorProxy directoryId correlationId

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

                            let subdirectoryVersionsList =
                                subdirectoryVersions.ToArray()
                                |> Array.sortBy (fun directoryVersion -> directoryVersion.RelativePath)

                            do! Storage.SaveState stateManager directoryVersionCacheStateName subdirectoryVersionsList this.correlationId

                            log.LogDebug("In DirectoryVersionActor.GetDirectoryVersionsRecursive({id}); Storing subdirectoryVersion list.", this.Id)

                            let repositoryActorProxy = Repository.CreateActorProxy directoryVersionDto.DirectoryVersion.RepositoryId correlationId
                            let! repositoryDto = repositoryActorProxy.Get correlationId

                            let deletionReminderState = (getDiscriminatedUnionCaseName ReminderTypes.DeleteCachedState, correlationId)

                            do!
                                (this :> IGraceReminder).ScheduleReminderAsync
                                    ReminderTypes.DeleteCachedState
                                    (Duration.FromDays(float repositoryDto.DirectoryVersionCacheDays))
                                    (serialize deletionReminderState)
                                    correlationId

                            log.LogDebug("In DirectoryVersionActor.GetDirectoryVersionsRecursive({id}); Delete cached state reminder was set.", this.Id)

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
                                directoryVersionEvents.Any(fun e ->
                                    match e.Event with
                                    | Created _ -> true
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
                                            Repository.CreateActorProxy directoryVersionDto.DirectoryVersion.RepositoryId metadata.CorrelationId

                                        let! repositoryDto = repositoryActorProxy.Get metadata.CorrelationId

                                        let (reminderState: PhysicalDeletionReminderState) = (deleteReason, metadata.CorrelationId)

                                        do!
                                            (this :> IGraceReminder).ScheduleReminderAsync
                                                ReminderTypes.PhysicalDeletion
                                                (Duration.FromDays(float repositoryDto.LogicalDeleteDays))
                                                (serialize reminderState)
                                                metadata.CorrelationId

                                        return Ok(LogicalDeleted deleteReason)
                                    | DeletePhysical ->
                                        isDisposed <- true
                                        return Ok(PhysicalDeleted)
                                    | Undelete -> return Ok(Undeleted)
                                }

                            match eventResult with
                            | Ok event -> return! this.ApplyEvent { Event = event; Metadata = metadata }
                            | Error error -> return Error error
                        with ex ->
                            return Error(GraceError.CreateWithMetadata $"{ExceptionResponse.Create ex}" metadata.CorrelationId metadata.Properties)
                    }

                task {
                    this.correlationId <- metadata.CorrelationId
                    currentCommand <- getDiscriminatedUnionCaseName command

                    match! isValid command metadata with
                    | Ok command -> return! processCommand command metadata
                    | Error error -> return Error error
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
                            do! Parallel.ForEachAsync(subdirectoryVersionIds, Constants.ParallelOptions, fun subdirectoryVersionId ct ->
                                ValueTask(task {
                                    // Call the subdirectory actor to get the .zip file URI, which will create the .zip file if it doesn't already exist.
                                    let subdirectoryActorProxy = DirectoryVersion.CreateActorProxy subdirectoryVersionId correlationId
                                    let! subdirectoryZipFileUri = subdirectoryActorProxy.GetZipFileUri correlationId
                                    zipFileUris[subdirectoryVersionId] <- subdirectoryZipFileUri
                                }))

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
                            archive.Dispose()   // Dispose the archive before uploading to ensure it's properly flushed to the disk.
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
                    let repositoryActorProxy = Repository.CreateActorProxy directoryVersion.RepositoryId correlationId
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
                        let deletionReminderState = (getDiscriminatedUnionCaseName DeleteZipFile, correlationId)

                        do!
                            (this :> IGraceReminder).ScheduleReminderAsync
                                DeleteZipFile
                                (Duration.FromDays(float repositoryDto.DirectoryVersionCacheDays))
                                (serialize deletionReminderState)
                                correlationId

                        let! uriWithSas = getUriWithReadSharedAccessSignature repositoryDto blobName correlationId
                        return uriWithSas
                }
