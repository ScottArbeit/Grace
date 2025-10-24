namespace Grace.Actors

open Azure.Storage.Blobs
open Azure.Storage.Blobs.Specialized
open DiffPlex
open DiffPlex.DiffBuilder.Model
open FSharpPlus
open Grace.Actors
open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Shared
open Grace.Shared.Diff
open Grace.Types.Reminder
open Grace.Types.Repository
open Grace.Types.Diff
open Grace.Types.Types
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Linq
open System.IO
open System.IO.Compression
open System.Threading.Tasks

module Diff =

    type DiffActor([<PersistentState(StateName.Diff, Constants.GraceObjectStorage)>] state: IPersistentState<DiffDto>) =
        inherit Grain()

        static let actorName = ActorName.Diff

        let log = loggerFactory.CreateLogger("Diff.Actor")

        let mutable diffDto: DiffDto = DiffDto.Default

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

        /// Deconstructs an ActorId of the form "{directoryVersionId1}*{directoryVersionId2}" into a tuple of the two DirectoryId values.
        let deconstructActorId (primaryKey: string) =
            let directoryIds = primaryKey.Split("*")
            (DirectoryVersionId directoryIds[0], DirectoryVersionId directoryIds[1])

        member val private correlationId: CorrelationId = String.Empty with get, set

        /// Builds a ServerGraceIndex from a root DirectoryId.
        member private this.buildGraceIndex (directoryId: DirectoryVersionId) correlationId =
            task {
                this.correlationId <- correlationId
                let graceIndex = ServerGraceIndex()

                let directory = orleansClient.CreateActorProxyWithCorrelationId<IDirectoryVersionActor>(directoryId, correlationId)

                let! directoryCreatedAt = directory.GetCreatedAt correlationId
                let! directoryContents = directory.GetRecursiveDirectoryVersions false correlationId
                //logToConsole $"In DiffActor.buildGraceIndex(): directoryContents.Count: {directoryContents.Count}"
                for directoryVersion in directoryContents do
                    graceIndex.TryAdd(directoryVersion.RelativePath, directoryVersion) |> ignore

                return (graceIndex, directoryCreatedAt, directoryContents[0].RepositoryId)
            }

        /// Gets a Stream from object storage for a specific FileVersion, using a generated Uri.
        member private this.getUncompressedStream (repositoryDto: RepositoryDto) (fileVersion: FileVersion) (url: UriWithSharedAccessSignature) correlationId =
            task {
                this.correlationId <- correlationId
                let objectStorageProvider = repositoryDto.ObjectStorageProvider

                match objectStorageProvider with
                | AWSS3 -> return new MemoryStream() :> Stream
                | AzureBlobStorage ->
                    let blobClient = BlockBlobClient(url)
                    let! fileStream = blobClient.OpenReadAsync(position = 0, bufferSize = (64 * 1024))

                    let uncompressedStream =
                        if fileVersion.IsBinary then
                            fileStream
                        else
                            let gzStream = new GZipStream(stream = fileStream, mode = CompressionMode.Decompress, leaveOpen = false)
                            gzStream :> Stream

                    return uncompressedStream
                | GoogleCloudStorage -> return new MemoryStream() :> Stream
                | ObjectStorageProvider.Unknown -> return new MemoryStream() :> Stream
            }

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()
            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            Task.CompletedTask

        member private this.GetDiffSimple() =
            if diffDto.DirectoryVersionId1 = DiffDto.Default.DirectoryVersionId1 then
                None |> returnValueTask
            else
                Some diffDto |> returnValueTask

        interface IDiffActor with
            /// Sets a Grace reminder to perform a physical deletion of this actor.
            member this.ScheduleReminderAsync reminderType delay state correlationId =
                task {
                    let reminder =
                        ReminderDto.Create
                            actorName
                            $"{this.IdentityString}"
                            diffDto.OwnerId
                            diffDto.OrganizationId
                            diffDto.RepositoryId
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
                    | ReminderTypes.DeleteCachedState, ReminderState.DiffDeleteCachedState deleteCachedStateReminderState ->
                        this.correlationId <- deleteCachedStateReminderState.CorrelationId

                        // Delete saved state for this actor.
                        do! state.ClearStateAsync()

                        log.LogInformation(
                            "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Deleted cache for diff; RepositoryId: {RepositoryId}; DirectoryVersionId1: {DirectoryVersionId1}; DirectoryVersionId2: {DirectoryVersionId2}; deleteReason: {deleteReason}.",
                            getCurrentInstantExtended (),
                            getMachineName,
                            deleteCachedStateReminderState.CorrelationId,
                            diffDto.RepositoryId,
                            diffDto.DirectoryVersionId1,
                            diffDto.DirectoryVersionId2,
                            deleteCachedStateReminderState.DeleteReason
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

            member this.Compute correlationId =
                this.correlationId <- correlationId

                // If it's already populated, skip this.
                if diffDto.DirectoryVersionId1 <> DiffDto.Default.DirectoryVersionId1 then
                    (true |> returnTask)
                else
                    task {
                        let (directoryVersionId1, directoryVersionId2) = deconstructActorId ($"{this.GetGrainId().Key}")

                        logToConsole $"In DiffActor.Populate(); DirectoryVersionId1: {directoryVersionId1}; DirectoryVersionId2: {directoryVersionId2}"

                        try
                            // Build a GraceIndex for each DirectoryId.
                            let! (graceIndex1, createdAt1, repositoryId1) = this.buildGraceIndex directoryVersionId1 correlationId

                            let! (graceIndex2, createdAt2, repositoryId2) = this.buildGraceIndex directoryVersionId2 correlationId
                            //logToConsole $"In DiffActor.Populate(); createdAt1: {createdAt1}; createdAt2: {createdAt2}."

                            // Compare the GraceIndices.
                            let! differences =
                                task {
                                    if createdAt1.CompareTo(createdAt2) > 0 then
                                        return! scanForDifferences graceIndex1 graceIndex2
                                    else
                                        return! scanForDifferences graceIndex2 graceIndex1
                                }

                            //logToConsole $"In Actor.Populate(); got differences."

                            let organizationId = RequestContext.Get(nameof OrganizationId) :?> OrganizationId
                            let repositoryActorProxy = Repository.CreateActorProxy organizationId repositoryId1 correlationId
                            let! repositoryDto = repositoryActorProxy.Get correlationId

                            /// Gets a Stream for a given RelativePath.
                            let getFileStream (graceIndex: ServerGraceIndex) (relativePath: RelativePath) (repositoryDto: RepositoryDto) =
                                task {
                                    let relativeDirectoryPath = getRelativeDirectory relativePath Constants.RootDirectoryPath
                                    //logToConsole $"In DiffActor.getFileStream(); relativePath: {relativePath}; relativeDirectoryPath: {relativeDirectoryPath}; graceIndex.Count: {graceIndex.Count}."
                                    let directory = graceIndex[relativeDirectoryPath]
                                    let fileVersion = directory.Files.First(fun f -> f.RelativePath = relativePath)

                                    let! uri = getUriWithReadSharedAccessSignatureForFileVersion repositoryDto fileVersion correlationId
                                    let! uncompressedStream = this.getUncompressedStream repositoryDto fileVersion uri correlationId
                                    return Ok(uncompressedStream, fileVersion)
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
                                                        let! result1 = getFileStream graceIndex1 difference.RelativePath repositoryDto

                                                        let! result2 = getFileStream graceIndex2 difference.RelativePath repositoryDto

                                                        match (result1, result2) with
                                                        | (Ok(fileStream1, fileVersion1), Ok(fileStream2, fileVersion2)) ->
                                                            try
                                                                // Compare the streams using DiffPlex, and get the Inline and Side-by-Side diffs.
                                                                let! diffResults =
                                                                    task {
                                                                        if createdAt1.CompareTo(createdAt2) < 0 then
                                                                            return! diffTwoFiles fileStream1 fileStream2
                                                                        else
                                                                            return! diffTwoFiles fileStream2 fileStream1
                                                                    }

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
                                                            finally
                                                                if not <| isNull fileStream1 then fileStream1.Dispose()
                                                                if not <| isNull fileStream2 then fileStream2.Dispose()
                                                        | (Error ex, _) -> raise ex
                                                        | (_, Error ex) -> raise ex
                                                | Add -> ()
                                                | Delete -> ()
                                            }
                                        ))
                                )

                            diffDto.FileDiffs.AddRange(fileDiffs.ToArray())

                            diffDto <-
                                { diffDto with
                                    HasDifferences = differences.Count <> 0
                                    RepositoryId = repositoryDto.RepositoryId
                                    DirectoryVersionId1 = directoryVersionId1
                                    Directory1CreatedAt = createdAt1
                                    DirectoryVersionId2 = directoryVersionId2
                                    Directory2CreatedAt = createdAt2
                                    Differences = differences }

                            state.State <- diffDto
                            do! state.WriteStateAsync()

                            let (deleteCachedStateReminderState: DeleteCachedStateReminderState) =
                                { DeleteReason = getDiscriminatedUnionCaseName ReminderTypes.DeleteCachedState; CorrelationId = correlationId }

                            do!
                                (this :> IDiffActor).ScheduleReminderAsync
                                    ReminderTypes.DeleteCachedState
                                    (Duration.FromDays(float repositoryDto.DiffCacheDays))
                                    (ReminderState.DiffDeleteCachedState deleteCachedStateReminderState)
                                    correlationId

                            return true
                        with ex ->
                            logToConsole $"Exception in DiffActor.Compute(): {ExceptionResponse.Create ex}"
                            logToConsole $"directoryVersionId1: {directoryVersionId1}; directoryVersionId2: {directoryVersionId2}"

                            Activity.Current
                                .SetStatus(ActivityStatusCode.Error, "Exception while creating diff.")
                                .AddTag("ex.Message", ex.Message)
                                .AddTag("ex.StackTrace", ex.StackTrace)

                                .AddTag("directoryVersionId1", $"{directoryVersionId1}")
                                .AddTag("directoryVersionId2", $"{directoryVersionId2}")
                            |> ignore

                            return false
                    }

            member this.GetDiff correlationId =
                task {
                    this.correlationId <- correlationId

                    if diffDto.DirectoryVersionId1.Equals(DiffDto.Default.DirectoryVersionId1) then
                        logToConsole $"In Actor.GetDiff(), not yet populated."
                        let! populated = (this :> IDiffActor).Compute correlationId
                        logToConsole $"In Actor.GetDiff(), now populated."
                        return diffDto
                    else
                        logToConsole $"In Actor.GetDiff(), already populated."
                        return diffDto
                }
