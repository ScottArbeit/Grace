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

    let mutable private graceWatchRuntimeModeValue = int GraceWatchRuntimeMode.HealthyIncremental

    /// Reads the process-local Grace Watch runtime mode used to gate scans and filesystem observations.
    let private currentGraceWatchRuntimeMode () = enum<GraceWatchRuntimeMode> (Volatile.Read(&graceWatchRuntimeModeValue))

    /// Updates the process-local Grace Watch runtime mode used by event handlers and status application.
    let private setGraceWatchRuntimeMode mode =
        Interlocked.Exchange(&graceWatchRuntimeModeValue, int mode)
        |> ignore

    /// Sets Grace Watch runtime mode for tests that exercise state-gated observation behavior.
    let internal setGraceWatchRuntimeModeForWatchTests mode = setGraceWatchRuntimeMode mode

    /// Reads Grace Watch runtime mode for tests that exercise confidence-loss transitions.
    let internal currentGraceWatchRuntimeModeForWatchTests () = currentGraceWatchRuntimeMode ()

    /// Checks whether the current runtime mode can record filesystem observations.
    let private canCaptureFilesystemObservation () = isGraceWatchObservationCaptureLegal (currentGraceWatchRuntimeMode ())

    let mutable private appendWatchJournalObservationsForWatch =
        fun observations -> Grace.CLI.LocalStateDb.appendWatchJournalObservations (Current().GraceStatusFile) observations

    let mutable private advanceWatchJournalAppliedThroughSequencesForWatch =
        fun sequences -> Grace.CLI.LocalStateDb.advanceWatchJournalAppliedThroughContiguousSequences (Current().GraceStatusFile) sequences

    let mutable private recoverWatchJournalBoundaryGapForWatch =
        fun _ _ ->
            task {
                let! _ = Grace.CLI.LocalStateDb.clearWatchJournal (Current().GraceStatusFile)
                return ()
            }

    let mutable private pruneWatchJournalRetentionForWatch = fun () -> Grace.CLI.LocalStateDb.pruneWatchJournalRetention (Current().GraceStatusFile)

    let mutable private recoverWatchJournalForStartupForWatch =
        fun scope -> Grace.CLI.LocalStateDb.recoverWatchJournalForStartup (Current().GraceStatusFile) scope

    let mutable private quarantineWatchJournalSequencesForWatch =
        fun sequences reason -> Grace.CLI.LocalStateDb.quarantineWatchJournalSequences (Current().GraceStatusFile) sequences reason

    let mutable private recordWatchLifecycleEventForWatch = fun event -> Grace.CLI.LocalStateDb.recordWatchLifecycleEvent (Current().GraceStatusFile) event

    /// Installs durable journal clients used by append-before-apply ordering tests.
    let internal setWatchJournalClientsForWatchTests appendObservations advanceAppliedSequences =
        appendWatchJournalObservationsForWatch <- appendObservations
        advanceWatchJournalAppliedThroughSequencesForWatch <- advanceAppliedSequences

    /// Installs durable journal maintenance clients used by boundary repair and retention tests.
    let internal setWatchJournalMaintenanceClientsForWatchTests recoverBoundaryGap pruneRetention =
        recoverWatchJournalBoundaryGapForWatch <- recoverBoundaryGap
        pruneWatchJournalRetentionForWatch <- pruneRetention

    /// Installs startup recovery clients used by replay and quarantine ordering tests.
    let internal setWatchJournalStartupClientsForWatchTests recoverStartup recordLifecycle =
        recoverWatchJournalForStartupForWatch <- recoverStartup
        recordWatchLifecycleEventForWatch <- recordLifecycle

    /// Restores durable journal clients after tests replace append or boundary behavior.
    let internal resetWatchJournalClientsForWatchTests () =
        appendWatchJournalObservationsForWatch <-
            fun observations -> Grace.CLI.LocalStateDb.appendWatchJournalObservations (Current().GraceStatusFile) observations

        advanceWatchJournalAppliedThroughSequencesForWatch <-
            fun sequences -> Grace.CLI.LocalStateDb.advanceWatchJournalAppliedThroughContiguousSequences (Current().GraceStatusFile) sequences

        recoverWatchJournalBoundaryGapForWatch <-
            fun _ _ ->
                task {
                    let! _ = Grace.CLI.LocalStateDb.clearWatchJournal (Current().GraceStatusFile)
                    return ()
                }

        pruneWatchJournalRetentionForWatch <- fun () -> Grace.CLI.LocalStateDb.pruneWatchJournalRetention (Current().GraceStatusFile)

        recoverWatchJournalForStartupForWatch <- fun scope -> Grace.CLI.LocalStateDb.recoverWatchJournalForStartup (Current().GraceStatusFile) scope

        quarantineWatchJournalSequencesForWatch <-
            fun sequences reason -> Grace.CLI.LocalStateDb.quarantineWatchJournalSequences (Current().GraceStatusFile) sequences reason

        recordWatchLifecycleEventForWatch <- fun event -> Grace.CLI.LocalStateDb.recordWatchLifecycleEvent (Current().GraceStatusFile) event

    /// Reports that a filesystem observation was ignored because the current runtime mode cannot capture it.
    let private logObservationSuppressed fullPath =
        logToAnsiConsole Colors.Verbose $"Grace Watch ignored filesystem observation for {fullPath} while runtime mode is {currentGraceWatchRuntimeMode ()}."

    let private uploadedFileVersions = ConcurrentDictionary<RelativePath * Sha256Hash * Blake3Hash, FileVersion>()

    /// Reads uploaded file version identity data needed by the command workflow without changing remote state.
    let private uploadedFileVersionIdentity (fileVersion: FileVersion) = (fileVersion.RelativePath, fileVersion.Sha256Hash, fileVersion.Blake3Hash)

    let private processedFileRelativePathsPendingStatusLock = obj ()

    let private processedFileRelativePathsPendingStatus = List<RelativePath>()

    let private canceledFileUploadDeleteRelativePathsLock = obj ()

    let private canceledFileUploadDeleteRelativePaths = List<RelativePath>()

    /// Makes an uploaded identity available under the casing that status application will write to GraceStatus.
    let private recordUploadedFileVersionForCanonicalPath (canonicalRelativePath: RelativePath) (uploadedFileVersion: FileVersion) =
        let canonicalRelativePath = RelativePath(normalizeFilePath $"{canonicalRelativePath}")

        if uploadedFileVersion.RelativePath
           <> canonicalRelativePath then
            let canonicalUploadedFileVersion = FileVersion()
            canonicalUploadedFileVersion.Class <- uploadedFileVersion.Class
            canonicalUploadedFileVersion.RelativePath <- canonicalRelativePath
            canonicalUploadedFileVersion.Sha256Hash <- uploadedFileVersion.Sha256Hash
            canonicalUploadedFileVersion.Blake3Hash <- uploadedFileVersion.Blake3Hash
            canonicalUploadedFileVersion.IsBinary <- uploadedFileVersion.IsBinary
            canonicalUploadedFileVersion.Size <- uploadedFileVersion.Size
            canonicalUploadedFileVersion.CreatedAt <- uploadedFileVersion.CreatedAt
            canonicalUploadedFileVersion.BlobUri <- uploadedFileVersion.BlobUri
            canonicalUploadedFileVersion.ContentReference <- uploadedFileVersion.ContentReference
            uploadedFileVersions[uploadedFileVersionIdentity canonicalUploadedFileVersion] <- canonicalUploadedFileVersion

    /// Records uploaded file identity data for watch tests that replace the storage upload client.
    let internal recordUploadedFileVersionForWatchTests (fileVersion: FileVersion) =
        uploadedFileVersions[uploadedFileVersionIdentity fileVersion] <- fileVersion

    /// Lists uploaded identity paths so watch tests can prove casing aliases are present or removed.
    let internal uploadedFileVersionRelativePathsForWatchTests () =
        uploadedFileVersions.Values
        |> Seq.map (fun fileVersion -> $"{fileVersion.RelativePath}")
        |> Seq.sort
        |> Seq.toArray

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

    /// Tracks startup-replayed journal sequences by normalized difference so status application does not append duplicates.
    let mutable private pendingStatusDifferenceReplaySequences = Dictionary<string, Queue<int64>>(StringComparer.Ordinal)

    let mutable private quarantinedWatchObservationCount = 0

    let mutable private graceWatchResyncGeneration = 0L

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
    let mutable private graceStatusRefreshGeneration = 0L
    let private watchStatusPublishLock = obj ()
    let mutable private lastPublishedHasPendingWatchWork: bool option = None

    /// Coordinates file deleted behavior for this CLI command path.
    let fileDeleted filePath = logToConsole $"In Delete: filePath: {filePath}"

    /// Evaluates is not directory against parsed options and command state.
    let isNotDirectory path = not <| Directory.Exists(path)
    /// Coordinates update in progress behavior for this CLI command path.
    let updateInProgress () = File.Exists(updateInProgressFileName ())
    /// Coordinates update not in progress behavior for this CLI command path.
    let updateNotInProgress () = not <| updateInProgress ()

    /// Reports whether the deleted callback path is an update marker path owned by any branch temp directory.
    let private isUpdateMarkerPath (markerFileName: string) =
        String.Equals(Path.GetFileName(markerFileName), Constants.UpdateInProgressFileName, StringComparison.OrdinalIgnoreCase)

    let private graceUpdateMarkerDeletionLock = obj ()
    let private graceUpdateMarkerDeletedObservationWindow = TimeSpan.FromSeconds(30.0)
    let private recentGraceUpdateMarkerCompletedUtc = List<DateTime>()
    let private observedGraceUpdateMarkerLastWriteUtc = Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)

    /// Gets the completion sidecar written before the Grace update marker is removed.
    let private updateMarkerCompletedFileName () = updateInProgressFileName () + ".completed"

    /// Gets the completion sidecar paired with a specific update marker callback path.
    let private updateMarkerCompletedFileNameForMarker markerFileName = markerFileName + ".completed"

    /// Records the completed Grace-owned update instant so delayed callbacks can be classified by switch authority.
    let private recordGraceUpdateMarkerCompletedUtc completedUtc =
        lock graceUpdateMarkerDeletionLock (fun () ->
            if completedUtc <> DateTime.MinValue then
                let cutoffUtc =
                    DateTime.UtcNow
                    - graceUpdateMarkerDeletedObservationWindow

                recentGraceUpdateMarkerCompletedUtc.RemoveAll(fun recordedUtc -> recordedUtc < cutoffUtc)
                |> ignore

                recentGraceUpdateMarkerCompletedUtc.Add(completedUtc))

    /// Clears every recent marker-deletion fact when tests reset Watch state.
    let private clearGraceUpdateMarkerDeletedUtcForReset () =
        lock graceUpdateMarkerDeletionLock (fun () ->
            recentGraceUpdateMarkerCompletedUtc.Clear()
            observedGraceUpdateMarkerLastWriteUtc.Clear())

    /// Removes the current marker sidecar when a new marker supersedes stale sidecar evidence.
    let private clearCurrentGraceUpdateMarkerCompletedSidecar () =
        try
            let completedFileName = updateMarkerCompletedFileName ()

            if File.Exists(completedFileName) then File.Delete(completedFileName)
        with
        | :? IOException -> ()
        | :? UnauthorizedAccessException -> ()

    /// Sets the recent Grace update marker deletion instant for deterministic Watch callback tests.
    let internal recordGraceUpdateMarkerDeletedUtcForTests deletedUtc =
        clearGraceUpdateMarkerDeletedUtcForReset ()
        recordGraceUpdateMarkerCompletedUtc deletedUtc

    /// Reads a marker completion sidecar for the specific marker path that produced the callback.
    let private tryReadGraceUpdateMarkerCompletedUtcFrom completedFileName =
        try
            if File.Exists(completedFileName) then
                match DateTime.TryParse(File.ReadAllText(completedFileName), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
                | true, completedUtc -> Some completedUtc
                | false, _ -> None
            else
                None
        with
        | :? IOException -> None
        | :? UnauthorizedAccessException -> None

    /// Reads the marker completion sidecar when a file callback arrives before Watch observes marker deletion.
    let private tryReadGraceUpdateMarkerCompletedUtc () =
        updateMarkerCompletedFileName ()
        |> tryReadGraceUpdateMarkerCompletedUtcFrom

    /// Reads the completion instant for the marker whose deletion callback is being processed.
    let private tryReadGraceUpdateMarkerCompletedUtcForMarker markerFileName =
        markerFileName
        |> updateMarkerCompletedFileNameForMarker
        |> tryReadGraceUpdateMarkerCompletedUtcFrom

    /// Records marker file freshness while the marker still exists so deletion cannot trust stale sidecars.
    let private observeGraceUpdateMarkerInstance markerFileName =
        try
            if File.Exists(markerFileName) then
                lock graceUpdateMarkerDeletionLock (fun () -> observedGraceUpdateMarkerLastWriteUtc[markerFileName] <- File.GetLastWriteTimeUtc(markerFileName))
        with
        | :? IOException -> ()
        | :? UnauthorizedAccessException -> ()

    /// Retires marker freshness state after a deletion callback consumes or rejects it.
    let private forgetObservedGraceUpdateMarkerInstance markerFileName =
        lock graceUpdateMarkerDeletionLock (fun () ->
            observedGraceUpdateMarkerLastWriteUtc.Remove(markerFileName)
            |> ignore)

    /// Reports whether the completed sidecar was written for the current marker instance Watch observed.
    let private currentUpdateMarkerMatchesCompletedSidecar completedUtc =
        try
            let currentMarkerFileName = updateInProgressFileName ()

            if File.Exists(currentMarkerFileName) then
                File.GetLastWriteTimeUtc(currentMarkerFileName)
                <= completedUtc
            else
                lock graceUpdateMarkerDeletionLock (fun () ->
                    match observedGraceUpdateMarkerLastWriteUtc.TryGetValue(currentMarkerFileName) with
                    | true, markerLastWriteUtc -> markerLastWriteUtc <= completedUtc
                    | false, _ -> false)
        with
        | :? IOException -> false
        | :? UnauthorizedAccessException -> false

    /// Classifies whether a deleted marker sidecar can be trusted for branch transition authority.
    type private DeletedMarkerSidecarAuthority =
        | ObservedCurrentMarker
        | ObservedStaleMarker
        | UnobservedMarker

    /// Proves whether a deleted marker sidecar was written for the marker instance Watch observed.
    let private classifyDeletedMarkerCompletedSidecar markerFileName completedUtc =
        lock graceUpdateMarkerDeletionLock (fun () ->
            match observedGraceUpdateMarkerLastWriteUtc.TryGetValue(markerFileName) with
            | true, markerLastWriteUtc ->
                observedGraceUpdateMarkerLastWriteUtc.Remove(markerFileName)
                |> ignore

                if markerLastWriteUtc <= completedUtc then
                    ObservedCurrentMarker
                else
                    ObservedStaleMarker
            | false, _ -> UnobservedMarker)

    /// Reports whether a deleted marker path is the current transition marker Watch was expected to observe.
    let private deletedMarkerIsCurrentMarker markerFileName = String.Equals(markerFileName, updateInProgressFileName (), StringComparison.OrdinalIgnoreCase)

    /// Reports whether the on-disk repository config has moved beyond Watch's cached branch identity.
    let private persistedConfigurationChangedSinceWatchCached () =
        try
            let cached = Current()

            match tryInspectCurrentDirectoryConfiguration () with
            | Ok inspection ->
                let persisted = inspection.Configuration

                cached.RepositoryId <> persisted.RepositoryId
                || not (String.Equals(cached.RepositoryName, persisted.RepositoryName, StringComparison.OrdinalIgnoreCase))
                || cached.BranchId <> persisted.BranchId
                || not (String.Equals(cached.BranchName, persisted.BranchName, StringComparison.OrdinalIgnoreCase))
                || not (String.Equals(cached.RootDirectory, persisted.RootDirectory, StringComparison.OrdinalIgnoreCase))
            | Error _ -> false
        with
        | _ -> false

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

    /// Provides the fallback path comparison until the repository volume can be probed.
    let private defaultWatchPathComparison () =
        if OperatingSystem.IsWindows() then
            StringComparison.OrdinalIgnoreCase
        else
            StringComparison.Ordinal

    let mutable private watchPathComparison = defaultWatchPathComparison ()
    let mutable private watchPathComparisonOverride: StringComparison option = None
    let mutable private watchPathComparisonConfiguredRoot: string option = None

    /// Removes uploaded identities after their matching watch work is either applied or proven content-equivalent.
    let private removeUploadedFileVersionsForPaths (relativePaths: seq<RelativePath>) =
        let normalizedPaths =
            relativePaths
            |> Seq.map normalizeFilePath
            |> Seq.toArray

        for uploadedFileVersion in uploadedFileVersions.Values.ToArray() do
            let normalizedUploadedPath = normalizeFilePath $"{uploadedFileVersion.RelativePath}"

            if normalizedPaths
               |> Array.exists (fun relativePath -> String.Equals(relativePath, normalizedUploadedPath, watchPathComparison)) then
                let mutable removedFileVersion = Unchecked.defaultof<FileVersion>

                uploadedFileVersions.TryRemove(uploadedFileVersionIdentity uploadedFileVersion, &removedFileVersion)
                |> ignore

    /// Checks whether the repository volume resolves differently-cased names to the same file.
    let private detectRepositoryPathCaseInsensitiveLookup (rootDirectory: string) =
        let mutable probePath = String.Empty
        let mutable alternateProbePath = String.Empty

        try
            try
                let probeDirectory =
                    let graceDirectory = Current().GraceDirectory

                    if String.IsNullOrWhiteSpace(graceDirectory) then
                        Path.Combine(rootDirectory, Constants.GraceConfigDirectory)
                    else
                        graceDirectory

                Directory.CreateDirectory(probeDirectory)
                |> ignore

                let probeName = $"grace-watch-case-probe-{Guid.NewGuid():N}"
                probePath <- Path.Combine(probeDirectory, probeName.ToLowerInvariant())
                alternateProbePath <- Path.Combine(probeDirectory, probeName.ToUpperInvariant())

                File.WriteAllText(probePath, String.Empty)
                File.Exists(alternateProbePath)
            with
            | _ -> OperatingSystem.IsWindows()
        finally
            for path in [| probePath; alternateProbePath |] do
                try
                    if
                        not (String.IsNullOrWhiteSpace(path))
                        && File.Exists(path)
                    then
                        File.Delete(path)
                with
                | _ -> ()

    let mutable private repositoryPathCaseInsensitiveLookupForWatch = detectRepositoryPathCaseInsensitiveLookup

    /// Aligns watch path matching with the active repository volume's case behavior.
    let private configureWatchPathComparisonForCurrentRepository () =
        match watchPathComparisonOverride with
        | Some comparison -> watchPathComparison <- comparison
        | None ->
            let normalizedRoot = Path.GetFullPath(Current().RootDirectory)

            if watchPathComparisonConfiguredRoot
               <> Some normalizedRoot then
                watchPathComparison <-
                    if repositoryPathCaseInsensitiveLookupForWatch normalizedRoot then
                        StringComparison.OrdinalIgnoreCase
                    else
                        StringComparison.Ordinal

                watchPathComparisonConfiguredRoot <- Some normalizedRoot

    /// Compares delete-trigger paths against GraceStatus with the legacy status delete semantics.
    let private deletedPathComparison = StringComparison.OrdinalIgnoreCase

    /// Finds the tracked file entry that owns a repository-relative path with the requested comparison.
    let private tryFindTrackedFileWithComparison (comparison: StringComparison) (status: GraceStatus) (relativePath: RelativePath) =
        let normalizedRelativePath = normalizeRelativePath relativePath

        status.Index.Values
        |> Seq.collect (fun directoryVersion -> directoryVersion.Files)
        |> Seq.tryFind (fun fileVersion -> String.Equals(normalizeRelativePath fileVersion.RelativePath, normalizedRelativePath, comparison))

    /// Finds the tracked directory entry that owns a repository-relative path with the requested comparison.
    let private tryFindTrackedDirectoryWithComparison (comparison: StringComparison) (status: GraceStatus) (relativePath: RelativePath) =
        let normalizedRelativePath = normalizeRelativePath relativePath

        status.Index.Values
        |> Seq.tryFind (fun directoryVersion -> String.Equals(normalizeRelativePath directoryVersion.RelativePath, normalizedRelativePath, comparison))

    /// Coordinates tracked deleted path kind behavior for this CLI command path.
    let private trackedDeletedPathKind (status: GraceStatus) (relativePath: string) =
        let deletedRelativePath = RelativePath(normalizeRelativePath relativePath)

        let isTrackedFile =
            tryFindTrackedFileWithComparison deletedPathComparison status deletedRelativePath
            |> Option.isSome

        let isTrackedDirectory =
            tryFindTrackedDirectoryWithComparison deletedPathComparison status deletedRelativePath
            |> Option.isSome

        match isTrackedFile, isTrackedDirectory with
        | true, _ -> DeletedFile
        | false, true -> DeletedDirectory
        | false, false -> DeletedPathKindUnknown

    let mutable private readGraceStatusFileForDeletedPathClassification = readGraceStatusFile

    let mutable private readGraceStatusFileForPendingWorkTransition = readGraceStatusFile
    let mutable private readGraceStatusFileForTransitionCompletion = readGraceStatusFile

    /// Records the branch-scoped SignalR identity that Watch currently trusts for auto-rebase events.
    type internal SignalRBranchSubscriptionState = { BranchId: BranchId; ParentBranchId: BranchId; Generation: int64 }

    /// Names the branch and generation that an in-flight SignalR parent refresh is allowed to update.
    type internal SignalRBranchSubscriptionRefreshAuthority = { BranchId: BranchId; Generation: int64 }

    let private emptySignalRBranchSubscription generation = { BranchId = BranchId.Empty; ParentBranchId = BranchId.Empty; Generation = generation }

    let private signalRBranchSubscriptionLock = obj ()
    let mutable private signalRBranchSubscription = emptySignalRBranchSubscription 0L
    let mutable private signalRBranchSubscriptionRefreshNeededAfterTransition = false
    let private signalRSubscriptionRefreshLock = obj ()
    let mutable private refreshSignalRBranchSubscriptionsForTransitionCompletion = ignore

    /// Clears branch-scoped SignalR trust while preserving whether transition recovery still owes a refresh attempt.
    let private clearSignalRBranchSubscriptionWithRefreshFlag refreshNeededAfterTransition =
        lock signalRBranchSubscriptionLock (fun () ->
            let generation = signalRBranchSubscription.Generation + 1L
            signalRBranchSubscription <- emptySignalRBranchSubscription generation
            signalRBranchSubscriptionRefreshNeededAfterTransition <- refreshNeededAfterTransition
            generation)

    /// Captures the exact branch and generation that may commit an in-flight parent refresh.
    let private beginSignalRBranchSubscriptionRefresh () =
        lock signalRBranchSubscriptionLock (fun () ->
            let generation = signalRBranchSubscription.Generation + 1L
            let currentBranchId = Current().BranchId
            signalRBranchSubscription <- emptySignalRBranchSubscription generation

            { BranchId = currentBranchId; Generation = generation })

    /// Replaces the watched SignalR branch identity only if the refresh still belongs to the current branch.
    let private trySetSignalRBranchSubscription refreshAuthority parentBranchId =
        lock signalRBranchSubscriptionLock (fun () ->
            let currentBranchId = Current().BranchId

            if currentBranchId = refreshAuthority.BranchId
               && signalRBranchSubscription.Generation = refreshAuthority.Generation then
                signalRBranchSubscription <- { BranchId = refreshAuthority.BranchId; ParentBranchId = parentBranchId; Generation = refreshAuthority.Generation }

                signalRBranchSubscriptionRefreshNeededAfterTransition <- false
                true
            else
                false)

    /// Clears branch-scoped SignalR trust while Watch recalculates the current parent branch.
    let private clearSignalRBranchSubscription () =
        clearSignalRBranchSubscriptionWithRefreshFlag false
        |> ignore

    /// Clears branch-scoped SignalR trust because transition completion invalidated the old parent.
    let private clearSignalRBranchSubscriptionForTransition () =
        clearSignalRBranchSubscriptionWithRefreshFlag true
        |> ignore

    /// Records that a transition parent refresh was attempted and left local parent trust empty.
    let private completeSignalRBranchSubscriptionRefreshWithoutTrust () =
        lock signalRBranchSubscriptionLock (fun () -> signalRBranchSubscriptionRefreshNeededAfterTransition <- false)

    /// Reports whether transition recovery still owes a SignalR parent refresh attempt.
    let private signalRBranchSubscriptionRefreshNeededForTransition () =
        lock signalRBranchSubscriptionLock (fun () -> signalRBranchSubscriptionRefreshNeededAfterTransition)

    /// Reads the watched SignalR branch identity without exposing the mutable cell.
    let internal signalRBranchSubscriptionForWatchTests () = lock signalRBranchSubscriptionLock (fun () -> signalRBranchSubscription)

    /// Replaces the watched SignalR branch identity in Watch tests without opening a HubConnection.
    let internal setSignalRBranchSubscriptionForWatchTests branchId parentBranchId =
        lock signalRBranchSubscriptionLock (fun () ->
            let generation = signalRBranchSubscription.Generation + 1L

            signalRBranchSubscription <- { BranchId = branchId; ParentBranchId = parentBranchId; Generation = generation }

            signalRBranchSubscriptionRefreshNeededAfterTransition <- false)

    /// Begins a test-controlled SignalR parent refresh for the current branch.
    let internal beginSignalRBranchSubscriptionRefreshForWatchTests () = beginSignalRBranchSubscriptionRefresh ()

    /// Attempts to commit a test-controlled SignalR parent refresh.
    let internal trySetSignalRBranchSubscriptionForWatchTests refreshAuthority parentBranchId = trySetSignalRBranchSubscription refreshAuthority parentBranchId

    /// Clears SignalR parent trust through the same transition path used by branch switch recovery.
    let internal clearSignalRBranchSubscriptionForTransitionForWatchTests () = clearSignalRBranchSubscriptionForTransition ()

    /// Reports whether transition recovery still owes a SignalR parent refresh attempt.
    let internal signalRBranchSubscriptionRefreshNeededForTransitionForWatchTests () = signalRBranchSubscriptionRefreshNeededForTransition ()

    /// Reports whether a SignalR automation event targets the parent branch Watch currently trusts.
    let internal signalRAutomationEventTargetsWatchedParentBranchForWatchTests targetBranchId =
        lock signalRBranchSubscriptionLock (fun () ->
            signalRBranchSubscription.ParentBranchId = targetBranchId
            && signalRBranchSubscription.ParentBranchId
               <> BranchId.Empty)

    /// Returns the current branch-scoped SignalR trust snapshot when the event targets the watched parent.
    let private signalRBranchSubscriptionForWatchedParentBranch targetBranchId =
        lock signalRBranchSubscriptionLock (fun () ->
            if signalRBranchSubscription.ParentBranchId = targetBranchId
               && signalRBranchSubscription.BranchId
                  <> BranchId.Empty
               && signalRBranchSubscription.ParentBranchId
                  <> BranchId.Empty then
                Some signalRBranchSubscription
            else
                Option.None)

    /// Reports whether the SignalR trust snapshot still belongs to the branch about to mutate its working tree.
    let private signalRBranchSubscriptionStillTrusted trustedSubscription =
        lock signalRBranchSubscriptionLock (fun () -> signalRBranchSubscription = trustedSubscription)

    /// Installs the active SignalR parent-branch refresh operation used after branch identity changes.
    let private registerSignalRSubscriptionRefresh refresh =
        lock signalRSubscriptionRefreshLock (fun () -> refreshSignalRBranchSubscriptionsForTransitionCompletion <- refresh)

        { new IDisposable with
            member _.Dispose() = lock signalRSubscriptionRefreshLock (fun () -> refreshSignalRBranchSubscriptionsForTransitionCompletion <- ignore)
        }

    /// Runs the current SignalR subscription refresh after transition identity reloads.
    let private refreshSignalRSubscriptionsAfterTransitionCompletion () =
        let refresh = lock signalRSubscriptionRefreshLock (fun () -> refreshSignalRBranchSubscriptionsForTransitionCompletion)
        refresh ()

    /// Installs the SignalR parent-branch refresh operation used by transition-completion tests.
    let internal setSignalRSubscriptionRefreshForWatchTests refresh =
        lock signalRSubscriptionRefreshLock (fun () -> refreshSignalRBranchSubscriptionsForTransitionCompletion <- refresh)

    /// Restores SignalR subscription refresh state after transition-completion tests.
    let internal resetSignalRSubscriptionRefreshForWatchTests () =
        lock signalRSubscriptionRefreshLock (fun () -> refreshSignalRBranchSubscriptionsForTransitionCompletion <- ignore)
        clearSignalRBranchSubscription ()

    let private updateMarkerWatcherRebindLock = obj ()
    let mutable private rebindUpdateMarkerWatcherForTransitionCompletion = ignore
    let private branchTransitionCompletionProbeLock = obj ()
    let mutable private branchTransitionCompletionAfterRetireProbe = ignore
    let private previousBranchIpcDowngradeRetryProbeLock = obj ()
    let mutable private previousBranchIpcDowngradeRetryProbe = ignore

    /// Installs the active update-marker watcher retarget operation used after branch identity changes.
    let private registerUpdateMarkerWatcherRebind rebind =
        lock updateMarkerWatcherRebindLock (fun () -> rebindUpdateMarkerWatcherForTransitionCompletion <- rebind)

        { new IDisposable with
            member _.Dispose() = lock updateMarkerWatcherRebindLock (fun () -> rebindUpdateMarkerWatcherForTransitionCompletion <- ignore)
        }

    /// Runs the current update-marker watcher retarget operation after transition identity reloads.
    let private rebindUpdateMarkerWatcherAfterTransitionCompletion () =
        let rebind = lock updateMarkerWatcherRebindLock (fun () -> rebindUpdateMarkerWatcherForTransitionCompletion)
        rebind ()

    /// Installs the update-marker watcher retarget operation used by transition-completion tests.
    let internal setUpdateMarkerWatcherRebindForWatchTests rebind =
        lock updateMarkerWatcherRebindLock (fun () -> rebindUpdateMarkerWatcherForTransitionCompletion <- rebind)

    /// Restores the default no-op update-marker watcher retarget operation after transition-completion tests.
    let internal resetUpdateMarkerWatcherRebindForWatchTests () =
        lock updateMarkerWatcherRebindLock (fun () -> rebindUpdateMarkerWatcherForTransitionCompletion <- ignore)

    /// Runs the transition-boundary probe after old branch IPC retirement but before branch identity reload.
    let private runBranchTransitionCompletionAfterRetireProbe () =
        let probe = lock branchTransitionCompletionProbeLock (fun () -> branchTransitionCompletionAfterRetireProbe)
        probe ()

    /// Installs a transition-boundary probe used to prove publishers cannot race old branch IPC retirement.
    let internal setBranchTransitionCompletionAfterRetireProbeForWatchTests probe =
        lock branchTransitionCompletionProbeLock (fun () -> branchTransitionCompletionAfterRetireProbe <- probe)

    /// Restores the transition-boundary probe to the production no-op behavior after Watch tests.
    let internal resetBranchTransitionCompletionAfterRetireProbeForWatchTests () =
        lock branchTransitionCompletionProbeLock (fun () -> branchTransitionCompletionAfterRetireProbe <- ignore)

    /// Runs the current previous-branch IPC downgrade retry probe after a failed verification.
    let private runPreviousBranchIpcDowngradeRetryProbe () =
        let probe = lock previousBranchIpcDowngradeRetryProbeLock (fun () -> previousBranchIpcDowngradeRetryProbe)
        probe ()

    /// Installs a retry probe used to make previous-branch IPC downgrade verification deterministic in tests.
    let internal setPreviousBranchIpcDowngradeRetryProbeForWatchTests probe =
        lock previousBranchIpcDowngradeRetryProbeLock (fun () -> previousBranchIpcDowngradeRetryProbe <- probe)

    /// Restores the previous-branch IPC downgrade retry probe to the production no-op behavior after Watch tests.
    let internal resetPreviousBranchIpcDowngradeRetryProbeForWatchTests () =
        lock previousBranchIpcDowngradeRetryProbeLock (fun () -> previousBranchIpcDowngradeRetryProbe <- ignore)

    /// Removes stale source-branch Watch IPC before transition completion publishes under the target identity.
    let mutable private retirePreviousBranchWatchIpcForTransitionCompletion = fun previousIpcFileName -> File.Delete(previousIpcFileName)

    /// Installs the previous-branch IPC retirement operation used by transition-completion tests.
    let internal setRetirePreviousBranchWatchIpcForTransitionCompletionForWatchTests retirePreviousBranchIpc =
        retirePreviousBranchWatchIpcForTransitionCompletion <- retirePreviousBranchIpc

    /// Restores previous-branch IPC retirement to the production file-delete behavior after Watch tests.
    let internal resetRetirePreviousBranchWatchIpcForTransitionCompletionForWatchTests () =
        retirePreviousBranchWatchIpcForTransitionCompletion <- fun previousIpcFileName -> File.Delete(previousIpcFileName)

    /// Reloads repository configuration after another Grace process changes the current branch.
    let private reloadConfigurationForTransitionCompletion () =
        resetConfiguration ()
        clearShouldIgnoreCache ()
        Current() |> ignore
        configureWatchPathComparisonForCurrentRepository ()

    let mutable private enumerateFilesForDirectoryUpload = fun directoryPath -> Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)

    /// Checks whether a directory and each repository-relative ancestor remains eligible for watch indexing.
    let private directoryAndAncestorsShouldNotBeIgnored (directoryPath: string) =
        let rootDirectory =
            Current().RootDirectory
            |> Path.GetFullPath
            |> Path.TrimEndingDirectorySeparator

        let rootDirectoryWithSeparator = rootDirectory + string Path.DirectorySeparatorChar

        let normalizedDirectoryPath =
            directoryPath
            |> Path.GetFullPath
            |> Path.TrimEndingDirectorySeparator

        let isInsideRepository =
            normalizedDirectoryPath.Equals(rootDirectory, watchPathComparison)
            || normalizedDirectoryPath.StartsWith(rootDirectoryWithSeparator, watchPathComparison)

        let mutable eligible = isInsideRepository
        let mutable currentDirectory = DirectoryInfo(normalizedDirectoryPath)

        while eligible
              && not (isNull currentDirectory)
              && not (currentDirectory.FullName.Equals(rootDirectory, watchPathComparison)) do
            if shouldNotIgnoreDirectory currentDirectory.FullName then
                currentDirectory <- currentDirectory.Parent
            else
                eligible <- false

        eligible

    /// Enumerates directory-add candidates under an affected subtree while pruning ignored directory branches.
    let private enumerateDirectoriesForDirectoryStatusAddWithPruning directoryPath =
        seq {
            let pendingDirectories = Stack<string>()
            pendingDirectories.Push(directoryPath)

            while pendingDirectories.Count > 0 do
                let parentDirectory = pendingDirectories.Pop()

                for childDirectory in Directory.EnumerateDirectories(parentDirectory) do
                    if directoryAndAncestorsShouldNotBeIgnored childDirectory then
                        yield childDirectory
                        pendingDirectories.Push(childDirectory)
        }

    let mutable private enumerateDirectoriesForDirectoryStatusAdd = enumerateDirectoriesForDirectoryStatusAddWithPruning

    /// Records an uploaded watch path until the status update pass drains every file upload batch.
    let private addProcessedFileRelativePathPendingStatus (relativePath: RelativePath) =
        lock processedFileRelativePathsPendingStatusLock (fun () ->
            let normalizedRelativePath = normalizeRelativePath relativePath

            let alreadyRecorded =
                processedFileRelativePathsPendingStatus
                |> Seq.exists (fun existing -> String.Equals(normalizeRelativePath existing, normalizedRelativePath, watchPathComparison))

            if not alreadyRecorded then
                processedFileRelativePathsPendingStatus.Add(relativePath))

    /// Captures uploaded watch paths that still need status application.
    let private processedFileRelativePathsPendingStatusSnapshot () =
        lock processedFileRelativePathsPendingStatusLock (fun () -> processedFileRelativePathsPendingStatus.ToList())

    /// Clears uploaded watch paths after their status effects have been applied or proven unnecessary.
    let private clearProcessedFileRelativePathsPendingStatus (relativePaths: seq<RelativePath>) =
        lock processedFileRelativePathsPendingStatusLock (fun () ->
            for relativePath in relativePaths do
                let normalizedRelativePath = normalizeRelativePath relativePath

                processedFileRelativePathsPendingStatus.RemoveAll (fun existing ->
                    String.Equals(normalizeRelativePath existing, normalizedRelativePath, watchPathComparison))
                |> ignore)

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

    /// Reports whether a marker completion instant is recent enough to suppress delayed Watch callbacks.
    let private isRecentGraceUpdateMarkerCompletion completedUtc =
        completedUtc <> DateTime.MinValue
        && DateTime.UtcNow - completedUtc
           <= graceUpdateMarkerDeletedObservationWindow

    /// Reads the switch-owned completion instant that should govern delayed Watch callback suppression.
    let private tryRecentGraceUpdateMarkerCompletedUtc () =
        let recordedUtc =
            lock graceUpdateMarkerDeletionLock (fun () ->
                let cutoffUtc =
                    DateTime.UtcNow
                    - graceUpdateMarkerDeletedObservationWindow

                recentGraceUpdateMarkerCompletedUtc.RemoveAll(fun completedUtc -> completedUtc < cutoffUtc)
                |> ignore

                recentGraceUpdateMarkerCompletedUtc
                |> Seq.sortDescending
                |> Seq.tryHead)

        let currentSidecarUtc =
            match tryReadGraceUpdateMarkerCompletedUtc () with
            | Some completedUtc when
                isRecentGraceUpdateMarkerCompletion completedUtc
                && currentUpdateMarkerMatchesCompletedSidecar completedUtc
                ->
                Some completedUtc
            | _ -> None

        match currentSidecarUtc, recordedUtc with
        | Some currentCompletedUtc, Some recordedCompletedUtc when isRecentGraceUpdateMarkerCompletion recordedCompletedUtc ->
            Some(max currentCompletedUtc recordedCompletedUtc)
        | Some currentCompletedUtc, _ -> Some currentCompletedUtc
        | None, Some recordedCompletedUtc when isRecentGraceUpdateMarkerCompletion recordedCompletedUtc -> Some recordedCompletedUtc
        | None, _ -> None

    /// Reports whether a missing path is already absent from the post-switch GraceStatus snapshot.
    let private completedSwitchRemovedPathFromGraceStatus fullPath =
        match repositoryRelativePath fullPath with
        | None -> false
        | Some relativePath ->
            try
                let completedStatus =
                    readGraceStatusFileForDeletedPathClassification()
                        .GetAwaiter()
                        .GetResult()

                match trackedDeletedPathKind completedStatus relativePath with
                | DeletedPathKindUnknown -> true
                | DeletedFile
                | DeletedDirectory
                | DeletedPathStatusUnavailable -> false
            with
            | _ -> false

    /// Reports whether an existing path belongs to the post-switch GraceStatus snapshot.
    let private completedSwitchTracksExistingPathFromGraceStatus fullPath =
        match repositoryRelativePath fullPath with
        | None -> false
        | Some relativePath ->
            try
                let completedStatus =
                    readGraceStatusFileForDeletedPathClassification()
                        .GetAwaiter()
                        .GetResult()

                match trackedDeletedPathKind completedStatus relativePath with
                | DeletedFile -> File.Exists fullPath
                | DeletedDirectory -> Directory.Exists fullPath
                | DeletedPathKindUnknown
                | DeletedPathStatusUnavailable -> false
            with
            | _ -> false

    /// Identifies delayed callbacks for writes that completed while a Grace update marker was still present.
    let private isDelayedGraceOwnedFileObservation fullPath =
        match tryRecentGraceUpdateMarkerCompletedUtc () with
        | None -> false
        | Some markerCompletedUtc when
            not (File.Exists fullPath)
            && not (Directory.Exists fullPath)
            ->
            completedSwitchRemovedPathFromGraceStatus fullPath
        | Some markerCompletedUtc ->
            completedSwitchTracksExistingPathFromGraceStatus fullPath
            && (try
                    let lastWriteTimeUtc =
                        if File.Exists fullPath then
                            File.GetLastWriteTimeUtc(fullPath)
                        else
                            Directory.GetLastWriteTimeUtc(fullPath)

                    lastWriteTimeUtc <= markerCompletedUtc
                with
                | :? IOException -> false
                | :? UnauthorizedAccessException -> false)

    /// Coordinates set grace status for watch tests behavior for this CLI command path.
    let internal setGraceStatusForWatchTests status = graceStatus <- status
    /// Replaces Watch's cached directory identity set after a trusted GraceStatus reload.
    let internal updateGraceStatusDirectoryIds (status: GraceStatus) = graceStatusDirectoryIds <- status.Index.Keys.ToHashSet()
    /// Reads the in-memory Grace Status snapshot so transition-publication tests can prove state isolation.
    let internal graceStatusForWatchTests () = graceStatus
    /// Reads the in-memory Grace Status directory identities so transition-publication tests can prove state isolation.
    let internal graceStatusDirectoryIdsForWatchTests () = graceStatusDirectoryIds
    /// Reports whether the Watch timer still owes a Grace Status refresh pass.
    let internal graceStatusHasChangedForWatchTests () = graceStatusHasChanged

    /// Checks whether set grace status has changed for watch tests is true for the parsed command input.
    let internal setGraceStatusHasChangedForWatchTests hasChanged =
        if hasChanged then
            Interlocked.Increment(&graceStatusRefreshGeneration)
            |> ignore

            graceStatusHasChanged <- true
        else
            Interlocked.Exchange(&graceStatusRefreshGeneration, 0L)
            |> ignore

            graceStatusHasChanged <- false

    /// Coordinates set read grace status file for watch tests behavior for this CLI command path.
    let internal setReadGraceStatusFileForWatchTests readStatusFile =
        readGraceStatusFileForDeletedPathClassification <- readStatusFile
        readGraceStatusFileForTransitionCompletion <- readStatusFile

    /// Sets the Grace Status reader used by pending-work transition tests.
    let internal setReadGraceStatusFileForPendingWorkTransitionForWatchTests readStatusFile = readGraceStatusFileForPendingWorkTransition <- readStatusFile
    /// Sets the Grace Status reader used by branch-transition completion tests.
    let internal setReadGraceStatusFileForTransitionCompletionForWatchTests readStatusFile = readGraceStatusFileForTransitionCompletion <- readStatusFile
    /// Reads set enumerate files for directory upload for watch tests data needed by the command workflow without changing remote state.
    let internal setEnumerateFilesForDirectoryUploadForWatchTests enumerateFiles = enumerateFilesForDirectoryUpload <- enumerateFiles

    /// Coordinates set watch path comparison for watch tests behavior for this CLI command path.
    let internal setWatchPathComparisonForWatchTests comparison =
        watchPathComparison <- comparison
        watchPathComparisonOverride <- Some comparison

    /// Installs the repository case-sensitivity detector used by watch tests.
    let internal setRepositoryPathCaseInsensitiveLookupForWatchTests detector =
        repositoryPathCaseInsensitiveLookupForWatch <- detector
        watchPathComparisonConfiguredRoot <- None

    /// Finds the tracked file entry that owns a repository-relative path in GraceStatus.
    let private tryFindTrackedFile (status: GraceStatus) (relativePath: RelativePath) = tryFindTrackedFileWithComparison watchPathComparison status relativePath

    /// Checks whether GraceStatus already tracks a repository-relative directory path.
    let private isTrackedDirectory (status: GraceStatus) (relativePath: RelativePath) =
        let normalizedRelativePath = normalizeRelativePath relativePath

        status.Index.Values
        |> Seq.exists (fun directoryVersion -> String.Equals(normalizeRelativePath directoryVersion.RelativePath, normalizedRelativePath, watchPathComparison))

    /// Finds the tracked directory entry that owns a repository-relative path in GraceStatus.
    let private tryFindTrackedDirectory (status: GraceStatus) (relativePath: RelativePath) =
        tryFindTrackedDirectoryWithComparison watchPathComparison status relativePath

    /// Uses the longest tracked ancestor casing before status application performs exact path lookups.
    let private canonicalizeTrackedAncestorCasing (status: GraceStatus) (relativePath: RelativePath) =
        let normalizedRelativePath = normalizeRelativePath relativePath
        let segments = normalizedRelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
        let mutable canonicalRelativePath = relativePath
        let mutable foundTrackedAncestor = false

        if segments.Length > 0 then
            for prefixLength in segments.Length .. -1 .. 1 do
                if not foundTrackedAncestor then
                    let ancestorRelativePath = RelativePath(String.Join("/", segments[0 .. prefixLength - 1]))

                    match tryFindTrackedDirectory status ancestorRelativePath with
                    | Some trackedAncestorDirectory ->
                        let trackedAncestorPath = normalizeRelativePath trackedAncestorDirectory.RelativePath

                        let canonicalPath =
                            if prefixLength = segments.Length then
                                trackedAncestorPath
                            else
                                let suffixPath = String.Join("/", segments[prefixLength..])
                                $"{trackedAncestorPath}/{suffixPath}"

                        canonicalRelativePath <- RelativePath canonicalPath
                        foundTrackedAncestor <- true
                    | None -> ()

        canonicalRelativePath

    /// Checks whether the uploaded file identity would change the tracked GraceStatus file content.
    let private uploadedFileContentChanged (trackedFile: LocalFileVersion) (uploadedFile: FileVersion) =
        uploadedFile.Sha256Hash <> trackedFile.Sha256Hash
        || (trackedFile.Blake3Hash <> Blake3Hash String.Empty
            && uploadedFile.Blake3Hash <> trackedFile.Blake3Hash)

    /// Describes the final filesystem kind for a path when event-derived status work is applied.
    type private FinalPathKind =
        | FinalPathMissing
        | FinalPathFile
        | FinalPathDirectory

    let mutable private fileExistsForWatchFinalPath = File.Exists
    let mutable private directoryExistsForWatchFinalPath = Directory.Exists

    /// Reads the final filesystem kind for a repository-relative path.
    let private finalPathKind (relativePath: RelativePath) =
        let fullPath = Path.Combine(Current().RootDirectory, $"{relativePath}")

        if fileExistsForWatchFinalPath fullPath then FinalPathFile
        elif directoryExistsForWatchFinalPath fullPath then FinalPathDirectory
        else FinalPathMissing

    /// Installs final path existence readers used by watch classification tests.
    let internal setFinalPathExistsForWatchTests fileExists directoryExists =
        fileExistsForWatchFinalPath <- fileExists
        directoryExistsForWatchFinalPath <- directoryExists

    /// Checks whether final path state still has the same kind as a delete difference.
    let private finalPathMatchesEntryType entryType relativePath =
        match entryType, finalPathKind relativePath with
        | FileSystemEntryType.File, FinalPathFile -> true
        | FileSystemEntryType.Directory, FinalPathDirectory -> true
        | _ -> false

    /// Checks whether a repository-relative path is inside a directory trigger.
    let private isPathUnderDirectoryTrigger (directoryRelativePath: RelativePath) (candidateRelativePath: RelativePath) =
        let normalizedDirectoryPath =
            normalizeRelativePath directoryRelativePath
            |> fun path -> path.TrimEnd('/')

        let normalizedCandidatePath = normalizeRelativePath candidateRelativePath

        normalizedCandidatePath.StartsWith(normalizedDirectoryPath + "/", watchPathComparison)

    /// Checks directory existence using the active watch path comparison so case-insensitive event paths still match disk.
    let private directoryExistsUsingWatchPathComparison (relativePath: RelativePath) =
        let normalizedRelativePath = normalizeRelativePath relativePath
        let fullPath = Path.Combine(Current().RootDirectory, normalizedRelativePath)

        if Directory.Exists(fullPath) then
            true
        else
            let segments = normalizedRelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            let mutable currentDirectory = Path.GetFullPath(Current().RootDirectory)
            let mutable exists = segments.Length > 0
            let mutable index = 0

            while exists && index < segments.Length do
                if Directory.Exists(currentDirectory) then
                    let matchingDirectory =
                        Directory.EnumerateDirectories(currentDirectory)
                        |> Seq.tryFind (fun candidate -> String.Equals(Path.GetFileName(candidate), segments[index], watchPathComparison))

                    match matchingDirectory with
                    | Some directory ->
                        currentDirectory <- directory
                        index <- index + 1
                    | None -> exists <- false
                else
                    exists <- false

            exists

    /// Describes whether a completed upload can be matched against the current final path content.
    type private UploadedFinalFileVersionResolution =
        | UploadedFinalFileVersionFound of FileVersion
        | UploadedFinalFileVersionUnavailable
        | UploadedFinalFileVersionUnmatched
        | UploadedFinalFileVersionMissing

    let mutable private createLocalFileVersionForWatchStatus = createLocalFileVersion

    /// Finds the uploaded identity that matches the final file content for a processed watch path.
    let private tryFindUploadedFinalFileVersion (relativePath: RelativePath) =
        task {
            let fullPath = Path.Combine(Current().RootDirectory, $"{relativePath}")

            if File.Exists(fullPath) then
                match! createLocalFileVersionForWatchStatus (FileInfo(fullPath)) with
                | Some localFileVersion ->
                    let identity = (localFileVersion.RelativePath, localFileVersion.Sha256Hash, localFileVersion.Blake3Hash)
                    let mutable uploadedFileVersion = Unchecked.defaultof<FileVersion>

                    if uploadedFileVersions.TryGetValue(identity, &uploadedFileVersion) then
                        return UploadedFinalFileVersionFound uploadedFileVersion
                    else
                        return UploadedFinalFileVersionUnmatched
                | None -> return UploadedFinalFileVersionUnavailable
            else
                return UploadedFinalFileVersionMissing
        }

    /// Installs the local file-version reader used by watch status derivation tests.
    let internal setCreateLocalFileVersionForWatchTests createLocalFileVersionClient = createLocalFileVersionForWatchStatus <- createLocalFileVersionClient

    /// Lists untracked directory add differences through the selected repository-relative path segment.
    let private deriveDirectoryAddDifferencesThroughSegment (status: GraceStatus) (relativePath: RelativePath) lastSegmentIndex =
        let differences = List<FileSystemDifference>()
        let normalizedRelativePath = normalizeRelativePath relativePath
        let segments = normalizedRelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)

        if segments.Length > 0 && lastSegmentIndex >= 0 then
            for index in 0 .. min lastSegmentIndex (segments.Length - 1) do
                let parentRelativePath = RelativePath(String.Join("/", segments[0..index]))
                let parentDifferenceRelativePath = canonicalizeTrackedAncestorCasing status parentRelativePath

                let alreadyAdded =
                    differences
                    |> Seq.exists (fun difference ->
                        difference.FileSystemEntryType = FileSystemEntryType.Directory
                        && difference.DifferenceType = Add
                        && String.Equals(normalizeRelativePath difference.RelativePath, normalizeRelativePath parentDifferenceRelativePath, watchPathComparison))

                if not alreadyAdded
                   && not (isTrackedDirectory status parentDifferenceRelativePath)
                   && directoryExistsUsingWatchPathComparison parentDifferenceRelativePath then
                    differences.Add(FileSystemDifference.Create Add FileSystemEntryType.Directory parentDifferenceRelativePath)

        differences

    /// Lists missing parent directories that must be added before a new uploaded file can link into GraceStatus.
    let private deriveParentDirectoryAddDifferences (status: GraceStatus) (relativePath: RelativePath) =
        let segmentCount =
            (normalizeRelativePath relativePath).Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries
            )
                .Length

        deriveDirectoryAddDifferencesThroughSegment status relativePath (segmentCount - 2)

    /// Lists new non-ignored directories under the observed directory without scanning outside that subtree.
    let private tryDeriveDirectorySubtreeAddDifferences (status: GraceStatus) (directoryPath: string) =
        let differences = List<FileSystemDifference>()

        /// Adds one directory add difference when GraceStatus does not already track the final path.
        let addDirectoryDifference fullPath =
            match repositoryRelativePath fullPath with
            | Some relativePath ->
                let canonicalRelativePath =
                    RelativePath relativePath
                    |> canonicalizeTrackedAncestorCasing status

                let segmentCount =
                    (normalizeRelativePath canonicalRelativePath)
                        .Split(
                        '/',
                        StringSplitOptions.RemoveEmptyEntries
                    )
                        .Length

                let directoryAddDifferences = deriveDirectoryAddDifferencesThroughSegment status canonicalRelativePath (segmentCount - 1)

                for directoryAddDifference in directoryAddDifferences do
                    let directoryDifferenceFullPath = Path.Combine(Current().RootDirectory, normalizeRelativePath directoryAddDifference.RelativePath)

                    let alreadyAdded =
                        differences
                        |> Seq.exists (fun difference ->
                            difference.FileSystemEntryType = FileSystemEntryType.Directory
                            && difference.DifferenceType = Add
                            && String.Equals(
                                normalizeRelativePath difference.RelativePath,
                                normalizeRelativePath directoryAddDifference.RelativePath,
                                watchPathComparison
                            ))

                    if not alreadyAdded
                       && directoryAndAncestorsShouldNotBeIgnored directoryDifferenceFullPath then
                        differences.Add(directoryAddDifference)
            | None -> ()

        if
            Directory.Exists(directoryPath)
            && directoryAndAncestorsShouldNotBeIgnored directoryPath
        then
            try
                addDirectoryDifference directoryPath

                for childDirectoryPath in enumerateDirectoriesForDirectoryStatusAdd directoryPath do
                    if shouldNotIgnoreDirectory childDirectoryPath then
                        addDirectoryDifference childDirectoryPath

                differences, true
            with
            | ex ->
                logToAnsiConsole
                    Colors.Error
                    $"Unable to enumerate directory subtree {directoryPath}; status update will retry after directory adds can be queued. {Markup.Escape(ex.Message)}"

                differences, false
        else
            differences, true

    /// Derives add and change differences from uploads already completed by the watch loop.
    let private deriveUploadedFileDifferences (status: GraceStatus) (processedFilePaths: seq<RelativePath>) =
        task {
            let differences = List<FileSystemDifference>()
            let unresolvedFilePaths = List<RelativePath>()

            for relativePath in
                processedFilePaths
                |> Seq.distinctBy normalizeRelativePath do
                match! tryFindUploadedFinalFileVersion relativePath with
                | UploadedFinalFileVersionFound uploadedFileVersion ->
                    match tryFindTrackedFile status uploadedFileVersion.RelativePath with
                    | Some trackedFile when uploadedFileContentChanged trackedFile uploadedFileVersion ->
                        recordUploadedFileVersionForCanonicalPath trackedFile.RelativePath uploadedFileVersion
                        differences.Add(FileSystemDifference.Create Change FileSystemEntryType.File trackedFile.RelativePath)
                    | Some _ -> ()
                    | None ->
                        differences.AddRange(deriveParentDirectoryAddDifferences status uploadedFileVersion.RelativePath)
                        let addRelativePath = canonicalizeTrackedAncestorCasing status uploadedFileVersion.RelativePath
                        recordUploadedFileVersionForCanonicalPath addRelativePath uploadedFileVersion
                        differences.Add(FileSystemDifference.Create Add FileSystemEntryType.File addRelativePath)
                | UploadedFinalFileVersionUnavailable
                | UploadedFinalFileVersionUnmatched -> unresolvedFilePaths.Add(relativePath)
                | UploadedFinalFileVersionMissing -> ()

            return differences, unresolvedFilePaths
        }

    /// Derives delete differences from GraceStatus while rejecting stale deletes for paths that exist again.
    let private deriveDeleteDifferences (status: GraceStatus) (statusTriggerSnapshot: StatusUpdateTriggerSnapshot array) =
        let differences = List<FileSystemDifference>()

        for statusTrigger in statusTriggerSnapshot do
            let relativePath = RelativePath statusTrigger.RelativePath

            match trackedDeletedPathKind status statusTrigger.RelativePath with
            | DeletedFile ->
                let trackedFile =
                    match tryFindTrackedFileWithComparison StringComparison.Ordinal status relativePath with
                    | Some exactTrackedFile -> Some exactTrackedFile
                    | None -> tryFindTrackedFileWithComparison deletedPathComparison status relativePath

                match trackedFile with
                | Some trackedFile ->
                    if not (finalPathMatchesEntryType FileSystemEntryType.File trackedFile.RelativePath) then
                        differences.Add(FileSystemDifference.Create Delete FileSystemEntryType.File trackedFile.RelativePath)
                | None ->
                    if not (finalPathMatchesEntryType FileSystemEntryType.File relativePath) then
                        differences.Add(FileSystemDifference.Create Delete FileSystemEntryType.File relativePath)
            | DeletedDirectory ->
                let trackedDirectoryRelativePath =
                    match tryFindTrackedDirectoryWithComparison StringComparison.Ordinal status relativePath with
                    | Some exactTrackedDirectory -> exactTrackedDirectory.RelativePath
                    | None ->
                        match tryFindTrackedDirectoryWithComparison deletedPathComparison status relativePath with
                        | Some trackedDirectory -> trackedDirectory.RelativePath
                        | None -> relativePath

                if not (finalPathMatchesEntryType FileSystemEntryType.Directory trackedDirectoryRelativePath) then
                    differences.Add(FileSystemDifference.Create Delete FileSystemEntryType.Directory trackedDirectoryRelativePath)
            | DeletedPathKindUnknown
            | DeletedPathStatusUnavailable -> ()

        differences

    /// Models the differences that can be applied and the queued work they resolve.
    type private StatusDifferencesForApply = { Applicable: List<FileSystemDifference>; Resolved: List<FileSystemDifference> }

    /// Returns the root BLAKE3 hash that durable Watch journal identity expects for replay.
    let private rootDirectoryBlake3HashForWatchJournal (status: GraceStatus) =
        let mutable rootDirectoryVersion = LocalDirectoryVersion.Default

        if status.Index.TryGetValue(status.RootDirectoryId, &rootDirectoryVersion) then
            rootDirectoryVersion.Blake3Hash
        elif not (String.IsNullOrWhiteSpace(string status.RootDirectoryBlake3Hash)) then
            status.RootDirectoryBlake3Hash
        else
            Blake3Hash String.Empty

    /// Captures the current repository identity that makes normalized Watch journal rows replayable.
    let private currentWatchJournalScope (status: GraceStatus) : Grace.CLI.LocalStateDb.WatchJournalScope =
        {
            RepositoryId = Current().RepositoryId
            BranchId = Current().BranchId
            WorkspaceRoot = Path.GetFullPath(Current().RootDirectory)
            WatchRoot = Path.GetFullPath(Current().RootDirectory)
            RootDirectoryId = status.RootDirectoryId
            RootDirectoryBlake3Hash = rootDirectoryBlake3HashForWatchJournal status
            WatchMode = "repository-root"
        }

    /// Converts an already-normalized status difference into the replay payload owned by the local Watch journal.
    let private journalObservationForDifference (difference: FileSystemDifference) : Grace.CLI.LocalStateDb.WatchJournalObservation =
        {
            Scope = currentWatchJournalScope graceStatus
            DifferenceType = difference.DifferenceType
            EntryType = difference.FileSystemEntryType
            RelativePath = difference.RelativePath
        }

    /// Checks whether a pending uploaded file difference must be dropped because final path state no longer has a file.
    let private isStaleUploadedFileDifference (difference: FileSystemDifference) =
        difference.FileSystemEntryType = FileSystemEntryType.File
        && (difference.DifferenceType = Add
            || difference.DifferenceType = Change)
        && finalPathKind difference.RelativePath
           <> FinalPathFile

    /// Checks whether a pending parent directory add no longer exists in final path state.
    let private isStaleParentDirectoryAddDifference (difference: FileSystemDifference) =
        difference.FileSystemEntryType = FileSystemEntryType.Directory
        && difference.DifferenceType = Add
        && not (directoryExistsUsingWatchPathComparison difference.RelativePath)

    /// Checks whether a queued directory add was already incorporated by a fresher GraceStatus snapshot.
    let private isAlreadyTrackedDirectoryAddDifference (status: GraceStatus) (difference: FileSystemDifference) =
        difference.FileSystemEntryType = FileSystemEntryType.Directory
        && difference.DifferenceType = Add
        && isTrackedDirectory status difference.RelativePath

    /// Checks whether a status-only trigger covers the same path as a pending file difference.
    let private hasMatchingStatusTrigger (statusTriggerSnapshot: StatusUpdateTriggerSnapshot array) (difference: FileSystemDifference) =
        statusTriggerSnapshot
        |> Array.exists (fun statusTrigger ->
            String.Equals(normalizeRelativePath statusTrigger.RelativePath, normalizeRelativePath difference.RelativePath, watchPathComparison))

    /// Checks whether a status-only directory trigger covers a pending file difference.
    let private hasContainingStatusTrigger (statusTriggerSnapshot: StatusUpdateTriggerSnapshot array) (difference: FileSystemDifference) =
        statusTriggerSnapshot
        |> Array.exists (fun statusTrigger -> isPathUnderDirectoryTrigger (RelativePath statusTrigger.RelativePath) difference.RelativePath)

    /// Checks whether a completed file upload already waits for status application on this path.
    let private hasProcessedFileRelativePath (processedRelativePaths: List<RelativePath>) (relativePath: RelativePath) =
        let normalizedRelativePath = normalizeRelativePath relativePath

        processedRelativePaths
        |> Seq.exists (fun processedPath -> String.Equals(normalizeRelativePath processedPath, normalizedRelativePath, watchPathComparison))

    /// Checks whether startup recovery already owns the stale delete decision for this path.
    let private hasMatchingStartupFileDeleteDifference (startupPendingDifferences: List<FileSystemDifference>) (relativePath: RelativePath) =
        let normalizedRelativePath = normalizeRelativePath relativePath

        startupPendingDifferences
        |> Seq.exists (fun difference ->
            difference.DifferenceType = DifferenceType.Delete
            && difference.FileSystemEntryType = FileSystemEntryType.File
            && String.Equals(normalizeRelativePath difference.RelativePath, normalizedRelativePath, watchPathComparison))

    /// Finds the GraceStatus file entry represented by a status-only trigger.
    let private tryFindTrackedFileForStatusTrigger (status: GraceStatus) (relativePath: RelativePath) =
        match tryFindTrackedFileWithComparison StringComparison.Ordinal status relativePath with
        | Some exactTrackedFile -> Some exactTrackedFile
        | None -> tryFindTrackedFileWithComparison deletedPathComparison status relativePath

    /// Checks whether a stale delete trigger's final file content differs from GraceStatus.
    let private finalFileContentChangedFromTrackedStatus (status: GraceStatus) (relativePath: RelativePath) =
        task {
            match tryFindTrackedFileForStatusTrigger status relativePath with
            | Some trackedFile when finalPathMatchesEntryType FileSystemEntryType.File relativePath ->
                let fullPath = Path.Combine(Current().RootDirectory, $"{relativePath}")

                match! createLocalFileVersionForWatchStatus (FileInfo(fullPath)) with
                | Some finalFileVersion -> return uploadedFileContentChanged trackedFile finalFileVersion.ToFileVersion
                | None -> return false
            | _ -> return false
        }

    /// Records a delete-triggered cancellation that may need the final file re-uploaded if the delete proves stale.
    let private addCanceledFileUploadDeleteRelativePath (relativePath: RelativePath) =
        lock canceledFileUploadDeleteRelativePathsLock (fun () ->
            let normalizedRelativePath = normalizeRelativePath relativePath

            let alreadyRecorded =
                canceledFileUploadDeleteRelativePaths
                |> Seq.exists (fun existing -> String.Equals(normalizeRelativePath existing, normalizedRelativePath, watchPathComparison))

            if not alreadyRecorded then
                canceledFileUploadDeleteRelativePaths.Add(relativePath))

    /// Captures delete-triggered upload cancellations waiting for final path resolution.
    let private canceledFileUploadDeleteRelativePathsSnapshot () =
        lock canceledFileUploadDeleteRelativePathsLock (fun () -> canceledFileUploadDeleteRelativePaths.ToList())

    /// Clears delete-triggered upload cancellations after they are requeued or their status trigger drains.
    let private clearCanceledFileUploadDeleteRelativePaths (relativePaths: seq<RelativePath>) =
        lock canceledFileUploadDeleteRelativePathsLock (fun () ->
            for relativePath in relativePaths do
                let normalizedRelativePath = normalizeRelativePath relativePath

                canceledFileUploadDeleteRelativePaths.RemoveAll (fun existing ->
                    String.Equals(normalizeRelativePath existing, normalizedRelativePath, watchPathComparison))
                |> ignore)

    /// Removes stale differences when final path state proves newer same-path work superseded them.
    let private splitApplicableStatusDifferences
        (status: GraceStatus)
        (statusTriggerSnapshot: StatusUpdateTriggerSnapshot array)
        (differences: List<FileSystemDifference>)
        =
        let applicable = List<FileSystemDifference>()
        let resolved = List<FileSystemDifference>()

        for difference in differences do
            if difference.DifferenceType = Delete
               && finalPathMatchesEntryType difference.FileSystemEntryType difference.RelativePath then
                resolved.Add(difference)
            elif isStaleUploadedFileDifference difference
                 && (hasMatchingStatusTrigger statusTriggerSnapshot difference
                     || hasContainingStatusTrigger statusTriggerSnapshot difference) then
                resolved.Add(difference)
            elif isStaleParentDirectoryAddDifference difference then
                resolved.Add(difference)
            elif isAlreadyTrackedDirectoryAddDifference status difference then
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

    /// Records a directory whose contents must be enumerated before related status triggers can apply.
    let private enqueueDirectoryUploadRetry directoryPath =
        if
            Directory.Exists(directoryPath)
            && shouldNotIgnoreDirectory directoryPath
        then
            directoriesToProcess.TryAdd(directoryPath, ())
            |> ignore

    /// Queues every non-ignored file under a directory and reports whether enumeration reached a durable answer.
    let private tryEnqueueDirectoryContentsForUpload directoryPath =
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

    /// Requeues uploaded paths whose final content could not be matched while the final file still exists.
    let private reenqueueUnresolvedUploadedFinalFileVersions (relativePaths: seq<RelativePath>) =
        let requeuedRelativePaths = List<RelativePath>()

        for relativePath in
            relativePaths
            |> Seq.distinctBy normalizeRelativePath do
            let fullPath = Path.Combine(Current().RootDirectory, $"{relativePath}")

            if finalPathMatchesEntryType FileSystemEntryType.File relativePath
               && File.Exists(fullPath)
               && enqueueFileUpload fullPath then
                requeuedRelativePaths.Add(relativePath)

        requeuedRelativePaths

    /// Requeues uploads when stale delete triggers resolve to live final file or directory content.
    let private reenqueueUploadsForResolvedDeletes
        (status: GraceStatus)
        (statusTriggerSnapshot: StatusUpdateTriggerSnapshot array)
        (canceledRelativePaths: List<RelativePath>)
        (processedRelativePaths: List<RelativePath>)
        (startupPendingDifferences: List<FileSystemDifference>)
        =
        task {
            let requeuedRelativePaths = List<RelativePath>()
            let mutable index = 0

            while index < statusTriggerSnapshot.Length do
                let statusTrigger = statusTriggerSnapshot[index]
                let relativePath = RelativePath statusTrigger.RelativePath

                if not (hasProcessedFileRelativePath processedRelativePaths relativePath) then
                    let uploadWasCanceled =
                        canceledRelativePaths
                        |> Seq.exists (fun canceledPath ->
                            String.Equals(normalizeRelativePath canceledPath, normalizeRelativePath relativePath, watchPathComparison))

                    let! finalFileContentChanged =
                        if uploadWasCanceled
                           || hasMatchingStartupFileDeleteDifference startupPendingDifferences relativePath then
                            Task.FromResult(false)
                        else
                            finalFileContentChangedFromTrackedStatus status relativePath

                    if (uploadWasCanceled || finalFileContentChanged)
                       && finalPathMatchesEntryType FileSystemEntryType.File relativePath then
                        let fullPath = Path.Combine(Current().RootDirectory, $"{relativePath}")

                        if enqueueFileUpload fullPath then requeuedRelativePaths.Add(relativePath)
                    elif uploadWasCanceled
                         && finalPathMatchesEntryType FileSystemEntryType.Directory relativePath then
                        let fullPath = Path.Combine(Current().RootDirectory, $"{relativePath}")

                        if tryEnqueueDirectoryContentsForUpload fullPath then
                            requeuedRelativePaths.Add(relativePath)
                        else
                            enqueueDirectoryUploadRetry fullPath
                            requeuedRelativePaths.Add(relativePath)

                index <- index + 1

            clearCanceledFileUploadDeleteRelativePaths requeuedRelativePaths
            return requeuedRelativePaths
        }

    /// Reads remove pending file upload data needed by the command workflow without changing remote state.
    let private removePendingFileUpload filePath =
        let mutable generation = 0L

        filesToProcess.TryRemove(filePath, &generation)

    /// Cancels pending upload work covered by a delete or rename-old path before status observes the disappearance.
    let private cancelPendingUploadsForDeletedPath fullPath =
        let mutable canceledFileUpload = removePendingFileUpload fullPath

        match repositoryRelativePath fullPath with
        | Some relativePath -> if removePendingFileUpload relativePath then canceledFileUpload <- true
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
            let normalizedQueuedFile =
                try
                    Path.GetFullPath(queuedFile)
                with
                | _ -> queuedFile

            let removeQueuedFile =
                normalizedQueuedFile.Equals(normalizedFullPath, watchPathComparison)
                || normalizedQueuedFile.StartsWith(fullPathPrefix, watchPathComparison)
                || (match relativePathPrefix with
                    | Some prefix -> queuedFile.StartsWith(prefix, watchPathComparison)
                    | None -> false)

            if removeQueuedFile then
                if removePendingFileUpload queuedFile then canceledFileUpload <- true

        canceledFileUpload

    /// Records an already-derived filesystem difference so status application can retry without duplicating work.
    let private addPendingStatusDifference (difference: FileSystemDifference) =
        lock pendingStatusDifferencesLock (fun () ->
            if not
               <| pendingStatusDifferences.Exists (fun (existing: FileSystemDifference) ->
                   existing.RelativePath = difference.RelativePath
                   && existing.DifferenceType = difference.DifferenceType
                   && existing.FileSystemEntryType = difference.FileSystemEntryType) then
                pendingStatusDifferences.Add(difference))

    /// Builds the stable key used to pair a startup-replayed difference with its existing journal sequence.
    let private pendingStatusDifferenceReplayKey (difference: FileSystemDifference) =
        $"{getDiscriminatedUnionCaseName difference.DifferenceType}|{getDiscriminatedUnionCaseName difference.FileSystemEntryType}|{normalizeRelativePath difference.RelativePath}"

    /// Rebuilds the replay sequence dictionary when repository path comparison changes.
    let private ensureStartupReplaySequenceComparer () =
        let desiredComparer =
            if watchPathComparison = StringComparison.OrdinalIgnoreCase
               || watchPathComparison = StringComparison.InvariantCultureIgnoreCase
               || watchPathComparison = StringComparison.CurrentCultureIgnoreCase then
                StringComparer.OrdinalIgnoreCase
            else
                StringComparer.Ordinal

        if not (obj.ReferenceEquals(pendingStatusDifferenceReplaySequences.Comparer, desiredComparer)) then
            let replacement = Dictionary<string, Queue<int64>>(desiredComparer)

            for pair in pendingStatusDifferenceReplaySequences do
                replacement[pair.Key] <- Queue<int64>(pair.Value)

            pendingStatusDifferenceReplaySequences <- replacement

    /// Records one replay sequence and optionally queues its difference for startup status application.
    let private recordStartupReplaySequence queueDifference sequence (difference: FileSystemDifference) =
        lock pendingStatusDifferencesLock (fun () ->
            ensureStartupReplaySequenceComparer ()

            if queueDifference then addPendingStatusDifference difference

            let key = pendingStatusDifferenceReplayKey difference
            let mutable queue = Unchecked.defaultof<Queue<int64>>

            if not (pendingStatusDifferenceReplaySequences.TryGetValue(key, &queue)) then
                queue <- Queue<int64>()
                pendingStatusDifferenceReplaySequences.Add(key, queue)

            queue.Enqueue(sequence))

    /// Queues one startup-replayed difference without creating a duplicate durable journal row.
    let private addStartupReplayedStatusDifference sequence (difference: FileSystemDifference) = recordStartupReplaySequence true sequence difference

    /// Remembers a row appended during deferred replay so the next retry does not append it again.
    let private rememberDeferredStartupReplaySequence sequence (difference: FileSystemDifference) = recordStartupReplaySequence false sequence difference

    /// Reads the existing startup replay sequences for a difference that should not be appended again.
    let private startupReplaySequencesForDifference (difference: FileSystemDifference) =
        lock pendingStatusDifferencesLock (fun () ->
            ensureStartupReplaySequenceComparer ()
            let key = pendingStatusDifferenceReplayKey difference
            let mutable queue = Unchecked.defaultof<Queue<int64>>

            if
                pendingStatusDifferenceReplaySequences.TryGetValue(key, &queue)
                && queue.Count > 0
            then
                queue.ToArray()
            else
                Array.empty<int64>)

    /// Reads the first existing startup replay sequence for tests that assert duplicate append prevention.
    let private tryPeekStartupReplaySequence (difference: FileSystemDifference) =
        match startupReplaySequencesForDifference difference with
        | [||] -> None
        | sequences -> Some sequences[0]

    /// Reports the replay sequence queued for a difference in startup recovery tests.
    let internal tryPeekStartupReplaySequenceForWatchTests difference = tryPeekStartupReplaySequence difference

    /// Consumes replay sequence metadata only after the corresponding pending status difference is cleared.
    let private clearStartupReplaySequences (differences: seq<FileSystemDifference>) =
        lock pendingStatusDifferencesLock (fun () ->
            ensureStartupReplaySequenceComparer ()

            for difference in differences do
                let key = pendingStatusDifferenceReplayKey difference
                let mutable queue = Unchecked.defaultof<Queue<int64>>

                if pendingStatusDifferenceReplaySequences.TryGetValue(key, &queue) then
                    pendingStatusDifferenceReplaySequences.Remove(key)
                    |> ignore)

    /// Reads the best available GraceStatus snapshot for classifying directory-create observations.
    let private readGraceStatusForDirectoryAddClassification () =
        if (not graceStatusHasChanged)
           && graceStatus.Index.Count > 0 then
            graceStatus
        else
            try
                let refreshedStatus =
                    (readGraceStatusFileForDeletedPathClassification ())
                        .GetAwaiter()
                        .GetResult()

                graceStatus <- refreshedStatus
                refreshedStatus
            with
            | _ -> GraceStatus.Default

    /// Queues directory add differences and reports whether the affected subtree was fully enumerated.
    let private tryEnqueueDirectoryStatusAdds fullPath =
        let status = readGraceStatusForDirectoryAddClassification ()
        let directoryAddDifferences, completedEnumeration = tryDeriveDirectorySubtreeAddDifferences status fullPath

        for difference in directoryAddDifferences do
            addPendingStatusDifference difference

        directoryAddDifferences.Count > 0, completedEnumeration

    /// Captures the already-derived differences waiting for the shared status-application path.
    let private pendingStatusDifferencesSnapshot () = lock pendingStatusDifferencesLock (fun () -> pendingStatusDifferences.ToList())

    /// Checks whether any pre-derived filesystem differences still need status application.
    let private hasPendingStatusDifferences () = lock pendingStatusDifferencesLock (fun () -> pendingStatusDifferences.Count > 0)

    /// Clears only the differences that a successful status update already applied.
    let private clearPendingStatusDifferences (differences: List<FileSystemDifference>) =
        lock pendingStatusDifferencesLock (fun () ->
            for difference in differences do
                pendingStatusDifferences.Remove(difference)
                |> ignore

            ())

    /// Clears pre-derived differences when tests reset watch module state.
    let private clearPendingStatusDifferencesForTests () = lock pendingStatusDifferencesLock (fun () -> pendingStatusDifferences.Clear())

    /// Records one pending observation as quarantined so confidence recovery never replays it implicitly.
    let private quarantineWatchObservation reason observationKind path =
        Interlocked.Increment(&quarantinedWatchObservationCount)
        |> ignore

        logToAnsiConsole Colors.Verbose $"Grace Watch quarantined {observationKind} observation for {path}: {reason}."

    /// Moves currently queued watch observations out of the trusted incremental path after confidence is lost.
    let private quarantinePendingWatchWork reason =
        let mutable quarantinedCount = 0
        let mutable fileGeneration = 0L
        let mutable triggerGeneration = 0L
        let mutable unitValue = ()

        for pendingFile in filesToProcess.ToArray() do
            if filesToProcess.TryRemove(pendingFile.Key, &fileGeneration) then
                quarantineWatchObservation reason "file" pendingFile.Key
                quarantinedCount <- quarantinedCount + 1

        for pendingDirectory in directoriesToProcess.ToArray() do
            if directoriesToProcess.TryRemove(pendingDirectory.Key, &unitValue) then
                quarantineWatchObservation reason "directory" pendingDirectory.Key
                quarantinedCount <- quarantinedCount + 1

        for pendingTrigger in statusUpdateTriggers.ToArray() do
            if statusUpdateTriggers.TryRemove(pendingTrigger.Key, &triggerGeneration) then
                quarantineWatchObservation reason "status-trigger" pendingTrigger.Key
                quarantinedCount <- quarantinedCount + 1

        let pendingDifferences =
            lock pendingStatusDifferencesLock (fun () ->
                let snapshot = pendingStatusDifferences.ToArray()
                pendingStatusDifferences.Clear()
                snapshot)

        for pendingDifference in pendingDifferences do
            quarantineWatchObservation reason "status-difference" $"{pendingDifference.RelativePath}"
            quarantinedCount <- quarantinedCount + 1

        uploadedFileVersions.Clear()
        lock processedFileRelativePathsPendingStatusLock (fun () -> processedFileRelativePathsPendingStatus.Clear())
        lock canceledFileUploadDeleteRelativePathsLock (fun () -> canceledFileUploadDeleteRelativePaths.Clear())

        if quarantinedCount > 0 then
            logToAnsiConsole Colors.Important $"Grace Watch quarantined {quarantinedCount} pending observations after confidence loss: {reason}."

    /// Reads the active explicit-resync attempt token used to reject stale recovery side effects.
    let private currentGraceWatchResyncAttempt () = Volatile.Read(&graceWatchResyncGeneration)

    /// Reports whether an explicit resync scan must run before incremental observation application resumes.
    let private isGraceWatchResyncPending () = currentGraceWatchResyncAttempt () <> 0L

    /// Reports whether an explicit resync scan is pending for Watch confidence-boundary tests.
    let internal isGraceWatchResyncPendingForWatchTests () = isGraceWatchResyncPending ()

    /// Reports whether an in-flight resync attempt still owns the recovery boundary.
    let private isGraceWatchResyncAttemptCurrent attempt =
        attempt <> 0L
        && currentGraceWatchResyncAttempt () = attempt

    /// Reports whether an in-flight resync attempt may still apply recovery side effects.
    let private isGraceWatchResyncAttemptActive attempt =
        isGraceWatchResyncAttemptCurrent attempt
        && currentGraceWatchRuntimeMode () = GraceWatchRuntimeMode.Resynchronizing

    let mutable private statusSideEffectTrustPredicate = fun () -> true

    /// Reports whether Watch still trusts the current status update enough to write remote or local side effects.
    let private statusSideEffectsStillTrusted () = statusSideEffectTrustPredicate ()

    /// Reports the active status side-effect trust state for Watch confidence-boundary tests.
    let internal statusSideEffectsStillTrustedForWatchTests () = statusSideEffectsStillTrusted ()

    /// Runs status application with a confidence predicate that can be rechecked between side effects.
    let private runWithStatusSideEffectTrustPredicate predicate operation =
        task {
            let previousPredicate = statusSideEffectTrustPredicate
            statusSideEffectTrustPredicate <- predicate

            try
                return! operation ()
            finally
                statusSideEffectTrustPredicate <- previousPredicate
        }

    /// Stops status application before a side effect when Watch lost the confidence boundary for this update.
    let private statusSideEffectStillAllowed sideEffectName =
        let allowed = statusSideEffectsStillTrusted ()

        if not allowed then
            logToAnsiConsole Colors.Important $"Grace Watch skipped {sideEffectName} because confidence was lost before status side effects completed."

        allowed

    /// Clears the pending resync request once scan-derived status has reached the durable boundary.
    let private clearGraceWatchResyncPending () = Volatile.Write(&graceWatchResyncGeneration, 0L)

    /// Clears a specific resync attempt without losing a newer confidence-loss request.
    let private tryClearGraceWatchResyncAttempt attempt = Interlocked.CompareExchange(&graceWatchResyncGeneration, 0L, attempt) = attempt

    /// Evaluates has pending watch work against parsed options and command state.
    let private hasPendingWatchWork () =
        isGraceWatchResyncPending ()
        || graceStatusHasChanged
        || not (
            filesToProcess.IsEmpty
            && directoriesToProcess.IsEmpty
            && statusUpdateTriggers.IsEmpty
            && not (hasPendingStatusDifferences ())
        )

    /// Reads the generation for Grace Status DB refresh events observed from the filesystem.
    let private currentGraceStatusRefreshGeneration () = Volatile.Read(&graceStatusRefreshGeneration)

    /// Records that a Grace Status DB, WAL, SHM, or journal observation must be applied by a timer pass.
    let private recordGraceStatusRefreshObservation () =
        Interlocked.Increment(&graceStatusRefreshGeneration)
        |> ignore

        graceStatusHasChanged <- true

    /// Clears exactly the Grace Status refresh generation consumed by a successful publication.
    let private tryClearGraceStatusRefreshGeneration observedGeneration =
        if Interlocked.CompareExchange(&graceStatusRefreshGeneration, 0L, observedGeneration) = observedGeneration then
            graceStatusHasChanged <- false
            true
        else
            graceStatusHasChanged <- true
            false

    /// Records the pending-work flag that the next Watch IPC snapshot should publish.
    let private setGraceWatchPendingWorkStatusFlag hasPendingWork = lock watchStatusPublishLock (fun () -> setGraceWatchHasPendingWorkForStatus hasPendingWork)

    /// Returns the root BLAKE3 hash that Watch expects to see in the IPC snapshot it just published.
    let private expectedRootDirectoryBlake3Hash (status: GraceStatus) =
        let mutable rootDirectoryVersion = LocalDirectoryVersion.Default

        if status.Index.TryGetValue(status.RootDirectoryId, &rootDirectoryVersion) then
            rootDirectoryVersion.Blake3Hash
        elif not (String.IsNullOrWhiteSpace(string status.RootDirectoryBlake3Hash)) then
            status.RootDirectoryBlake3Hash
        else
            Blake3Hash String.Empty

    /// Verifies the on-disk Watch IPC snapshot is the snapshot this publication attempt wrote.
    let private statusMatchesVerifiedPublication
        (expectedStatus: GraceStatus)
        (expectedDirectoryIds: HashSet<DirectoryVersionId>)
        hasPendingWork
        publicationStartedAt
        (status: GraceWatchStatus)
        =
        status.UpdatedAt >= publicationStartedAt
        && status.HasPendingWatchWork = hasPendingWork
        && status.IsWorkingTreeClean = not hasPendingWork
        && status.RootDirectoryId = expectedStatus.RootDirectoryId
        && status.RootDirectorySha256Hash = expectedStatus.RootDirectorySha256Hash
        && status.RootDirectoryBlake3Hash = expectedRootDirectoryBlake3Hash expectedStatus
        && not (isNull status.DirectoryIds)
        && status.DirectoryIds.SetEquals(expectedDirectoryIds)

    /// Advances the pending-work publication cache only after the exact IPC snapshot proves it reached disk.
    let private cachePendingWatchWorkPublicationIfVerified expectedStatus expectedDirectoryIds hasPendingWork publicationStartedAt =
        let transitionWasPublished =
            try
                let inspection = inspectGraceWatchStatus().GetAwaiter().GetResult()

                inspection.Status
                |> Option.exists (statusMatchesVerifiedPublication expectedStatus expectedDirectoryIds hasPendingWork publicationStartedAt)
            with
            | _ -> false

        lock watchStatusPublishLock (fun () -> lastPublishedHasPendingWatchWork <- if transitionWasPublished then Some hasPendingWork else None)

        if not transitionWasPublished then
            logToAnsiConsole Colors.Important $"Grace Watch will retry pending-work status publication on the next transition check."

        transitionWasPublished

    /// Publishes Watch IPC after reading queued work inside the serialized status boundary.
    let private tryPublishWatchIpcWithFreshPendingWorkProbe expectedStatus expectedDirectoryIds (writeSnapshot: unit -> Task<unit>) =
        lock watchStatusPublishLock (fun () ->
            let mutable attempts = 0
            let mutable transitionWasPublished = false
            let mutable pendingWorkChangedDuringWrite = true

            while pendingWorkChangedDuringWrite && attempts < 2 do
                attempts <- attempts + 1
                let hasPendingWork = hasPendingWatchWork ()
                setGraceWatchHasPendingWorkForStatus hasPendingWork
                let publicationStartedAt = getCurrentInstant ()

                try
                    writeSnapshot().GetAwaiter().GetResult()
                    transitionWasPublished <- cachePendingWatchWorkPublicationIfVerified expectedStatus expectedDirectoryIds hasPendingWork publicationStartedAt
                with
                | ex ->
                    lastPublishedHasPendingWatchWork <- None
                    logToAnsiConsole Colors.Error $"Grace Watch failed to publish pending-work status transition: {ex.Message}"

                pendingWorkChangedDuringWrite <- hasPendingWatchWork () <> hasPendingWork

            transitionWasPublished)

    /// Publishes Watch IPC after reading queued work inside the serialized status boundary.
    let private publishWatchIpcWithFreshPendingWorkProbe expectedStatus expectedDirectoryIds writeSnapshot =
        tryPublishWatchIpcWithFreshPendingWorkProbe expectedStatus expectedDirectoryIds writeSnapshot
        |> ignore

    /// Publishes only clean/dirty Watch IPC transitions so duplicate raw observations do not churn status.
    let private publishPendingWatchWorkTransitionIfNeeded () =
        lock watchStatusPublishLock (fun () ->
            let hasPendingWork = hasPendingWatchWork ()

            if
                File.Exists(IpcFileName())
                && lastPublishedHasPendingWatchWork
                   <> Some hasPendingWork
            then
                setGraceWatchHasPendingWorkForStatus hasPendingWork

                let runtimeMode = currentGraceWatchRuntimeMode ()

                let shouldPublish, statusForPublish, directoryIdsForPublish =
                    if
                        runtimeMode = GraceWatchRuntimeMode.HealthyIncremental
                        && not (isGraceWatchResyncPending ())
                    then
                        try
                            let status =
                                readGraceStatusFileForPendingWorkTransition()
                                    .GetAwaiter()
                                    .GetResult()

                            let statusDirectoryIds = status.Index.Keys.ToHashSet()
                            true, status, Some statusDirectoryIds
                        with
                        | ex ->
                            if hasPendingWork then
                                logToAnsiConsole
                                    Colors.Error
                                    $"Grace Watch could not read Grace Status for pending-work dirty status transition; publishing non-incremental status and retrying later: {ex.Message}"

                                true, GraceStatus.Default, Some(HashSet<DirectoryVersionId>())
                            else
                                lastPublishedHasPendingWatchWork <- None

                                logToAnsiConsole
                                    Colors.Error
                                    $"Grace Watch could not read Grace Status for pending-work clean status transition; retrying later: {ex.Message}"

                                false, GraceStatus.Default, Some(HashSet<DirectoryVersionId>())
                    else
                        true, GraceStatus.Default, Some(HashSet<DirectoryVersionId>())

                if shouldPublish then
                    let publicationStartedAt = getCurrentInstant ()

                    try
                        let writeTask =
                            if runtimeMode = GraceWatchRuntimeMode.Suspended then
                                updateGraceWatchInterprocessFileForSuspendedMode statusForPublish directoryIdsForPublish
                            else
                                updateGraceWatchInterprocessFile statusForPublish directoryIdsForPublish

                        writeTask.GetAwaiter().GetResult()
                    with
                    | ex ->
                        lastPublishedHasPendingWatchWork <- None
                        logToAnsiConsole Colors.Error $"Grace Watch failed to publish pending-work status transition: {ex.Message}"

                    let transitionWasPublished =
                        try
                            let inspection = inspectGraceWatchStatus().GetAwaiter().GetResult()

                            let expectedDirectoryIds =
                                directoryIdsForPublish
                                |> Option.defaultWith (fun () -> statusForPublish.Index.Keys.ToHashSet())

                            inspection.Status
                            |> Option.exists (statusMatchesVerifiedPublication statusForPublish expectedDirectoryIds hasPendingWork publicationStartedAt)
                        with
                        | _ -> false

                    if transitionWasPublished then
                        lastPublishedHasPendingWatchWork <- Some hasPendingWork
                    else
                        lastPublishedHasPendingWatchWork <- None
                        logToAnsiConsole Colors.Important $"Grace Watch will retry pending-work status publication on the next transition check.")

    /// Publishes a pending-work transition through the normal Watch IPC writer for deterministic Watch tests.
    let internal publishPendingWatchWorkTransitionIfNeededForWatchTests () = publishPendingWatchWorkTransitionIfNeeded ()

    /// Records that durable Grace Status changed and immediately advertises pending Watch work to other commands.
    let private markGraceStatusChangedAndPublishPendingWorkTransition () =
        recordGraceStatusRefreshObservation ()

        publishPendingWatchWorkTransitionIfNeeded ()

    /// Completes startup only when recovery did not leave Watch in another runtime mode or with pending resync work.
    let private promoteStartupModeIfRecoverySucceeded () =
        if
            currentGraceWatchRuntimeMode () = GraceWatchRuntimeMode.StartingUp
            && not (isGraceWatchResyncPending ())
        then
            setGraceWatchRuntimeMode GraceWatchRuntimeMode.HealthyIncremental

    /// Completes startup for tests that verify failed recovery does not resume incremental mode.
    let internal promoteStartupModeIfRecoverySucceededForWatchTests () = promoteStartupModeIfRecoverySucceeded ()

    /// Verifies that the persisted IPC snapshot forces readers away from incremental shortcuts during resync.
    let private isGraceWatchResyncRequiredStatusPublished (inspection: GraceWatchStatusInspection) =
        inspection.Status
        |> Option.exists (fun status ->
            status.HasPendingWatchWork
            && not status.IsWorkingTreeClean
            && not inspection.IsUsable
            && inspection.EffectiveMode
               <> Some GraceWatchRuntimeMode.HealthyIncremental)

    /// Publishes a non-incremental IPC snapshot so other Grace processes do not trust stale Watch status during resync.
    let private publishGraceWatchResyncRequired () =
        let hasPendingWork = hasPendingWatchWork ()

        try
            lock watchStatusPublishLock (fun () -> setGraceWatchHasPendingWorkForStatus hasPendingWork)

            let writeTask =
                match currentGraceWatchRuntimeMode () with
                | GraceWatchRuntimeMode.Suspended -> updateGraceWatchInterprocessFileForSuspendedMode GraceStatus.Default (Some(HashSet<DirectoryVersionId>()))
                | _ -> updateGraceWatchInterprocessFile GraceStatus.Default (Some(HashSet<DirectoryVersionId>()))

            writeTask.GetAwaiter().GetResult()

            let resyncRequiredWasPublished =
                try
                    inspectGraceWatchStatus().GetAwaiter().GetResult()
                    |> isGraceWatchResyncRequiredStatusPublished
                with
                | _ -> false

            lock watchStatusPublishLock (fun () -> lastPublishedHasPendingWatchWork <- if resyncRequiredWasPublished then Some hasPendingWork else None)

            if not resyncRequiredWasPublished then
                logToAnsiConsole Colors.Important $"Grace Watch will retry resync-required status publication on the next transition check."
        with
        | ex ->
            lock watchStatusPublishLock (fun () -> lastPublishedHasPendingWatchWork <- None)
            logToAnsiConsole Colors.Error $"Grace Watch could not publish resync-required status: {Markup.Escape(ex.Message)}."

    /// Suspends only the resync attempt that observed the failure, preserving newer overflow requests.
    let private suspendGraceWatchAttemptAfterFailedRecovery attempt reason =
        if isGraceWatchResyncAttemptActive attempt then
            quarantinePendingWatchWork reason
            setGraceWatchRuntimeMode GraceWatchRuntimeMode.Suspended

            if tryClearGraceWatchResyncAttempt attempt then
                publishGraceWatchResyncRequired ()
                logToAnsiConsole Colors.Error $"Grace Watch suspended incremental processing: {reason}."
            else
                setGraceWatchRuntimeMode GraceWatchRuntimeMode.Resynchronizing

                logToAnsiConsole Colors.Important $"Grace Watch kept newer resync attempt pending after stale attempt {attempt} failed."
        else
            logToAnsiConsole Colors.Important $"Grace Watch ignored stale resync failure for attempt {attempt} because a newer resync attempt is pending."

    /// Requests a scan-derived resync and quarantines observations captured under the previous root confidence.
    let private requestGraceWatchExplicitResync reason =
        quarantinePendingWatchWork reason

        Interlocked.Increment(&graceWatchResyncGeneration)
        |> ignore

        setGraceWatchRuntimeMode GraceWatchRuntimeMode.Resynchronizing
        publishGraceWatchResyncRequired ()
        logToAnsiConsole Colors.Important $"Grace Watch requires an explicit resync before incremental observations can resume: {reason}."

    /// Requests explicit resync for tests that exercise confidence-loss and deferred-observation behavior.
    let internal requestGraceWatchExplicitResyncForWatchTests reason = requestGraceWatchExplicitResync reason

    /// Counts quarantined observations for tests that verify confidence loss does not replay stale work.
    let internal quarantinedWatchObservationCountForWatchTests () = Volatile.Read(&quarantinedWatchObservationCount)

    /// Verifies a reloaded branch configuration has enough identity to own a branch-scoped Watch IPC file.
    let private isTransitionCompletionConfigurationCoherent () =
        let current = Current()

        let hasRepositoryIdentity =
            current.RepositoryId <> RepositoryId.Empty
            || not (String.IsNullOrWhiteSpace(string current.RepositoryName))

        let hasBranchIdentity =
            current.BranchId <> BranchId.Empty
            || not (String.IsNullOrWhiteSpace(string current.BranchName))

        hasRepositoryIdentity
        && hasBranchIdentity
        && not (String.IsNullOrWhiteSpace current.RootDirectory)
        && Directory.Exists(current.RootDirectory)

    /// Verifies a reloaded GraceStatus can support a trusted incremental Watch snapshot after branch switch.
    let private isTransitionCompletionGraceStatusCoherent (status: GraceStatus) =
        let hasRootIdentity =
            status.RootDirectoryId <> DirectoryVersionId.Empty
            && not (String.IsNullOrWhiteSpace($"{status.RootDirectorySha256Hash}"))
            && not (String.IsNullOrWhiteSpace($"{status.RootDirectoryBlake3Hash}"))

        let mutable rootDirectoryVersion = LocalDirectoryVersion.Default

        hasRootIdentity
        && not (isNull status.Index)
        && status.Index.TryGetValue(status.RootDirectoryId, &rootDirectoryVersion)
        && rootDirectoryVersion.Sha256Hash = status.RootDirectorySha256Hash
        && rootDirectoryVersion.Blake3Hash = status.RootDirectoryBlake3Hash

    /// Publishes a branch-scoped non-incremental transition snapshot when Watch cannot safely resume.
    let private publishNonIncrementalTransitionCompletionStatus context =
        let emptyDirectoryIds = HashSet<DirectoryVersionId>()

        publishWatchIpcWithFreshPendingWorkProbe GraceStatus.Default emptyDirectoryIds (fun () ->
            match currentGraceWatchRuntimeMode () with
            | GraceWatchRuntimeMode.Suspended -> updateGraceWatchInterprocessFileForSuspendedMode GraceStatus.Default (Some emptyDirectoryIds)
            | _ -> updateGraceWatchInterprocessFile GraceStatus.Default (Some emptyDirectoryIds))

        logToAnsiConsole Colors.Important $"Grace Watch published non-incremental IPC for branch transition completion: {context}."

    /// Removes the previous branch IPC snapshot so stale healthy status cannot survive a branch transition.
    let private retirePreviousBranchWatchIpc previousIpcFileName =
        if File.Exists(previousIpcFileName) then
            retirePreviousBranchWatchIpcForTransitionCompletion previousIpcFileName

            logToAnsiConsole Colors.Important $"Grace Watch retired previous branch IPC before completing branch transition: {previousIpcFileName}."

        not (File.Exists(previousIpcFileName))

    /// Verifies that the previous branch IPC no longer exposes a healthy incremental shortcut.
    let private previousBranchWatchIpcRetiredOrNonIncremental previousIpcFileName =
        if not (File.Exists(previousIpcFileName)) then
            true
        else
            try
                let inspection = inspectGraceWatchStatus().GetAwaiter().GetResult()

                String.Equals(IpcFileName(), previousIpcFileName, StringComparison.OrdinalIgnoreCase)
                && isGraceWatchResyncRequiredStatusPublished inspection
            with
            | _ -> false

    /// Retries deletion or downgrades the previous branch IPC before Watch abandons that branch identity.
    let private retryOrDowngradePreviousBranchWatchIpc previousIpcFileName failure =
        requestGraceWatchExplicitResync $"previous branch IPC retirement failed: {failure}"

        let maxAttempts = 3
        let mutable attempt = 1
        let mutable verified = previousBranchWatchIpcRetiredOrNonIncremental previousIpcFileName

        while not verified && attempt < maxAttempts do
            runPreviousBranchIpcDowngradeRetryProbe ()

            Thread.Sleep(TimeSpan.FromMilliseconds(float (attempt * 25)))
            attempt <- attempt + 1

            try
                retirePreviousBranchWatchIpc previousIpcFileName
                |> ignore
            with
            | ex ->
                logToAnsiConsole
                    Colors.Important
                    $"Grace Watch retry {attempt} could not retire previous branch IPC before transition completion: {Markup.Escape(ex.Message)}."

            if File.Exists(previousIpcFileName) then publishGraceWatchResyncRequired ()

            verified <- previousBranchWatchIpcRetiredOrNonIncremental previousIpcFileName

        if not verified then
            logToAnsiConsole
                Colors.Error
                $"Grace Watch could not verify previous branch IPC was retired or downgraded after {attempt} attempts; target branch IPC publication is deferred."

        verified

    /// Publishes a target-branch non-incremental snapshot when old IPC retirement is temporarily blocked.
    let private publishNonIncrementalTransitionCompletionAfterRetireFailure completedUtc failure =
        clearSignalRBranchSubscriptionForTransition ()
        let previousIpcFileName = IpcFileName()

        logToAnsiConsole
            Colors.Error
            $"Grace Watch could not retire previous branch IPC after transition marker deletion at {completedUtc:O}; verifying old IPC downgrade before target publication: {Markup.Escape(failure)}."

        if retryOrDowngradePreviousBranchWatchIpc previousIpcFileName failure then
            try
                reloadConfigurationForTransitionCompletion ()
                rebindUpdateMarkerWatcherAfterTransitionCompletion ()
                publishNonIncrementalTransitionCompletionStatus $"previous branch IPC retirement failed: {failure}"
            with
            | ex ->
                logToAnsiConsole
                    Colors.Error
                    $"Grace Watch could not publish target branch resync IPC after previous branch IPC retirement failed at {completedUtc:O}: {Markup.Escape(ex.Message)}."

    /// Completes a Grace-owned branch transition inside the serialized Watch publication boundary.
    let private completeGraceUpdateTransitionAfterMarkerDeletion completedUtc =
        lock watchStatusPublishLock (fun () ->
            recordGraceUpdateMarkerCompletedUtc completedUtc
            clearSignalRBranchSubscriptionForTransition ()
            let previousIpcFileName = IpcFileName()

            let previousIpcRetired =
                try
                    let retired = retirePreviousBranchWatchIpc previousIpcFileName

                    if not retired then
                        publishNonIncrementalTransitionCompletionAfterRetireFailure completedUtc "previous branch IPC still existed after delete"

                    retired
                with
                | ex ->
                    publishNonIncrementalTransitionCompletionAfterRetireFailure completedUtc ex.Message
                    false

            if previousIpcRetired then
                runBranchTransitionCompletionAfterRetireProbe ()

                let reloadedConfiguration =
                    try
                        reloadConfigurationForTransitionCompletion ()
                        rebindUpdateMarkerWatcherAfterTransitionCompletion ()
                        true
                    with
                    | ex ->
                        setGraceWatchRuntimeMode GraceWatchRuntimeMode.Resynchronizing
                        graceStatusHasChanged <- true

                        lastPublishedHasPendingWatchWork <- None

                        logToAnsiConsole
                            Colors.Error
                            $"Grace Watch could not reload configuration or rebind marker watcher after transition marker deletion at {completedUtc:O}; old branch IPC will not be republished: {Markup.Escape(ex.Message)}."

                        false

                if reloadedConfiguration then
                    try
                        let refreshedStatus =
                            readGraceStatusFileForTransitionCompletion()
                                .GetAwaiter()
                                .GetResult()

                        graceStatus <- refreshedStatus
                        updateGraceStatusDirectoryIds graceStatus

                        if isTransitionCompletionConfigurationCoherent ()
                           && isTransitionCompletionGraceStatusCoherent graceStatus then
                            if
                                currentGraceWatchRuntimeMode () = GraceWatchRuntimeMode.HealthyIncremental
                                && not (isGraceWatchResyncPending ())
                            then
                                let transitionPublicationVerified =
                                    tryPublishWatchIpcWithFreshPendingWorkProbe graceStatus graceStatusDirectoryIds (fun () ->
                                        updateGraceWatchInterprocessFile graceStatus (Some graceStatusDirectoryIds))

                                if transitionPublicationVerified then
                                    try
                                        refreshSignalRSubscriptionsAfterTransitionCompletion ()

                                        logToAnsiConsole
                                            Colors.Important
                                            $"Grace Watch completed branch transition from marker deletion at {completedUtc:O}; incremental observations may resume for {Current().BranchName}."
                                    with
                                    | ex ->
                                        completeSignalRBranchSubscriptionRefreshWithoutTrust ()

                                        logToAnsiConsole
                                            Colors.Error
                                            $"Grace Watch completed branch transition but could not refresh SignalR parent subscription; parent-triggered auto-rebase is disabled until the next successful registration: {Markup.Escape(ex.Message)}."
                                else
                                    requestGraceWatchExplicitResync "branch transition completion could not verify new branch IPC publication"
                            else
                                publishNonIncrementalTransitionCompletionStatus
                                    $"runtime mode is {currentGraceWatchRuntimeMode ()} and resync pending is {isGraceWatchResyncPending ()}"
                        else
                            requestGraceWatchExplicitResync "branch transition completion reloaded incoherent configuration or GraceStatus"
                    with
                    | ex -> requestGraceWatchExplicitResync $"branch transition completion could not reload GraceStatus: {ex.Message}")

    /// Combines status differences without applying the same filesystem observation twice.
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

    /// Checks whether two status differences represent the same repository path observation.
    let private statusDifferenceMatches (left: FileSystemDifference) (right: FileSystemDifference) =
        left.DifferenceType = right.DifferenceType
        && left.FileSystemEntryType = right.FileSystemEntryType
        && String.Equals(normalizeRelativePath left.RelativePath, normalizeRelativePath right.RelativePath, watchPathComparison)

    /// Checks whether a difference came from uploaded file work that newer event-derived upload state can supersede.
    let private isUploadedFileAddOrChangeDifference (difference: FileSystemDifference) =
        difference.FileSystemEntryType = FileSystemEntryType.File
        && (difference.DifferenceType = DifferenceType.Add
            || difference.DifferenceType = DifferenceType.Change)

    /// Checks whether uploaded identity data exists for a repository-relative path.
    let private hasUploadedFileVersionForPath (relativePath: RelativePath) =
        let normalizedRelativePath = normalizeRelativePath relativePath

        uploadedFileVersions.Values
        |> Seq.exists (fun uploadedFileVersion ->
            String.Equals(normalizeRelativePath uploadedFileVersion.RelativePath, normalizedRelativePath, watchPathComparison))

    /// Combines retryable pending differences with the current event-derived result for the same watch pass.
    let private mergeCurrentStatusDifferences
        (processedFilePaths: RelativePath seq)
        (pendingDifferences: List<FileSystemDifference>)
        (eventDerivedDifferences: List<FileSystemDifference>)
        =
        let retainedPendingDifferences = List<FileSystemDifference>()

        let processedUploadedPaths =
            processedFilePaths
            |> Seq.map normalizeRelativePath
            |> Seq.toArray

        for pendingDifference in pendingDifferences do
            let supersededUploadedDifference =
                isUploadedFileAddOrChangeDifference pendingDifference
                && hasUploadedFileVersionForPath pendingDifference.RelativePath
                && processedUploadedPaths
                   |> Array.exists (fun processedPath -> String.Equals(normalizeRelativePath pendingDifference.RelativePath, processedPath, watchPathComparison))
                && not (
                    eventDerivedDifferences
                    |> Seq.exists (statusDifferenceMatches pendingDifference)
                )

            if not supersededUploadedDifference then
                retainedPendingDifferences.Add(pendingDifference)

        mergeStatusDifferences retainedPendingDifferences eventDerivedDifferences

    /// Clears inherited pending watch work for tests values so explicitly scoped access commands do not target child resources accidentally.
    let internal clearPendingWatchWorkForTests () =
        filesToProcess.Clear()
        directoriesToProcess.Clear()
        statusUpdateTriggers.Clear()
        uploadedFileVersions.Clear()
        lock processedFileRelativePathsPendingStatusLock (fun () -> processedFileRelativePathsPendingStatus.Clear())
        lock canceledFileUploadDeleteRelativePathsLock (fun () -> canceledFileUploadDeleteRelativePaths.Clear())
        clearPendingStatusDifferencesForTests ()
        lock pendingStatusDifferencesLock (fun () -> pendingStatusDifferenceReplaySequences.Clear())
        clearGraceWatchResyncPending ()

        Interlocked.Exchange(&quarantinedWatchObservationCount, 0)
        |> ignore

        Interlocked.Exchange(&statusUpdateTriggerGeneration, 0L)
        |> ignore

        Interlocked.Exchange(&fileUploadWorkGeneration, 0L)
        |> ignore

        Interlocked.Exchange(&graceStatusRefreshGeneration, 0L)
        |> ignore

        graceStatus <- GraceStatus.Default
        graceStatusHasChanged <- false

        lock watchStatusPublishLock (fun () ->
            lastPublishedHasPendingWatchWork <- None
            setGraceWatchHasPendingWorkForStatus false)

        readGraceStatusFileForDeletedPathClassification <- readGraceStatusFile
        readGraceStatusFileForPendingWorkTransition <- readGraceStatusFile
        readGraceStatusFileForTransitionCompletion <- readGraceStatusFile
        clearShouldIgnoreCache ()
        resetBranchTransitionCompletionAfterRetireProbeForWatchTests ()
        resetSignalRSubscriptionRefreshForWatchTests ()
        resetWatchJournalClientsForWatchTests ()
        enumerateFilesForDirectoryUpload <- fun directoryPath -> Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)
        enumerateDirectoriesForDirectoryStatusAdd <- enumerateDirectoriesForDirectoryStatusAddWithPruning
        clearGraceUpdateMarkerDeletedUtcForReset ()
        clearCurrentGraceUpdateMarkerCompletedSidecar ()
        watchPathComparison <- defaultWatchPathComparison ()
        watchPathComparisonOverride <- None
        watchPathComparisonConfiguredRoot <- None
        repositoryPathCaseInsensitiveLookupForWatch <- detectRepositoryPathCaseInsensitiveLookup
        fileExistsForWatchFinalPath <- File.Exists
        directoryExistsForWatchFinalPath <- Directory.Exists
        createLocalFileVersionForWatchStatus <- createLocalFileVersion
        setGraceWatchRuntimeMode GraceWatchRuntimeMode.HealthyIncremental
        setLastScanForDifferencesSuccessfulForWatchTests true

    /// Coordinates pending watch work snapshot for tests behavior for this CLI command path.
    let internal pendingWatchWorkSnapshotForTests () =
        {
            FilesToProcess = filesToProcess.Keys.OrderBy(id).ToArray()
            DirectoriesToProcess = directoriesToProcess.Keys.OrderBy(id).ToArray()
            StatusUpdateTriggers = statusUpdateTriggers.Keys.OrderBy(id).ToArray()
        }

    /// Lists processed upload paths that are waiting for GraceStatus application in watch tests.
    let internal processedFileRelativePathsPendingStatusForWatchTests () =
        processedFileRelativePathsPendingStatusSnapshot ()
        |> Seq.map string
        |> Seq.sort
        |> Seq.toArray

    /// Coordinates status only trigger snapshot behavior for this CLI command path.
    let private statusOnlyTriggerSnapshot () =
        directoriesToProcess.Keys.ToArray(),
        statusUpdateTriggers
            .ToArray()
            .Select(fun trigger -> { RelativePath = trigger.Key; Generation = trigger.Value })
            .ToArray()

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
                |> Seq.map (fun difference -> string difference.RelativePath)
                |> HashSet

            let mutable unitValue = ()

            for directory in directorySnapshot do
                if directoryPathsToDrain.Contains(directory) then
                    directoriesToProcess.TryRemove(directory, &unitValue)
                    |> ignore

            for statusTrigger in statusTriggerSnapshot do
                let statusTriggerRelativePath = RelativePath statusTrigger.RelativePath

                let statusTriggerResolvedByChild =
                    appliedDifferences
                    |> Seq.exists (fun difference -> isPathUnderDirectoryTrigger statusTriggerRelativePath difference.RelativePath)

                if
                    statusTriggerPathsToDrain.Contains(statusTrigger.RelativePath)
                    || statusTriggerResolvedByChild
                then
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

    /// Defines the machine-readable payload emitted by `grace watch --check`.
    type private WatchCheckStatusDto =
        {
            IsRunning: bool
            CanUseIncrementalStatus: bool
            Mode: string
            Reason: string
            Message: string
            SafetyFlags: string array
            IsFresh: bool
            HasUsableRootSnapshot: bool
            HasDirectoryIndexSnapshot: bool
            IsStartupClaim: bool
            UpdatedAt: string option
            RootDirectoryId: DirectoryVersionId option
        }

    /// Reports whether a Watch IPC snapshot has the root identity needed by incremental status shortcuts.
    let private hasUsableRootSnapshotForCheck (status: GraceWatchStatus) =
        status.RootDirectoryId <> Guid.Empty
        && not (String.IsNullOrWhiteSpace($"{status.RootDirectorySha256Hash}"))
        && not (String.IsNullOrWhiteSpace($"{status.RootDirectoryBlake3Hash}"))

    /// Reports whether a Watch IPC snapshot includes directory identities for current-state comparisons.
    let private hasDirectoryIndexSnapshotForCheck (status: GraceWatchStatus) =
        not (isNull status.DirectoryIds)
        && status.DirectoryIds.Count > 0

    /// Builds safety flags for `watch --check` after combining persisted and derived mode facts.
    let private safetyFlagsForWatchCheck (inspection: GraceWatchStatusInspection) =
        let flags = HashSet<string>(inspection.SafetyFlags, StringComparer.Ordinal)

        if not inspection.IsUsable then
            flags.Remove("incrementalSafe") |> ignore

            if not (flags.Contains("pendingWatchWork")) then
                flags.Add("requiresExplicitResync") |> ignore

        flags |> Seq.sort |> Seq.toArray

    /// Maps a Watch IPC inspection result to the stable status reason emitted to humans and agents.
    let private watchCheckReason (inspection: GraceWatchStatusInspection) =
        match inspection.ReadError, inspection.Exists, inspection.Status, inspection.EffectiveMode with
        | Some _, _, _, _ -> "unreadableStatus"
        | None, false, _, _ -> "notRunning"
        | None, true, None, _ -> "unreadableStatus"
        | None, true, Some _, _ when not inspection.IsFresh -> "staleStatus"
        | None, true, Some status, _ when status.IsStartupClaim -> "startingUp"
        | None, true, Some _, Some GraceWatchRuntimeMode.HealthyIncremental when inspection.IsUsable -> "running"
        | None, true, Some _, Some GraceWatchRuntimeMode.Resynchronizing -> "resynchronizing"
        | None, true, Some _, Some GraceWatchRuntimeMode.Suspended -> "suspended"
        | None, true, Some _, Some GraceWatchRuntimeMode.Stopping -> "stopping"
        | None, true, Some _, Some GraceWatchRuntimeMode.StartingUp -> "startingUp"
        | _ -> "notReady"

    /// Explains Watch check status without exposing local IPC paths, raw repository paths, or exception stacks.
    let private watchCheckMessage reason =
        match reason with
        | "running" -> "GraceWatch is running in HealthyIncremental mode. Incremental status shortcuts are available."
        | "startingUp" -> "GraceWatch is starting and has not published a usable status snapshot yet."
        | "resynchronizing" -> "GraceWatch is resynchronizing trusted state; incremental status shortcuts are suspended until resync completes."
        | "suspended" -> "GraceWatch is suspended after confidence loss; restart watch or run an explicit resync before relying on incremental status."
        | "stopping" -> "GraceWatch is stopping and should not be treated as a live incremental source."
        | "staleStatus" -> "GraceWatch status is stale. The previous watcher may have exited before removing its status file."
        | "unreadableStatus" -> "GraceWatch status exists but could not be read. Restart watch or clear the stale status file by starting watch again."
        | "notRunning" -> "GraceWatch is not running."
        | _ -> "GraceWatch status is not ready for incremental shortcuts."

    /// Converts Watch IPC inspection into the public `watch --check` status payload.
    let private toWatchCheckStatusDto (inspection: GraceWatchStatusInspection) =
        let status = inspection.Status
        let reason = watchCheckReason inspection

        let mode =
            inspection.EffectiveMode
            |> Option.map (fun mode -> $"{mode}")
            |> Option.defaultValue "Unavailable"

        {
            IsRunning = inspection.IsLiveProcess
            CanUseIncrementalStatus = inspection.IsUsable
            Mode = mode
            Reason = reason
            Message = watchCheckMessage reason
            SafetyFlags = safetyFlagsForWatchCheck inspection
            IsFresh = inspection.IsFresh
            HasUsableRootSnapshot =
                status
                |> Option.map hasUsableRootSnapshotForCheck
                |> Option.defaultValue false
            HasDirectoryIndexSnapshot =
                status
                |> Option.map hasDirectoryIndexSnapshotForCheck
                |> Option.defaultValue false
            IsStartupClaim =
                status
                |> Option.map (fun status -> status.IsStartupClaim)
                |> Option.defaultValue false
            UpdatedAt =
                status
                |> Option.map (fun status -> status.UpdatedAt.ToString())
            RootDirectoryId =
                status
                |> Option.map (fun status -> status.RootDirectoryId)
        }

    /// Renders `watch --check` in either human or machine-readable mode while preserving check exit semantics.
    let private renderWatchCheckStatus parseResult (status: WatchCheckStatusDto) =
        if parseResult |> json then
            let renderExit =
                Ok(GraceReturnValue.Create status (getCorrelationId parseResult))
                |> renderOutput parseResult

            if renderExit <> 0 then renderExit
            elif status.CanUseIncrementalStatus then 0
            else -1
        else
            let color =
                if status.CanUseIncrementalStatus then Colors.Important
                elif status.IsRunning then Colors.Important
                else Colors.Error

            logToAnsiConsole color status.Message

            if status.CanUseIncrementalStatus then 0 else -1

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

    /// Reads a GUID-valued property from a SignalR automation event payload without throwing on unsupported input.
    let private tryParseAutomationEventGuidProperty (propertyName: string) (dataJson: string) =
        try
            use document = JsonDocument.Parse(dataJson)
            let root = document.RootElement
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
        with
        | _ -> Option.None

    /// Returns a trusted SignalR branch snapshot only for promotion events targeting the watched parent.
    let private trustedSignalRBranchSubscriptionForAutomationEvent (envelope: AutomationEventEnvelope) =
        if envelope.EventType = AutomationEventType.PromotionSetApplied then
            let targetBranchId =
                tryParseAutomationEventGuidProperty "targetBranchId" envelope.DataJson
                |> Option.defaultValue BranchId.Empty

            signalRBranchSubscriptionForWatchedParentBranch targetBranchId
        else
            Option.None

    /// Handles SignalR automation events that can drive Watch auto-rebase.
    let private handleSignalRAutomationEvent readStatus rebaseCurrentBranch (envelope: AutomationEventEnvelope) =
        task {
            try
                match trustedSignalRBranchSubscriptionForAutomationEvent envelope with
                | Some trustedSubscription ->
                    let terminalReferenceId =
                        tryParseAutomationEventGuidProperty "terminalPromotionReferenceId" envelope.DataJson
                        |> Option.defaultValue ReferenceId.Empty

                    logToAnsiConsole
                        Colors.Highlighted
                        $"Parent branch {trustedSubscription.ParentBranchId} received terminal promotion {terminalReferenceId}; evaluating auto-rebase."

                    let! currentStatus = readStatus ()

                    let currentBranchId = Current().BranchId

                    if currentBranchId = trustedSubscription.BranchId
                       && signalRBranchSubscriptionStillTrusted trustedSubscription then
                        logToAnsiConsole
                            Colors.Highlighted
                            $"Parent branch {trustedSubscription.ParentBranchId} still matches branch {trustedSubscription.BranchId}; starting auto-rebase."

                        do! rebaseCurrentBranch currentStatus
                    else
                        logToAnsiConsole
                            Colors.Important
                            $"Skipped auto-rebase for parent branch {trustedSubscription.ParentBranchId} because Watch branch identity changed before rebase."
                | None -> ()
            with
            | ex -> logToAnsiConsole Colors.Error $"Failed to process automation event payload for {envelope.EventType}: {Markup.Escape(ex.Message)}."
        }

    /// Exposes the SignalR automation-event path to Watch tests without opening a HubConnection.
    let internal handleSignalRAutomationEventForWatchTests readStatus rebaseCurrentBranch envelope =
        handleSignalRAutomationEvent readStatus rebaseCurrentBranch envelope

    /// Registers the current repository branch with its parent-branch SignalR group and refreshes local event trust.
    let private registerCurrentSignalRParentBranch (signalRConnection: HubConnection) cancellationToken =
        task {
            let refreshAuthority = beginSignalRBranchSubscriptionRefresh ()

            let current = Current()

            let branchGetParameters =
                GetBranchParameters(
                    OwnerId = $"{current.OwnerId}",
                    OrganizationId = $"{current.OrganizationId}",
                    RepositoryId = $"{current.RepositoryId}",
                    BranchId = $"{current.BranchId}"
                )

            match! Branch.GetParentBranch branchGetParameters with
            | Ok returnValue ->
                let parentBranchDto = returnValue.ReturnValue

                do! signalRConnection.InvokeAsync("RegisterParentBranch", refreshAuthority.BranchId, parentBranchDto.BranchId, cancellationToken)

                if trySetSignalRBranchSubscription refreshAuthority parentBranchDto.BranchId then
                    logToAnsiConsole
                        Colors.Highlighted
                        $"SignalR Hub connection state: {signalRConnection.State}. Listening for changes in parent branch {parentBranchDto.BranchName} ({parentBranchDto.BranchId}); connectionId: {signalRConnection.ConnectionId}."

                    return Ok parentBranchDto
                else
                    logToAnsiConsole
                        Colors.Important
                        $"Grace Watch ignored stale SignalR parent subscription refresh for branch {refreshAuthority.BranchId}; current branch identity changed before registration completed."

                    return Ok parentBranchDto
            | Error error ->
                completeSignalRBranchSubscriptionRefreshWithoutTrust ()
                return Error error
        }

    /// Evaluates is grace status artifact against parsed options and command state.
    let private isGraceStatusArtifact (fullPath: string) =
        let statusFile = Current().GraceStatusFile

        fullPath.Equals(statusFile, StringComparison.InvariantCultureIgnoreCase)
        || fullPath.Equals(statusFile + "-wal", StringComparison.InvariantCultureIgnoreCase)
        || fullPath.Equals(statusFile + "-shm", StringComparison.InvariantCultureIgnoreCase)
        || fullPath.Equals(statusFile + "-journal", StringComparison.InvariantCultureIgnoreCase)

    /// Coordinates on created behavior for this CLI command path.
    let OnCreated (args: FileSystemEventArgs) =
        if not (canCaptureFilesystemObservation ()) then
            logObservationSuppressed args.FullPath
        elif updateNotInProgress ()
             && isGraceStatusArtifact args.FullPath then
            markGraceStatusChangedAndPublishPendingWorkTransition ()
        elif isDelayedGraceOwnedFileObservation args.FullPath then
            logObservationSuppressed args.FullPath
        elif
            updateNotInProgress ()
            && Directory.Exists(args.FullPath)
        then
            let directoryStatusAddQueued, directoryStatusEnumerationComplete = tryEnqueueDirectoryStatusAdds args.FullPath

            if directoryStatusAddQueued then
                logToAnsiConsole Colors.Added $"I saw that directory {args.FullPath} was created."

            if not directoryStatusEnumerationComplete then
                enqueueDirectoryUploadRetry args.FullPath

            if directoryStatusAddQueued
               || not directoryStatusEnumerationComplete then
                publishPendingWatchWorkTransitionIfNeeded ()
        elif updateNotInProgress ()
             && isNotDirectory args.FullPath then
            if enqueueFileUpload args.FullPath then
                logToAnsiConsole Colors.Added $"I saw that {args.FullPath} was created."
                publishPendingWatchWorkTransitionIfNeeded ()

            if isGraceStatusArtifact args.FullPath then
                markGraceStatusChangedAndPublishPendingWorkTransition ()

    /// Coordinates on changed behavior for this CLI command path.
    let OnChanged (args: FileSystemEventArgs) =
        if not (canCaptureFilesystemObservation ()) then
            logObservationSuppressed args.FullPath
        elif updateNotInProgress ()
             && isGraceStatusArtifact args.FullPath then
            markGraceStatusChangedAndPublishPendingWorkTransition ()
            logToAnsiConsole Colors.Important $"Grace Status file has been updated."
        elif isDelayedGraceOwnedFileObservation args.FullPath then
            logObservationSuppressed args.FullPath
        elif updateNotInProgress ()
             && isNotDirectory args.FullPath then
            let shouldIgnore = shouldIgnoreFile args.FullPath
            //logToAnsiConsole Colors.Verbose $"Should ignore {args.FullPath}: {shouldIgnore}."

            if not <| shouldIgnore then
                logToAnsiConsole Colors.Changed $"I saw that {args.FullPath} changed."
                enqueueFileUpload args.FullPath |> ignore
                publishPendingWatchWorkTransitionIfNeeded ()

    /// Reads enqueue directory contents for upload data needed by the command workflow without changing remote state.
    let private enqueueDirectoryContentsForUpload directoryPath = tryEnqueueDirectoryContentsForUpload directoryPath

    /// Retries directory content enumeration before status application consumes directory rename-old triggers.
    let private retryPendingDirectoryUploads () =
        let mutable unitValue = ()

        for directoryPath in directoriesToProcess.Keys.ToArray() do
            let _, directoryStatusEnumerationComplete = tryEnqueueDirectoryStatusAdds directoryPath
            let fileUploadEnumerationComplete = enqueueDirectoryContentsForUpload directoryPath

            if directoryStatusEnumerationComplete
               && fileUploadEnumerationComplete then
                directoriesToProcess.TryRemove(directoryPath, &unitValue)
                |> ignore

    /// Coordinates on deleted behavior for this CLI command path.
    let OnDeleted (args: FileSystemEventArgs) =
        if not (canCaptureFilesystemObservation ()) then
            logObservationSuppressed args.FullPath
        elif updateNotInProgress ()
             && isGraceStatusArtifact args.FullPath then
            markGraceStatusChangedAndPublishPendingWorkTransition ()
        elif updateNotInProgress () then
            let canceledFileUpload = cancelPendingUploadsForDeletedPath args.FullPath

            if isDelayedGraceOwnedFileObservation args.FullPath
               && not canceledFileUpload then
                logObservationSuppressed args.FullPath
            elif enqueueStatusUpdateTrigger args.FullPath then
                match repositoryRelativePath args.FullPath with
                | Some relativePath ->
                    let invalidatedRelativePath = RelativePath relativePath

                    if
                        canceledFileUpload
                        || not (finalPathMatchesEntryType FileSystemEntryType.File invalidatedRelativePath)
                    then
                        clearProcessedFileRelativePathsPendingStatus [ invalidatedRelativePath ]
                        removeUploadedFileVersionsForPaths [ invalidatedRelativePath ]
                | None -> ()

                if canceledFileUpload then
                    match repositoryRelativePath args.FullPath with
                    | Some relativePath -> addCanceledFileUploadDeleteRelativePath (RelativePath relativePath)
                    | None -> ()

                logToAnsiConsole Colors.Deleted $"I saw that {args.FullPath} was deleted."
                publishPendingWatchWorkTransitionIfNeeded ()

            if isGraceStatusArtifact args.FullPath then
                markGraceStatusChangedAndPublishPendingWorkTransition ()

    /// Coordinates on renamed behavior for this CLI command path.
    let OnRenamed (args: RenamedEventArgs) =
        if not (canCaptureFilesystemObservation ()) then
            logObservationSuppressed args.FullPath
        elif updateNotInProgress () then
            let mutable queuedPendingWork = false
            let newPathIsDirectory = Directory.Exists(args.FullPath)

            let canceledFileUpload = cancelPendingUploadsForDeletedPath args.OldFullPath

            let oldPathStatusQueued = enqueueStatusUpdateTrigger args.OldFullPath
            queuedPendingWork <- queuedPendingWork || oldPathStatusQueued

            if oldPathStatusQueued then
                if canceledFileUpload then
                    match repositoryRelativePath args.OldFullPath with
                    | Some relativePath -> addCanceledFileUploadDeleteRelativePath (RelativePath relativePath)
                    | None -> ()

                logToAnsiConsole Colors.Changed $"I saw that {args.OldFullPath} was renamed to {args.FullPath}."

            if newPathIsDirectory then
                let directoryStatusAddQueued, directoryStatusEnumerationComplete = tryEnqueueDirectoryStatusAdds args.FullPath
                queuedPendingWork <- queuedPendingWork || directoryStatusAddQueued

                if directoryStatusAddQueued
                   && not oldPathStatusQueued then
                    logToAnsiConsole Colors.Changed $"I saw that {args.OldFullPath} was renamed to {args.FullPath}."

                let fileUploadWorkCountBefore = filesToProcess.Count
                let fileUploadEnumerationComplete = enqueueDirectoryContentsForUpload args.FullPath

                if not fileUploadEnumerationComplete then
                    enqueueDirectoryUploadRetry args.FullPath
                    queuedPendingWork <- true
                elif filesToProcess.Count > fileUploadWorkCountBefore then
                    queuedPendingWork <- true

                if not directoryStatusEnumerationComplete then
                    enqueueDirectoryUploadRetry args.FullPath
                    queuedPendingWork <- true
            elif enqueueFileUpload args.FullPath then
                logToAnsiConsole Colors.Changed $"I saw that {args.OldFullPath} was renamed to {args.FullPath}."
                queuedPendingWork <- true

            if queuedPendingWork then publishPendingWatchWorkTransitionIfNeeded ()

    /// Coordinates on error behavior for this CLI command path.
    let OnError (args: ErrorEventArgs) =
        let correlationId = generateCorrelationId ()
        let message = args.GetException().Message

        requestGraceWatchExplicitResync $"FileSystemWatcher error {correlationId}: {message}"

        logToAnsiConsole
            Colors.Error
            $"I saw that the FileSystemWatcher threw an exception: {message}. grace watch is resynchronizing before incremental work resumes."

    /// Coordinates on grace update in progress created behavior for this CLI command path.
    let OnGraceUpdateInProgressCreated (args: FileSystemEventArgs) =
        if isUpdateMarkerPath args.FullPath then
            observeGraceUpdateMarkerInstance args.FullPath

        if args.FullPath = updateInProgressFileName () then
            if updateInProgress () then
                let hasCurrentCompletedSidecar =
                    match tryReadGraceUpdateMarkerCompletedUtc () with
                    | Some completedUtc -> currentUpdateMarkerMatchesCompletedSidecar completedUtc
                    | None -> false

                if not hasCurrentCompletedSidecar then
                    clearCurrentGraceUpdateMarkerCompletedSidecar ()

                logToAnsiConsole Colors.Important $"Update is in progress from another Grace instance."
            else
                logToAnsiConsole Colors.Important $"{updateInProgressFileName ()} should already exist, but it doesn't."

    /// Reports whether a marker deletion callback still owns branch transition completion authority.
    let private deletedMarkerCanCompleteCurrentTransition markerFileName =
        let currentMarkerFileName = updateInProgressFileName ()

        String.Equals(markerFileName, currentMarkerFileName, StringComparison.OrdinalIgnoreCase)
        || not (File.Exists(currentMarkerFileName))

    /// Coordinates on grace update in progress deleted behavior for this CLI command path.
    let OnGraceUpdateInProgressDeleted (args: FileSystemEventArgs) =
        if isUpdateMarkerPath args.FullPath then
            if not (File.Exists(args.FullPath)) then
                match tryReadGraceUpdateMarkerCompletedUtcForMarker args.FullPath with
                | Some completedUtc ->
                    match classifyDeletedMarkerCompletedSidecar args.FullPath completedUtc with
                    | ObservedCurrentMarker ->
                        if deletedMarkerCanCompleteCurrentTransition args.FullPath then
                            completeGraceUpdateTransitionAfterMarkerDeletion completedUtc
                            logToAnsiConsole Colors.Important $"Update has finished in another Grace instance."
                        else
                            recordGraceUpdateMarkerCompletedUtc completedUtc

                            logToAnsiConsole
                                Colors.Important
                                $"Ignored stale update marker deletion for {args.FullPath}; current marker {updateInProgressFileName ()} is still live."
                    | UnobservedMarker when
                        deletedMarkerIsCurrentMarker args.FullPath
                        && isRecentGraceUpdateMarkerCompletion completedUtc
                        && persistedConfigurationChangedSinceWatchCached ()
                        ->
                        requestGraceWatchExplicitResync "branch transition marker deletion had a fresh completion sidecar but no observed marker creation"
                        completeGraceUpdateTransitionAfterMarkerDeletion completedUtc

                        logToAnsiConsole
                            Colors.Important
                            $"Update marker ended with an unobserved fresh completed sidecar for {args.FullPath}; grace watch is resynchronizing before incremental work resumes."
                    | ObservedStaleMarker
                    | UnobservedMarker ->
                        logToAnsiConsole
                            Colors.Important
                            $"Update marker ended with a stale completed sidecar for {args.FullPath}; delayed observations will be processed normally."
                | None ->
                    forgetObservedGraceUpdateMarkerInstance args.FullPath
                    logToAnsiConsole Colors.Important $"Update marker ended without a completed sidecar; delayed observations will be processed normally."
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
                if statusSideEffectStillAllowed "missing-upload retry queueing" then
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

                    let! result =
                        if statusSideEffectStillAllowed "directory-version upload" then
                            saveDirectoryVersions directoryVersionsToSave correlationId
                        else
                            Task.FromResult(Error(GraceError.Create "Watch confidence lost before directory-version upload." correlationId))

                    match result with
                    | Ok returnValue ->
                        let newGraceStatus = syncGraceStatusRootDirectoryHash newGraceStatus

                        if not (statusSideEffectStillAllowed "object-cache update") then
                            return None
                        else
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
                                let! saveReferenceResult =
                                    if statusSideEffectStillAllowed "save reference creation" then
                                        createSaveReference (getRootDirectoryVersion newGraceStatus) message correlationId
                                    else
                                        Task.FromResult(Error(GraceError.Create "Watch confidence lost before save reference creation." correlationId))

                                match saveReferenceResult with
                                | Ok returnValue ->
                                    let newGraceStatusWithUpdatedTime = { newGraceStatus with LastSuccessfulDirectoryVersionUpload = getCurrentInstant () }

                                    if statusSideEffectStillAllowed "local status application" then
                                        // Apply incremental changes to the Grace Status DB.
                                        do! applyGraceStatusIncremental newGraceStatusWithUpdatedTime newDirectoryVersions differences

                                        for fileVersion in directoryUploadedFileVersions do
                                            let mutable removedFileVersion = Unchecked.defaultof<FileVersion>

                                            uploadedFileVersions.TryRemove(uploadedFileVersionIdentity fileVersion, &removedFileVersion)
                                            |> ignore

                                        //logToAnsiConsole Colors.Important $"Setting graceStatusHasChanged to false in updateGraceStatus(). Current value: {graceStatusHasChanged}."
                                        graceStatusHasChanged <- currentGraceStatusRefreshGeneration () <> 0L // We *just* changed it ourselves, but a newer observed refresh must still be processed.
                                        return Some newGraceStatusWithUpdatedTime
                                    else
                                        return None
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
            let runtimeMode = currentGraceWatchRuntimeMode ()

            if not (isGraceWatchScanLegal runtimeMode) then
                logToAnsiConsole Colors.Verbose $"Grace Watch skipped working-tree scan while runtime mode is {runtimeMode}."
                return Some graceStatus
            else
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

    /// Publishes a trusted Watch IPC snapshot only when incremental mode is currently safe for other processes.
    let private tryPublishGraceWatchInterprocessFileForCurrentConfidence trustedStatus directoryIds updateGraceWatchInterprocessFileClient context =
        task {
            let runtimeMode = currentGraceWatchRuntimeMode ()

            let publicationVerified =
                if
                    runtimeMode = GraceWatchRuntimeMode.HealthyIncremental
                    && not (isGraceWatchResyncPending ())
                then
                    tryPublishWatchIpcWithFreshPendingWorkProbe trustedStatus directoryIds (fun () ->
                        updateGraceWatchInterprocessFileClient trustedStatus (Some directoryIds))
                else
                    let emptyDirectoryIds = HashSet<DirectoryVersionId>()

                    let verified =
                        tryPublishWatchIpcWithFreshPendingWorkProbe GraceStatus.Default emptyDirectoryIds (fun () ->
                            updateGraceWatchInterprocessFileClient GraceStatus.Default (Some emptyDirectoryIds))

                    logToAnsiConsole
                        Colors.Important
                        $"Grace Watch published non-incremental IPC for {context} while runtime mode is {runtimeMode} and resync pending is {isGraceWatchResyncPending ()}."

                    verified

            return publicationVerified
        }

    /// Publishes a trusted Watch IPC snapshot only when incremental mode is currently safe for other processes.
    let private publishGraceWatchInterprocessFileForCurrentConfidence trustedStatus directoryIds updateGraceWatchInterprocessFileClient context =
        task {
            let! _ = tryPublishGraceWatchInterprocessFileForCurrentConfidence trustedStatus directoryIds updateGraceWatchInterprocessFileClient context

            ()
        }

    /// Publishes Watch IPC for tests that verify confidence-lost states cannot advertise incremental safety.
    let internal publishGraceWatchInterprocessFileForCurrentConfidenceForWatchTests trustedStatus directoryIds updateGraceWatchInterprocessFileClient =
        publishGraceWatchInterprocessFileForCurrentConfidence trustedStatus directoryIds updateGraceWatchInterprocessFileClient "test refresh"

    /// Publishes a reloaded Grace Status snapshot after clearing the refresh flag consumed by this timer tick.
    let private publishGraceStatusRefreshSnapshot observedRefreshGeneration updatedGraceStatus updateGraceWatchInterprocessFileClient =
        task {
            graceStatus <- updatedGraceStatus
            updateGraceStatusDirectoryIds graceStatus

            graceStatusHasChanged <-
                currentGraceStatusRefreshGeneration ()
                <> observedRefreshGeneration

            let! publicationVerified =
                tryPublishGraceWatchInterprocessFileForCurrentConfidence
                    graceStatus
                    graceStatusDirectoryIds
                    updateGraceWatchInterprocessFileClient
                    "Grace Status refresh"

            if publicationVerified then
                tryClearGraceStatusRefreshGeneration observedRefreshGeneration
                |> ignore
            else
                graceStatusHasChanged <- true
        }

    /// Publishes a Grace Status refresh snapshot for tests that verify clean IPC after refresh consumption.
    let internal publishGraceStatusRefreshSnapshotForWatchTests updatedGraceStatus updateGraceWatchInterprocessFileClient =
        publishGraceStatusRefreshSnapshot (currentGraceStatusRefreshGeneration ()) updatedGraceStatus updateGraceWatchInterprocessFileClient

    /// Publishes the startup catch-up boundary as dirty so commands cannot trust pre-scan incremental status.
    let private publishStartupCatchUpPendingStatus trustedStatus directoryIds updateGraceWatchInterprocessFileClient =
        task {
            setGraceWatchPendingWorkStatusFlag true
            let publicationStartedAt = getCurrentInstant ()
            do! updateGraceWatchInterprocessFileClient trustedStatus (Some directoryIds)

            cachePendingWatchWorkPublicationIfVerified trustedStatus directoryIds true publicationStartedAt
            |> ignore
        }

    /// Publishes startup catch-up IPC for tests that verify startup scans do not expose trusted clean state early.
    let internal publishStartupCatchUpPendingStatusForWatchTests trustedStatus directoryIds updateGraceWatchInterprocessFileClient =
        publishStartupCatchUpPendingStatus trustedStatus directoryIds updateGraceWatchInterprocessFileClient

    /// Uploads pending file content while preserving the confidence boundary for the current Watch mode.
    let private uploadPendingWatchFilesForStatusRetry
        readGraceStatusMetaClient
        copyFileToObjectDirectoryAndUploadToStorageClient
        applyGraceStatusIncrementalClient
        correlationId
        recordProcessedPaths
        resyncAttempt
        =
        task {
            let! graceStatusFromDisk = readGraceStatusMetaClient ()
            graceStatus <- graceStatusFromDisk

            let mutable lastFileUploadInstant = graceStatus.LastSuccessfulFileUpload
            let mutable processedAnyFile = false

            retryPendingDirectoryUploads ()

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

                    let currentMode = currentGraceWatchRuntimeMode ()

                    let uploadResultStillTrusted =
                        match resyncAttempt with
                        | Some attempt -> isGraceWatchResyncAttemptActive attempt
                        | None ->
                            recordProcessedPaths
                            && not (isGraceWatchResyncPending ())
                            && (isGraceWatchObservationApplicationLegal currentMode
                                || currentMode = GraceWatchRuntimeMode.StartingUp
                                || currentMode = GraceWatchRuntimeMode.Stopping)

                    if
                        uploadResultStillTrusted
                        && (filesToProcess :> ICollection<KeyValuePair<string, int64>>)
                            .Contains(pendingPair)
                    then
                        (filesToProcess :> ICollection<KeyValuePair<string, int64>>)
                            .Remove(pendingPair)
                        |> ignore

                        if recordProcessedPaths then
                            match repositoryRelativePath pendingFile.FullPath with
                            | Some relativePath -> addProcessedFileRelativePathPendingStatus (RelativePath relativePath)
                            | None -> ()

                        processedAnyFile <- true
                        lastFileUploadInstant <- getCurrentInstant ()
                    else
                        logToAnsiConsole
                            Colors.Important
                            $"Grace Watch discarded upload completion for {pendingFile.FullPath} because runtime mode is {currentMode} and resync pending is {isGraceWatchResyncPending ()}."

            if processedAnyFile then
                graceStatus <- { graceStatus with LastSuccessfulFileUpload = lastFileUploadInstant }
                do! applyGraceStatusIncrementalClient graceStatus Seq.empty Seq.empty

            return processedAnyFile
        }

    /// Invokes status application only while the caller's confidence boundary still holds.
    let private updateGraceStatusFromDifferencesWhenTrusted trustPredicate updateGraceStatusFromDifferencesClient graceStatus differences correlationId =
        task {
            if trustPredicate () then
                return!
                    runWithStatusSideEffectTrustPredicate trustPredicate (fun () -> updateGraceStatusFromDifferencesClient graceStatus differences correlationId)
            else
                logToAnsiConsole Colors.Important "Grace Watch skipped status application because confidence was lost before status side effects started."
                return None
        }

    /// Prunes applied Watch journal rows immediately after a trusted applied-boundary advance.
    let private pruneWatchJournalAfterTrustedBoundaryAdvance (appendedWatchJournalSequences: int64 array) =
        task {
            if appendedWatchJournalSequences.Length > 0 then
                try
                    do! pruneWatchJournalRetentionForWatch ()
                with
                | ex ->
                    logToAnsiConsole
                        Colors.Verbose
                        $"Grace Watch could not prune applied journal rows after a trusted boundary advance; retention will retry after later status work: {Markup.Escape(ex.Message)}."
        }

    /// Requests resync before fallible journal repair so non-incremental IPC is published even when repair fails.
    let private repairWatchJournalAfterStatusTrustLoss advancedThrough requiredAppliedThrough resyncReason repairFailureContext =
        task {
            requestGraceWatchExplicitResync resyncReason

            try
                do! recoverWatchJournalBoundaryGapForWatch advancedThrough requiredAppliedThrough
            with
            | ex -> logToAnsiConsole Colors.Error $"{repairFailureContext}; resync was already requested before repair failed: {Markup.Escape(ex.Message)}."
        }

    /// Clears appended journal rows after status returns no durable result so the pending retry can reappend them.
    let private clearWatchJournalAfterNoDurableStatus appendedWatchJournalSequences =
        task {
            if Array.isEmpty appendedWatchJournalSequences then
                return false
            else
                let requiredAppliedThrough = appendedWatchJournalSequences |> Array.max

                try
                    do! recoverWatchJournalBoundaryGapForWatch 0L requiredAppliedThrough
                    return false
                with
                | ex ->
                    requestGraceWatchExplicitResync $"Watch status application returned no durable status and journal repair failed: {ex.Message}"

                    logToAnsiConsole
                        Colors.Error
                        $"Grace Watch appended normalized observations and status application returned no durable status, but journal repair failed; resync is required before incremental work can resume: {Markup.Escape(ex.Message)}."

                    return true
        }

    /// Combines durable journal sequences without reordering the first observation for each sequence.
    let private combineWatchJournalSequences (first: int64 array) (second: int64 array) =
        seq {
            yield! first
            yield! second
        }
        |> Seq.filter (fun sequence -> sequence > 0L)
        |> Seq.distinct
        |> Seq.toArray

    /// Converges appended journal rows after status side effects commit so recovery never resumes from stale pending rows.
    let private convergeWatchJournalAfterStatusApplication (appendedWatchJournalSequences: int64 array) =
        task {
            if appendedWatchJournalSequences.Length = 0 then
                return true
            else
                let requiredAppliedThrough = appendedWatchJournalSequences |> Array.max

                try
                    let! advancedThrough = advanceWatchJournalAppliedThroughSequencesForWatch appendedWatchJournalSequences

                    if advancedThrough < requiredAppliedThrough then
                        do!
                            repairWatchJournalAfterStatusTrustLoss
                                advancedThrough
                                requiredAppliedThrough
                                $"Watch journal applied boundary advanced only through {advancedThrough} after status application required {requiredAppliedThrough}."
                                "Grace Watch applied status but could not repair the durable journal boundary through the appended observations"

                        logToAnsiConsole
                            Colors.Error
                            $"Grace Watch applied status but could not advance the durable journal boundary through the appended observations; resync is required before status work is drained."

                        return false
                    else
                        do! pruneWatchJournalAfterTrustedBoundaryAdvance appendedWatchJournalSequences
                        return true
                with
                | ex ->
                    do!
                        repairWatchJournalAfterStatusTrustLoss
                            0L
                            requiredAppliedThrough
                            $"Watch journal applied boundary advancement failed after status application: {ex.Message}"
                            "Grace Watch applied status but could not repair the durable journal boundary after advancement failed"

                    logToAnsiConsole
                        Colors.Error
                        $"Grace Watch applied status but could not advance the durable journal boundary; resync was requested before journal repair so incremental shortcuts cannot replay already-applied work: {Markup.Escape(ex.Message)}."

                    return false
        }

    /// Processes any changed files since the last timer tick.
    let internal processChangedFilesWithClients
        readGraceStatusMetaClient
        readGraceStatusFileClient
        copyFileToObjectDirectoryAndUploadToStorageClient
        _updateGraceStatusClient
        (_scanForDifferencesClient: GraceStatus -> Task<List<FileSystemDifference>>)
        (updateGraceStatusFromDifferencesClient: GraceStatus -> List<FileSystemDifference> -> CorrelationId -> Task<GraceStatus option>)
        applyGraceStatusIncrementalClient
        updateGraceWatchInterprocessFileClient
        =
        task {
            configureWatchPathComparisonForCurrentRepository ()

            // First, check if there's anything to process.
            if isGraceWatchResyncPending () then
                let resyncAttempt = currentGraceWatchResyncAttempt ()

                try
                    let correlationId = generateCorrelationId ()

                    let! uploadedRetryContent, uploadRetryFailed =
                        if filesToProcess.IsEmpty then
                            Task.FromResult(false, false)
                        else
                            task {
                                try
                                    let! uploadedRetryContent =
                                        uploadPendingWatchFilesForStatusRetry
                                            readGraceStatusMetaClient
                                            copyFileToObjectDirectoryAndUploadToStorageClient
                                            applyGraceStatusIncrementalClient
                                            correlationId
                                            false
                                            (Some resyncAttempt)

                                    return uploadedRetryContent, false
                                with
                                | ex ->
                                    if isGraceWatchResyncAttemptActive resyncAttempt then
                                        setGraceWatchRuntimeMode GraceWatchRuntimeMode.Resynchronizing

                                    logToAnsiConsole
                                        Colors.Error
                                        $"Grace Watch resync upload retry failed and will remain queued for another tick: {ex.Message}"

                                    return false, true
                            }

                    let resyncStatusUpdateStillTrusted () = isGraceWatchResyncAttemptActive resyncAttempt

                    if uploadRetryFailed then
                        ()
                    elif not (resyncStatusUpdateStillTrusted ()) then
                        logToAnsiConsole Colors.Important $"Grace Watch skipped stale resync attempt {resyncAttempt} because a newer resync attempt is pending."
                    else
                        if uploadedRetryContent then
                            logToAnsiConsole
                                Colors.Important
                                "Grace Watch uploaded file content queued by resync recovery; retrying the scan-derived status boundary."

                        let! graceStatusSnapshot = readGraceStatusFileClient ()
                        graceStatus <- graceStatusSnapshot

                        let! scanDerivedDifferences = _scanForDifferencesClient graceStatus

                        if not (wasLastScanForDifferencesSuccessful ()) then
                            failwith "scan-derived resync did not complete successfully"

                        let! statusUpdateResult =
                            if scanDerivedDifferences.Count > 0 then
                                updateGraceStatusFromDifferencesWhenTrusted
                                    resyncStatusUpdateStillTrusted
                                    updateGraceStatusFromDifferencesClient
                                    graceStatus
                                    scanDerivedDifferences
                                    correlationId
                            else
                                Task.FromResult(Some graceStatus)

                        match statusUpdateResult with
                        | Some newGraceStatus when resyncStatusUpdateStillTrusted () ->
                            graceStatus <- newGraceStatus
                            updateGraceStatusDirectoryIds graceStatus

                            setGraceWatchRuntimeMode GraceWatchRuntimeMode.HealthyIncremental

                            let clearedResyncAttempt = tryClearGraceWatchResyncAttempt resyncAttempt

                            if clearedResyncAttempt then
                                publishWatchIpcWithFreshPendingWorkProbe graceStatus graceStatusDirectoryIds (fun () ->
                                    updateGraceWatchInterprocessFileClient graceStatus (Some graceStatusDirectoryIds))

                                if signalRBranchSubscriptionRefreshNeededForTransition () then
                                    try
                                        refreshSignalRSubscriptionsAfterTransitionCompletion ()
                                    with
                                    | ex ->
                                        completeSignalRBranchSubscriptionRefreshWithoutTrust ()

                                        logToAnsiConsole
                                            Colors.Error
                                            $"Grace Watch resync recovered local status but could not refresh SignalR parent subscription; parent-triggered auto-rebase is disabled until the next successful registration: {Markup.Escape(ex.Message)}."

                                logToAnsiConsole
                                    Colors.Important
                                    $"Grace Watch resync applied {scanDerivedDifferences.Count} scan-derived differences; incremental observations may resume."
                            else
                                setGraceWatchRuntimeMode GraceWatchRuntimeMode.Resynchronizing

                                logToAnsiConsole
                                    Colors.Important
                                    $"Grace Watch kept newer resync attempt pending after stale attempt {resyncAttempt} completed."
                        | Some _ ->
                            logToAnsiConsole
                                Colors.Important
                                $"Grace Watch skipped stale resync commit for attempt {resyncAttempt} because confidence changed before commit."
                        | None ->
                            if filesToProcess.IsEmpty then
                                suspendGraceWatchAttemptAfterFailedRecovery resyncAttempt "scan-derived status update returned no durable status"
                            else
                                setGraceWatchRuntimeMode GraceWatchRuntimeMode.Resynchronizing

                                logToAnsiConsole
                                    Colors.Important
                                    $"Grace Watch resync queued file uploads before applying scan-derived status; retry will continue after uploads complete."

                    graceStatus <- GraceStatus.Default
                    GC.Collect(2, GCCollectionMode.Forced, blocking = true, compacting = true)
                with
                | ex ->
                    suspendGraceWatchAttemptAfterFailedRecovery resyncAttempt $"resync failed before the durable status boundary: {ex.Message}"

                    logToAnsiConsole
                        Colors.Error
                        $"Error in processChangedFiles resync: Message: {ex.Message}{Environment.NewLine}{Environment.NewLine}{ex.StackTrace}"
            elif hasPendingWatchWork () then
                try
                    let correlationId = generateCorrelationId ()

                    let! _ =
                        uploadPendingWatchFilesForStatusRetry
                            readGraceStatusMetaClient
                            copyFileToObjectDirectoryAndUploadToStorageClient
                            applyGraceStatusIncrementalClient
                            correlationId
                            true
                            None

                    // If we've drained all of the files that changed (and we'll almost always have done so), update all the things:
                    //   GraceStatus, directory versions, etc.
                    if filesToProcess.IsEmpty
                       && directoriesToProcess.IsEmpty then
                        let directorySnapshot, statusTriggerSnapshot = statusOnlyTriggerSnapshot ()
                        let fileWorkGenerationBeforeStatusUpdate = Volatile.Read(&fileUploadWorkGeneration)
                        let! graceStatusSnapshot = readGraceStatusFileClient ()
                        graceStatus <- graceStatusSnapshot
                        let canceledFileUploadDeleteRelativePathsForStatus = canceledFileUploadDeleteRelativePathsSnapshot ()
                        let processedFileRelativePathsForStatus = processedFileRelativePathsPendingStatusSnapshot ()
                        let startupPendingDifferences = pendingStatusDifferencesSnapshot ()

                        let! requeuedResolvedFileDeletePaths =
                            reenqueueUploadsForResolvedDeletes
                                graceStatus
                                statusTriggerSnapshot
                                canceledFileUploadDeleteRelativePathsForStatus
                                processedFileRelativePathsForStatus
                                startupPendingDifferences

                        let! uploadedFileDifferences, unresolvedUploadedFilePaths =
                            deriveUploadedFileDifferences graceStatus processedFileRelativePathsForStatus

                        let requeuedUnresolvedUploadedFilePaths = reenqueueUnresolvedUploadedFinalFileVersions unresolvedUploadedFilePaths

                        let deleteDifferences = deriveDeleteDifferences graceStatus statusTriggerSnapshot
                        let eventDerivedDifferences = mergeStatusDifferences deleteDifferences uploadedFileDifferences

                        for eventDerivedDifference in (eventDerivedDifferences :> seq<FileSystemDifference>) do
                            addPendingStatusDifference eventDerivedDifference

                        let pendingDifferencesToClear = mergeStatusDifferences startupPendingDifferences eventDerivedDifferences

                        let statusDifferencesForApply =
                            splitApplicableStatusDifferences
                                graceStatus
                                statusTriggerSnapshot
                                (mergeCurrentStatusDifferences processedFileRelativePathsForStatus startupPendingDifferences eventDerivedDifferences)

                        let statusApplicationModeStillTrusted () =
                            let runtimeModeBeforeStatusApplication = currentGraceWatchRuntimeMode ()

                            if runtimeModeBeforeStatusApplication = GraceWatchRuntimeMode.Stopping then
                                false
                            elif startupPendingDifferences.Count > 0 then
                                runtimeModeBeforeStatusApplication = GraceWatchRuntimeMode.StartingUp
                                || runtimeModeBeforeStatusApplication = GraceWatchRuntimeMode.HealthyIncremental
                            else
                                isGraceWatchObservationApplicationLegal runtimeModeBeforeStatusApplication

                        let statusApplicationLegal = statusApplicationModeStillTrusted ()
                        let mutable appendedWatchJournalSequences = Array.empty<int64>
                        let mutable newlyAppendedWatchJournalSequences = Array.empty<int64>
                        let mutable newlyAppendedWatchJournalReplayRows = Array.empty<int64 * FileSystemDifference>
                        let mutable startupReplayRowsParticipated = false
                        let mutable appendedWatchJournalConverged = false

                        let statusUpdateStillTrusted () =
                            filesToProcess.IsEmpty
                            && Volatile.Read(&fileUploadWorkGeneration) = fileWorkGenerationBeforeStatusUpdate
                            && not (isGraceWatchResyncPending ())
                            && statusApplicationModeStillTrusted ()

                        let! statusUpdateResult =
                            if requeuedResolvedFileDeletePaths.Count > 0 then
                                logToAnsiConsole
                                    Colors.Important
                                    $"Grace Status stale delete differences requeued live final content; requeued uploads will finish before status application."

                                Task.FromResult(None)
                            elif requeuedUnresolvedUploadedFilePaths.Count > 0 then
                                logToAnsiConsole
                                    Colors.Important
                                    $"Grace Status uploaded file differences include unresolved final content; requeued uploads will finish before status application."

                                Task.FromResult(None)
                            elif not statusApplicationLegal then
                                logToAnsiConsole
                                    Colors.Verbose
                                    $"Grace Watch deferred status application while runtime mode is {currentGraceWatchRuntimeMode ()}."

                                Task.FromResult(None)
                            elif statusDifferencesForApply.Applicable.Count > 0 then
                                task {
                                    let! appendResult =
                                        task {
                                            try
                                                let startupReplaySequences =
                                                    statusDifferencesForApply.Applicable
                                                    |> Seq.collect startupReplaySequencesForDifference
                                                    |> Seq.toArray

                                                let differencesNeedingJournalAppend =
                                                    statusDifferencesForApply.Applicable
                                                    |> Seq.filter (fun difference ->
                                                        tryPeekStartupReplaySequence difference
                                                        |> Option.isNone)
                                                    |> Seq.toArray

                                                let! sequences =
                                                    if Array.isEmpty differencesNeedingJournalAppend then
                                                        Task.FromResult(Array.empty<int64>)
                                                    else
                                                        differencesNeedingJournalAppend
                                                        |> Seq.map journalObservationForDifference
                                                        |> appendWatchJournalObservationsForWatch

                                                return Ok(startupReplaySequences, sequences, differencesNeedingJournalAppend)
                                            with
                                            | ex -> return Error ex
                                        }

                                    match appendResult with
                                    | Ok (startupReplaySequences, sequences, newlyAppendedDifferences) ->
                                        startupReplayRowsParticipated <- startupReplaySequences.Length > 0
                                        newlyAppendedWatchJournalSequences <- sequences
                                        newlyAppendedWatchJournalReplayRows <- Array.zip sequences newlyAppendedDifferences
                                        appendedWatchJournalSequences <- combineWatchJournalSequences startupReplaySequences sequences

                                        try
                                            return!
                                                updateGraceStatusFromDifferencesWhenTrusted
                                                    statusUpdateStillTrusted
                                                    updateGraceStatusFromDifferencesClient
                                                    graceStatus
                                                    statusDifferencesForApply.Applicable
                                                    correlationId
                                        with
                                        | ex ->
                                            let requiredAppliedThrough =
                                                if appendedWatchJournalSequences.Length = 0 then
                                                    0L
                                                else
                                                    appendedWatchJournalSequences |> Array.max

                                            do!
                                                repairWatchJournalAfterStatusTrustLoss
                                                    0L
                                                    requiredAppliedThrough
                                                    $"Watch status application failed after journal append: {ex.Message}"
                                                    "Grace Watch appended normalized observations but could not repair the journal after status application failed"

                                            appendedWatchJournalConverged <- true

                                            logToAnsiConsole
                                                Colors.Error
                                                $"Grace Watch appended normalized observations but status application failed; resync was requested before journal repair so pending rows cannot replay as still-unapplied work: {Markup.Escape(ex.Message)}."

                                            return None
                                    | Error ex ->
                                        requestGraceWatchExplicitResync $"Watch journal append failed before status application: {ex.Message}"

                                        logToAnsiConsole
                                            Colors.Error
                                            $"Grace Watch could not append normalized observations to the durable journal; unjournaled status application was skipped and resync is required: {Markup.Escape(ex.Message)}."

                                        return None
                                }
                            else
                                Task.FromResult(Some graceStatus)

                        match statusUpdateResult with
                        | Some newGraceStatus ->
                            let commitRuntimeMode = currentGraceWatchRuntimeMode ()

                            let resolvedStartupReplaySequences =
                                statusDifferencesForApply.Resolved
                                |> Seq.collect startupReplaySequencesForDifference
                                |> Seq.toArray

                            appendedWatchJournalSequences <- combineWatchJournalSequences appendedWatchJournalSequences resolvedStartupReplaySequences

                            let statusUpdateCanCommit =
                                statusUpdateStillTrusted ()
                                && (startupPendingDifferences.Count > 0
                                    || isGraceWatchObservationApplicationLegal commitRuntimeMode)

                            let! watchJournalBoundaryTrusted = convergeWatchJournalAfterStatusApplication appendedWatchJournalSequences
                            appendedWatchJournalConverged <- true

                            if statusUpdateCanCommit then
                                if watchJournalBoundaryTrusted then
                                    graceStatus <- newGraceStatus

                                    if startupPendingDifferences.Count = 0 then
                                        drainStatusOnlyTriggers directorySnapshot statusTriggerSnapshot
                                    else
                                        drainAppliedStatusWork directorySnapshot statusTriggerSnapshot statusDifferencesForApply.Resolved

                                    let canceledFileUploadDeletePathsToClear =
                                        if startupPendingDifferences.Count = 0 then
                                            statusTriggerSnapshot
                                            |> Seq.map (fun statusTrigger -> RelativePath statusTrigger.RelativePath)
                                        else
                                            statusDifferencesForApply.Resolved
                                            |> Seq.filter (fun difference -> difference.FileSystemEntryType = FileSystemEntryType.File)
                                            |> Seq.map (fun difference -> difference.RelativePath)

                                    clearCanceledFileUploadDeleteRelativePaths canceledFileUploadDeletePathsToClear

                                    clearPendingStatusDifferences (mergeStatusDifferences pendingDifferencesToClear statusDifferencesForApply.Resolved)
                                    clearStartupReplaySequences (mergeStatusDifferences pendingDifferencesToClear statusDifferencesForApply.Resolved)
                                    clearProcessedFileRelativePathsPendingStatus processedFileRelativePathsForStatus
                                    removeUploadedFileVersionsForPaths processedFileRelativePathsForStatus
                            else
                                clearPendingStatusDifferences pendingDifferencesToClear
                                clearStartupReplaySequences pendingDifferencesToClear
                                clearProcessedFileRelativePathsPendingStatus processedFileRelativePathsForStatus
                                removeUploadedFileVersionsForPaths processedFileRelativePathsForStatus

                                logToAnsiConsole
                                    Colors.Important
                                    $"Grace Status file update completed while newer file upload work was pending; status-only triggers will retry."
                        | None ->
                            if startupReplayRowsParticipated then
                                for sequence, difference in newlyAppendedWatchJournalReplayRows do
                                    rememberDeferredStartupReplaySequence sequence difference

                                appendedWatchJournalConverged <- true

                                if newlyAppendedWatchJournalReplayRows.Length > 0 then
                                    logToAnsiConsole
                                        Colors.Important
                                        $"Grace Watch kept existing startup replay rows durable after status application deferred; newly appended rows will retry without duplicate journal append."
                            elif newlyAppendedWatchJournalSequences.Length > 0
                                 && not appendedWatchJournalConverged then
                                let! noDurableStatusRepairFailed = clearWatchJournalAfterNoDurableStatus newlyAppendedWatchJournalSequences
                                appendedWatchJournalConverged <- true

                                if noDurableStatusRepairFailed then
                                    logToAnsiConsole
                                        Colors.Error
                                        $"Grace Watch appended normalized observations but status application returned no durable status; repair failed and resync is required before incremental work can resume."
                                else
                                    logToAnsiConsole
                                        Colors.Important
                                        $"Grace Watch appended normalized observations but status application returned no durable status; appended journal rows were cleared so the pending status work can retry."

                            logToAnsiConsole Colors.Important $"Grace Status file was not updated."
                            () // Something went wrong, don't update the in-memory Grace Status.

                    let runtimeModeAfterProcessing = currentGraceWatchRuntimeMode ()

                    if
                        runtimeModeAfterProcessing = GraceWatchRuntimeMode.HealthyIncremental
                        && not (isGraceWatchResyncPending ())
                    then
                        updateGraceStatusDirectoryIds graceStatus

                        publishWatchIpcWithFreshPendingWorkProbe graceStatus graceStatusDirectoryIds (fun () ->
                            updateGraceWatchInterprocessFileClient graceStatus (Some graceStatusDirectoryIds))
                    else
                        let emptyDirectoryIds = HashSet<DirectoryVersionId>()

                        publishWatchIpcWithFreshPendingWorkProbe GraceStatus.Default emptyDirectoryIds (fun () ->
                            updateGraceWatchInterprocessFileClient GraceStatus.Default (Some emptyDirectoryIds))

                        logToAnsiConsole
                            Colors.Important
                            $"Grace Watch published non-incremental IPC after processing ended in runtime mode {runtimeModeAfterProcessing} with resync pending {isGraceWatchResyncPending ()}."

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
                let runtimeMode = currentGraceWatchRuntimeMode ()

                if
                    runtimeMode = GraceWatchRuntimeMode.HealthyIncremental
                    && not (isGraceWatchResyncPending ())
                then
                    let! graceStatusFromDisk = readGraceStatusMetaClient ()

                    publishWatchIpcWithFreshPendingWorkProbe graceStatusFromDisk graceStatusDirectoryIds (fun () ->
                        updateGraceWatchInterprocessFileClient graceStatusFromDisk (Some graceStatusDirectoryIds))
                elif runtimeMode = GraceWatchRuntimeMode.Suspended then
                    let emptyDirectoryIds = HashSet<DirectoryVersionId>()

                    publishWatchIpcWithFreshPendingWorkProbe GraceStatus.Default emptyDirectoryIds (fun () ->
                        updateGraceWatchInterprocessFileForSuspendedMode GraceStatus.Default (Some emptyDirectoryIds))
                else
                    let emptyDirectoryIds = HashSet<DirectoryVersionId>()

                    publishWatchIpcWithFreshPendingWorkProbe GraceStatus.Default emptyDirectoryIds (fun () ->
                        updateGraceWatchInterprocessFileClient GraceStatus.Default (Some emptyDirectoryIds))

                    logToAnsiConsole
                        Colors.Important
                        $"Grace Watch refreshed non-incremental IPC while runtime mode is {runtimeMode} and resync pending is {isGraceWatchResyncPending ()}."

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

    /// Checks whether replayed startup file content can no longer produce an upload side effect.
    let private isStaleStartupFileReplayRow (row: Grace.CLI.LocalStateDb.WatchJournalPendingReplay) =
        row.EntryType = FileSystemEntryType.File
        && (row.DifferenceType = DifferenceType.Add
            || row.DifferenceType = DifferenceType.Change)
        && finalPathKind row.RelativePath <> FinalPathFile

    /// Records a startup lifecycle event as diagnostics that cannot be replayed as Watch correctness data.
    let private recordStartupLifecycleEvent status eventType message =
        recordWatchLifecycleEventForWatch (
            { Scope = currentWatchJournalScope status; EventType = eventType; Message = message }: Grace.CLI.LocalStateDb.WatchLifecycleEvent
        )

    /// Replays compatible pending journal rows after startup reconciliation and quarantines rows from stale scopes.
    let private recoverStartupWatchJournalAfterReconciliation status =
        task {
            do! recordStartupLifecycleEvent status "startup-reconciliation-complete" "Startup scan reconciliation completed before journal replay."
            let! recovery = recoverWatchJournalForStartupForWatch (currentWatchJournalScope status)

            if recovery.QuarantinedRows.Length > 0 then
                Interlocked.Add(&quarantinedWatchObservationCount, recovery.QuarantinedRows.Length)
                |> ignore

                logToAnsiConsole Colors.Important $"Grace Watch quarantined {recovery.QuarantinedRows.Length} incompatible startup journal rows before replay."

            let staleFileReplayRows, replayRows =
                recovery.CompatibleReplayRows
                |> Array.partition isStaleStartupFileReplayRow

            if staleFileReplayRows.Length > 0 then
                let staleReplaySequences =
                    staleFileReplayRows
                    |> Array.map (fun row -> row.Sequence)

                let! _ = quarantineWatchJournalSequencesForWatch staleReplaySequences "stale startup replay file content missing before status application"

                Interlocked.Add(&quarantinedWatchObservationCount, staleFileReplayRows.Length)
                |> ignore

                logToAnsiConsole Colors.Important $"Grace Watch retired {staleFileReplayRows.Length} stale startup file replay rows before status application."

                do!
                    recordStartupLifecycleEvent
                        status
                        "startup-stale-file-replay-retired"
                        $"Startup replay retired {staleFileReplayRows.Length} stale file add/change rows because their final file content was missing."

            for row in replayRows do
                let difference = FileSystemDifference.Create row.DifferenceType row.EntryType row.RelativePath
                addStartupReplayedStatusDifference row.Sequence difference

            if replayRows.Length > 0 then
                logToAnsiConsole Colors.Important $"Grace Watch replayed {replayRows.Length} compatible pending journal rows after startup reconciliation."

            do!
                recordStartupLifecycleEvent
                    status
                    "startup-replay-complete"
                    $"Startup replay queued {replayRows.Length} compatible rows, retired {staleFileReplayRows.Length} stale file rows, and quarantined {recovery.QuarantinedRows.Length} incompatible rows."

            return recovery
        }

    /// Exposes startup journal recovery to tests without starting the foreground watcher loop.
    let internal recoverStartupWatchJournalAfterReconciliationForWatchTests status = recoverStartupWatchJournalAfterReconciliation status

    /// Re-enters startup replay and promotion after delayed startup work drains on a later timer pass.
    let private completeStartupRecoveryIfPendingWorkDrained readGraceStatusFileClient updateGraceWatchInterprocessFileClient processChangedFilesClient =
        task {
            let mutable attemptedStartupCompletion = false

            if
                not (hasPendingWatchWork ())
                && currentGraceWatchRuntimeMode () = GraceWatchRuntimeMode.StartingUp
            then
                attemptedStartupCompletion <- true
                let! reconciledStartupStatus = readGraceStatusFileClient ()
                graceStatus <- reconciledStartupStatus
                updateGraceStatusDirectoryIds reconciledStartupStatus
                let! startupRecovery = recoverStartupWatchJournalAfterReconciliation reconciledStartupStatus

                if startupRecovery.CompatibleReplayRows.Length > 0 then
                    graceStatus <- GraceStatus.Default
                    do! processChangedFilesClient ()

            if
                attemptedStartupCompletion
                && not (hasPendingWatchWork ())
            then
                promoteStartupModeIfRecoverySucceeded ()

            if
                attemptedStartupCompletion
                && not (hasPendingWatchWork ())
            then
                let! startupCatchUpStatus = readGraceStatusFileClient ()
                updateGraceStatusDirectoryIds startupCatchUpStatus

                do!
                    publishGraceWatchInterprocessFileForCurrentConfidence
                        startupCatchUpStatus
                        graceStatusDirectoryIds
                        updateGraceWatchInterprocessFileClient
                        "startup catch-up"
        }

    /// Exposes delayed startup replay completion to tests without starting the foreground watcher loop.
    let internal completeStartupRecoveryIfPendingWorkDrainedForWatchTests
        readGraceStatusFileClient
        updateGraceWatchInterprocessFileClient
        processChangedFilesClient
        =
        completeStartupRecoveryIfPendingWorkDrained readGraceStatusFileClient updateGraceWatchInterprocessFileClient processChangedFilesClient

    /// Executes the watch command by binding ParseResult values to the SDK request and CLI output contract.
    type Watch() =
        inherit AsynchronousCommandLineAction()

        /// Runs the asynchronous watch action when System.CommandLine dispatches the parsed command.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) =
            task {
                try
                    if isCheckRequested parseResult then
                        let! watchStatusInspection = inspectGraceWatchStatus ()
                        let watchCheckStatus = toWatchCheckStatusDto watchStatusInspection
                        raise (WatchCommandExit(renderWatchCheckStatus parseResult watchCheckStatus))

                    let! claimedGraceWatchStatus = tryClaimGraceWatchInterprocessFile ()

                    if not claimedGraceWatchStatus then
                        logToAnsiConsole Colors.Error "GraceWatch is already running."
                        raise (WatchCommandExit -1)

                    setGraceWatchRuntimeMode GraceWatchRuntimeMode.StartingUp
                    configureWatchPathComparisonForCurrentRepository ()

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

                    use updateMarkerWatcherRebindRegistration =
                        registerUpdateMarkerWatcherRebind (fun () ->
                            let markerDirectory = Path.GetDirectoryName(updateInProgressFileName ())

                            Directory.CreateDirectory(markerDirectory)
                            |> ignore

                            let currentPath = Path.GetFullPath(updateInProgressFileSystemWatcher.Path)
                            let targetPath = Path.GetFullPath(markerDirectory)

                            if
                                not
                                <| String.Equals
                                    (
                                        currentPath,
                                        targetPath,
                                        if OperatingSystem.IsWindows() then
                                            StringComparison.OrdinalIgnoreCase
                                        else
                                            StringComparison.Ordinal
                                    )
                            then
                                let wasEnabled = updateInProgressFileSystemWatcher.EnableRaisingEvents
                                updateInProgressFileSystemWatcher.EnableRaisingEvents <- false
                                updateInProgressFileSystemWatcher.Path <- markerDirectory
                                updateInProgressFileSystemWatcher.EnableRaisingEvents <- wasEnabled

                                logToAnsiConsole Colors.Important $"Grace Watch rebound update marker watcher to {markerDirectory}.")

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
                    do! publishStartupCatchUpPendingStatus graceStatus graceStatusDirectoryIds updateGraceWatchInterprocessFile

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

                    use refreshSignalRSubscriptions =
                        registerSignalRSubscriptionRefresh (fun () ->
                            try
                                match (registerCurrentSignalRParentBranch signalRConnection cancellationToken)
                                    .GetAwaiter()
                                    .GetResult()
                                    with
                                | Ok _ -> ()
                                | Error error ->
                                    completeSignalRBranchSubscriptionRefreshWithoutTrust ()
                                    logToAnsiConsole Colors.Error $"Failed to refresh SignalR parent branch subscription: {Markup.Escape(error.ToString())}"
                            with
                            | ex ->
                                completeSignalRBranchSubscriptionRefreshWithoutTrust ()

                                logToAnsiConsole
                                    Colors.Error
                                    $"Failed to refresh SignalR parent branch subscription; parent-triggered auto-rebase is disabled until the next successful registration: {Markup.Escape(ex.Message)}.")

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
                                (handleSignalRAutomationEvent
                                    readGraceStatusFile
                                    (fun currentStatus ->
                                        task {
                                            let! _ = Branch.rebaseHandler (parseResult |> getNormalizedIdsAndNames) currentStatus
                                            ()
                                        })
                                    envelope)
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
                    // Repository-wide notifications are keyed only by RepositoryId, so same-process branch switches do not change this group.
                    do! signalRConnection.InvokeAsync("RegisterRepository", Current().RepositoryId, cancellationToken)

                    logToAnsiConsole
                        Colors.Highlighted
                        $"SignalR Hub connection state: {signalRConnection.State}. Listening for changes in repository {Current().RepositoryName} ({Current().RepositoryId}); connectionId: {signalRConnection.ConnectionId}."

                    match! registerCurrentSignalRParentBranch signalRConnection cancellationToken with
                    | Ok _ -> ()
                    | Error error ->
                        logToAnsiConsole Colors.Error $"Failed to retrieve branch metadata. Cannot connect to SignalR Hub."

                        logToAnsiConsole Colors.Error $"{Markup.Escape(error.ToString())}"

                    let! startupRecovery = recoverStartupWatchJournalAfterReconciliation graceStatus

                    if startupRecovery.CompatibleReplayRows.Length > 0 then
                        logToAnsiConsole Colors.Verbose $"Grace Watch recovered startup journal rows before scanning for offline differences."

                    // Check for changes that occurred while not running.
                    logToAnsiConsole Colors.Verbose $"Scanning for differences."

                    let! differences =
                        if isGraceWatchScanLegal (currentGraceWatchRuntimeMode ()) then
                            scanForDifferences graceStatus // <--- This always finds the directories with updated write times, but we never update GraceStatus below..
                        else
                            logToAnsiConsole Colors.Verbose $"Grace Watch skipped startup scan while runtime mode is {currentGraceWatchRuntimeMode ()}."

                            Task.FromResult(List<FileSystemDifference>())

                    if differences |> Seq.isEmpty then
                        logToAnsiConsole Colors.Verbose $"Already up-to-date."
                    else
                        logToAnsiConsole Colors.Verbose $"Found {differences.Count} differences."

                    for difference in differences do
                        queueStartupDifferenceForWatch difference

                    // Process any changes that occurred while not running.
                    graceStatus <- GraceStatus.Default
                    do! processChangedFiles ()
                    do! completeStartupRecoveryIfPendingWorkDrained readGraceStatusFile updateGraceWatchInterprocessFile processChangedFiles

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
                            let refreshGeneration = currentGraceStatusRefreshGeneration ()
                            let! updatedGraceStatus = readGraceStatusFile ()
                            do! publishGraceStatusRefreshSnapshot refreshGeneration updatedGraceStatus updateGraceWatchInterprocessFile

                        do! processChangedFiles ()
                        do! completeStartupRecoveryIfPendingWorkDrained readGraceStatusFile updateGraceWatchInterprocessFile processChangedFiles
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

                    setGraceWatchRuntimeMode GraceWatchRuntimeMode.Stopping

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
