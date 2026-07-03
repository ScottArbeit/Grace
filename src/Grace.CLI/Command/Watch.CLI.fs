namespace Grace.CLI.Command

open Grace.CLI.Common
open Grace.CLI.Services
open Grace.SDK
open Grace.SDK.Common
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Parameters.Branch
open Grace.Shared.Services
open Grace.Types.Common
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Http.Connections
open Microsoft.AspNetCore.SignalR.Client
open NodaTime
open Spectre.Console
open System
open System.Buffers
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.ComponentModel
open System.Diagnostics
open System.Globalization
open System.IO
open System.IO.Compression
open System.IO.Enumeration
open System.Linq
open System.Net
open System.Net.Http
open System.Reactive.Linq
open System.Security.Cryptography
open System.Text.Json
open System.Threading.Tasks
open System.Threading
open System.Collections.Concurrent
open Spectre.Console
open System.Text
open Grace.Shared.Parameters.Storage
open Grace.CLI.Text
open Grace.Types.Automation

/// Groups the watch command parser, handlers, and output helpers.
module Watch =
    exception private WatchCommandExit of int

    let private uploadedFileVersions = ConcurrentDictionary<RelativePath * Sha256Hash * Blake3Hash, FileVersion>()

    /// Reads uploaded file version identity data needed by the command workflow without changing remote state.
    let private uploadedFileVersionIdentity (fileVersion: FileVersion) = (fileVersion.RelativePath, fileVersion.Sha256Hash, fileVersion.Blake3Hash)

    /// Removes uploaded identities after their matching watch work is either applied or proven content-equivalent.
    let private removeUploadedFileVersionsForPaths (relativePaths: seq<RelativePath>) =
        let normalizedPaths =
            relativePaths
            |> Seq.map normalizeFilePath
            |> HashSet

        for uploadedFileVersion in uploadedFileVersions.Values.ToArray() do
            if normalizedPaths.Contains(normalizeFilePath $"{uploadedFileVersion.RelativePath}") then
                let mutable removedFileVersion = Unchecked.defaultof<FileVersion>

                uploadedFileVersions.TryRemove(uploadedFileVersionIdentity uploadedFileVersion, &removedFileVersion)
                |> ignore

    /// Records uploaded file identity data for watch tests that replace the storage upload client.
    let internal recordUploadedFileVersionForWatchTests (fileVersion: FileVersion) =
        uploadedFileVersions[uploadedFileVersionIdentity fileVersion] <- fileVersion

    /// Defines the options parsed by the watch command handlers.
    module private Options =
        let ownerId =
            new Option<OwnerId>(
                OptionName.OwnerId,
                Required = false,
                Description = "The repository's owner ID <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> OwnerId.Empty)
            )

        let ownerName =
            new Option<String>(
                OptionName.OwnerName,
                Required = false,
                Description = "The repository's owner name. [default: current owner]",
                Arity = ArgumentArity.ExactlyOne
            )

        let organizationId =
            new Option<OrganizationId>(
                OptionName.OrganizationId,
                Required = false,
                Description = "The repository's organization ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> OrganizationId.Empty)
            )

        let organizationName =
            new Option<String>(
                OptionName.OrganizationName,
                Required = false,
                Description = "The repository's organization name. [default: current organization]",
                Arity = ArgumentArity.ZeroOrOne
            )

        let repositoryId =
            new Option<RepositoryId>(
                OptionName.RepositoryId,
                [| "-r" |],
                Required = false,
                Description = "The repository's ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> RepositoryId.Empty)
            )

        let repositoryName =
            new Option<String>(
                OptionName.RepositoryName,
                [| "-n" |],
                Required = false,
                Description = "The name of the repository. [default: current repository]",
                Arity = ArgumentArity.ExactlyOne
            )

        let branchId =
            new Option<BranchId>(
                OptionName.BranchId,
                [| "-i" |],
                Required = false,
                Description = "The branch's ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> BranchId.Empty)
            )

        let branchName =
            new Option<String>(
                OptionName.BranchName,
                [| "-b" |],
                Required = false,
                Description = "The name of the branch. [default: current branch]",
                Arity = ArgumentArity.ExactlyOne
            )

        let check =
            new Option<bool>(OptionName.Check, Required = false, Description = "Checks whether GraceWatch is already running without starting the watcher.")

    /// Evaluates is check requested against parsed options and command state.
    let isCheckRequested (parseResult: ParseResult) = parseResult.GetValue(Options.check)

    /// Defines structured data exchanged by CLI helpers.
    type private PendingFileWorkSnapshot = { FullPath: string; Generation: int64 }

    /// Holds a list of the created or changed files that we need to process, as determined by the FileSystemWatcher.
    ///
    /// The generation lets a same-path change observed during an upload remain queued after the upload removes only
    /// the snapshot it processed.
    let private filesToProcess = ConcurrentDictionary<string, int64>()

    let mutable private fileUploadWorkGeneration = 0L

    /// Holds a list of the created or changed directories that we need to process, as determined by the FileSystemWatcher.
    ///
    /// Note: We're using ConcurrentDictionary because it's safe for multithreading, doesn't allow us to insert the same key twice, and for its algorithms. We're not using the values of the ConcurrentDictionary here, only the keys.
    let private directoriesToProcess = ConcurrentDictionary<string, unit>()

    /// Defines structured data exchanged by CLI helpers.
    type private StatusUpdateTriggerSnapshot = { RelativePath: string; Generation: int64 }

    /// Holds a list of repo-relative paths that should trigger a Grace Status update without uploading file content.
    ///
    /// FileSystemWatcher delete and rename-old events do not have durable file content to upload. They wake the timer
    /// loop so status application can derive delete differences from GraceStatus and final path state.
    let mutable private statusUpdateTriggerGeneration = 0L

    let private statusUpdateTriggers = ConcurrentDictionary<string, int64>()

    let private pendingStatusDifferencesLock = obj ()

    let private pendingStatusDifferences = List<FileSystemDifference>()

    let mutable private pendingStatusDifferencesRequireRescanRetry = false

    /// Defines structured data exchanged by CLI helpers.
    type internal PendingWatchWorkSnapshot = { FilesToProcess: string array; DirectoriesToProcess: string array; StatusUpdateTriggers: string array }

    /// Executes the watch parameters command by binding ParseResult values to the SDK request and CLI output contract.
    type WatchParameters() =
        inherit ParameterBase()
        /// Stores a parsed command value for handler execution.
        member val public RepositoryPath: string = String.Empty with get, set
        /// Stores a parsed command value for handler execution.
        member val public NamedSections: string [] = Array.empty with get, set

    let mutable private graceStatus = GraceStatus.Default
    let mutable private graceStatusDirectoryIds = HashSet<DirectoryVersionId>()
    let mutable graceStatusMemoryStream: MemoryStream = null
    let mutable graceStatusHasChanged = false

    /// Coordinates file deleted behavior for this CLI command path.
    let fileDeleted filePath = logToConsole $"In Delete: filePath: {filePath}"

    /// Evaluates is not directory against parsed options and command state.
    let isNotDirectory path = not <| Directory.Exists(path)
    /// Coordinates update in progress behavior for this CLI command path.
    let updateInProgress () = File.Exists(updateInProgressFileName ())
    /// Coordinates update not in progress behavior for this CLI command path.
    let updateNotInProgress () = not <| updateInProgress ()

    /// Models the explicit access-assignment scope selected by mutually exclusive CLI options.
    type private DeletedPathKind =
        | DeletedFile
        | DeletedDirectory
        | DeletedPathKindUnknown
        | DeletedPathStatusUnavailable

    /// Coordinates repository relative path behavior for this CLI command path.
    let private repositoryRelativePath (fullPath: string) =
        let rootDirectory =
            Current().RootDirectory
            |> Path.GetFullPath
            |> Path.TrimEndingDirectorySeparator

        let normalizedFullPath = Path.GetFullPath(fullPath)

        if normalizedFullPath.Equals(rootDirectory, StringComparison.InvariantCultureIgnoreCase) then
            Some Constants.RootDirectoryPath
        else
            let rootDirectoryWithSeparator = rootDirectory + string Path.DirectorySeparatorChar

            if normalizedFullPath.StartsWith(rootDirectoryWithSeparator, StringComparison.InvariantCultureIgnoreCase) then
                normalizedFullPath
                    .Substring(rootDirectoryWithSeparator.Length)
                    .Replace(Path.DirectorySeparatorChar, '/')
                |> Some
            else
                None

    /// Normalizes Grace ids for relative path by keeping explicit scope values and clearing implicit child scopes.
    let private normalizeRelativePath (relativePath: RelativePath) =
        $"{relativePath}"
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/')

    /// Coordinates tracked deleted path kind behavior for this CLI command path.
    let private trackedDeletedPathKind (status: GraceStatus) (relativePath: string) =
        let deletedRelativePath = normalizeRelativePath relativePath

        let isTrackedFile =
            status.Index.Values
            |> Seq.exists (fun directoryVersion ->
                directoryVersion.Files
                |> Seq.exists (fun fileVersion ->
                    String.Equals(normalizeRelativePath fileVersion.RelativePath, deletedRelativePath, StringComparison.InvariantCultureIgnoreCase)))

        let isTrackedDirectory =
            status.Index.Values
            |> Seq.exists (fun directoryVersion ->
                String.Equals(normalizeRelativePath directoryVersion.RelativePath, deletedRelativePath, StringComparison.InvariantCultureIgnoreCase))

        match isTrackedFile, isTrackedDirectory with
        | true, _ -> DeletedFile
        | false, true -> DeletedDirectory
        | false, false -> DeletedPathKindUnknown

    let mutable private readGraceStatusFileForDeletedPathClassification = readGraceStatusFile

    let mutable private enumerateFilesForDirectoryUpload = fun directoryPath -> Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)

    /// Coordinates default watch path comparison behavior for this CLI command path.
    let private defaultWatchPathComparison () =
        if OperatingSystem.IsWindows() then
            StringComparison.OrdinalIgnoreCase
        else
            StringComparison.Ordinal

    let mutable private watchPathComparison = defaultWatchPathComparison ()

    /// Reads tracked deleted path kind data needed by the CLI workflow.
    let private readTrackedDeletedPathKind (relativePath: string) =
        try
            let status =
                if graceStatusHasChanged
                   || graceStatus.Index.Count = 0 then
                    let refreshedStatus =
                        readGraceStatusFileForDeletedPathClassification()
                            .GetAwaiter()
                            .GetResult()

                    graceStatus <- refreshedStatus
                    refreshedStatus
                else
                    graceStatus

            trackedDeletedPathKind status relativePath
        with
        | _ -> DeletedPathStatusUnavailable

    /// Coordinates set grace status for watch tests behavior for this CLI command path.
    let internal setGraceStatusForWatchTests status = graceStatus <- status
    /// Checks whether set grace status has changed for watch tests is true for the parsed command input.
    let internal setGraceStatusHasChangedForWatchTests hasChanged = graceStatusHasChanged <- hasChanged
    /// Coordinates set read grace status file for watch tests behavior for this CLI command path.
    let internal setReadGraceStatusFileForWatchTests readStatusFile = readGraceStatusFileForDeletedPathClassification <- readStatusFile
    /// Reads set enumerate files for directory upload for watch tests data needed by the command workflow without changing remote state.
    let internal setEnumerateFilesForDirectoryUploadForWatchTests enumerateFiles = enumerateFilesForDirectoryUpload <- enumerateFiles
    /// Coordinates set watch path comparison for watch tests behavior for this CLI command path.
    let internal setWatchPathComparisonForWatchTests comparison = watchPathComparison <- comparison

    /// Finds the tracked file entry that owns a repository-relative path in GraceStatus.
    let private tryFindTrackedFile (status: GraceStatus) (relativePath: RelativePath) =
        let normalizedRelativePath = normalizeRelativePath relativePath

        status.Index.Values
        |> Seq.collect (fun directoryVersion -> directoryVersion.Files)
        |> Seq.tryFind (fun fileVersion ->
            String.Equals(normalizeRelativePath fileVersion.RelativePath, normalizedRelativePath, StringComparison.InvariantCultureIgnoreCase))

    /// Checks whether the uploaded file identity would change the tracked GraceStatus file content.
    let private uploadedFileContentChanged (trackedFile: LocalFileVersion) (uploadedFile: FileVersion) =
        uploadedFile.Sha256Hash <> trackedFile.Sha256Hash
        || (trackedFile.Blake3Hash <> Blake3Hash String.Empty
            && uploadedFile.Blake3Hash <> trackedFile.Blake3Hash)

    /// Checks whether a stale delete should be ignored because the path exists again before status application.
    let private finalPathExists (relativePath: RelativePath) =
        let fullPath = Path.Combine(Current().RootDirectory, $"{relativePath}")

        File.Exists(fullPath)
        || Directory.Exists(fullPath)

    /// Finds the uploaded identity that matches the final file content for a processed watch path.
    let private tryFindUploadedFinalFileVersion (relativePath: RelativePath) =
        task {
            let fullPath = Path.Combine(Current().RootDirectory, $"{relativePath}")

            if File.Exists(fullPath) then
                match! createLocalFileVersion (FileInfo(fullPath)) with
                | Some localFileVersion ->
                    let identity = (localFileVersion.RelativePath, localFileVersion.Sha256Hash, localFileVersion.Blake3Hash)
                    let mutable uploadedFileVersion = Unchecked.defaultof<FileVersion>

                    if uploadedFileVersions.TryGetValue(identity, &uploadedFileVersion) then
                        return Some uploadedFileVersion
                    else
                        return None
                | None -> return None
            else
                return None
        }

    /// Derives add and change differences from uploads already completed by the watch loop.
    let private deriveUploadedFileDifferences (status: GraceStatus) (processedFilePaths: seq<RelativePath>) =
        task {
            let differences = List<FileSystemDifference>()

            for relativePath in
                processedFilePaths
                |> Seq.distinctBy normalizeRelativePath do
                match! tryFindUploadedFinalFileVersion relativePath with
                | Some uploadedFileVersion ->
                    match tryFindTrackedFile status uploadedFileVersion.RelativePath with
                    | Some trackedFile when uploadedFileContentChanged trackedFile uploadedFileVersion ->
                        differences.Add(FileSystemDifference.Create Change FileSystemEntryType.File uploadedFileVersion.RelativePath)
                    | Some _ -> ()
                    | None -> differences.Add(FileSystemDifference.Create Add FileSystemEntryType.File uploadedFileVersion.RelativePath)
                | None -> ()

            return differences
        }

    /// Derives delete differences from GraceStatus while rejecting stale deletes for paths that exist again.
    let private deriveDeleteDifferences (status: GraceStatus) (statusTriggerSnapshot: StatusUpdateTriggerSnapshot array) =
        let differences = List<FileSystemDifference>()

        for statusTrigger in statusTriggerSnapshot do
            let relativePath = RelativePath statusTrigger.RelativePath

            if not (finalPathExists relativePath) then
                match trackedDeletedPathKind status statusTrigger.RelativePath with
                | DeletedFile -> differences.Add(FileSystemDifference.Create Delete FileSystemEntryType.File relativePath)
                | DeletedDirectory -> differences.Add(FileSystemDifference.Create Delete FileSystemEntryType.Directory relativePath)
                | DeletedPathKindUnknown
                | DeletedPathStatusUnavailable -> ()

        differences

    /// Models the differences that can be applied and the queued work they resolve.
    type private StatusDifferencesForApply = { Applicable: List<FileSystemDifference>; Resolved: List<FileSystemDifference> }

    /// Removes stale delete differences when final path state proves the path exists again.
    let private splitApplicableStatusDifferences (differences: List<FileSystemDifference>) =
        let applicable = List<FileSystemDifference>()
        let resolved = List<FileSystemDifference>()

        for difference in differences do
            if difference.DifferenceType = Delete
               && finalPathExists difference.RelativePath then
                resolved.Add(difference)
            else
                applicable.Add(difference)
                resolved.Add(difference)

        { Applicable = applicable; Resolved = resolved }

    /// Coordinates should ignore deleted path behavior for this CLI command path.
    let private shouldIgnoreDeletedPath (pathKind: DeletedPathKind) (fullPath: string) =
        let configuration = Current()
        let normalizedFullPath = Path.GetFullPath(fullPath)
        let graceDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuration.GraceDirectory))
        let fileInfo = FileInfo(fullPath)
        let deletedDirectoryInfo = DirectoryInfo(normalizedFullPath)

        let isInGraceDirectory =
            normalizedFullPath.Equals(graceDirectory, StringComparison.InvariantCultureIgnoreCase)
            || normalizedFullPath.StartsWith(
                graceDirectory
                + string Path.DirectorySeparatorChar,
                StringComparison.InvariantCultureIgnoreCase
            )

        let isGraceStatusArtifact =
            normalizedFullPath.Equals(configuration.GraceStatusFile, StringComparison.InvariantCultureIgnoreCase)
            || normalizedFullPath.Equals(configuration.GraceStatusFile + "-wal", StringComparison.InvariantCultureIgnoreCase)
            || normalizedFullPath.Equals(configuration.GraceStatusFile + "-shm", StringComparison.InvariantCultureIgnoreCase)
            || normalizedFullPath.Equals(configuration.GraceStatusFile + "-journal", StringComparison.InvariantCultureIgnoreCase)

        if isInGraceDirectory || isGraceStatusArtifact then
            true
        elif pathKind = DeletedPathStatusUnavailable then
            false
        else
            /// Coordinates directory ignore matches behavior for this CLI command path.
            let directoryIgnoreMatches graceIgnoreLine =
                if isNull fileInfo.Directory then
                    false
                else
                    checkIgnoreLineAgainstDirectory fileInfo.Directory graceIgnoreLine

            (pathKind <> DeletedDirectory
             && normalizedFullPath.EndsWith(".gracetmp", StringComparison.InvariantCultureIgnoreCase))
            || (pathKind = DeletedPathKindUnknown
                && configuration.GraceDirectoryIgnoreEntries
                   |> Array.exists directoryIgnoreMatches)
            || (pathKind = DeletedPathKindUnknown
                && configuration.GraceDirectoryIgnoreEntries
                   |> Array.exists (fun graceIgnoreLine -> checkIgnoreLineAgainstDirectory deletedDirectoryInfo graceIgnoreLine))
            || (pathKind <> DeletedDirectory
                && configuration.GraceDirectoryIgnoreEntries
                   |> Array.exists (fun graceIgnoreLine -> checkIgnoreLineAgainstFile normalizedFullPath graceIgnoreLine))
            || (pathKind = DeletedPathKindUnknown
                && configuration.GraceFileIgnoreEntries
                   |> Array.exists (fun graceIgnoreLine -> checkIgnoreLineAgainstFile normalizedFullPath graceIgnoreLine))

    /// Coordinates enqueue status update trigger behavior for this CLI command path.
    let private enqueueStatusUpdateTrigger fullPath =
        match repositoryRelativePath fullPath with
        | Some relativePath when
            not
            <| shouldIgnoreDeletedPath (readTrackedDeletedPathKind relativePath) fullPath
            ->
            let generation = Interlocked.Increment(&statusUpdateTriggerGeneration)

            statusUpdateTriggers.AddOrUpdate(relativePath, generation, (fun _ _ -> generation))
            |> ignore

            true
        | _ -> false

    /// Coordinates remove status update trigger behavior for this CLI command path.
    let private removeStatusUpdateTrigger fullPath =
        match repositoryRelativePath fullPath with
        | Some relativePath ->
            let mutable generation = 0L

            statusUpdateTriggers.TryRemove(relativePath, &generation)
            |> ignore
        | _ -> ()

    /// Reads enqueue file upload data needed by the command workflow without changing remote state.
    let private enqueueFileUpload fullPath =
        let shouldIgnore = shouldIgnoreFile fullPath

        if not shouldIgnore then
            let generation = Interlocked.Increment(&fileUploadWorkGeneration)

            filesToProcess.AddOrUpdate(fullPath, generation, (fun _ _ -> generation))
            |> ignore

            true
        else
            false

    /// Reads remove pending file upload data needed by the command workflow without changing remote state.
    let private removePendingFileUpload filePath =
        let mutable generation = 0L

        filesToProcess.TryRemove(filePath, &generation)
        |> ignore

    /// Reads cancel pending uploads for deleted path data needed by the command workflow without changing remote state.
    let private cancelPendingUploadsForDeletedPath fullPath =
        removePendingFileUpload fullPath

        match repositoryRelativePath fullPath with
        | Some relativePath -> removePendingFileUpload relativePath
        | None -> ()

        let normalizedFullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath))

        let fullPathPrefix =
            normalizedFullPath
            + string Path.DirectorySeparatorChar

        let relativePathPrefix =
            match repositoryRelativePath fullPath with
            | Some relativePath -> Some(relativePath.TrimEnd('/') + "/")
            | None -> None

        for queuedFile in filesToProcess.Keys.ToArray() do
            let removeQueuedFile =
                let normalizedQueuedFile =
                    try
                        Path.GetFullPath(queuedFile)
                    with
                    | _ -> queuedFile

                normalizedQueuedFile.Equals(normalizedFullPath, watchPathComparison)
                || normalizedQueuedFile.StartsWith(fullPathPrefix, watchPathComparison)
                || (match relativePathPrefix with
                    | Some prefix -> queuedFile.StartsWith(prefix, watchPathComparison)
                    | None -> false)

            if removeQueuedFile then removePendingFileUpload queuedFile

    /// Records an already-derived filesystem difference so status application can retry without duplicating work.
    let private addPendingStatusDifference (difference: FileSystemDifference) =
        lock pendingStatusDifferencesLock (fun () ->
            if not
               <| pendingStatusDifferences.Exists (fun (existing: FileSystemDifference) ->
                   existing.RelativePath = difference.RelativePath
                   && existing.DifferenceType = difference.DifferenceType
                   && existing.FileSystemEntryType = difference.FileSystemEntryType) then
                pendingStatusDifferences.Add(difference))

    /// Captures the already-derived differences waiting for the shared status-application path.
    let private pendingStatusDifferencesSnapshot () = lock pendingStatusDifferencesLock (fun () -> pendingStatusDifferences.ToList())

    /// Checks whether a failed startup rescan left pending differences that must rescan again before applying.
    let private pendingStatusDifferencesRequireRescanRetrySnapshot () = lock pendingStatusDifferencesLock (fun () -> pendingStatusDifferencesRequireRescanRetry)

    /// Records that pending startup differences still need a successful rescan before they can be applied.
    let private requirePendingStatusDifferencesRescanRetry () = lock pendingStatusDifferencesLock (fun () -> pendingStatusDifferencesRequireRescanRetry <- true)

    /// Checks whether any pre-derived filesystem differences still need status application.
    let private hasPendingStatusDifferences () = lock pendingStatusDifferencesLock (fun () -> pendingStatusDifferences.Count > 0)

    /// Clears only the differences that a successful status update already applied.
    let private clearPendingStatusDifferences (differences: List<FileSystemDifference>) =
        lock pendingStatusDifferencesLock (fun () ->
            for difference in differences do
                pendingStatusDifferences.Remove(difference)
                |> ignore

            if pendingStatusDifferences.Count = 0 then
                pendingStatusDifferencesRequireRescanRetry <- false)

    /// Clears pre-derived differences when tests reset watch module state.
    let private clearPendingStatusDifferencesForTests () =
        lock pendingStatusDifferencesLock (fun () ->
            pendingStatusDifferences.Clear()
            pendingStatusDifferencesRequireRescanRetry <- false)

    /// Combines pre-derived and freshly scanned status differences without applying the same filesystem observation twice.
    let private mergeStatusDifferences (first: List<FileSystemDifference>) (second: List<FileSystemDifference>) =
        let merged = List<FileSystemDifference>()

        let addIfMissing (difference: FileSystemDifference) =
            if not
               <| merged.Exists (fun existing ->
                   existing.RelativePath = difference.RelativePath
                   && existing.DifferenceType = difference.DifferenceType
                   && existing.FileSystemEntryType = difference.FileSystemEntryType) then
                merged.Add(difference)

        for difference in first do
            addIfMissing difference

        for difference in second do
            addIfMissing difference

        merged

    /// Clears inherited pending watch work for tests values so explicitly scoped access commands do not target child resources accidentally.
    let internal clearPendingWatchWorkForTests () =
        filesToProcess.Clear()
        directoriesToProcess.Clear()
        statusUpdateTriggers.Clear()
        uploadedFileVersions.Clear()
        clearPendingStatusDifferencesForTests ()

        Interlocked.Exchange(&statusUpdateTriggerGeneration, 0L)
        |> ignore

        Interlocked.Exchange(&fileUploadWorkGeneration, 0L)
        |> ignore

        graceStatus <- GraceStatus.Default
        graceStatusHasChanged <- false
        readGraceStatusFileForDeletedPathClassification <- readGraceStatusFile
        enumerateFilesForDirectoryUpload <- fun directoryPath -> Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)
        watchPathComparison <- defaultWatchPathComparison ()
        setLastScanForDifferencesSuccessfulForWatchTests true

    /// Coordinates pending watch work snapshot for tests behavior for this CLI command path.
    let internal pendingWatchWorkSnapshotForTests () =
        {
            FilesToProcess = filesToProcess.Keys.OrderBy(id).ToArray()
            DirectoriesToProcess = directoriesToProcess.Keys.OrderBy(id).ToArray()
            StatusUpdateTriggers = statusUpdateTriggers.Keys.OrderBy(id).ToArray()
        }

    /// Evaluates has pending watch work against parsed options and command state.
    let private hasPendingWatchWork () =
        not (
            filesToProcess.IsEmpty
            && directoriesToProcess.IsEmpty
            && statusUpdateTriggers.IsEmpty
            && not (hasPendingStatusDifferences ())
        )

    /// Coordinates status only trigger snapshot behavior for this CLI command path.
    let private statusOnlyTriggerSnapshot () =
        directoriesToProcess.Keys.ToArray(),
        statusUpdateTriggers
            .ToArray()
            .Select(fun trigger -> { RelativePath = trigger.Key; Generation = trigger.Value })
            .ToArray()

    /// Coordinates reset working tree scan cache for status only triggers behavior for this CLI command path.
    let private resetWorkingTreeScanCacheForStatusOnlyTriggers (directorySnapshot: string array) (statusTriggerSnapshot: StatusUpdateTriggerSnapshot array) =
        if directorySnapshot.Length > 0
           || statusTriggerSnapshot.Length > 0 then
            clearWorkingDirectoryWriteTimesForWatchRescan ()

    /// Coordinates drain status only triggers behavior for this CLI command path.
    let private drainStatusOnlyTriggers (directorySnapshot: string array) (statusTriggerSnapshot: StatusUpdateTriggerSnapshot array) =
        let mutable unitValue = ()

        for directory in directorySnapshot do
            directoriesToProcess.TryRemove(directory, &unitValue)
            |> ignore

        for statusTrigger in statusTriggerSnapshot do
            let pair = KeyValuePair(statusTrigger.RelativePath, statusTrigger.Generation)

            (statusUpdateTriggers :> ICollection<KeyValuePair<string, int64>>)
                .Remove(pair)
            |> ignore

    /// Drains the watch work proven by either a scan-derived update or the matched pre-derived differences.
    let private drainAppliedStatusWork
        (directorySnapshot: string array)
        (statusTriggerSnapshot: StatusUpdateTriggerSnapshot array)
        (appliedDifferences: List<FileSystemDifference>)
        =
        if appliedDifferences.Count = 0 then
            drainStatusOnlyTriggers directorySnapshot statusTriggerSnapshot
        else
            let directoryPathsToDrain =
                appliedDifferences
                |> Seq.filter (fun difference -> difference.FileSystemEntryType = FileSystemEntryType.Directory)
                |> Seq.map (fun difference -> string difference.RelativePath)
                |> HashSet

            let statusTriggerPathsToDrain =
                appliedDifferences
                |> Seq.filter (fun difference ->
                    difference.FileSystemEntryType = FileSystemEntryType.File
                    && difference.DifferenceType = DifferenceType.Delete)
                |> Seq.map (fun difference -> string difference.RelativePath)
                |> HashSet

            let mutable unitValue = ()

            for directory in directorySnapshot do
                if directoryPathsToDrain.Contains(directory) then
                    directoriesToProcess.TryRemove(directory, &unitValue)
                    |> ignore

            for statusTrigger in statusTriggerSnapshot do
                if statusTriggerPathsToDrain.Contains(statusTrigger.RelativePath) then
                    let pair = KeyValuePair(statusTrigger.RelativePath, statusTrigger.Generation)

                    (statusUpdateTriggers :> ICollection<KeyValuePair<string, int64>>)
                        .Remove(pair)
                    |> ignore

    /// Resolves signal raccess token result from command options, configuration, or local state.
    let resolveSignalRAccessTokenResult (tokenResult: Result<string option, string>) =
        match tokenResult with
        | Ok (Some token) when not (String.IsNullOrWhiteSpace token) -> Ok token
        | Ok _ ->
            Error $"No access token is available. Run `grace authenticate login` or set {Constants.EnvironmentVariables.GraceToken} before starting watch."
        | Error message -> Error $"Unable to acquire an access token for SignalR notifications: {message}"

    /// Reads signal raccess token from ParseResult, local configuration, or Grace ids.
    let private getSignalRAccessToken () =
        task {
            let! tokenResult = Grace.CLI.Command.Auth.tryGetAccessToken ()
            return resolveSignalRAccessTokenResult tokenResult
        }

    /// Renders json result results only when the selected output mode includes human-readable console text.
    let private renderJsonResult parseResult started completed message =
        let configuration = Current()

        let dto: LocalOutputDto.WatchResultDto =
            {
                Started = started
                Completed = completed
                Message = message
                RootDirectory = configuration.RootDirectory
                ServerUri = configuration.ServerUri
                RepositoryId = configuration.RepositoryId
                BranchId = configuration.BranchId
            }

        Ok(GraceReturnValue.Create dto (getCorrelationId parseResult))
        |> renderOutput parseResult

    /// Renders json error results only when the selected output mode includes human-readable console text.
    let private renderJsonError parseResult message =
        Error(GraceError.Create message (getCorrelationId parseResult))
        |> renderOutput parseResult

    /// Opens the SignalR connection Grace Watch uses to receive server-side notifications.
    let private createSignalRConnection (signalRUrl: Uri) =
        HubConnectionBuilder()
            .WithAutomaticReconnect()
            .WithUrl(
                signalRUrl,
                fun options ->
                    options.Transports <- HttpTransportType.ServerSentEvents

                    options.AccessTokenProvider <-
                        Func<Task<string>> (fun () ->
                            task {
                                let! accessTokenResult = getSignalRAccessToken ()

                                match accessTokenResult with
                                | Ok accessToken -> return accessToken
                                | Error message -> return raise (InvalidOperationException(message))
                            })
            )
            .Build()

    /// Evaluates is grace status artifact against parsed options and command state.
    let private isGraceStatusArtifact (fullPath: string) =
        let statusFile = Current().GraceStatusFile

        fullPath.Equals(statusFile, StringComparison.InvariantCultureIgnoreCase)
        || fullPath.Equals(statusFile + "-wal", StringComparison.InvariantCultureIgnoreCase)
        || fullPath.Equals(statusFile + "-shm", StringComparison.InvariantCultureIgnoreCase)
        || fullPath.Equals(statusFile + "-journal", StringComparison.InvariantCultureIgnoreCase)

    /// Coordinates on created behavior for this CLI command path.
    let OnCreated (args: FileSystemEventArgs) =
        // Ignore directory creation; need to think about this more... should we capture new empty directories?
        if updateNotInProgress ()
           && isNotDirectory args.FullPath then
            let shouldIgnore = shouldIgnoreFile args.FullPath
            //logToAnsiConsole Colors.Verbose $"Should ignore {args.FullPath}: {shouldIgnore}."

            if not <| shouldIgnore then
                logToAnsiConsole Colors.Added $"I saw that {args.FullPath} was created."
                enqueueFileUpload args.FullPath |> ignore

            if (isGraceStatusArtifact args.FullPath)
               && (not <| graceStatusHasChanged) then
                graceStatusHasChanged <- true

    /// Coordinates on changed behavior for this CLI command path.
    let OnChanged (args: FileSystemEventArgs) =
        if updateNotInProgress ()
           && isNotDirectory args.FullPath then
            let shouldIgnore = shouldIgnoreFile args.FullPath
            //logToAnsiConsole Colors.Verbose $"Should ignore {args.FullPath}: {shouldIgnore}."

            if not <| shouldIgnore then
                logToAnsiConsole Colors.Changed $"I saw that {args.FullPath} changed."
                enqueueFileUpload args.FullPath |> ignore

            // Special handling for the Grace status file; if that is the changed file, we'll set this flag so we reload it in OnWatch() in the main loop
            if (isGraceStatusArtifact args.FullPath)
               && (not <| graceStatusHasChanged) then
                //logToAnsiConsole Colors.Important $"Setting graceStatusHasChanged to true in OnChanged(). Current value: {graceStatusHasChanged}."
                graceStatusHasChanged <- true
                logToAnsiConsole Colors.Important $"Grace Status file has been updated."

    /// Reads enqueue directory contents for upload data needed by the command workflow without changing remote state.
    let private enqueueDirectoryContentsForUpload directoryPath =
        if
            Directory.Exists(directoryPath)
            && shouldNotIgnoreDirectory directoryPath
        then
            try
                for filePath in enumerateFilesForDirectoryUpload directoryPath do
                    enqueueFileUpload filePath |> ignore

                true
            with
            | ex ->
                logToAnsiConsole
                    Colors.Error
                    $"Unable to enumerate renamed directory {directoryPath}; status update will retry after file uploads can be queued. {Markup.Escape(ex.Message)}"

                false
        else
            true

    /// Coordinates on deleted behavior for this CLI command path.
    let OnDeleted (args: FileSystemEventArgs) =
        if updateNotInProgress () then
            cancelPendingUploadsForDeletedPath args.FullPath

            if enqueueStatusUpdateTrigger args.FullPath then
                logToAnsiConsole Colors.Deleted $"I saw that {args.FullPath} was deleted."

            if (isGraceStatusArtifact args.FullPath)
               && (not <| graceStatusHasChanged) then
                graceStatusHasChanged <- true

    /// Coordinates on renamed behavior for this CLI command path.
    let OnRenamed (args: RenamedEventArgs) =
        if updateNotInProgress () then
            let newPathIsDirectory = Directory.Exists(args.FullPath)

            cancelPendingUploadsForDeletedPath args.OldFullPath

            let oldPathStatusQueued = enqueueStatusUpdateTrigger args.OldFullPath

            if oldPathStatusQueued then
                logToAnsiConsole Colors.Changed $"I saw that {args.OldFullPath} was renamed to {args.FullPath}."

            if newPathIsDirectory then
                enqueueDirectoryContentsForUpload args.FullPath
                |> ignore
            elif enqueueFileUpload args.FullPath then
                logToAnsiConsole Colors.Changed $"I saw that {args.OldFullPath} was renamed to {args.FullPath}."

    /// Coordinates on error behavior for this CLI command path.
    let OnError (args: ErrorEventArgs) =
        let correlationId = generateCorrelationId ()

        logToAnsiConsole Colors.Error $"I saw that the FileSystemWatcher threw an exception: {args.GetException().Message}. grace watch should be restarted."

    /// Coordinates on grace update in progress created behavior for this CLI command path.
    let OnGraceUpdateInProgressCreated (args: FileSystemEventArgs) =
        if args.FullPath = updateInProgressFileName () then
            if updateInProgress () then
                logToAnsiConsole Colors.Important $"Update is in progress from another Grace instance."
            else
                logToAnsiConsole Colors.Important $"{updateInProgressFileName ()} should already exist, but it doesn't."

    /// Coordinates on grace update in progress deleted behavior for this CLI command path.
    let OnGraceUpdateInProgressDeleted (args: FileSystemEventArgs) =
        if args.FullPath = updateInProgressFileName () then
            if updateNotInProgress () then
                logToAnsiConsole Colors.Important $"Update has finished in another Grace instance."
            else
                logToAnsiConsole Colors.Important $"{updateInProgressFileName ()} should have been deleted, but it hasn't yet."

    /// Instantiates the FileSystemWatcher used to observe repository changes under the requested path.
    let createFileSystemWatcher path =
        let fileSystemWatcher = new FileSystemWatcher(path)
        fileSystemWatcher.InternalBufferSize <- (64 * 1024) // Default is 4K, choosing maximum of 64K for safety.
        fileSystemWatcher.IncludeSubdirectories <- true

        fileSystemWatcher.NotifyFilter <-
            NotifyFilters.DirectoryName
            ||| NotifyFilters.FileName
            ||| NotifyFilters.LastWrite
            ||| NotifyFilters.Security

        fileSystemWatcher

    /// Formats print differences data for Spectre.Console output.
    let printDifferences (differences: List<FileSystemDifference>) =
        if differences.Count > 0 then
            logToAnsiConsole Colors.Verbose $"Differences detected since last save/checkpoint/commit:"

        for difference in differences.OrderBy(fun diff -> diff.RelativePath) do
            logToAnsiConsole
                Colors.Verbose
                $"{getDiscriminatedUnionCaseName difference.DifferenceType} for {getDiscriminatedUnionCaseName difference.FileSystemEntryType} {difference.RelativePath}"

    /// Update the Grace Object Cache file with the new DirectoryVersions.
    let updateObjectCacheFile (newDirectoryVersions: List<LocalDirectoryVersion>) = task { do! upsertObjectCache newDirectoryVersions }

    /// Applies already-derived filesystem differences to Grace Status, object cache, and save history.
    let updateGraceStatusFromDifferences graceStatus (differences: List<FileSystemDifference>) correlationId =
        task {
            printDifferences differences

            // Get an updated Grace Index, and any new DirectoryVersions that were needed to build it.
            let! (newGraceStatus, newDirectoryVersions) = getNewGraceStatusAndDirectoryVersions graceStatus differences

            // Log the changes.
            for dv in newDirectoryVersions do
                logToAnsiConsole
                    Colors.Verbose
                    $"new Sha256Hash: {dv.Sha256Hash.Substring(0, 8)}; DirectoryId: {dv.DirectoryVersionId.ToString().Substring(0, 9)}...; RelativePath: {dv.RelativePath}"

            let directoryUploadedFileVersions =
                let fileIdentities =
                    newDirectoryVersions
                    |> Seq.collect (fun directoryVersion -> directoryVersion.Files)
                    |> Seq.map (fun fileVersion -> (fileVersion.RelativePath, fileVersion.Sha256Hash, fileVersion.Blake3Hash))
                    |> HashSet

                uploadedFileVersions.Values
                |> Seq.filter (fun fileVersion -> fileIdentities.Contains(uploadedFileVersionIdentity fileVersion))
                |> Seq.toArray

            let uploadedFileVersionIdentities =
                directoryUploadedFileVersions
                |> Seq.map uploadedFileVersionIdentity
                |> HashSet

            let newFileVersionsByPath = Dictionary<RelativePath, LocalFileVersion>()

            for directoryVersion in newDirectoryVersions do
                for fileVersion in directoryVersion.Files do
                    newFileVersionsByPath[fileVersion.RelativePath] <- fileVersion

            let unuploadedFileDifferences =
                differences
                    .Where(fun diff ->
                        diff.FileSystemEntryType = FileSystemEntryType.File
                        && (diff.DifferenceType = DifferenceType.Add
                            || diff.DifferenceType = DifferenceType.Change))
                    .Where(fun diff ->
                        let mutable fileVersion = Unchecked.defaultof<LocalFileVersion>

                        if newFileVersionsByPath.TryGetValue(diff.RelativePath, &fileVersion) then
                            not
                            <| uploadedFileVersionIdentities.Contains((fileVersion.RelativePath, fileVersion.Sha256Hash, fileVersion.Blake3Hash))
                        else
                            true)
                    .ToList()

            if unuploadedFileDifferences.Count > 0 then
                for fileDifference in unuploadedFileDifferences do
                    let fullPath = Path.Combine(Current().RootDirectory, string fileDifference.RelativePath)

                    enqueueFileUpload fullPath |> ignore

                logToAnsiConsole
                    Colors.Important
                    $"Grace Status update found {unuploadedFileDifferences.Count} file add/change differences without uploaded file content; file uploads will run before retrying the status update."

                return None
            else
                let! savedDirectoryVersionsResult = getSavedDirectoryVersionsForRootDirectory graceStatus.RootDirectoryId correlationId

                match savedDirectoryVersionsResult with
                | Error error ->
                    logToAnsiConsole Colors.Error $"{Markup.Escape(error.Error)}"
                    return None
                | Ok savedDirectoryVersions ->
                    // Upload the new directory versions.
                    let directoryVersionsToSave =
                        applyUploadedFileVersionsToDirectoryVersionsWithSavedDirectoryVersions
                            directoryUploadedFileVersions
                            savedDirectoryVersions
                            newDirectoryVersions

                    let! result = saveDirectoryVersions directoryVersionsToSave correlationId

                    match result with
                    | Ok returnValue ->
                        let newGraceStatus = syncGraceStatusRootDirectoryHash newGraceStatus

                        do! updateObjectCacheFile newDirectoryVersions

                        let fileDifferences =
                            differences
                                .Where(fun diff -> diff.FileSystemEntryType = FileSystemEntryType.File)
                                .ToList()

                        let message =
                            if fileDifferences |> Seq.isEmpty then
                                String.Empty
                            else
                                let sb = stringBuilderPool.Get()

                                try
                                    for fileDifference in fileDifferences do
                                        //sb.AppendLine($"{(getDiscriminatedUnionCaseNameToString fileDifference.DifferenceType)}: {fileDifference.RelativePath}") |> ignore
                                        match fileDifference.DifferenceType with
                                        | Change ->
                                            sb.AppendLine($"{fileDifference.RelativePath}")
                                            |> ignore
                                        | Add ->
                                            sb.AppendLine($"Add {fileDifference.RelativePath}")
                                            |> ignore
                                        | Delete ->
                                            sb.AppendLine($"Delete {fileDifference.RelativePath}")
                                            |> ignore

                                    let saveMessage = sb.ToString()
                                    saveMessage.Remove(saveMessage.LastIndexOf(Environment.NewLine), Environment.NewLine.Length)
                                finally
                                    stringBuilderPool.Return(sb)

                        // If there are changes either to files or just to directories, create a save reference.
                        if (differences.Count > 0) then
                            match! createSaveReference (getRootDirectoryVersion newGraceStatus) message correlationId with
                            | Ok returnValue ->
                                let newGraceStatusWithUpdatedTime = { newGraceStatus with LastSuccessfulDirectoryVersionUpload = getCurrentInstant () }
                                // Apply incremental changes to the Grace Status DB.
                                do! applyGraceStatusIncremental newGraceStatusWithUpdatedTime newDirectoryVersions differences

                                for fileVersion in directoryUploadedFileVersions do
                                    let mutable removedFileVersion = Unchecked.defaultof<FileVersion>

                                    uploadedFileVersions.TryRemove(uploadedFileVersionIdentity fileVersion, &removedFileVersion)
                                    |> ignore

                                //logToAnsiConsole Colors.Important $"Setting graceStatusHasChanged to false in updateGraceStatus(). Current value: {graceStatusHasChanged}."
                                graceStatusHasChanged <- false // We *just* changed it ourselves, so we don't have to re-process it in the timer loop.
                                return Some newGraceStatusWithUpdatedTime
                            | Error error ->
                                logToAnsiConsole Colors.Error $"{Markup.Escape(error.Error)}"
                                return None
                        else
                            // There were no changes to process, so just return the existing GraceStatus.
                            //logToAnsiConsole Colors.Verbose "No fileDifferences or newDirectoryVersions to process; not updating GraceStatus."
                            return Some graceStatus
                    | Error error ->
                        logToAnsiConsole Colors.Error $"{Markup.Escape(error.Error)}"
                        return None
        }

    /// Updates the Grace Status file's Index with updates detected from the file system.
    let updateGraceStatus graceStatus correlationId =
        task {
            // Get the list of differences between what's in the working directory, and what Grace Index knows about.
            let! differences = scanForDifferences graceStatus
            return! updateGraceStatusFromDifferences graceStatus differences correlationId
        }

    /// Reads cached file version for upload skip from ParseResult, local configuration, or Grace ids.
    let private getCachedFileVersionForUploadSkip (fullPath: FilePath) =
        task {
            let fileInfo = FileInfo(fullPath)

            match! createLocalFileVersion fileInfo with
            | Some localFileVersion ->
                let relativeDirectoryPath = getLocalRelativeDirectory fullPath (Current().RootDirectory)

                let objectFilePath =
                    Path.Combine(
                        Current().ObjectDirectory,
                        relativeDirectoryPath,
                        getLocalObjectCacheFileName localFileVersion.RelativePath localFileVersion.Sha256Hash localFileVersion.Blake3Hash
                    )

                if File.Exists(objectFilePath) then
                    return Some localFileVersion.ToFileVersion
                else
                    return None
            | _ -> return None
        }

    /// Reads upload file version to storage data needed by the command workflow without changing remote state.
    let internal uploadFileVersionToStorage
        (uploadFileVersions: GetUploadMetadataForFilesParameters -> Task<GraceResult<FileVersion array>>)
        (getUploadMetadataForFilesParameters: GetUploadMetadataForFilesParameters)
        (fileVersion: FileVersion)
        =
        task {
            getUploadMetadataForFilesParameters.FileVersions <- [| fileVersion |]

            match! uploadFileVersions getUploadMetadataForFilesParameters with
            | Ok returnValue ->
                for uploadedFileVersion in returnValue.ReturnValue do
                    uploadedFileVersions[uploadedFileVersionIdentity uploadedFileVersion] <- uploadedFileVersion

                logToAnsiConsole Colors.Verbose $"File {fileVersion.GetObjectFileName} has been uploaded to storage."
            | Error error -> raise (InvalidOperationException($"Failed to upload {fileVersion.GetObjectFileName} to storage: {error.Error}"))
        }

    /// Reads copy file to object directory and upload to storage with clients data needed by the command workflow without changing remote state.
    let internal copyFileToObjectDirectoryAndUploadToStorageWithClients
        (copyFileToObjectCache: FilePath -> Task<FileVersion option>)
        (getCachedFileVersion: FilePath -> Task<FileVersion option>)
        (uploadFileVersions: GetUploadMetadataForFilesParameters -> Task<GraceResult<FileVersion array>>)
        (getUploadMetadataForFilesParameters: GetUploadMetadataForFilesParameters)
        fullPath
        =
        task {
            //logToConsole $"*In fileChanged for {fullPath}."
            match! copyFileToObjectCache fullPath with
            | Some fileVersion -> do! uploadFileVersionToStorage uploadFileVersions getUploadMetadataForFilesParameters fileVersion
            | None ->
                match! getCachedFileVersion fullPath with
                | Some fileVersion -> do! uploadFileVersionToStorage uploadFileVersions getUploadMetadataForFilesParameters fileVersion
                | None -> raise (InvalidOperationException($"Failed to copy {fullPath} to the object cache before upload."))
        }

    /// Copies a file from the working directory to the object directory, with its SHA-256 hash, and then uploads it to storage.
    let copyFileToObjectDirectoryAndUploadToStorage (getUploadMetadataForFilesParameters: GetUploadMetadataForFilesParameters) fullPath =
        copyFileToObjectDirectoryAndUploadToStorageWithClients
            copyToObjectDirectory
            getCachedFileVersionForUploadSkip
            uploadFilesToObjectStorage
            getUploadMetadataForFilesParameters
            fullPath

    /// Decompresses the GraceStatus information from the memory stream.
    let retrieveGraceStatusFromMemoryStream () =
        task {
            logToAnsiConsole Colors.Verbose $"Retrieving Grace Status from compressed memory stream."
            graceStatusMemoryStream.Position <- 0
            use gzStream = new GZipStream(graceStatusMemoryStream, CompressionMode.Decompress)

            let! retrievedGraceStatus = JsonSerializer.DeserializeAsync<GraceStatus>(gzStream, Constants.JsonSerializerOptions)
            graceStatus <- retrievedGraceStatus
            logToAnsiConsole Colors.Verbose $"Retrieved Grace Status from compressed memory stream."

            do! gzStream.DisposeAsync() // Dispose the GZipStream first, before disposing the MemoryStream.
            do! graceStatusMemoryStream.DisposeAsync()
            graceStatusMemoryStream <- null
        }

    /// Compresses the GraceStatus information into a gzipped memory stream.
    let storeGraceStatusInMemoryStream () =
        task {
            logToAnsiConsole Colors.Verbose $"Storing Grace Status in compressed memory stream."
            graceStatusMemoryStream <- new MemoryStream()

            use gzStream = new GZipStream(graceStatusMemoryStream, CompressionLevel.SmallestSize, leaveOpen = true)

            do! serializeAsync gzStream graceStatus
            do! gzStream.FlushAsync()
            do! graceStatusMemoryStream.FlushAsync()
            logToAnsiConsole Colors.Verbose $"Stored Grace Status in compressed memory stream."
            do! gzStream.DisposeAsync()
            graceStatus <- GraceStatus.Default
        }

    /// Coordinates update grace status directory ids behavior for this CLI command path.
    let updateGraceStatusDirectoryIds (status: GraceStatus) = graceStatusDirectoryIds <- status.Index.Keys.ToHashSet()

    /// Processes any changed files since the last timer tick.
    let internal processChangedFilesWithClients
        readGraceStatusMetaClient
        readGraceStatusFileClient
        copyFileToObjectDirectoryAndUploadToStorageClient
        _updateGraceStatusClient
        scanForDifferencesClient
        updateGraceStatusFromDifferencesClient
        applyGraceStatusIncrementalClient
        updateGraceWatchInterprocessFileClient
        =
        task {
            // First, check if there's anything to process.
            if hasPendingWatchWork () then
                try
                    let correlationId = generateCorrelationId ()
                    let! graceStatusFromDisk = readGraceStatusMetaClient ()
                    graceStatus <- graceStatusFromDisk

                    let mutable lastFileUploadInstant = graceStatus.LastSuccessfulFileUpload
                    let mutable processedAnyFile = false
                    let processedFileRelativePaths = List<RelativePath>()

                    // Loop through no more than 50 files. Copy them to the objects directory, and upload them to storage.
                    //   In the incredibly rare event that more than 50 files have changed, we'll get 50-per-timer-tick,
                    //   and clear the queue quickly without overwhelming the system.
                    let getUploadMetadataForFilesParameters =
                        GetUploadMetadataForFilesParameters(
                            OwnerId = $"{Current().OwnerId}",
                            OrganizationId = $"{Current().OrganizationId}",
                            RepositoryId = $"{Current().RepositoryId}",
                            CorrelationId = correlationId
                        )

                    for pendingFile in
                        filesToProcess
                            .ToArray()
                            .Take(50)
                            .Select(fun entry -> { FullPath = entry.Key; Generation = entry.Value }) do
                        let pendingPair = KeyValuePair(pendingFile.FullPath, pendingFile.Generation)

                        if (filesToProcess :> ICollection<KeyValuePair<string, int64>>)
                            .Contains(pendingPair) then
                            logToAnsiConsole Colors.Verbose $"Processing {pendingFile.FullPath}. filesToProcess.Count: {filesToProcess.Count}."
                            do! copyFileToObjectDirectoryAndUploadToStorageClient getUploadMetadataForFilesParameters (FilePath pendingFile.FullPath)

                            (filesToProcess :> ICollection<KeyValuePair<string, int64>>)
                                .Remove(pendingPair)
                            |> ignore

                            match repositoryRelativePath pendingFile.FullPath with
                            | Some relativePath -> processedFileRelativePaths.Add(RelativePath relativePath)
                            | None -> ()

                            processedAnyFile <- true
                            lastFileUploadInstant <- getCurrentInstant ()

                    if processedAnyFile then
                        graceStatus <- { graceStatus with LastSuccessfulFileUpload = lastFileUploadInstant }
                        do! applyGraceStatusIncrementalClient graceStatus Seq.empty Seq.empty

                    // If we've drained all of the files that changed (and we'll almost always have done so), update all the things:
                    //   GraceStatus, directory versions, etc.
                    if filesToProcess.IsEmpty then
                        let directorySnapshot, statusTriggerSnapshot = statusOnlyTriggerSnapshot ()
                        let fileWorkGenerationBeforeStatusUpdate = Volatile.Read(&fileUploadWorkGeneration)
                        let! graceStatusSnapshot = readGraceStatusFileClient ()
                        graceStatus <- graceStatusSnapshot
                        resetWorkingTreeScanCacheForStatusOnlyTriggers directorySnapshot statusTriggerSnapshot
                        let startupPendingDifferences = pendingStatusDifferencesSnapshot ()

                        let pendingDifferencesNeedRescan =
                            startupPendingDifferences.Count > 0
                            && (processedAnyFile
                                || pendingStatusDifferencesRequireRescanRetrySnapshot ())

                        let! startupDifferences =
                            if pendingDifferencesNeedRescan then
                                task {
                                    let! rescannedDifferences = scanForDifferencesClient graceStatus
                                    return mergeStatusDifferences startupPendingDifferences rescannedDifferences
                                }
                            else
                                Task.FromResult(startupPendingDifferences)

                        let! uploadedFileDifferences = deriveUploadedFileDifferences graceStatus processedFileRelativePaths
                        let deleteDifferences = deriveDeleteDifferences graceStatus statusTriggerSnapshot
                        let eventDerivedDifferences = mergeStatusDifferences uploadedFileDifferences deleteDifferences

                        for eventDerivedDifference in (eventDerivedDifferences :> seq<FileSystemDifference>) do
                            addPendingStatusDifference eventDerivedDifference

                        let pendingDifferencesToClear = mergeStatusDifferences startupPendingDifferences eventDerivedDifferences
                        let statusDifferencesForApply = splitApplicableStatusDifferences (mergeStatusDifferences startupDifferences eventDerivedDifferences)

                        let! statusUpdateResult =
                            if
                                startupPendingDifferences.Count > 0
                                && pendingDifferencesNeedRescan
                                && not (wasLastScanForDifferencesSuccessful ())
                            then
                                requirePendingStatusDifferencesRescanRetry ()

                                logToAnsiConsole
                                    Colors.Important
                                    $"Grace Status pending startup differences need a successful rescan before status application; status-only triggers will retry."

                                Task.FromResult(None)
                            elif statusDifferencesForApply.Applicable.Count > 0 then
                                updateGraceStatusFromDifferencesClient graceStatus statusDifferencesForApply.Applicable correlationId
                            else
                                Task.FromResult(Some graceStatus)

                        match statusUpdateResult with
                        | Some newGraceStatus ->
                            let statusUpdateCanCommit =
                                filesToProcess.IsEmpty
                                && Volatile.Read(&fileUploadWorkGeneration) = fileWorkGenerationBeforeStatusUpdate
                                && (not pendingDifferencesNeedRescan
                                    || wasLastScanForDifferencesSuccessful ())

                            if statusUpdateCanCommit then
                                graceStatus <- newGraceStatus

                                if startupPendingDifferences.Count = 0 then
                                    drainStatusOnlyTriggers directorySnapshot statusTriggerSnapshot
                                else
                                    drainAppliedStatusWork directorySnapshot statusTriggerSnapshot statusDifferencesForApply.Resolved

                                clearPendingStatusDifferences (mergeStatusDifferences pendingDifferencesToClear statusDifferencesForApply.Resolved)
                                removeUploadedFileVersionsForPaths processedFileRelativePaths
                            else
                                if wasLastScanForDifferencesSuccessful () then
                                    clearPendingStatusDifferences pendingDifferencesToClear

                                logToAnsiConsole
                                    Colors.Important
                                    $"Grace Status file update completed while file upload work or a failed scan was pending; status-only triggers will retry."
                        | None ->
                            logToAnsiConsole Colors.Important $"Grace Status file was not updated."
                            () // Something went wrong, don't update the in-memory Grace Status.

                    updateGraceStatusDirectoryIds graceStatus
                    do! updateGraceWatchInterprocessFileClient graceStatus (Some graceStatusDirectoryIds)

                    // Reset the in-memory Grace Status to empty to minimize memory usage.
                    graceStatus <- GraceStatus.Default
                    GC.Collect(2, GCCollectionMode.Forced, blocking = true, compacting = true)
                with
                | ex ->
                    logToAnsiConsole
                        Colors.Error
                        $"Error in processChangedFiles: Message: {ex.Message}{Environment.NewLine}{Environment.NewLine}{ex.StackTrace}"
            // Refresh the file every (just under) 5 minutes to indicate that `grace watch` is still alive.
            elif
                graceWatchStatusUpdateTime
                < getCurrentInstant().Minus(Duration.FromMinutes(4.8))
            then
                let! graceStatusFromDisk = readGraceStatusMeta ()
                do! updateGraceWatchInterprocessFile graceStatusFromDisk (Some graceStatusDirectoryIds)
                GC.Collect(2, GCCollectionMode.Forced, blocking = true, compacting = true)
        }

    /// Coordinates process changed files behavior for this CLI command path.
    let processChangedFiles () =
        processChangedFilesWithClients
            readGraceStatusMeta
            readGraceStatusFile
            copyFileToObjectDirectoryAndUploadToStorage
            updateGraceStatus
            scanForDifferences
            updateGraceStatusFromDifferences
            applyGraceStatusIncremental
            updateGraceWatchInterprocessFile

    /// Coordinates queue startup difference for watch behavior for this CLI command path.
    let internal queueStartupDifferenceForWatch (difference: FileSystemDifference) =
        addPendingStatusDifference difference

        match difference.FileSystemEntryType, difference.DifferenceType with
        | Directory, _ ->
            directoriesToProcess.TryAdd(difference.RelativePath, ())
            |> ignore
        | File, Delete ->
            let generation = Interlocked.Increment(&statusUpdateTriggerGeneration)

            statusUpdateTriggers.AddOrUpdate(difference.RelativePath, generation, (fun _ _ -> generation))
            |> ignore
        | File, _ ->
            enqueueFileUpload difference.RelativePath
            |> ignore

    /// Executes the watch command by binding ParseResult values to the SDK request and CLI output contract.
    type Watch() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous watch action when System.CommandLine dispatches the parsed command.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) =
            task {
                try
                    if isCheckRequested parseResult then
                        let! existingGraceWatchStatus = getGraceWatchStatus ()

                        match existingGraceWatchStatus with
                        | Some _ ->
                            logToAnsiConsole Colors.Important "GraceWatch is running."
                            raise (WatchCommandExit 0)
                        | None ->
                            logToAnsiConsole Colors.Error "GraceWatch is not running."
                            raise (WatchCommandExit -1)

                    let! claimedGraceWatchStatus = tryClaimGraceWatchInterprocessFile ()

                    if not claimedGraceWatchStatus then
                        logToAnsiConsole Colors.Error "GraceWatch is already running."
                        raise (WatchCommandExit -1)

                    // Create the FileSystemWatcher, but don't enable it yet.
                    use rootDirectoryFileSystemWatcher = createFileSystemWatcher (Current().RootDirectory)

                    use created =
                        Observable
                            .FromEventPattern<FileSystemEventArgs>(rootDirectoryFileSystemWatcher, "Created")
                            .Select(fun e -> e.EventArgs)
                            .Subscribe(OnCreated)

                    use changed =
                        Observable
                            .FromEventPattern<FileSystemEventArgs>(rootDirectoryFileSystemWatcher, "Changed")
                            .Select(fun e -> e.EventArgs)
                            .Subscribe(OnChanged)

                    use deleted =
                        Observable
                            .FromEventPattern<FileSystemEventArgs>(rootDirectoryFileSystemWatcher, "Deleted")
                            .Select(fun e -> e.EventArgs)
                            .Subscribe(OnDeleted)

                    use renamed =
                        Observable
                            .FromEventPattern<RenamedEventArgs>(rootDirectoryFileSystemWatcher, "Renamed")
                            .Select(fun e -> e.EventArgs)
                            .Subscribe(OnRenamed)

                    use errored =
                        Observable
                            .FromEventPattern<ErrorEventArgs>(rootDirectoryFileSystemWatcher, "Error")
                            .Select(fun e -> e.EventArgs)
                            .Subscribe(OnError) // I want all of the errors.

                    Directory.CreateDirectory(Path.GetDirectoryName(updateInProgressFileName ()))
                    |> ignore

                    use updateInProgressFileSystemWatcher = createFileSystemWatcher (Path.GetDirectoryName(updateInProgressFileName ()))

                    use updateInProgressChanged =
                        Observable
                            .FromEventPattern<FileSystemEventArgs>(updateInProgressFileSystemWatcher, "Created")
                            .Select(fun e -> e.EventArgs)
                            .Subscribe(OnGraceUpdateInProgressCreated)

                    use updateInProgressDeleted =
                        Observable
                            .FromEventPattern<FileSystemEventArgs>(updateInProgressFileSystemWatcher, "Deleted")
                            .Select(fun e -> e.EventArgs)
                            .Subscribe(OnGraceUpdateInProgressDeleted)

                    // Load the Grace Index file.
                    let! status = readGraceStatusFile ()
                    graceStatus <- status
                    updateGraceStatusDirectoryIds graceStatus

                    // Create the inter-process communication file.
                    do! updateGraceWatchInterprocessFile graceStatus (Some graceStatusDirectoryIds)

                    // Enable the FileSystemWatcher.
                    rootDirectoryFileSystemWatcher.EnableRaisingEvents <- true
                    updateInProgressFileSystemWatcher.EnableRaisingEvents <- true

                    let timerTimeSpan = TimeSpan.FromSeconds(1.0)

                    logToAnsiConsole Colors.Verbose $"The change processor timer will tick every {timerTimeSpan.TotalSeconds:F1} seconds."

                    // Open a SignalR connection to the server.
                    let signalRUrl = Uri($"{Current().ServerUri}/notifications")
                    logToConsole $"signalRUrl: {signalRUrl}."

                    match! getSignalRAccessToken () with
                    | Error message -> raise (InvalidOperationException(message))
                    | Ok _ -> ()

                    use signalRConnection = createSignalRConnection signalRUrl

                    let mutable watchedParentBranchId = BranchId.Empty

                    use notifyRepository =
                        signalRConnection.On<RepositoryId, ReferenceId>(
                            "NotifyRepository",
                            fun repositoryId referenceId ->
                                (task { logToAnsiConsole Colors.Highlighted $"ReferenceId {referenceId} was created in repository {repositoryId}." }) :> Task
                        )

                    use serverToClient =
                        signalRConnection.On<string>(
                            "ServerToClientMessage",
                            (fun message -> logToAnsiConsole Colors.Important $"From Grace Server: {message}")
                        )

                    use notifyAutomationEvent =
                        signalRConnection.On<AutomationEventEnvelope>(
                            "NotifyAutomationEvent",
                            fun envelope ->
                                (task {
                                    if envelope.EventType = AutomationEventType.PromotionSetApplied then
                                        try
                                            use document = JsonDocument.Parse(envelope.DataJson)
                                            let root = document.RootElement

                                            /// Tries to map parse guid property and returns a GraceError instead of throwing on unsupported input.
                                            let tryParseGuidProperty (propertyName: string) : Guid option =
                                                let mutable propertyValue = Unchecked.defaultof<JsonElement>

                                                if root.TryGetProperty(propertyName, &propertyValue) then
                                                    let propertyText = propertyValue.GetString()
                                                    let mutable parsedGuid = Guid.Empty

                                                    if
                                                        String.IsNullOrWhiteSpace propertyText |> not
                                                        && Guid.TryParse(propertyText, &parsedGuid)
                                                    then
                                                        Some parsedGuid
                                                    else
                                                        Option.None
                                                else
                                                    Option.None

                                            let targetBranchId =
                                                tryParseGuidProperty "targetBranchId"
                                                |> Option.defaultValue BranchId.Empty

                                            let terminalReferenceId =
                                                tryParseGuidProperty "terminalPromotionReferenceId"
                                                |> Option.defaultValue ReferenceId.Empty

                                            if watchedParentBranchId = targetBranchId
                                               && watchedParentBranchId <> BranchId.Empty then
                                                logToAnsiConsole
                                                    Colors.Highlighted
                                                    $"Parent branch {watchedParentBranchId} received terminal promotion {terminalReferenceId}; starting auto-rebase."

                                                let! currentStatus = readGraceStatusFile ()
                                                let! _ = Branch.rebaseHandler (parseResult |> getNormalizedIdsAndNames) currentStatus
                                                ()
                                        with
                                        | ex ->
                                            logToAnsiConsole
                                                Colors.Error
                                                $"Failed to process automation event payload for {envelope.EventType}: {Markup.Escape(ex.Message)}."
                                })
                                :> Task
                        )

                    use notifyOnSave =
                        signalRConnection.On<BranchName, BranchName, BranchId, ReferenceId>(
                            "NotifyOnSave",
                            fun branchName parentBranchName parentBranchId referenceId ->
                                (task {
                                    logToAnsiConsole
                                        Colors.Highlighted
                                        $"Branch {branchName} with parent branch {parentBranchName} has a new save; referenceId: {referenceId}."
                                })
                                :> Task
                        )

                    use notifyOnCheckpoint =
                        signalRConnection.On<BranchName, BranchName, BranchId, ReferenceId>(
                            "NotifyOnCheckpoint",
                            fun branchName parentBranchName parentBranchId referenceId ->
                                (task {
                                    logToAnsiConsole
                                        Colors.Highlighted
                                        $"Branch {branchName} with parent branch {parentBranchName} has a new checkpoint; referenceId: {referenceId}."
                                })
                                :> Task
                        )

                    use notifyOnCommit =
                        signalRConnection.On<BranchName, BranchName, BranchId, ReferenceId>(
                            "NotifyOnCommit",
                            fun branchName parentBranchName parentBranchId referenceId ->
                                (task {
                                    logToAnsiConsole
                                        Colors.Highlighted
                                        $"Branch {branchName} with parent branch {parentBranchName} has a new commit; referenceId: {referenceId}."
                                })
                                :> Task
                        )

                    signalRConnection.add_Closed (fun ex -> task { logToAnsiConsole Colors.Error $"SignalR connection closed: {ex.Message}." })

                    signalRConnection.add_Reconnecting (fun ex -> task { logToAnsiConsole Colors.Important $"SignalR connection reconnecting: {ex.Message}." })

                    signalRConnection.add_Reconnected (fun connectionId ->
                        task { logToAnsiConsole Colors.Important $"SignalR connection reconnected: {connectionId}." })

                    do! signalRConnection.StartAsync(cancellationToken)
                    do! signalRConnection.InvokeAsync("RegisterRepository", Current().RepositoryId, cancellationToken)

                    logToAnsiConsole
                        Colors.Highlighted
                        $"SignalR Hub connection state: {signalRConnection.State}. Listening for changes in repository {Current().RepositoryName} ({Current().RepositoryId}); connectionId: {signalRConnection.ConnectionId}."

                    // Get the parent BranchId so we can tell SignalR what to notify us about.
                    let branchGetParameters =
                        GetBranchParameters(
                            OwnerId = $"{Current().OwnerId}",
                            OrganizationId = $"{Current().OrganizationId}",
                            RepositoryId = $"{Current().RepositoryId}",
                            BranchId = $"{Current().BranchId}"
                        )

                    match! Branch.GetParentBranch branchGetParameters with
                    | Ok returnValue ->
                        let parentBranchDto = returnValue.ReturnValue
                        watchedParentBranchId <- parentBranchDto.BranchId

                        do! signalRConnection.InvokeAsync("RegisterParentBranch", Current().BranchId, parentBranchDto.BranchId, cancellationToken)

                        logToAnsiConsole
                            Colors.Highlighted
                            $"SignalR Hub connection state: {signalRConnection.State}. Listening for changes in parent branch {parentBranchDto.BranchName} ({parentBranchDto.BranchId}); connectionId: {signalRConnection.ConnectionId}."
                    | Error error ->
                        logToAnsiConsole Colors.Error $"Failed to retrieve branch metadata. Cannot connect to SignalR Hub."

                        logToAnsiConsole Colors.Error $"{Markup.Escape(error.ToString())}"

                    // Check for changes that occurred while not running.
                    logToAnsiConsole Colors.Verbose $"Scanning for differences."
                    let! differences = scanForDifferences graceStatus // <--- This always finds the directories with updated write times, but we never update GraceStatus below..

                    if differences |> Seq.isEmpty then
                        logToAnsiConsole Colors.Verbose $"Already up-to-date."
                    else
                        logToAnsiConsole Colors.Verbose $"Found {differences.Count} differences."

                    for difference in differences do
                        queueStartupDifferenceForWatch difference

                    // Process any changes that occurred while not running.
                    graceStatus <- GraceStatus.Default
                    do! processChangedFiles ()

                    // Create a timer to process the file changes detected by the FileSystemWatcher.
                    // This timer is the reason that there's a delay in stopping `grace watch`.
                    logToAnsiConsole Colors.Verbose $"Starting timer."
                    use periodicTimer = new PeriodicTimer(timerTimeSpan)
                    let! tick = periodicTimer.WaitForNextTickAsync()
                    let mutable previousGC = getCurrentInstant ()
                    let mutable ticked = true

                    while ticked
                          && not (cancellationToken.IsCancellationRequested) do
                        // Grace Status may have changed from branch switch, or other commands.
                        if graceStatusHasChanged then
                            let! updatedGraceStatus = readGraceStatusFile ()
                            graceStatus <- updatedGraceStatus
                            updateGraceStatusDirectoryIds graceStatus
                            do! updateGraceWatchInterprocessFile graceStatus (Some graceStatusDirectoryIds)
                            //logToAnsiConsole Colors.Important $"Setting graceStatusHasChanged to false in OnWatch(). Current value: {graceStatusHasChanged}."
                            graceStatusHasChanged <- false

                        do! processChangedFiles ()
                        let! tick = periodicTimer.WaitForNextTickAsync()
                        ticked <- tick

                        // About once a minute, do a full GC to be kind with our memory usage. This is for looks, not for function.
                        //
                        // In .NET, when a computer has lots of available memory, and there's no memory pressure signal from the OS, GC doesn't happen much, if at all.
                        //   With no memory pressure, `grace watch` wouldn't bother releasing its unused heap after handling events like saves and auto-rebases.
                        //   Seeing that kind of memory usage could lead to uninformed people saying things like, "OMG, `grace watch` takes up so much memory!"
                        //   Actually, `grace watch` only grabs a lot of memory at the moment of processing events. As soon as we're done, we want to release that
                        //   memory back to the OS, that means forcing a full GC.
                        //
                        // Because of DATAS (see https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/datas), it may take more than one GC.Collect()
                        //   call to fully compact the heap (and that's OK). If we weren't being so aggressive about memory usage, we would just let DATAS compute
                        //   a close-to-optimal heap size on its own over time.
                        if
                            previousGC
                            < getCurrentInstant().Minus(Duration.FromMinutes(1.0))
                        then
                            //let memoryBeforeGC = Process.GetCurrentProcess().WorkingSet64
                            GC.Collect(2, GCCollectionMode.Forced, blocking = true, compacting = true)
                            //logToAnsiConsole Colors.Verbose $"Memory before GC: {memoryBeforeGC:N0}; after: {Process.GetCurrentProcess().WorkingSet64:N0}."
                            previousGC <- getCurrentInstant ()

                    if parseResult |> json then
                        return renderJsonResult parseResult true true "Watch completed because cancellation was requested or the timer stopped."
                    else
                        return 0
                with
                | WatchCommandExit exitCode -> return exitCode
                | :? HttpRequestException as httpEx when
                    httpEx.StatusCode.HasValue
                    && httpEx.StatusCode.Value = HttpStatusCode.Unauthorized
                    ->
                    let message =
                        $"SignalR negotiation failed with 401 Unauthorized. Run `grace authenticate login` or set {Constants.EnvironmentVariables.GraceToken}, then retry `grace watch`."

                    if parseResult |> json then
                        return renderJsonError parseResult message
                    else
                        logToAnsiConsole Colors.Error message
                        return -1
                | :? InvalidOperationException as invalidOperationException when
                    invalidOperationException.Message.Contains
                        (
                            "access token",
                            StringComparison.OrdinalIgnoreCase
                        )
                    ->
                    if parseResult |> json then
                        return renderJsonError parseResult invalidOperationException.Message
                    else
                        logToAnsiConsole Colors.Error $"{Markup.Escape(invalidOperationException.Message)}"
                        return -1
                | ex ->
                    //let exceptionMarkup = Markup.Escape($"{ExceptionResponse.Create ex}").Replace("\\\\", @"\").Replace("\r\n", Environment.NewLine)
                    //logToAnsiConsole Colors.Error $"{exceptionMarkup}"
                    if parseResult |> json then
                        return renderJsonError parseResult $"{ExceptionResponse.Create ex}"
                    else
                        let exceptionSettings = ExceptionSettings(Format = ExceptionFormats.Default)
                        // Need to fill in some exception styles here.
                        AnsiConsole.WriteException(ex, exceptionSettings)
                        return -1
            }

    let Build =
        /// Adds options or child commands to a command definition.
        let addCommonOptions (command: Command) =
            command
            |> addOption Options.ownerName
            |> addOption Options.ownerId
            |> addOption Options.organizationName
            |> addOption Options.organizationId
            |> addOption Options.repositoryName
            |> addOption Options.repositoryId
            |> addOption Options.branchName
            |> addOption Options.branchId
            |> addOption Options.check

        // Create main command and aliases, if any.
        let watchCommand =
            new Command("watch", Description = "Watches your repo for changes, and uploads new versions of your files.")
            |> addCommonOptions

        watchCommand.Aliases.Add("w")
        watchCommand.Action <- Watch()
        watchCommand
