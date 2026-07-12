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
open Grace.Types.Common
open Grace.Types.ContentBlockMetadata
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
open Azure.Storage

/// Groups Orleans actor helpers for directory version keys, proxies, state, or workflow transitions.
module DirectoryVersion =

    /// Result of validating a single whole-file content reference.
    type FileValidationResult =
        | Valid of fileVersion: FileVersion * computedSha256Hash: Sha256Hash * computedBlake3Hash: Blake3Hash * elapsedMs: float
        | HashMismatch of
            fileVersion: FileVersion *
            expectedSha256Hash: Sha256Hash *
            computedSha256Hash: Sha256Hash *
            expectedBlake3Hash: Blake3Hash *
            computedBlake3Hash: Blake3Hash *
            elapsedMs: float
        | MissingInStorage of fileVersion: FileVersion * elapsedMs: float
        | ValidationError of fileVersion: FileVersion * errorMessage: string * elapsedMs: float

    /// Validates recursive directory versions complete before the operation continues.
    let validateRecursiveDirectoryVersionsComplete rootDirectoryVersionId (directoryVersionDtos: DirectoryVersionDto seq) correlationId =
        let directoryVersionDtoArray = directoryVersionDtos |> Seq.toArray

        let directoryVersionIds =
            HashSet<DirectoryVersionId>(
                directoryVersionDtoArray
                |> Seq.map (fun directoryVersionDto -> directoryVersionDto.DirectoryVersion.DirectoryVersionId)
            )

        if not (directoryVersionIds.Contains rootDirectoryVersionId) then
            Error(
                (GraceError.Create "Recursive directory traversal did not include the root DirectoryVersion." correlationId)
                    .enhance (nameof DirectoryVersionId, rootDirectoryVersionId)
            )
        else
            let missingChild =
                directoryVersionDtoArray
                |> Seq.collect (fun directoryVersionDto ->
                    directoryVersionDto.DirectoryVersion.Directories
                    |> Seq.map (fun childDirectoryVersionId -> directoryVersionDto.DirectoryVersion, childDirectoryVersionId))
                |> Seq.tryFind (fun (_, childDirectoryVersionId) -> not (directoryVersionIds.Contains childDirectoryVersionId))

            match missingChild with
            | Some (parentDirectoryVersion, childDirectoryVersionId) ->
                Error(
                    (GraceError.Create "Recursive directory traversal did not include a declared child DirectoryVersion." correlationId)
                        .enhance(nameof DirectoryVersionId, rootDirectoryVersionId)
                        .enhance("ParentDirectoryVersionId", parentDirectoryVersion.DirectoryVersionId)
                        .enhance ("ChildDirectoryVersionId", childDirectoryVersionId)
                )
            | None -> Ok directoryVersionDtoArray

    /// Validates a single file's version hashes by downloading from storage and computing.
    /// Note: Non-binary files are stored as GZip-compressed streams, so we need to decompress them first.
    let validateFileSha256 (repositoryDto: RepositoryDto) (fileVersion: FileVersion) (correlationId: CorrelationId) =
        task {
            let stopwatch = Stopwatch.StartNew()

            try
                let! blobClient = getReadableAzureBlobClientForFileVersion repositoryDto fileVersion correlationId
                let! existsResponse = blobClient.ExistsAsync()

                if not existsResponse.Value then
                    stopwatch.Stop()
                    return MissingInStorage(fileVersion, stopwatch.Elapsed.TotalMilliseconds)
                else
                    /// Downloads a directory-version blob and computes the hashes recorded for validation.
                    let computeHashesFromBlob () =
                        task {
                            use! blobStream = blobClient.OpenReadAsync(position = 0, bufferSize = (64 * 1024))

                            if fileVersion.IsBinary then
                                return! computeHashesForFile blobStream fileVersion.RelativePath
                            else
                                use gzStream = new GZipStream(stream = blobStream, mode = CompressionMode.Decompress, leaveOpen = false)
                                return! computeHashesForFile gzStream fileVersion.RelativePath
                        }

                    let! computedSha256Hash, computedBlake3Hash = computeHashesFromBlob ()

                    stopwatch.Stop()

                    if computedSha256Hash = fileVersion.Sha256Hash
                       && not (String.IsNullOrWhiteSpace fileVersion.Blake3Hash)
                       && computedBlake3Hash = fileVersion.Blake3Hash then
                        return Valid(fileVersion, computedSha256Hash, computedBlake3Hash, stopwatch.Elapsed.TotalMilliseconds)
                    else
                        return
                            HashMismatch(
                                fileVersion,
                                fileVersion.Sha256Hash,
                                computedSha256Hash,
                                fileVersion.Blake3Hash,
                                computedBlake3Hash,
                                stopwatch.Elapsed.TotalMilliseconds
                            )
            with
            | ex ->
                stopwatch.Stop()
                return ValidationError(fileVersion, ex.Message, stopwatch.Elapsed.TotalMilliseconds)
        }

    /// Coordinates normalize content reference logic for the DirectoryVersion actor.
    let private normalizeContentReference (fileVersion: FileVersion) =
        if isNull (box fileVersion.ContentReference) then
            FileContentReference.WholeFileContent
        else
            fileVersion.ContentReference

    /// Determines which files need validation by comparing with a previously validated DirectoryVersion.
    /// Returns the list of files that need to be validated.
    let getFilesToValidate (newFiles: List<FileVersion>) (previouslyValidatedFiles: List<FileVersion>) : FileVersion array =
        if previouslyValidatedFiles.Count > 0 then
            /// Builds the identity tuple used to compare file content references across directory versions.
            let contentReferenceIdentity (fileVersion: FileVersion) =
                let contentReference = normalizeContentReference fileVersion

                match contentReference.ReferenceType, contentReference.Manifest with
                | FileContentReferenceType.FileManifest, Some manifest -> $"{contentReference.ReferenceType}:{manifest.ManifestAddress}"
                | referenceType, _ -> $"{referenceType}"

            /// Builds the identity tuple used to compare file paths and content references.
            let fileIdentity (fileVersion: FileVersion) =
                (fileVersion.RelativePath, fileVersion.Sha256Hash, fileVersion.Blake3Hash, contentReferenceIdentity fileVersion)

            let previousFilesLookup = HashSet<RelativePath * Sha256Hash * Blake3Hash * string>()

            previouslyValidatedFiles
            |> Seq.iter (fun previousFile ->
                previousFilesLookup.Add(fileIdentity previousFile)
                |> ignore)

            // Return files that are not in the old set (new or changed)
            newFiles
                .Where(fun f -> not (previousFilesLookup.Contains(fileIdentity f)))
                .ToArray()
        else
            newFiles.ToArray()

    /// Checks whether a file version stores whole-file content instead of manifest-backed content.
    let private isWholeFileContentReference (fileVersion: FileVersion) =
        (normalizeContentReference fileVersion)
            .ReferenceType = FileContentReferenceType.WholeFileContent

    /// Selects files whose content references still need save-boundary validation.
    let getFilesToValidateForSaveBoundary (newFiles: List<FileVersion>) (previouslyValidatedFiles: List<FileVersion>) : FileVersion array =
        getFilesToValidate newFiles previouslyValidatedFiles
        |> Array.filter isWholeFileContentReference

    /// Coordinates directory version hash error logic for the DirectoryVersion actor.
    let private directoryVersionHashError correlationId (directoryVersion: DirectoryVersion) message =
        GraceError.Create $"DirectoryVersion '{directoryVersion.RelativePath}' {message}" correlationId

    /// Checks whether legacy file metadata is missing one of the expected hashes.
    let private hasMissingHash (value: string) = String.IsNullOrWhiteSpace value

    /// Normalizes directory-version children and files before save-boundary validation.
    let normalizeDirectoryVersionForSaveBoundary (directoryVersion: DirectoryVersion) =
        if directoryVersion.Size
           <> Constants.InitialDirectorySize then
            directoryVersion
        else
            let normalizedDirectoryVersion = DirectoryVersion()
            normalizedDirectoryVersion.Class <- directoryVersion.Class
            normalizedDirectoryVersion.DirectoryVersionId <- directoryVersion.DirectoryVersionId
            normalizedDirectoryVersion.OwnerId <- directoryVersion.OwnerId
            normalizedDirectoryVersion.OrganizationId <- directoryVersion.OrganizationId
            normalizedDirectoryVersion.RepositoryId <- directoryVersion.RepositoryId
            normalizedDirectoryVersion.RelativePath <- directoryVersion.RelativePath
            normalizedDirectoryVersion.Sha256Hash <- directoryVersion.Sha256Hash
            normalizedDirectoryVersion.Blake3Hash <- directoryVersion.Blake3Hash
            normalizedDirectoryVersion.Directories <- directoryVersion.Directories
            normalizedDirectoryVersion.Files <- directoryVersion.Files
            normalizedDirectoryVersion.Size <- getDirectorySize directoryVersion.Files
            normalizedDirectoryVersion.CreatedAt <- directoryVersion.CreatedAt
            normalizedDirectoryVersion.HashesValidated <- directoryVersion.HashesValidated
            normalizedDirectoryVersion

    /// Computes directory hashes from child directory-version and file hashes.
    let computeDirectoryVersionHashesFromChildren (relativePath: RelativePath) (childDirectoryVersions: seq<DirectoryVersion>) (files: seq<FileVersion>) =
        let directoryEntries =
            childDirectoryVersions
            |> Seq.map (fun directoryVersion ->
                DirectoryVersionPreimageEntry.Directory
                    directoryVersion.RelativePath
                    directoryVersion.Size
                    directoryVersion.Blake3Hash
                    directoryVersion.Sha256Hash)

        let fileEntries =
            files
            |> Seq.map (fun fileVersion ->
                DirectoryVersionPreimageEntry.File fileVersion.RelativePath fileVersion.Size fileVersion.Blake3Hash fileVersion.Sha256Hash)

        let entries =
            Seq.append directoryEntries fileEntries
            |> Seq.toArray

        computeSha256ForDirectoryEntries relativePath entries, computeBlake3ForDirectory relativePath entries

    let validateDirectoryVersionHashesWithChildrenAndPreviousFiles
        correlationId
        (directoryVersion: DirectoryVersion)
        (childDirectoryVersions: seq<DirectoryVersion>)
        (previouslyValidatedFiles: FileVersion seq)
        =
        let mutable error: GraceError option = None
        let childDirectoryVersions = childDirectoryVersions |> Seq.toArray
        let previouslyValidatedFiles = previouslyValidatedFiles |> Seq.toArray

        if hasMissingHash directoryVersion.Sha256Hash then
            error <- Some(directoryVersionHashError correlationId directoryVersion "must include DirectoryVersion.Sha256Hash before Save.")
        elif hasMissingHash directoryVersion.Blake3Hash then
            error <- Some(directoryVersionHashError correlationId directoryVersion "must include DirectoryVersion.Blake3Hash before Save.")
        else
            let mutable childIndex = 0

            while childIndex < childDirectoryVersions.Length
                  && error.IsNone do
                let childDirectoryVersion = childDirectoryVersions[childIndex]

                if hasMissingHash childDirectoryVersion.Sha256Hash then
                    error <-
                        Some(
                            directoryVersionHashError
                                correlationId
                                directoryVersion
                                $"has child directory '{childDirectoryVersion.RelativePath}' without Sha256Hash."
                        )
                elif hasMissingHash childDirectoryVersion.Blake3Hash then
                    error <-
                        Some(
                            directoryVersionHashError
                                correlationId
                                directoryVersion
                                $"has child directory '{childDirectoryVersion.RelativePath}' without Blake3Hash."
                        )

                childIndex <- childIndex + 1

            let mutable fileIndex = 0

            while fileIndex < directoryVersion.Files.Count
                  && error.IsNone do
                let fileVersion = directoryVersion.Files[fileIndex]

                if hasMissingHash fileVersion.Sha256Hash then
                    error <- Some(directoryVersionHashError correlationId directoryVersion $"has child file '{fileVersion.RelativePath}' without Sha256Hash.")
                elif hasMissingHash fileVersion.Blake3Hash then
                    error <- Some(directoryVersionHashError correlationId directoryVersion $"has child file '{fileVersion.RelativePath}' without Blake3Hash.")

                fileIndex <- fileIndex + 1

        match error with
        | Some error -> Error error
        | None ->
            /// Coordinates expected sha256 hash logic for the DirectoryVersion actor.
            let expectedSha256Hash, expectedBlake3Hash =
                computeDirectoryVersionHashesFromChildren directoryVersion.RelativePath childDirectoryVersions directoryVersion.Files

            if expectedSha256Hash <> directoryVersion.Sha256Hash then
                Error(
                    directoryVersionHashError
                        correlationId
                        directoryVersion
                        $"has mismatched Sha256Hash. Expected {expectedSha256Hash}; received {directoryVersion.Sha256Hash}."
                )
            elif expectedBlake3Hash <> directoryVersion.Blake3Hash then
                Error(
                    directoryVersionHashError
                        correlationId
                        directoryVersion
                        $"has mismatched Blake3Hash. Expected {expectedBlake3Hash}; received {directoryVersion.Blake3Hash}."
                )
            else
                Ok()

    /// Validates directory version hashes with children before the operation continues.
    let validateDirectoryVersionHashesWithChildren correlationId (directoryVersion: DirectoryVersion) (childDirectoryVersions: seq<DirectoryVersion>) =
        validateDirectoryVersionHashesWithChildrenAndPreviousFiles correlationId directoryVersion childDirectoryVersions Array.empty<FileVersion>

    /// Returns child directory versions for validation data from the DirectoryVersion actor state or related storage.
    let private getChildDirectoryVersionsForValidation repositoryId correlationId (directoryVersion: DirectoryVersion) =
        task {
            let childDirectoryVersions = ResizeArray<DirectoryVersion>()
            let mutable error: GraceError option = None
            let mutable childIndex = 0

            while childIndex < directoryVersion.Directories.Count
                  && error.IsNone do
                let childDirectoryId = directoryVersion.Directories[childIndex]
                let childDirectoryActor = DirectoryVersion.CreateActorProxy childDirectoryId repositoryId correlationId
                let! exists = childDirectoryActor.Exists correlationId

                if not exists then
                    error <- Some(directoryVersionHashError correlationId directoryVersion $"references missing child DirectoryVersionId '{childDirectoryId}'.")
                else
                    let! childDirectoryDto = childDirectoryActor.Get correlationId
                    let childDirectoryVersion = normalizeDirectoryVersionForSaveBoundary childDirectoryDto.DirectoryVersion

                    if
                        hasMissingHash childDirectoryVersion.Blake3Hash
                        && not (hasMissingHash childDirectoryVersion.Sha256Hash)
                    then
                        // The child came from an existing actor, so this local DTO can carry the persisted-child marker
                        // that lets parent saves tolerate immutable SHA-only legacy children without relaxing new creates.
                        childDirectoryVersion.HashesValidated <- true

                    childDirectoryVersions.Add childDirectoryVersion

                childIndex <- childIndex + 1

            match error with
            | Some error -> return Error error
            | None -> return Ok(childDirectoryVersions.ToArray())
        }

    /// Coordinates manifest validation error logic for the DirectoryVersion actor.
    let private manifestValidationError correlationId (fileVersion: FileVersion) message =
        GraceError.Create $"FileManifest reference for '{fileVersion.RelativePath}' {message}" correlationId

    /// Validates manifest blocks before the operation continues.
    let private validateManifestBlocks correlationId (fileVersion: FileVersion) (manifest: FileManifest) =
        if
            isNull (box manifest.Blocks)
            || manifest.Blocks.Count = 0
        then
            Error(manifestValidationError correlationId fileVersion "must include at least one ContentBlock before Save.")
        else
            let mutable expectedOffset = 0L
            let mutable index = 0
            let mutable error: GraceError option = None

            while index < manifest.Blocks.Count && error.IsNone do
                let block = manifest.Blocks[index]

                if isNull (box block) then
                    error <- Some(manifestValidationError correlationId fileVersion $"has a null ContentBlock at index {index}.")
                elif not (ContentAddress.isValidAddress block.Address) then
                    error <- Some(manifestValidationError correlationId fileVersion $"has an invalid ContentBlockAddress at index {index}.")
                elif block.Offset <> expectedOffset then
                    error <- Some(manifestValidationError correlationId fileVersion $"must have contiguous ContentBlock offsets before Save.")
                elif block.Size <= 0L then
                    error <- Some(manifestValidationError correlationId fileVersion $"has a non-positive ContentBlock size at index {index}.")
                else
                    expectedOffset <- expectedOffset + block.Size

                index <- index + 1

            match error with
            | Some error -> Error error
            | None when expectedOffset <> manifest.Size ->
                Error(manifestValidationError correlationId fileVersion "must have ContentBlocks that cover the full manifest size before Save.")
            | None -> Ok()

    /// Validates manifest backed file for save boundary before the operation continues.
    let validateManifestBackedFileForSaveBoundary correlationId (fileVersion: FileVersion) (manifest: FileManifest) =
        try
            if isNull (box manifest) then
                Error(manifestValidationError correlationId fileVersion "is required before Save.")
            elif String.IsNullOrWhiteSpace manifest.ManifestAddress then
                Error(manifestValidationError correlationId fileVersion "must be finalized before Save.")
            elif not (ContentAddress.isValidAddress manifest.ManifestAddress) then
                Error(manifestValidationError correlationId fileVersion "has an invalid ManifestAddress before Save.")
            elif String.IsNullOrWhiteSpace manifest.StoragePoolId then
                Error(manifestValidationError correlationId fileVersion "must include a StoragePoolId before Save.")
            elif String.IsNullOrWhiteSpace manifest.ChunkingSuiteId then
                Error(manifestValidationError correlationId fileVersion "must include a ChunkingSuiteId before Save.")
            elif not (ContentAddress.isValidAddress manifest.FileContentHash) then
                Error(manifestValidationError correlationId fileVersion "has an invalid FileContentHash before Save.")
            elif String.IsNullOrWhiteSpace fileVersion.Blake3Hash then
                Error(manifestValidationError correlationId fileVersion "must include FileVersion.Blake3Hash before Save.")
            elif fileVersion.Blake3Hash <> manifest.FileContentHash then
                Error(manifestValidationError correlationId fileVersion "must match FileVersion.Blake3Hash before Save.")
            elif manifest.Size <= 0L then
                Error(manifestValidationError correlationId fileVersion "must have a positive size before Save.")
            elif fileVersion.Size <> manifest.Size then
                Error(manifestValidationError correlationId fileVersion "must match the FileVersion size before Save.")
            else
                validateManifestBlocks correlationId fileVersion manifest
                |> Result.bind (fun () ->
                    let expectedAddress = ContentAddress.computeManifestAddressForManifest manifest

                    if expectedAddress <> manifest.ManifestAddress then
                        Error(
                            manifestValidationError
                                correlationId
                                fileVersion
                                "must have a finalized ManifestAddress that matches its reconstruction contract before Save."
                        )
                    else
                        Ok())
        with
        | ex -> Error(GraceError.CreateWithException ex $"FileManifest reference for '{fileVersion.RelativePath}' is invalid before Save." correlationId)

    /// Wraps manifest reference for save boundary records exchanged by actor queries or projections.
    type ManifestReferenceForSaveBoundary = { Manifest: FileManifest; AuthorizedScope: RelativePath }

    /// Returns manifest references for save boundary data from the DirectoryVersion actor state or related storage.
    let getManifestReferencesForSaveBoundary (directoryVersion: DirectoryVersion) correlationId =
        let manifests = Dictionary<StoragePoolId * ManifestAddress * RelativePath, ManifestReferenceForSaveBoundary>()
        let mutable index = 0
        let mutable error: GraceError option = None

        while index < directoryVersion.Files.Count
              && error.IsNone do
            let fileVersion = directoryVersion.Files[index]
            let contentReference = normalizeContentReference fileVersion

            if contentReference.ReferenceType = FileContentReferenceType.FileManifest then
                match contentReference.Manifest with
                | None -> error <- Some(manifestValidationError correlationId fileVersion "is required before Save.")
                | Some manifest ->
                    match validateManifestBackedFileForSaveBoundary correlationId fileVersion manifest with
                    | Ok () ->
                        let manifestKey = manifest.StoragePoolId, manifest.ManifestAddress, fileVersion.RelativePath

                        if not (manifests.ContainsKey manifestKey) then
                            manifests.Add(manifestKey, { Manifest = manifest; AuthorizedScope = fileVersion.RelativePath })
                    | Error graceError -> error <- Some graceError

            index <- index + 1

        match error with
        | Some graceError -> Error graceError
        | None -> Ok(manifests.Values |> Seq.toList)

    /// Coordinates content block metadata actor key for save boundary logic for the DirectoryVersion actor.
    let contentBlockMetadataActorKeyForSaveBoundary storagePoolId contentBlockAddress = $"{storagePoolId}|{contentBlockAddress}"

    let validateManifestReferencesForSaveBoundaryWithResolver
        (getScopedRangePresence: StoragePoolId
                                     -> RepositoryId
                                     -> RelativePath
                                     -> ManifestAddress
                                     -> ContentBlockAddress
                                     -> ContentBlockRangeQuery
                                     -> Task<ContentBlockRangePresence>)
        repositoryId
        correlationId
        (manifestReferences: ManifestReferenceForSaveBoundary seq)
        =
        task {
            let manifestReferenceArray = manifestReferences |> Seq.toArray
            let mutable manifestIndex = 0
            let mutable error: GraceError option = None

            while manifestIndex < manifestReferenceArray.Length
                  && error.IsNone do
                let manifestReference = manifestReferenceArray[manifestIndex]
                let manifest = manifestReference.Manifest
                let mutable blockIndex = 0

                while blockIndex < manifest.Blocks.Count && error.IsNone do
                    let block = manifest.Blocks[blockIndex]
                    let query: ContentBlockRangeQuery = { OrdinalStart = 0; OrdinalCount = 1 }

                    let! presence =
                        getScopedRangePresence
                            manifest.StoragePoolId
                            repositoryId
                            manifestReference.AuthorizedScope
                            manifest.ManifestAddress
                            block.Address
                            query

                    if presence = ContentBlockRangePresence.Absent then
                        error <-
                            Some(
                                GraceError.Create
                                    $"FileManifest '{manifest.ManifestAddress}' references ContentBlock '{block.Address}' at manifest block index {blockIndex}, but no finalized scoped ContentBlock metadata range exists for repository '{repositoryId}' and authorized scope '{manifestReference.AuthorizedScope}' at content-block ordinal 0."
                                    correlationId
                            )

                    blockIndex <- blockIndex + 1

                manifestIndex <- manifestIndex + 1

            return
                match error with
                | Some graceError -> Error graceError
                | None -> Ok()
        }

    /// Validates manifest references for save boundary before the operation continues.
    let private validateManifestReferencesForSaveBoundary repositoryId correlationId manifestReferences =
        /// Returns scoped range presence data from the DirectoryVersion actor state or related storage.
        let getScopedRangePresence storagePoolId repositoryId authorizedScope manifestAddress contentBlockAddress query =
            task {
                let dedupeIndexActor = orleansClient.CreateActorProxyWithCorrelationId<IDedupeIndexActor>("dedupe-index:v1", correlationId)

                match!
                    dedupeIndexActor.TryGetFinalizedScopedContentBlockMetadata
                        (
                            storagePoolId,
                            repositoryId,
                            authorizedScope,
                            manifestAddress,
                            contentBlockAddress,
                            correlationId
                        )
                    with
                | Some metadata -> return Grace.Types.ContentBlockMetadata.rangePresence metadata query
                | None -> return ContentBlockRangePresence.Absent
            }

        validateManifestReferencesForSaveBoundaryWithResolver getScopedRangePresence repositoryId correlationId manifestReferences

    /// Implements the Orleans grain for directory version actor.
    type DirectoryVersionActor
        (
            [<PersistentState(StateName.DirectoryVersion, Constants.GraceActorStorage)>] state: IPersistentState<List<DirectoryVersionEvent>>
        ) =
        inherit Grain()

        static let actorName = ActorName.DirectoryVersion

        let log = loggerFactory.CreateLogger("DirectoryVersion.Actor")

        let mutable directoryVersionDto = DirectoryVersionDto.Default
        let mutable currentCommand = String.Empty

        /// Gets the name of the blob file that holds the cached recursive directory version list.
        let getRecursiveDirectoryVersionsCacheFileName (directoryVersionId: DirectoryVersionId) = $"{directoryVersionId}.msgpack"

        /// Gets the name of the blob file that holds the .zip file for the directory version.
        let getZipFileBlobName (directoryVersionId: DirectoryVersionId) = $"{GraceZipFilesFolderName}/{directoryVersionId}.zip"

        /// Stores the correlation id used by this actor while reporting timings and errors.
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            // Apply the events to build the current Dto.
            directoryVersionDto <-
                state.State
                |> Seq.fold
                    (fun directoryVersionDto directoryVersionEvent ->
                        directoryVersionDto
                        |> DirectoryVersionDto.UpdateDto directoryVersionEvent)
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

                        let directoryVersion = directoryVersionDto.DirectoryVersion

                        let repositoryActorProxy =
                            Repository.CreateActorProxy directoryVersion.OrganizationId directoryVersion.RepositoryId reminderState.CorrelationId

                        let! repositoryDto = repositoryActorProxy.Get reminderState.CorrelationId

                        // Delete cached state for this actor.
                        let! directoryVersionBlobClient =
                            getAzureBlobClient
                                repositoryDto
                                (getRecursiveDirectoryVersionsCacheFileName directoryVersion.DirectoryVersionId)
                                reminderState.CorrelationId

                        let! deleted = directoryVersionBlobClient.DeleteIfExistsAsync()

                        if deleted.HasValue && deleted.Value then
                            log.LogInformation(
                                "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Deleted cached state for directory version; RepositoryId: {RepositoryId}; DirectoryVersionId: {DirectoryVersionId}; deleteReason: {deleteReason}.",
                                getCurrentInstantExtended (),
                                getMachineName,
                                reminderState.CorrelationId,
                                directoryVersion.RepositoryId,
                                directoryVersion.DirectoryVersionId,
                                reminderState.DeleteReason
                            )
                        else
                            log.LogWarning(
                                "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Failed to delete cached state for directory version (it may have already been deleted); RepositoryId: {RepositoryId}; DirectoryVersionId: {DirectoryVersionId}; deleteReason: {deleteReason}.",
                                getCurrentInstantExtended (),
                                getMachineName,
                                reminderState.CorrelationId,
                                directoryVersion.RepositoryId,
                                directoryVersion.DirectoryVersionId,
                                reminderState.DeleteReason
                            )

                        return Ok()
                    | ReminderTypes.DeleteZipFile, ReminderState.DirectoryVersionDeleteZipFile reminderState ->
                        this.correlationId <- reminderState.CorrelationId

                        let directoryVersion = directoryVersionDto.DirectoryVersion

                        let repositoryActorProxy =
                            Repository.CreateActorProxy directoryVersion.OrganizationId directoryVersion.RepositoryId reminderState.CorrelationId

                        let! repositoryDto = repositoryActorProxy.Get reminderState.CorrelationId

                        // Delete zip file for this directory version.
                        let blobName = getZipFileBlobName directoryVersion.DirectoryVersionId
                        let! zipFileBlobClient = getAzureBlobClient repositoryDto blobName reminderState.CorrelationId

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

        /// Applies one persisted DirectoryVersion event to this activation's in-memory state.
        member private this.ApplyEvent directoryVersionEvent =
            task {
                try
                    // Add the event to the list of events, and save it to actor state.
                    state.State.Add(directoryVersionEvent)
                    do! state.WriteStateAsync()

                    // Update the Dto with the event.
                    directoryVersionDto <-
                        directoryVersionDto
                        |> DirectoryVersionDto.UpdateDto directoryVersionEvent

                    // Publish the event to the rest of the world.
                    let graceEvent = GraceEvent.DirectoryVersionEvent directoryVersionEvent
                    do! publishGraceEvent graceEvent directoryVersionEvent.Metadata

                    let returnValue = GraceReturnValue.Create "Directory version command succeeded." directoryVersionEvent.Metadata.CorrelationId

                    returnValue
                        .enhance(nameof RepositoryId, directoryVersionDto.DirectoryVersion.RepositoryId)
                        .enhance(nameof DirectoryVersionId, directoryVersionDto.DirectoryVersion.DirectoryVersionId)
                        .enhance(nameof Sha256Hash, directoryVersionDto.DirectoryVersion.Sha256Hash)
                        .enhance (nameof DirectoryVersionEventType, getDiscriminatedUnionFullName directoryVersionEvent.Event)
                    |> ignore

                    return Ok returnValue
                with
                | ex ->
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
            /// Returns the repository id recorded in this DirectoryVersion actor state.
            member this.GetRepositoryId correlationId =
                directoryVersionDto.DirectoryVersion.RepositoryId
                |> returnTask

        interface IDirectoryVersionActor with
            /// Reports whether this DirectoryVersion actor has persisted state.
            member this.Exists correlationId =
                this.correlationId <- correlationId

                (directoryVersionDto.DirectoryVersion.DirectoryVersionId
                 <> DirectoryVersion.Default.DirectoryVersionId)
                |> returnTask

            /// Removes or invalidates delete data from the DirectoryVersion actor state.
            member this.Delete correlationId =
                this.correlationId <- correlationId

                GraceResult.Error(GraceError.Create "Not implemented" correlationId)
                |> returnTask

            /// Returns the current DirectoryVersion actor state snapshot.
            member this.Get correlationId =
                this.correlationId <- correlationId
                directoryVersionDto |> returnTask

            /// Returns the creation timestamp recorded for this directory version.
            member this.GetCreatedAt correlationId =
                this.correlationId <- correlationId

                directoryVersionDto.DirectoryVersion.CreatedAt
                |> returnTask

            /// Returns child directory-version references recorded by this directory version.
            member this.GetDirectories correlationId =
                this.correlationId <- correlationId

                directoryVersionDto.DirectoryVersion.Directories
                |> returnTask

            /// Returns file-version entries recorded by this directory version.
            member this.GetFiles correlationId =
                this.correlationId <- correlationId

                directoryVersionDto.DirectoryVersion.Files
                |> returnTask

            /// Returns the SHA-256 directory hash recorded by this directory version.
            member this.GetSha256Hash correlationId =
                this.correlationId <- correlationId

                directoryVersionDto.DirectoryVersion.Sha256Hash
                |> returnTask

            /// Returns the aggregate byte size recorded for this directory version.
            member this.GetSize correlationId =
                this.correlationId <- correlationId

                directoryVersionDto.DirectoryVersion.Size
                |> returnTask

            /// Traverses child directory versions and returns the recursive directory tree.
            member this.GetRecursiveDirectoryVersions (forceRegenerate: bool) correlationId =
                this.correlationId <- correlationId

                task {
                    try
                        let directoryVersion = directoryVersionDto.DirectoryVersion
                        let repositoryActorProxy = Repository.CreateActorProxy directoryVersion.OrganizationId directoryVersion.RepositoryId correlationId
                        let! repositoryDto = repositoryActorProxy.Get correlationId

                        // Get the blob client for the cached recursive directory versions file.
                        let! directoryVersionBlobClient =
                            getAzureBlobClient
                                repositoryDto
                                (getRecursiveDirectoryVersionsCacheFileName directoryVersionDto.DirectoryVersion.DirectoryVersionId)
                                correlationId

                        // Check if the subdirectory versions have already been generated and cached.
                        let cachedSubdirectoryVersions =
                            task {
                                if not forceRegenerate
                                   && directoryVersionBlobClient.Exists() then
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

                            let subdirectoryVersionDtos = ConcurrentDictionary<RelativePath, DirectoryVersionDto>()

                            log.LogTrace(
                                "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; In DirectoryVersionActor.GetRecursiveDirectoryVersions({id}): Adding current directory version. RelativePath: {relativePath}",
                                getCurrentInstantExtended (),
                                getMachineName,
                                correlationId,
                                this.GetPrimaryKey(),
                                directoryVersionDto.DirectoryVersion.RelativePath
                            )

                            // First, add the current directory version to the dictionary.
                            subdirectoryVersionDtos.TryAdd(directoryVersionDto.DirectoryVersion.RelativePath, directoryVersionDto)
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

                                                    // Get the contents of the subdirectory itself.
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

                                                    // Get the full recursive contents of the subdirectory.
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

                                                        subdirectoryVersionDtos.AddOrUpdate(
                                                            directoryVersion.RelativePath,
                                                            directoryVersionDto,
                                                            (fun _ _ -> directoryVersionDto)
                                                        )
                                                        |> ignore
                                                with
                                                | ex ->
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
                                subdirectoryVersionDtos
                                    .Values
                                    .OrderBy(fun directoryVersionDto -> directoryVersionDto.DirectoryVersion.RelativePath)
                                    .ToArray()

                            match validateRecursiveDirectoryVersionsComplete directoryVersion.DirectoryVersionId subdirectoryVersionsList correlationId with
                            | Error graceError ->
                                log.LogWarning(
                                    "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; In DirectoryVersionActor.GetRecursiveDirectoryVersions({id}): Skipping recursive directory version cache write because traversal was incomplete. Error: {error}",
                                    getCurrentInstantExtended (),
                                    getMachineName,
                                    correlationId,
                                    this.GetPrimaryKey(),
                                    graceError
                                )
                            | Ok completeSubdirectoryVersionsList ->
                                // Save the recursive results to Azure Blob Storage only after completeness is known.
                                let repositoryActorProxy =
                                    Repository.CreateActorProxy
                                        directoryVersionDto.DirectoryVersion.OrganizationId
                                        directoryVersionDto.DirectoryVersion.RepositoryId
                                        correlationId

                                let! repositoryDto = repositoryActorProxy.Get correlationId

                                let tags = Dictionary<string, string>()
                                tags.Add(nameof OwnerId, $"{repositoryDto.OwnerId}")
                                tags.Add(nameof OrganizationId, $"{repositoryDto.OrganizationId}")
                                tags.Add(nameof RepositoryId, $"{repositoryDto.RepositoryId}")
                                tags.Add(nameof DirectoryVersionId, $"{directoryVersionDto.DirectoryVersion.DirectoryVersionId}")
                                tags.Add(nameof RelativePath, $"{directoryVersionDto.DirectoryVersion.RelativePath}")
                                tags.Add(nameof Sha256Hash, $"{directoryVersionDto.DirectoryVersion.Sha256Hash}")
                                tags.Add("RecursiveSize", $"{directoryVersionDto.RecursiveSize}")

                                // Write the JSON using MessagePack serialization for efficiency.
                                let blockBlobOpenWriteOptions =
                                    BlockBlobOpenWriteOptions(Tags = tags, HttpHeaders = BlobHttpHeaders(ContentType = "application/msgpack"))

                                let conditionsSummary =
                                    let conditionsProperty = typeof<BlockBlobOpenWriteOptions>.GetProperty ("Conditions")

                                    if isNull conditionsProperty then
                                        "not supported"
                                    else
                                        let conditionsValue = conditionsProperty.GetValue(blockBlobOpenWriteOptions)

                                        if isNull conditionsValue then
                                            "null"
                                        else
                                            let conditionProperties = conditionsValue.GetType().GetProperties()

                                            conditionProperties
                                            |> Seq.map (fun propertyInfo ->
                                                let value = propertyInfo.GetValue(conditionsValue)
                                                $"{propertyInfo.Name}={value}")
                                            |> String.concat "; "

                                log.LogDebug(
                                    "In DirectoryVersionActor.GetRecursiveDirectoryVersions({id}); Blob write conditions: {conditionsSummary}.",
                                    this.GetPrimaryKey(),
                                    conditionsSummary
                                )

                                use! blobStream = directoryVersionBlobClient.OpenWriteAsync(overwrite = true, options = blockBlobOpenWriteOptions)
                                do! MessagePackSerializer.SerializeAsync(blobStream, completeSubdirectoryVersionsList, messagePackSerializerOptions)
                                do! blobStream.DisposeAsync()

                                log.LogDebug(
                                    "In DirectoryVersionActor.GetRecursiveDirectoryVersions({id}); Saving cached list of directory versions. RelativePath: {relativePath}.",
                                    this.GetPrimaryKey(),
                                    directoryVersionDto.DirectoryVersion.RelativePath
                                )

                                // Create a reminder to delete the cached state after the configured number of cache days.
                                let deletionReminderState: PhysicalDeletionReminderState =
                                    { DeleteReason = getDiscriminatedUnionCaseName ReminderTypes.DeleteCachedState; CorrelationId = correlationId }

                                do!
                                    (this :> IGraceReminderWithGuidKey)
                                        .ScheduleReminderAsync
                                        ReminderTypes.DeleteCachedState
                                        (Duration.FromDays(float repositoryDto.DirectoryVersionCacheDays))
                                        (ReminderState.DirectoryVersionDeleteCachedState deletionReminderState)
                                        correlationId

                                log.LogDebug(
                                    "In DirectoryVersionActor.GetRecursiveDirectoryVersions({id}); Delete cached state reminder was set.",
                                    this.GetPrimaryKey()
                                )

                            return subdirectoryVersionsList
                    with
                    | ex ->
                        log.LogError(
                            "{CurrentInstant}: Error in {methodName}. Exception: {exception}",
                            getCurrentInstantExtended (),
                            "GetRecursiveDirectoryVersions",
                            ExceptionResponse.Create ex
                        )

                        return Array.Empty<DirectoryVersionDto>()
                }

            /// Routes a public actor command to the domain operation that validates and persists it.
            member this.Handle command metadata =
                /// Checks whether command validation succeeded before emitting the domain event.
                let isValid command (metadata: EventMetadata) =
                    task {
                        match command with
                        | DirectoryVersionCommand.Create (directoryVersion, repositoryDto) ->
                            if
                                state.State.Any (fun e ->
                                    match e.Event with
                                    | DirectoryVersionEventType.Created _ -> true
                                    | _ -> false) then
                                return
                                    Error(
                                        GraceError.Create
                                            (DirectoryVersionError.getErrorMessage DirectoryVersionError.DirectoryAlreadyExists)
                                            metadata.CorrelationId
                                    )
                            else
                                match! getChildDirectoryVersionsForValidation repositoryDto.RepositoryId metadata.CorrelationId directoryVersion with
                                | Error graceError -> return Error graceError
                                | Ok childDirectoryVersions ->
                                    let! mostRecentDirectoryVersion =
                                        getMostRecentDirectoryVersionByRelativePath
                                            repositoryDto.RepositoryId
                                            directoryVersion.RelativePath
                                            metadata.CorrelationId

                                    let previouslyValidatedFiles =
                                        match mostRecentDirectoryVersion with
                                        | Some previousDirectoryVersion -> previousDirectoryVersion.Files :> seq<FileVersion>
                                        | None -> Seq.empty

                                    match
                                        validateDirectoryVersionHashesWithChildrenAndPreviousFiles
                                            metadata.CorrelationId
                                            directoryVersion
                                            childDirectoryVersions
                                            previouslyValidatedFiles
                                        with
                                    | Error graceError -> return Error graceError
                                    | Ok () -> return Ok command

                        | _ ->
                            if directoryVersionDto.DirectoryVersion.CreatedAt = DirectoryVersion.Default.CreatedAt then
                                return Error(GraceError.Create (DirectoryVersionError.getErrorMessage DirectoryDoesNotExist) metadata.CorrelationId)
                            else
                                return Ok command
                    }

                /// Runs DirectoryVersion command decisions, applies emitted events, and persists the result.
                let processCommand (command: DirectoryVersionCommand) (metadata: EventMetadata) =
                    task {
                        try
                            let! event =
                                task {
                                    match command with
                                    | Create (directoryVersion, repositoryDto) ->
                                        match getManifestReferencesForSaveBoundary directoryVersion metadata.CorrelationId with
                                        | Error graceError -> return Error graceError
                                        | Ok manifests ->
                                            match! validateManifestReferencesForSaveBoundary repositoryDto.RepositoryId metadata.CorrelationId manifests with
                                            | Error graceError -> return Error graceError
                                            | Ok () ->

                                                // Determine which whole-file entries need validation using incremental validation logic.
                                                // Manifest-backed files are accepted only after their ContentBlock metadata ranges exist.
                                                let! mostRecentDirectoryVersion =
                                                    getMostRecentDirectoryVersionByRelativePath
                                                        repositoryDto.RepositoryId
                                                        directoryVersion.RelativePath
                                                        metadata.CorrelationId

                                                let filesToValidate =
                                                    match mostRecentDirectoryVersion with
                                                    | Some previousDirectoryVersion ->
                                                        getFilesToValidateForSaveBoundary directoryVersion.Files previousDirectoryVersion.Files
                                                    | None -> getFilesToValidateForSaveBoundary directoryVersion.Files (List<FileVersion>())

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
                                                do!
                                                    Parallel.ForEachAsync(
                                                        filesToValidate,
                                                        Constants.ParallelOptions,
                                                        (fun fileVersion ct ->
                                                            ValueTask(
                                                                task {
                                                                    let! result = validateFileSha256 repositoryDto fileVersion metadata.CorrelationId
                                                                    validationResults.Enqueue result
                                                                }
                                                            ))
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
                                                        | Valid (_, _, _, ms) -> ms
                                                        | HashMismatch (_, _, _, _, _, ms) -> ms
                                                        | MissingInStorage (_, ms) -> ms
                                                        | ValidationError (_, _, ms) -> ms)

                                                // Log validation results
                                                for result in validationResults do
                                                    match result with
                                                    | Valid (fv, computedSha256Hash, computedBlake3Hash, elapsedMs) ->
                                                        log.LogDebug(
                                                            "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; file version hash validation passed; File: {RelativePath}; Sha256Hash: {Sha256Hash}; Blake3Hash: {Blake3Hash}; ElapsedMs: {ElapsedMs}.",
                                                            getCurrentInstantExtended (),
                                                            getMachineName,
                                                            metadata.CorrelationId,
                                                            fv.RelativePath,
                                                            computedSha256Hash,
                                                            computedBlake3Hash,
                                                            elapsedMs
                                                        )
                                                    | HashMismatch (fv,
                                                                    expectedSha256Hash,
                                                                    computedSha256Hash,
                                                                    expectedBlake3Hash,
                                                                    computedBlake3Hash,
                                                                    elapsedMs) ->
                                                        log.LogWarning(
                                                            "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; file version hash mismatch; File: {RelativePath}; ExpectedSha256Hash: {ExpectedSha256Hash}; ComputedSha256Hash: {ComputedSha256Hash}; ExpectedBlake3Hash: {ExpectedBlake3Hash}; ComputedBlake3Hash: {ComputedBlake3Hash}; ElapsedMs: {ElapsedMs}.",
                                                            getCurrentInstantExtended (),
                                                            getMachineName,
                                                            metadata.CorrelationId,
                                                            fv.RelativePath,
                                                            expectedSha256Hash,
                                                            computedSha256Hash,
                                                            expectedBlake3Hash,
                                                            computedBlake3Hash,
                                                            elapsedMs
                                                        )
                                                    | MissingInStorage (fv, elapsedMs) ->
                                                        log.LogWarning(
                                                            "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; File not found in object storage; File: {RelativePath}; ExpectedHash: {ExpectedHash}; ElapsedMs: {ElapsedMs}.",
                                                            getCurrentInstantExtended (),
                                                            getMachineName,
                                                            metadata.CorrelationId,
                                                            fv.RelativePath,
                                                            fv.Sha256Hash,
                                                            elapsedMs
                                                        )
                                                    | ValidationError (fv, errorMessage, elapsedMs) ->
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
                                                            | HashMismatch (fv, expectedSha256, computedSha256, expectedBlake3, computedBlake3, _) ->
                                                                $"File '{fv.RelativePath}': hash mismatch (expected SHA-256: {expectedSha256}, computed SHA-256: {computedSha256}, expected BLAKE3: {expectedBlake3}, computed BLAKE3: {computedBlake3})"
                                                            | MissingInStorage (fv, _) -> $"File '{fv.RelativePath}': not found in object storage"
                                                            | ValidationError (fv, msg, _) -> $"File '{fv.RelativePath}': validation error ({msg})"
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

                                                    let newDirectoryVersion = DirectoryVersion()
                                                    newDirectoryVersion.Class <- directoryVersion.Class
                                                    newDirectoryVersion.DirectoryVersionId <- directoryVersion.DirectoryVersionId
                                                    newDirectoryVersion.OwnerId <- directoryVersion.OwnerId
                                                    newDirectoryVersion.OrganizationId <- directoryVersion.OrganizationId
                                                    newDirectoryVersion.RepositoryId <- directoryVersion.RepositoryId
                                                    newDirectoryVersion.RelativePath <- directoryVersion.RelativePath
                                                    newDirectoryVersion.Sha256Hash <- directoryVersion.Sha256Hash
                                                    newDirectoryVersion.Blake3Hash <- directoryVersion.Blake3Hash
                                                    newDirectoryVersion.Directories <- directoryVersion.Directories
                                                    newDirectoryVersion.Files <- directoryVersion.Files
                                                    newDirectoryVersion.Size <- directoryVersion.Size
                                                    newDirectoryVersion.CreatedAt <- directoryVersion.CreatedAt
                                                    newDirectoryVersion.HashesValidated <- true
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
                                            (this :> IGraceReminderWithGuidKey)
                                                .ScheduleReminderAsync
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
                        with
                        | ex ->
                            let metadataObj = Dictionary<string, obj>(metadata.Properties.Select(fun kvp -> KeyValuePair<string, obj>(kvp.Key, kvp.Value)))
                            return Error(GraceError.CreateWithMetadata ex String.Empty metadata.CorrelationId metadataObj)
                    }

                task {
                    try
                        this.correlationId <- metadata.CorrelationId
                        currentCommand <- getDiscriminatedUnionCaseName command

                        let normalizedCommand =
                            match command with
                            | DirectoryVersionCommand.Create (directoryVersion, repositoryDto) ->
                                DirectoryVersionCommand.Create(normalizeDirectoryVersionForSaveBoundary directoryVersion, repositoryDto)
                            | _ -> command

                        match! isValid normalizedCommand metadata with
                        | Ok command -> return! processCommand command metadata
                        | Error error -> return Error error
                    with
                    | ex ->
                        logToConsole $"Exception in DirectoryVersionActor.Handle(): {ExceptionResponse.Create ex}"
                        return Error(GraceError.CreateWithException ex "Exception in DirectoryVersionActor.Handle()" metadata.CorrelationId)
                }

            /// Calculates recursive byte size from this directory version and its children.
            member this.GetRecursiveSize correlationId =
                this.correlationId <- correlationId

                task {
                    if directoryVersionDto.RecursiveSize = Constants.InitialDirectorySize then
                        let! directoryVersions =
                            (this :> IDirectoryVersionActor)
                                .GetRecursiveDirectoryVersions
                                false
                                correlationId

                        let recursiveSize =
                            directoryVersions
                            |> Seq.sumBy (fun directoryVersionDto -> directoryVersionDto.DirectoryVersion.Size)

                        match! (this :> IDirectoryVersionActor).Handle (SetRecursiveSize recursiveSize) (EventMetadata.New correlationId "GraceSystem") with
                        | Ok returnValue -> return recursiveSize
                        | Error error -> return Constants.InitialDirectorySize
                    else
                        return directoryVersionDto.RecursiveSize
                }

            /// Creates a readable URI for the zip artifact associated with this directory version.
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
                                let subdirectoryZipFileName = getZipFileBlobName subdirectoryVersionId
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

                                let! fileBlobClient = getReadableAzureBlobClientForFileVersion repositoryDto fileVersion correlationId
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
                            let! response = zipFileBlobClient.UploadAsync(tempZipFileStream)
                            ()
                        finally
                            // Step 5: Delete the local ZIP file
                            if File.Exists(tempZipPath) then File.Delete(tempZipPath)
                    }

                task {
                    logToConsole $"In GetZipFileUri: DirectoryVersionId: {directoryVersion.DirectoryVersionId}; RelativePath: {directoryVersion.RelativePath}."
                    let repositoryActorProxy = Repository.CreateActorProxy directoryVersion.OrganizationId directoryVersion.RepositoryId correlationId
                    let! repositoryDto = repositoryActorProxy.Get correlationId

                    let blobName = getZipFileBlobName directoryVersion.DirectoryVersionId
                    let! zipFileBlobClient = getAzureBlobClient repositoryDto blobName correlationId

                    let! zipFileExists = zipFileBlobClient.ExistsAsync()

                    if zipFileExists.Value = true then
                        // We already have this .zip file, so just return the URI with SAS.
                        logToConsole
                            $"In GetZipFileUri: .zip file already exists for DirectoryVersionId: {directoryVersion.DirectoryVersionId}; RelativePath: {directoryVersion.RelativePath}."

                        let! uriWithSas = getUriWithReadSharedAccessSignature repositoryDto blobName correlationId
                        return uriWithSas
                    else
                        // We don't have the .zip file saved, so let's create it.
                        logToConsole
                            $"In GetZipFileUri: Creating .zip file for DirectoryVersionId: {directoryVersion.DirectoryVersionId}; RelativePath: {directoryVersion.RelativePath}."

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
                            (this :> IGraceReminderWithGuidKey)
                                .ScheduleReminderAsync
                                DeleteZipFile
                                (Duration.FromDays(float repositoryDto.DirectoryVersionCacheDays))
                                (ReminderState.DirectoryVersionDeleteZipFile deletionReminderState)
                                correlationId

                        let! uriWithSas = getUriWithReadSharedAccessSignature repositoryDto blobName correlationId
                        return uriWithSas
                }
