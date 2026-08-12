namespace Grace.CLI.Command

open Grace.CLI
open Grace.CLI.Services
open Grace.Shared.Client.Configuration
open Grace.Shared.Constants
open Grace.Shared.Services
open Grace.Shared.Utilities
open Grace.Types.Common
open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

/// Applies the sealed Branch exact-root transaction without accepting caller-controlled paths, state readers, or finalizers.
module internal WorkingDirectoryUpdate =
    module Contracts = WorkingDirectoryUpdateContracts

    /// Holds the exact immutable DirectoryVersion graph selected by Branch after remote resolution completes.
    type TargetGraph = private TargetGraph of Contracts.Target * GraceStatus

    /// Produces stable complete-status evidence for a sealed admission or an under-lease reread.
    let private statusFingerprint (status: GraceStatus) =
        let builder = StringBuilder()

        builder.Append(status.RootDirectoryId.ToString("N"))
        |> ignore

        builder
            .Append('|')
            .Append(string status.RootDirectorySha256Hash)
        |> ignore

        builder
            .Append('|')
            .Append(string status.RootDirectoryBlake3Hash)
        |> ignore

        status.Index.Values
        |> Seq.sortBy (fun directory -> string directory.RelativePath)
        |> Seq.iter (fun directory ->
            builder
                .Append('|')
                .Append(string directory.RelativePath)
            |> ignore

            builder
                .Append('|')
                .Append(directory.DirectoryVersionId.ToString("N"))
            |> ignore

            builder
                .Append('|')
                .Append(string directory.Sha256Hash)
            |> ignore

            builder
                .Append('|')
                .Append(string directory.Blake3Hash)
            |> ignore

            directory.Directories
            |> Seq.sort
            |> Seq.iter (fun child ->
                builder.Append('|').Append(child.ToString("N"))
                |> ignore)

            directory.Files
            |> Seq.sortBy (fun file -> string file.RelativePath)
            |> Seq.iter (fun file ->
                builder
                    .Append('|')
                    .Append(string file.RelativePath)
                |> ignore

                builder.Append('|').Append(string file.Sha256Hash)
                |> ignore

                builder.Append('|').Append(string file.Blake3Hash)
                |> ignore

                builder.Append('|').Append(file.Size) |> ignore))

        SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    /// Captures the only no-Save Branch baseline before target resolution and prepared-content retrieval begin.
    let internal captureAcceptedBranchPhase localStateDbPath actionToken =
        task {
            let! revision = LocalStateDb.readLocalStatusRevision localStateDbPath
            let! status = LocalStateDb.readStatusSnapshot localStateDbPath

            return
                Contracts.AcceptedBranchPhase.create revision (statusFingerprint status) actionToken
                |> Result.defaultWith invalidOp
        }

    /// Resolves a declared relative path below the active Grace root without allowing traversal to escape it.
    let private pathUnderRoot root (relativePath: RelativePath) =
        let normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
        let candidate = Path.GetFullPath(Path.Combine(normalizedRoot, string relativePath))

        let prefix =
            normalizedRoot
            + string Path.DirectorySeparatorChar

        if candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then
            candidate
        else
            invalidOp "Working Directory Update rejected a path outside its configured root."

    /// Recomputes both content identities from disk immediately before an object or working-copy boundary.
    let private computeHashes path =
        task {
            use stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)
            return! computeHashesForFile stream (RelativePath(Path.GetFileName(path)))
        }

    /// Requires a real file to retain the exact dual hashes declared by the selected graph.
    let private verifyFile path sha256Hash blake3Hash =
        task {
            if not (File.Exists(path)) then
                return Error "The required file is missing."
            else
                let! actualSha256Hash, actualBlake3Hash = computeHashes path

                if actualSha256Hash <> sha256Hash then
                    return Error "File bytes do not match their prepared SHA-256 hash."
                elif actualBlake3Hash <> blake3Hash then
                    return Error "File bytes do not match their prepared BLAKE3 hash."
                else
                    return Ok()
        }

    /// Publishes an immutable prepared file to the object cache and verifies its final bytes before local mutation starts.
    let private publishObject preparedContent objectRoot relativePath sha256Hash blake3Hash =
        task {
            let objectFileName = getLocalObjectCacheFileName relativePath sha256Hash blake3Hash
            let objectPath = pathUnderRoot objectRoot (RelativePath(Path.Combine(string relativePath, objectFileName)))
            let objectDirectory = Path.GetDirectoryName(objectPath)

            Directory.CreateDirectory(objectDirectory)
            |> ignore

            let temporaryPath = Path.Combine(objectDirectory, $".{objectFileName}.{Guid.NewGuid():N}.tmp")

            try
                match! verifyFile objectPath sha256Hash blake3Hash with
                | Ok () -> return Ok objectPath
                | Error _ ->
                    match Contracts.PreparedContent.openRead preparedContent relativePath with
                    | Error error -> return Error error
                    | Ok source ->
                        use source = source
                        use destination = File.Open(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                        do! source.CopyToAsync(destination)
                        destination.Flush(true)
                        destination.Dispose()

                        match! verifyFile temporaryPath sha256Hash blake3Hash with
                        | Error error -> return Error error
                        | Ok () ->
                            File.Move(temporaryPath, objectPath, true)

                            match! verifyFile objectPath sha256Hash blake3Hash with
                            | Ok () -> return Ok objectPath
                            | Error error -> return Error error
            finally
                if File.Exists(temporaryPath) then File.Delete(temporaryPath)
        }

    /// Builds the exact selected graph only when its root agrees with the immutable operation target.
    module TargetGraph =
        /// Creates the target graph carried to the five-input transaction after remote resolution is complete.
        let create target (status: GraceStatus) =
            if LocalStateDb.validateCompleteStatusTree status
               |> Result.isError then
                Error "Working Directory Update target graph is not a complete canonical status tree."
            elif status.RootDirectoryId
                 <> Contracts.Target.rootDirectoryVersionId target
                 || status.RootDirectorySha256Hash
                    <> Contracts.Target.sha256Hash target
                 || status.RootDirectoryBlake3Hash
                    <> Contracts.Target.blake3Hash target then
                Error "Working Directory Update target graph does not match the selected exact root."
            else
                Ok(TargetGraph(target, status))

        /// Returns the selected target only to the canonical local transaction.
        let internal target (TargetGraph (target, _)) = target

        /// Returns the complete immutable local-status projection only to the canonical local transaction.
        let internal status (TargetGraph (_, status)) = status

    /// Creates a classified private failure without exposing local mutable transaction state.
    let private failure reason =
        Contracts.Failure.create reason
        |> Result.defaultWith invalidOp

    /// Creates a receipt after the operation identity has been derived from the sealed Branch selection.
    let private receipt target operation bytesChanged =
        Contracts.Receipt.create target operation bytesChanged
        |> Result.defaultWith invalidOp

    /// Lists all target files in stable path order for object publication and working-copy materialization.
    let private targetFiles preparedContent =
        Contracts.PreparedContent.manifest preparedContent
        |> Contracts.PreparedManifest.entries
        |> Seq.choose (function
            | Contracts.PreparedManifestEntry.File (path, sha256Hash, blake3Hash) -> Some(path, sha256Hash, blake3Hash)
            | Contracts.PreparedManifestEntry.Directory _ -> None)
        |> Seq.sortBy (fun (path, _, _) -> string path)
        |> Seq.toArray

    /// Verifies every target file and recursively recomputes the selected root from actual working-tree bytes.
    let rec private verifyDirectory localRoot (status: GraceStatus) directoryId =
        task {
            let mutable directory = LocalDirectoryVersion.Default

            if not (status.Index.TryGetValue(directoryId, &directory)) then
                return Error $"Working Directory Update final verification is missing directory {directoryId}."
            else
                let directoryPath =
                    if directory.RelativePath = RootDirectoryPath then
                        Path.GetFullPath(localRoot)
                    else
                        pathUnderRoot localRoot directory.RelativePath

                if not (Directory.Exists(directoryPath)) then
                    return Error $"Working Directory Update final verification is missing directory '{directory.RelativePath}'."
                else
                    let entries = ResizeArray<DirectoryVersionPreimageEntry>()
                    let mutable fileError = None
                    let mutable directSize = 0L

                    for file in directory.Files do
                        if fileError.IsNone then
                            let filePath = pathUnderRoot localRoot file.RelativePath

                            match! verifyFile filePath file.Sha256Hash file.Blake3Hash with
                            | Error error -> fileError <- Some error
                            | Ok () ->
                                let size = FileInfo(filePath).Length

                                if size <> file.Size then
                                    fileError <- Some $"File '{file.RelativePath}' has an unexpected size."
                                else
                                    directSize <- directSize + size
                                    entries.Add(DirectoryVersionPreimageEntry.File file.RelativePath size file.Blake3Hash file.Sha256Hash)

                    match fileError with
                    | Some error -> return Error error
                    | None when directSize <> directory.Size -> return Error $"Directory '{directory.RelativePath}' has an unexpected direct-file size."
                    | None ->
                        let mutable childError = None

                        for childId in directory.Directories do
                            if childError.IsNone then
                                let! child = verifyDirectory localRoot status childId

                                match child with
                                | Error error -> childError <- Some error
                                | Ok (childPath, childSize, childSha256Hash, childBlake3Hash) ->
                                    entries.Add(DirectoryVersionPreimageEntry.Directory childPath childSize childBlake3Hash childSha256Hash)

                        match childError with
                        | Some error -> return Error error
                        | None ->
                            let actualSha256Hash = computeSha256ForDirectoryEntries directory.RelativePath entries
                            let actualBlake3Hash = computeBlake3ForDirectory directory.RelativePath entries

                            if actualSha256Hash <> directory.Sha256Hash then
                                return Error $"Directory '{directory.RelativePath}' does not match its SHA-256 hash."
                            elif actualBlake3Hash <> directory.Blake3Hash then
                                return Error $"Directory '{directory.RelativePath}' does not match its BLAKE3 hash."
                            else
                                return Ok(directory.RelativePath, directSize, actualSha256Hash, actualBlake3Hash)
        }

    /// Ensures the complete final graph and both selected root hashes are proven from the real working tree.
    let private verifySelectedRoot localRoot scanInput target (status: GraceStatus) =
        task {
            match! scanWorkingTreeForDifferencesReadOnly scanInput status with
            | Error error -> return Error $"Working Directory Update final tree scan failed: {error}"
            | Ok differences when differences.Count > 0 -> return Error "Working Directory Update final tree contains missing or unexpected eligible entries."
            | Ok _ ->
                match! verifyDirectory localRoot status status.RootDirectoryId with
                | Ok (_, _, sha256Hash, blake3Hash) when
                    sha256Hash = Contracts.Target.sha256Hash target
                    && blake3Hash = Contracts.Target.blake3Hash target
                    ->
                    return Ok()
                | Ok _ -> return Error "Working Directory Update final root does not match the selected target."
                | Error error -> return Error error
        }

    /// Records every tracked topology action before the first local working-tree mutation.
    type private TopologyPlan =
        {
            FilesToDelete: RelativePath array
            DirectoriesToDelete: RelativePath array
            DirectoriesToCreate: RelativePath array
            FilesToMaterialize: (RelativePath * Sha256Hash * Blake3Hash) array
        }

    /// Turns a configuration into a scan snapshot that cannot consult the process-wide configuration cache.
    let private scanInputFor (configuration: GraceConfiguration) : WorkingTreeScanInput =
        {
            RootDirectory = configuration.RootDirectory
            GraceDirectory = configuration.GraceDirectory
            GraceStatusFile = configuration.GraceStatusFile
            DirectoryIgnoreEntries = Array.copy configuration.GraceDirectoryIgnoreEntries
            FileIgnoreEntries = Array.copy configuration.GraceFileIgnoreEntries
        }

    /// Requires the disk configuration read at the final gate to retain the original local transaction scope.
    let private isSameConfiguration (original: GraceConfiguration) (fresh: GraceConfiguration) =
        original.RepositoryId = fresh.RepositoryId
        && original.BranchId = fresh.BranchId
        && String.Equals(original.RootDirectory, fresh.RootDirectory, StringComparison.OrdinalIgnoreCase)
        && String.Equals(original.GraceDirectory, fresh.GraceDirectory, StringComparison.OrdinalIgnoreCase)
        && String.Equals(original.GraceStatusFile, fresh.GraceStatusFile, StringComparison.OrdinalIgnoreCase)
        && String.Equals(original.ObjectDirectory, fresh.ObjectDirectory, StringComparison.OrdinalIgnoreCase)
        && original.GraceDirectoryIgnoreEntries = fresh.GraceDirectoryIgnoreEntries
        && original.GraceFileIgnoreEntries = fresh.GraceFileIgnoreEntries

    /// Maps one absolute entry to the canonical relative spelling used by local status.
    let private relativePathForRoot root path =
        let relative = Path.GetRelativePath(root, path)

        if relative = "." then RootDirectoryPath else RelativePath(relative)

    /// Rejects a directory replacement unless every existing descendant is already a tracked path scheduled for removal.
    let private ensureOnlyPlannedTrackedEntries root (trackedPaths: HashSet<string>) (plannedRemovalPaths: HashSet<string>) fullPath =
        try
            Directory.EnumerateFileSystemEntries(fullPath, "*", SearchOption.AllDirectories)
            |> Seq.iter (fun entry ->
                let relative = relativePathForRoot root entry |> string

                if
                    not
                        (
                            trackedPaths.Contains(relative)
                            && plannedRemovalPaths.Contains(relative)
                        )
                then
                    invalidOp $"Working Directory Update refuses to replace '{relative}' because it contains ignored or untracked content.")

            Ok()
        with
        | ex -> Error ex.Message

    /// Enumerates all tracked blockers and ordered actions while the tree is still untouched.
    let private buildTopologyPlan localRoot (currentStatus: GraceStatus) (targetStatus: GraceStatus) files =
        try
            let targetFilePaths = HashSet<string>(files |> Seq.map (fun (path, _, _) -> string path), StringComparer.OrdinalIgnoreCase)

            let targetDirectories =
                targetStatus.Index.Values
                |> Seq.map (fun directory -> directory.RelativePath)
                |> Seq.distinct
                |> Seq.filter (fun path -> path <> RootDirectoryPath)
                |> Seq.sortBy (fun path -> (string path).Length, string path)
                |> Seq.toArray

            let targetDirectoryPaths = HashSet<string>(targetDirectories |> Seq.map string, StringComparer.OrdinalIgnoreCase)

            let currentFiles =
                currentStatus.Index.Values
                |> Seq.collect (fun directory -> directory.Files)
                |> Seq.map (fun file -> file.RelativePath)
                |> Seq.distinct
                |> Seq.toArray

            let currentDirectories =
                currentStatus.Index.Values
                |> Seq.map (fun directory -> directory.RelativePath)
                |> Seq.distinct
                |> Seq.filter (fun path -> path <> RootDirectoryPath)
                |> Seq.toArray

            let currentFilePaths = HashSet<string>(currentFiles |> Seq.map string, StringComparer.OrdinalIgnoreCase)
            let currentDirectoryPaths = HashSet<string>(currentDirectories |> Seq.map string, StringComparer.OrdinalIgnoreCase)

            let filesToDelete =
                currentFiles
                |> Seq.filter (fun path -> not (targetFilePaths.Contains(string path)))
                |> Seq.sortByDescending (fun path -> (string path).Length, string path)
                |> Seq.toArray

            let directoriesToDelete =
                currentDirectories
                |> Seq.filter (fun path -> not (targetDirectoryPaths.Contains(string path)))
                |> Seq.sortByDescending (fun path -> (string path).Length, string path)
                |> Seq.toArray

            let plannedRemovalPaths =
                HashSet<string>(Seq.append (filesToDelete |> Seq.map string) (directoriesToDelete |> Seq.map string), StringComparer.OrdinalIgnoreCase)

            let trackedPaths =
                HashSet<string>(Seq.append (currentFiles |> Seq.map string) (currentDirectories |> Seq.map string), StringComparer.OrdinalIgnoreCase)

            for directory in targetDirectories do
                let fullPath = pathUnderRoot localRoot directory

                if
                    File.Exists(fullPath)
                    && not (currentFilePaths.Contains(string directory))
                then
                    invalidOp $"Working Directory Update refuses ignored or untracked file blocker '{directory}'."

            for path, _, _ in files do
                let fullPath = pathUnderRoot localRoot path

                if Directory.Exists(fullPath) then
                    if not (currentDirectoryPaths.Contains(string path)) then
                        invalidOp $"Working Directory Update refuses ignored or untracked directory blocker '{path}'."

                    match ensureOnlyPlannedTrackedEntries localRoot trackedPaths plannedRemovalPaths fullPath with
                    | Ok () -> ()
                    | Error error -> invalidOp error

            for directory in directoriesToDelete do
                let fullPath = pathUnderRoot localRoot directory

                if Directory.Exists(fullPath) then
                    match ensureOnlyPlannedTrackedEntries localRoot trackedPaths plannedRemovalPaths fullPath with
                    | Ok () -> ()
                    | Error error -> invalidOp error

            Ok { FilesToDelete = filesToDelete; DirectoriesToDelete = directoriesToDelete; DirectoriesToCreate = targetDirectories; FilesToMaterialize = files }
        with
        | ex -> Error ex.Message

    /// Applies a previously complete topology plan, setting the incomplete boundary immediately before each tracked mutation.
    let private applyTopologyPlan localRoot (objectPaths: Dictionary<string, string>) (plan: TopologyPlan) markMutationStarted =
        task {
            for path in plan.FilesToDelete do
                let fullPath = pathUnderRoot localRoot path

                if File.Exists(fullPath) then
                    markMutationStarted ()
                    File.Delete(fullPath)

            for path in plan.DirectoriesToDelete do
                let fullPath = pathUnderRoot localRoot path

                if Directory.Exists(fullPath) then
                    if
                        Directory.EnumerateFileSystemEntries(fullPath)
                        |> Seq.isEmpty
                    then
                        markMutationStarted ()
                        Directory.Delete(fullPath)
                    else
                        invalidOp $"Working Directory Update planned non-empty tracked directory '{path}' for removal."

            for path in plan.DirectoriesToCreate do
                let fullPath = pathUnderRoot localRoot path

                if File.Exists(fullPath) then
                    invalidOp $"Working Directory Update planned a file where target directory '{path}' is required."
                elif not (Directory.Exists(fullPath)) then
                    markMutationStarted ()
                    Directory.CreateDirectory(fullPath) |> ignore

            for path, sha256Hash, blake3Hash in plan.FilesToMaterialize do
                let destination = pathUnderRoot localRoot path

                if Directory.Exists(destination) then
                    invalidOp $"Working Directory Update planned a directory where target file '{path}' is required."

                match! verifyFile destination sha256Hash blake3Hash with
                | Ok () -> ()
                | Error _ ->
                    let destinationDirectory = Path.GetDirectoryName(destination)

                    if not (Directory.Exists(destinationDirectory)) then
                        invalidOp $"Working Directory Update planned missing parent directory for '{path}'."

                    markMutationStarted ()
                    File.Copy(objectPaths[string path], destination, true)

                    match! verifyFile destination sha256Hash blake3Hash with
                    | Ok () -> ()
                    | Error error -> invalidOp error
        }

    /// Converts every owned-marker cleanup disposition into the one pre-mutation reject rule.
    let private cleanOwnedMarkerBeforeMutation scope attempt context =
        task {
            let! cleanup = WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope attempt

            return
                match cleanup with
                | WorkingDirectoryUpdateCoordination.MarkerCleanup.NoMarker
                | WorkingDirectoryUpdateCoordination.MarkerCleanup.ExactMatchCleaned -> Ok()
                | WorkingDirectoryUpdateCoordination.MarkerCleanup.ExactCleanupFailed ->
                    Error $"{context}; retained exact marker evidence; run `grace doctor --repair-local-state`."
                | WorkingDirectoryUpdateCoordination.MarkerCleanup.DifferentOperationEvidence ->
                    Error $"{context}; preserved different-operation marker evidence; run `grace doctor --repair-local-state`."
                | WorkingDirectoryUpdateCoordination.MarkerCleanup.MalformedOrUnsupportedEvidence ->
                    Error $"{context}; preserved malformed marker evidence; run `grace doctor --repair-local-state`."
                | WorkingDirectoryUpdateCoordination.MarkerCleanup.UnreadableEvidence ->
                    Error $"{context}; preserved unreadable marker evidence; run `grace doctor --repair-local-state`."
        }

    /// Selects a rejected pre-mutation outcome without ever discarding the owned marker cleanup result.
    let private rejectedAfterCleanup normalReason cleanup =
        match cleanup with
        | Ok () -> Contracts.Rejected(failure normalReason)
        | Error error -> Contracts.Rejected(failure error)

    /// Runs the canonical five-input Branch transaction for a selected Reference or exact DirectoryVersion root.
    let run
        (acceptedPhase: Contracts.AcceptedBranchPhase)
        (selection: Contracts.BranchSelection)
        (targetGraph: TargetGraph)
        (preparedContent: Contracts.PreparedContent)
        (_correlationId: CorrelationId)
        =
        task {
            let target = TargetGraph.target targetGraph
            let targetStatus = TargetGraph.status targetGraph
            let cancellationToken = Contracts.AcceptedBranchPhase.actionToken acceptedPhase
            let configuration = Current()

            let scanInput = scanInputFor configuration

            let operation =
                Contracts.Operation.branchSwitchWithSelection configuration.BranchId selection target
                |> Result.defaultWith invalidOp

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create (Contracts.Target.repositoryId target) configuration.RootDirectory
                |> Result.defaultWith invalidOp

            let mutable mutationStarted = false
            let mutable ownedAttempt = None

            try
                use preparedLifetime =
                    { new IDisposable with
                        member _.Dispose() = Contracts.PreparedContent.dispose preparedContent
                    }

                use! heldLease = WorkingDirectoryUpdateCoordination.Lease.acquire scope cancellationToken
                let! currentRevision = LocalStateDb.readLocalStatusRevision configuration.GraceStatusFile
                let! currentStatus = LocalStateDb.readStatusSnapshot configuration.GraceStatusFile
                let! completion = LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target operation
                let! pending = LocalStateDb.readPendingWorkingDirectoryUpdateFinalization configuration.GraceStatusFile
                let! marker = WorkingDirectoryUpdateCoordination.Marker.inspect scope target operation

                match completion with
                | Some LocalStateDb.WorkingDirectoryUpdateCompletion.Terminal -> return Contracts.Unchanged(receipt target operation false)
                | Some LocalStateDb.WorkingDirectoryUpdateCompletion.Pending ->
                    let isExactPending =
                        match pending with
                        | Some (LocalStateDb.PendingWorkingDirectoryUpdateFinalization.PendingBranchFinalization (pendingTarget, pendingOperation, _, _)) ->
                            Contracts.Target.canonical pendingTarget = Contracts.Target.canonical target
                            && Contracts.Operation.value pendingOperation = Contracts.Operation.value operation
                        | _ -> false

                    if not isExactPending then
                        return Contracts.Rejected(failure "A different Working Directory Update finalization is already pending.")
                    else
                        let receipt = receipt target operation false

                        let! retryCleanup =
                            match marker with
                            | WorkingDirectoryUpdateCoordination.MarkerInspection.Missing -> task { return Ok() }
                            | WorkingDirectoryUpdateCoordination.MarkerInspection.ExactMatch ->
                                if cancellationToken.IsCancellationRequested then
                                    task {
                                        return
                                            Error
                                                "Working Directory Update retry was cancelled before exact marker cleanup; run `grace doctor --repair-local-state`."
                                    }
                                else
                                    task {
                                        let! cleanup = WorkingDirectoryUpdateCoordination.Marker.tryRemoveExactOperation scope target operation

                                        return
                                            match cleanup with
                                            | WorkingDirectoryUpdateCoordination.MarkerCleanup.ExactMatchCleaned -> Ok()
                                            | WorkingDirectoryUpdateCoordination.MarkerCleanup.NoMarker -> Ok()
                                            | WorkingDirectoryUpdateCoordination.MarkerCleanup.ExactCleanupFailed ->
                                                Error "Working Directory Update retry retained exact marker evidence; run `grace doctor --repair-local-state`."
                                            | WorkingDirectoryUpdateCoordination.MarkerCleanup.DifferentOperationEvidence ->
                                                Error
                                                    "Working Directory Update retry preserved different-operation marker evidence; run `grace doctor --repair-local-state`."
                                            | WorkingDirectoryUpdateCoordination.MarkerCleanup.MalformedOrUnsupportedEvidence ->
                                                Error
                                                    "Working Directory Update retry preserved malformed marker evidence; run `grace doctor --repair-local-state`."
                                            | WorkingDirectoryUpdateCoordination.MarkerCleanup.UnreadableEvidence ->
                                                Error
                                                    "Working Directory Update retry preserved unreadable marker evidence; run `grace doctor --repair-local-state`."
                                    }
                            | WorkingDirectoryUpdateCoordination.MarkerInspection.DifferentOperation ->
                                task {
                                    return
                                        Error
                                            "Working Directory Update retry preserved different-operation marker evidence; run `grace doctor --repair-local-state`."
                                }
                            | WorkingDirectoryUpdateCoordination.MarkerInspection.MalformedOrUnsupported ->
                                task {
                                    return Error "Working Directory Update retry preserved malformed marker evidence; run `grace doctor --repair-local-state`."
                                }
                            | WorkingDirectoryUpdateCoordination.MarkerInspection.Unreadable ->
                                task {
                                    return Error "Working Directory Update retry preserved unreadable marker evidence; run `grace doctor --repair-local-state`."
                                }

                        match retryCleanup with
                        | Error error -> return Contracts.FinalizationIncomplete(receipt, failure error)
                        | Ok () when cancellationToken.IsCancellationRequested ->
                            return
                                Contracts.FinalizationIncomplete(
                                    receipt,
                                    failure "Working Directory Update retry was cancelled before terminal recording; run `grace doctor --repair-local-state`."
                                )
                        | Ok () ->
                            try
                                do! LocalStateDb.finalizeWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target operation

                                try
                                    do! WorkingDirectoryUpdateCoordination.Sidecar.write scope operation
                                with
                                | _ -> ()

                                return Contracts.Updated(receipt)
                            with
                            | ex ->
                                return
                                    Contracts.FinalizationIncomplete(
                                        receipt,
                                        failure
                                            $"Working Directory Update could not record terminal completion: {ex.Message}; run `grace doctor --repair-local-state`."
                                    )
                | _ when
                    currentRevision
                    <> Contracts.AcceptedBranchPhase.localStatusRevision acceptedPhase
                    || statusFingerprint currentStatus
                       <> Contracts.AcceptedBranchPhase.statusFingerprint acceptedPhase
                    ->
                    return Contracts.Rejected(failure "Working Directory Update rejected the stale accepted Branch phase before mutation.")
                | _ when pending.IsSome -> return Contracts.Rejected(failure "A different Working Directory Update finalization is already pending.")
                | _ when
                    marker = WorkingDirectoryUpdateCoordination.MarkerInspection.DifferentOperation
                    || marker = WorkingDirectoryUpdateCoordination.MarkerInspection.MalformedOrUnsupported
                    || marker = WorkingDirectoryUpdateCoordination.MarkerInspection.Unreadable
                    ->
                    return Contracts.Rejected(failure "Working Directory Update preserved disallowed marker evidence; run `grace doctor --repair-local-state`.")
                | _ ->
                    let! adoptionCleanup =
                        if marker = WorkingDirectoryUpdateCoordination.MarkerInspection.ExactMatch then
                            WorkingDirectoryUpdateCoordination.Marker.tryRemoveExactOperation scope target operation
                        else
                            task { return WorkingDirectoryUpdateCoordination.MarkerCleanup.NoMarker }

                    match adoptionCleanup with
                    | WorkingDirectoryUpdateCoordination.MarkerCleanup.ExactCleanupFailed ->
                        return Contracts.Rejected(failure "Working Directory Update retained exact marker evidence; run `grace doctor --repair-local-state`.")
                    | WorkingDirectoryUpdateCoordination.MarkerCleanup.DifferentOperationEvidence
                    | WorkingDirectoryUpdateCoordination.MarkerCleanup.MalformedOrUnsupportedEvidence
                    | WorkingDirectoryUpdateCoordination.MarkerCleanup.UnreadableEvidence ->
                        return Contracts.Rejected(failure "Working Directory Update preserved marker evidence; run `grace doctor --repair-local-state`.")
                    | WorkingDirectoryUpdateCoordination.MarkerCleanup.ExactMatchCleaned
                    | WorkingDirectoryUpdateCoordination.MarkerCleanup.NoMarker ->
                        let attempt = Contracts.AttemptToken.create ()

                        let markerDocument =
                            WorkingDirectoryUpdateCoordination.Marker.create scope attempt target operation
                            |> Result.defaultWith invalidOp

                        do! WorkingDirectoryUpdateCoordination.Marker.write scope markerDocument
                        ownedAttempt <- Some attempt

                        let! initialScan = scanWorkingTreeForDifferencesReadOnly scanInput currentStatus

                        match initialScan with
                        | Error error -> return Contracts.Rejected(failure $"Working Directory Update could not verify the accepted working tree: {error}")
                        | Ok differences when
                            cancellationToken.IsCancellationRequested
                            || differences.Count > 0
                            ->
                            let reason =
                                if cancellationToken.IsCancellationRequested then
                                    "Working Directory Update was cancelled before mutation."
                                else
                                    "Working Directory Update rejected changed eligible working-tree content before mutation."

                            let! cleanup = cleanOwnedMarkerBeforeMutation scope attempt reason
                            return rejectedAfterCleanup reason cleanup
                        | Ok _ ->
                            let files = targetFiles preparedContent
                            let objectPaths = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            let mutable objectError = None

                            for path, sha256Hash, blake3Hash in files do
                                if objectError.IsNone then
                                    match! publishObject preparedContent configuration.ObjectDirectory path sha256Hash blake3Hash with
                                    | Ok objectPath -> objectPaths[string path] <- objectPath
                                    | Error error -> objectError <- Some error

                            match objectError, tryInspectCurrentDirectoryConfiguration () with
                            | Some error, _ ->
                                let! cleanup = cleanOwnedMarkerBeforeMutation scope attempt "Working Directory Update could not publish prepared objects"
                                return rejectedAfterCleanup $"Working Directory Update could not publish prepared objects: {error}" cleanup
                            | None, Error _ ->
                                let! cleanup =
                                    cleanOwnedMarkerBeforeMutation
                                        scope
                                        attempt
                                        "Working Directory Update could not reread disk configuration immediately before mutation"

                                return rejectedAfterCleanup "Working Directory Update could not reread disk configuration immediately before mutation." cleanup
                            | None, Ok inspection when not (isSameConfiguration configuration inspection.Configuration) ->
                                let! cleanup =
                                    cleanOwnedMarkerBeforeMutation
                                        scope
                                        attempt
                                        "Working Directory Update rejected changed disk configuration immediately before mutation"

                                return rejectedAfterCleanup "Working Directory Update rejected changed disk configuration immediately before mutation." cleanup
                            | None, Ok inspection ->
                                let freshConfiguration = inspection.Configuration
                                let freshScanInput = scanInputFor freshConfiguration
                                let! finalRevision = LocalStateDb.readLocalStatusRevision freshConfiguration.GraceStatusFile
                                let! finalStatus = LocalStateDb.readStatusSnapshot freshConfiguration.GraceStatusFile
                                let! finalCompletion = LocalStateDb.readWorkingDirectoryUpdateCompletion freshConfiguration.GraceStatusFile target operation
                                let! finalPending = LocalStateDb.readPendingWorkingDirectoryUpdateFinalization freshConfiguration.GraceStatusFile
                                let! finalMarker = WorkingDirectoryUpdateCoordination.Marker.inspectOwnedAttempt scope attempt target operation
                                let! finalScan = scanWorkingTreeForDifferencesReadOnly freshScanInput finalStatus

                                let finalGateIsFresh =
                                    finalRevision = Contracts.AcceptedBranchPhase.localStatusRevision acceptedPhase
                                    && statusFingerprint finalStatus = Contracts.AcceptedBranchPhase.statusFingerprint acceptedPhase
                                    && finalCompletion.IsNone
                                    && finalPending.IsNone
                                    && finalMarker = WorkingDirectoryUpdateCoordination.MarkerInspection.ExactMatch
                                    && (match finalScan with
                                        | Ok finalDifferences -> finalDifferences.Count = 0
                                        | Error _ -> false)

                                match buildTopologyPlan freshConfiguration.RootDirectory finalStatus targetStatus files with
                                | _ when
                                    not finalGateIsFresh
                                    || cancellationToken.IsCancellationRequested
                                    ->
                                    let reason =
                                        if cancellationToken.IsCancellationRequested then
                                            "Working Directory Update was cancelled before mutation."
                                        else
                                            "Working Directory Update rejected stale final gate evidence immediately before mutation."

                                    let! cleanup = cleanOwnedMarkerBeforeMutation scope attempt reason
                                    return rejectedAfterCleanup reason cleanup
                                | Error error ->
                                    let! cleanup = cleanOwnedMarkerBeforeMutation scope attempt "Working Directory Update rejected the immutable topology plan"
                                    return rejectedAfterCleanup $"Working Directory Update rejected the immutable topology plan: {error}" cleanup
                                | Ok topologyPlan ->
                                    try
                                        do! applyTopologyPlan freshConfiguration.RootDirectory objectPaths topologyPlan (fun () -> mutationStarted <- true)

                                        match! verifySelectedRoot freshConfiguration.RootDirectory freshScanInput target targetStatus with
                                        | Error error ->
                                            return
                                                Contracts.UpdateIncomplete(
                                                    failure $"Working Directory Update changed files but final verification failed: {error}"
                                                )
                                        | Ok () ->
                                            let! _ =
                                                LocalStateDb.commitWorkingDirectoryUpdateCompletion
                                                    freshConfiguration.GraceStatusFile
                                                    targetStatus
                                                    targetStatus.Index.Values
                                                    (LocalStateDb.WorkingDirectoryUpdateCompletionDetails.BranchDirectoryVersionFinalization
                                                        freshConfiguration.BranchId)
                                                    target
                                                    operation

                                            let completedReceipt = receipt target operation mutationStarted

                                            let! cleanup =
                                                cleanOwnedMarkerBeforeMutation
                                                    scope
                                                    attempt
                                                    "Working Directory Update completed local SQLite state but could not clean marker evidence"

                                            match cleanup with
                                            | Error error -> return Contracts.FinalizationIncomplete(completedReceipt, failure error)
                                            | Ok () ->
                                                try
                                                    do!
                                                        LocalStateDb.finalizeWorkingDirectoryUpdateCompletion
                                                            freshConfiguration.GraceStatusFile
                                                            target
                                                            operation

                                                    try
                                                        do! WorkingDirectoryUpdateCoordination.Sidecar.write scope operation
                                                    with
                                                    | _ -> ()

                                                    return Contracts.Updated(completedReceipt)
                                                with
                                                | ex ->
                                                    return
                                                        Contracts.FinalizationIncomplete(
                                                            completedReceipt,
                                                            failure
                                                                $"Working Directory Update could not record terminal completion: {ex.Message}; run `grace doctor --repair-local-state`."
                                                        )
                                    with
                                    | ex when mutationStarted -> return Contracts.UpdateIncomplete(failure ex.Message)
                                    | ex ->
                                        let! cleanup = cleanOwnedMarkerBeforeMutation scope attempt "Working Directory Update failed before mutation"
                                        return rejectedAfterCleanup ex.Message cleanup
            with
            | :? OperationCanceledException -> return Contracts.Rejected(failure "Working Directory Update was cancelled before mutation.")
            | ex when mutationStarted -> return Contracts.UpdateIncomplete(failure ex.Message)
            | ex ->
                match ownedAttempt with
                | Some attempt ->
                    let! cleanup = cleanOwnedMarkerBeforeMutation scope attempt "Working Directory Update failed before mutation"

                    return rejectedAfterCleanup ex.Message cleanup
                | None -> return Contracts.Rejected(failure ex.Message)
        }
