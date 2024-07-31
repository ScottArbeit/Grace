namespace Grace.Actors

open Azure.Storage.Blobs
open Azure.Storage.Blobs.Specialized
open Dapr.Actors
open Dapr.Actors.Runtime
open DiffPlex
open DiffPlex.DiffBuilder.Model
open FSharpPlus
open Grace.Actors
open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Diff
open Grace.Shared.Dto.Diff
open Grace.Shared.Dto.Repository
open Grace.Shared.Types
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Linq
open System.IO
open System.IO.Compression
open System.Threading.Tasks

module Diff =

    /// Deconstructs an ActorId of the form "{directoryId1}*{directoryId2}" into a tuple of the two DirectoryId values.
    let private deconstructActorId (id: ActorId) =
        let directoryIds = id.GetId().Split("*")
        (DirectoryVersionId directoryIds[0], DirectoryVersionId directoryIds[1])

    type DiffActor(host: ActorHost) =
        inherit Actor(host)

        let dtoStateName = StateName.Diff
        let mutable diffDto: DiffDto = DiffDto.Default
        let actorName = ActorName.Diff
        let log = loggerFactory.CreateLogger("Diff.Actor")
        let mutable actorStartTime = Instant.MinValue
        let mutable logScope: IDisposable = null

        /// Gets a Dictionary for indexed lookups by relative path.
        let getLookupCache (graceIndex: ServerGraceIndex) =
            let lookupCache = Dictionary<FileSystemEntryType * RelativePath, Sha256Hash>()

            for directoryVersion in graceIndex.Values do
                // Add the directory to the lookup cache.
                lookupCache.TryAdd((FileSystemEntryType.Directory, directoryVersion.RelativePath), directoryVersion.Sha256Hash)
                |> ignore
                // Add each file to the lookup cache.
                for file in directoryVersion.Files do
                    lookupCache.TryAdd((FileSystemEntryType.File, file.RelativePath), file.Sha256Hash)
                    |> ignore

            lookupCache

        /// Scans two ServerGraceIndex instances for differences.
        let scanForDifferences (newerGraceIndex: ServerGraceIndex) (olderGraceIndex: ServerGraceIndex) =
            task {
                let emptyLookup = KeyValuePair(String.Empty, Sha256Hash String.Empty)
                let differences = List<FileSystemDifference>()

                // Create an indexed lookup table of path -> lastWriteTimeUtc from the Grace index file.
                let olderLookupCache = getLookupCache olderGraceIndex
                let newerLookupCache = getLookupCache newerGraceIndex

                // Compare them for differences.
                for kvp in olderLookupCache do
                    let ((fileSystemEntryType, relativePath), sha256Hash) = kvp.Deconstruct()
                    // Find the entries that changed
                    if
                        newerLookupCache.ContainsKey((fileSystemEntryType, relativePath))
                        && sha256Hash <> newerLookupCache.Item((fileSystemEntryType, relativePath))
                    then
                        differences.Add(FileSystemDifference.Create Change fileSystemEntryType relativePath)

                    // Find the entries that were deleted
                    elif not <| newerLookupCache.ContainsKey((fileSystemEntryType, relativePath)) then
                        differences.Add(FileSystemDifference.Create Delete fileSystemEntryType relativePath)

                // Find the entries that were added
                for kvp in newerLookupCache do
                    let ((fileSystemEntryType, relativePath), sha256Hash) = kvp.Deconstruct()

                    if not <| olderLookupCache.ContainsKey((fileSystemEntryType, relativePath)) then
                        differences.Add(FileSystemDifference.Create Add fileSystemEntryType relativePath)

                return differences
            }

        member val private correlationId: CorrelationId = String.Empty with get, set

        /// Builds a ServerGraceIndex from a root DirectoryId.
        member private this.buildGraceIndex (directoryId: DirectoryVersionId) correlationId =
            task {
                this.correlationId <- correlationId
                let graceIndex = ServerGraceIndex()

                let directory = actorProxyFactory.CreateActorProxyWithCorrelationId<IDirectoryVersionActor>(DirectoryVersion.GetActorId(directoryId), ActorName.DirectoryVersion, correlationId)

                let! directoryCreatedAt = directory.GetCreatedAt correlationId
                let! directoryContents = directory.GetRecursiveDirectoryVersions false correlationId
                //logToConsole $"In DiffActor.buildGraceIndex(): directoryContents.Count: {directoryContents.Count}"
                for directoryVersion in directoryContents do
                    graceIndex.TryAdd(directoryVersion.RelativePath, directoryVersion) |> ignore

                return (graceIndex, directoryCreatedAt, directoryContents[0].RepositoryId)
            }

        /// Gets a Stream from object storage for a specific FileVersion, using a generated Uri.
        member private this.getFileStream (fileVersion: FileVersion) (url: UriWithSharedAccessSignature) correlationId =
            task {
                this.correlationId <- correlationId
                let repositoryActorId = Repository.GetActorId(fileVersion.RepositoryId)

                let repositoryActorProxy = actorProxyFactory.CreateActorProxyWithCorrelationId<IRepositoryActor>(repositoryActorId, ActorName.Repository, correlationId)

                let! objectStorageProvider = repositoryActorProxy.GetObjectStorageProvider correlationId

                match objectStorageProvider with
                | AWSS3 -> return new MemoryStream() :> Stream
                | AzureBlobStorage ->
                    let blobClient = BlockBlobClient(Uri(url))
                    //logToConsole $"In DiffActor.getFileStream(): blobClient.Uri: {blobClient.Uri}."
                    let fileStream = blobClient.OpenRead(position = 0, bufferSize = (64 * 1024))
                    let uncompressedStream = if fileVersion.IsBinary then fileStream else fileStream
                    //let gzStream = new GZipStream(fileStream, CompressionMode.Decompress, leaveOpen = true)
                    //gzStream :> Stream
                    return uncompressedStream
                | GoogleCloudStorage -> return new MemoryStream() :> Stream
                | ObjectStorageProvider.Unknown -> return new MemoryStream() :> Stream
            }

        /// Sets a delete reminder for this actor's state.
        member private this.setDeleteReminder() =
            let task = this.RegisterReminderAsync(ReminderType.DeleteCachedState, Array.empty<byte>, TimeSpan.FromDays(3.0), TimeSpan.FromMilliseconds(-1.0))
            task.Wait()
            ()

        override this.OnActivateAsync() =
            let activateStartTime = getCurrentInstant ()
            let stateManager = this.StateManager

            task {
                let mutable message = String.Empty
                let! retrievedDto = Storage.RetrieveState<DiffDto> stateManager dtoStateName

                match retrievedDto with
                | Some retrievedDto ->
                    diffDto <- retrievedDto
                    message <- "Retrieved from database."
                | None ->
                    diffDto <- DiffDto.Default
                    message <- "Not found in database."

                let duration_ms = getCurrentInstant().Minus(activateStartTime).TotalMilliseconds.ToString("F3")

                log.LogInformation(
                    "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId:             ; Activated {ActorType} {ActorId}. BranchName: {BranchName}; {message}.",
                    getCurrentInstantExtended (),
                    actorName,
                    host.Id,
                    message,
                    duration_ms
                )
            }
            :> Task

        override this.OnPreActorMethodAsync(context) =
            this.correlationId <- String.Empty
            actorStartTime <- getCurrentInstant ()
            logScope <- log.BeginScope("Actor {actorName}", actorName)

            log.LogTrace("{CurrentInstant}: Started {ActorName}.{MethodName} Id: {Id}.", getCurrentInstantExtended (), actorName, context.MethodName, this.Id)

            Task.CompletedTask

        override this.OnPostActorMethodAsync(context) =
            let duration_ms = (getCurrentInstant().Minus(actorStartTime).TotalMilliseconds).ToString("F3")

            log.LogInformation(
                "{CurrentInstant}: CorrelationId: {correlationId}; Finished {ActorName}.{MethodName}; Id: {Id}; Duration: {duration_ms}ms.",
                getCurrentInstantExtended (),
                this.correlationId,
                actorName,
                context.MethodName,
                this.Id,
                duration_ms
            )

            logScope.Dispose()
            Task.CompletedTask

        member private this.GetDiffSimple() =
            if diffDto.DirectoryId1 = DiffDto.Default.DirectoryId1 then
                None |> returnValueTask
            else
                Some diffDto |> returnValueTask

        interface IDiffActor with
            member this.Compute correlationId =
                this.correlationId <- correlationId

                // If it's already populated, skip this.
                if diffDto.DirectoryId1 <> DiffDto.Default.DirectoryId1 then
                    (true |> returnTask)
                else
                    let stateManager = this.StateManager

                    task {
                        let (directoryId1, directoryId2) = deconstructActorId this.Id

                        logToConsole $"In DiffActor.Populate(); DirectoryId1: {directoryId1}; DirectoryId2: {directoryId2}"

                        try
                            // Build a GraceIndex for each DirectoryId.
                            let! (graceIndex1, createdAt1, repositoryId1) = this.buildGraceIndex directoryId1 correlationId

                            let! (graceIndex2, createdAt2, repositoryId2) = this.buildGraceIndex directoryId2 correlationId
                            //logToConsole $"In DiffActor.Populate(); createdAt1: {createdAt1}; createdAt2: {createdAt2}."

                            // Compare the GraceIndices.
                            let! differences =
                                task {
                                    if createdAt1.CompareTo(createdAt2) > 0 then
                                        return! scanForDifferences graceIndex1 graceIndex2
                                    else
                                        return! scanForDifferences graceIndex2 graceIndex1
                                }

                            //logToConsole $"In Actor.Populate(), got differences."

                            // If there are any differences - there likely are - get the RepositoryDto so we can get download url's.
                            let! repositoryDto =
                                task {
                                    if differences.Count > 0 then
                                        let repositoryActorProxy = Repository.CreateActorProxy repositoryId1 correlationId

                                        let! repositoryDtoFromActor = repositoryActorProxy.Get correlationId
                                        return repositoryDtoFromActor
                                    else
                                        return RepositoryDto.Default
                                }

                            /// Gets a Stream for a given RelativePath.
                            let getFileStream (graceIndex: ServerGraceIndex) (relativePath: RelativePath) (repositoryDto: RepositoryDto) =
                                task {
                                    let relativeDirectoryPath = getRelativeDirectory relativePath Constants.RootDirectoryPath
                                    //logToConsole $"In DiffActor.getFileStream(); relativePath: {relativePath}; relativeDirectoryPath: {relativeDirectoryPath}; graceIndex.Count: {graceIndex.Count}."
                                    let directory = graceIndex[relativeDirectoryPath]
                                    let fileVersion = directory.Files.First(fun f -> f.RelativePath = relativePath)
                                    let! uri = getReadSharedAccessSignature repositoryDto fileVersion correlationId
                                    let! stream = this.getFileStream fileVersion (Result.get uri) correlationId
                                    return (stream, fileVersion)
                                }

                            // Process each difference.
                            let fileDiffs = ConcurrentBag<FileDiff>()

                            do!
                                Parallel.ForEachAsync(
                                    differences,
                                    Constants.ParallelOptions,
                                    (fun difference ct ->
                                        ValueTask(
                                            task {
                                                match difference.DifferenceType with
                                                | Change ->
                                                    // This is the only case that we need to generate file diffs for.
                                                    match difference.FileSystemEntryType with
                                                    | Directory -> () // Might have to revisit this.
                                                    | File ->
                                                        // Get streams for both file versions.
                                                        let! (fileStream1, fileVersion1) = getFileStream graceIndex1 difference.RelativePath repositoryDto

                                                        let! (fileStream2, fileVersion2) = getFileStream graceIndex2 difference.RelativePath repositoryDto

                                                        // Compare the streams using DiffPlex, and get the Inline and Side-by-Side diffs.
                                                        let! diffResults =
                                                            task {
                                                                let (fv1, fv2) = (fileVersion1, fileVersion2)

                                                                if createdAt1.CompareTo(createdAt2) < 0 then
                                                                    return! diffTwoFiles fileStream1 fileStream2
                                                                else
                                                                    return! diffTwoFiles fileStream2 fileStream1
                                                            }

                                                        if not <| isNull (fileStream1) then do! fileStream1.DisposeAsync()

                                                        if not <| isNull (fileStream2) then do! fileStream2.DisposeAsync()

                                                        // Create a FileDiff with the DiffPlex results and corresponding Sha256Hash values.
                                                        let fileDiff =
                                                            if createdAt1.CompareTo(createdAt2) < 0 then
                                                                FileDiff.Create
                                                                    fileVersion1.RelativePath
                                                                    fileVersion1.Sha256Hash
                                                                    fileVersion1.CreatedAt
                                                                    fileVersion2.Sha256Hash
                                                                    fileVersion2.CreatedAt
                                                                    (fileVersion1.IsBinary || fileVersion2.IsBinary)
                                                                    diffResults.InlineDiff
                                                                    diffResults.SideBySideOld
                                                                    diffResults.SideBySideNew
                                                            else
                                                                FileDiff.Create
                                                                    fileVersion1.RelativePath
                                                                    fileVersion2.Sha256Hash
                                                                    fileVersion1.CreatedAt
                                                                    fileVersion1.Sha256Hash
                                                                    fileVersion2.CreatedAt
                                                                    (fileVersion1.IsBinary || fileVersion2.IsBinary)
                                                                    diffResults.InlineDiff
                                                                    diffResults.SideBySideOld
                                                                    diffResults.SideBySideNew

                                                        fileDiffs.Add(fileDiff)
                                                | Add -> ()
                                                | Delete -> ()
                                            }
                                        ))
                                )

                            diffDto.FileDiffs.AddRange(fileDiffs.ToArray())

                            diffDto <-
                                { diffDto with
                                    HasDifferences = differences.Count <> 0
                                    DirectoryId1 = directoryId1
                                    Directory1CreatedAt = createdAt1
                                    DirectoryId2 = directoryId2
                                    Directory2CreatedAt = createdAt2
                                    Differences = differences }

                            do! Storage.SaveState stateManager dtoStateName diffDto

                            this.setDeleteReminder ()
                            return true
                        with ex ->
                            logToConsole $"Exception in DiffActor.Compute(): {createExceptionResponse ex}"
                            logToConsole $"directoryId1: {directoryId1}; directoryId2: {directoryId2}"

                            Activity.Current
                                .SetStatus(ActivityStatusCode.Error, "Exception while creating diff.")
                                .AddTag("ex.Message", ex.Message)
                                .AddTag("ex.StackTrace", ex.StackTrace)

                                .AddTag("directoryId1", $"{directoryId1}")
                                .AddTag("directoryId2", $"{directoryId2}")
                            |> ignore

                            return false
                    }

            member this.GetDiff correlationId =
                task {
                    this.correlationId <- correlationId

                    if diffDto.DirectoryId1.Equals(DiffDto.Default.DirectoryId1) then
                        //logToConsole $"In Actor.GetDiff(), not yet populated."
                        let! populated = (this :> IDiffActor).Compute correlationId
                        //logToConsole $"In Actor.GetDiff(), now populated."
                        return diffDto
                    else
                        //logToConsole $"In Actor.GetDiff(), already populated."
                        return diffDto
                }

        interface IRemindable with
            member this.ReceiveReminderAsync(reminderName, state, dueTime, period) =
                match reminderName with
                | ReminderType.DeleteCachedState ->
                    let stateManager = this.StateManager

                    task {
                        return! Storage.DeleteState stateManager dtoStateName
                    }
                    :> Task
                | _ -> Task.CompletedTask
