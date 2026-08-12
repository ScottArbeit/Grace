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

            let scanInput: WorkingTreeScanInput =
                {
                    RootDirectory = configuration.RootDirectory
                    GraceDirectory = configuration.GraceDirectory
                    GraceStatusFile = configuration.GraceStatusFile
                    DirectoryIgnoreEntries = Array.copy configuration.GraceDirectoryIgnoreEntries
                    FileIgnoreEntries = Array.copy configuration.GraceFileIgnoreEntries
                }

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
                    let attempt = Contracts.AttemptToken.create ()

                    let markerDocument =
                        WorkingDirectoryUpdateCoordination.Marker.create scope attempt target operation
                        |> Result.defaultWith invalidOp

                    do! WorkingDirectoryUpdateCoordination.Marker.write scope markerDocument
                    ownedAttempt <- Some attempt

                    if cancellationToken.IsCancellationRequested then
                        let! cleanup = WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope attempt

                        return
                            match cleanup with
                            | WorkingDirectoryUpdateCoordination.MarkerCleanup.ExactMatchCleaned
                            | WorkingDirectoryUpdateCoordination.MarkerCleanup.NoMarker ->
                                Contracts.Rejected(failure "Working Directory Update was cancelled before mutation.")
                            | _ ->
                                Contracts.Rejected(
                                    failure
                                        "Working Directory Update was cancelled before mutation and retained marker evidence; run `grace doctor --repair-local-state`."
                                )
                    else
                        match! scanWorkingTreeForDifferencesReadOnly scanInput currentStatus with
                        | Error error -> return Contracts.Rejected(failure $"Working Directory Update could not verify the accepted working tree: {error}")
                        | Ok differences when differences.Count > 0 ->
                            let! _ = WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope attempt
                            return Contracts.Rejected(failure "Working Directory Update rejected changed eligible working-tree content before mutation.")
                        | Ok _ ->
                            let files = targetFiles preparedContent
                            let objectPaths = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            let mutable objectError = None

                            for path, sha256Hash, blake3Hash in files do
                                if objectError.IsNone then
                                    match! publishObject preparedContent configuration.ObjectDirectory path sha256Hash blake3Hash with
                                    | Ok objectPath -> objectPaths[string path] <- objectPath
                                    | Error error -> objectError <- Some error

                            match objectError with
                            | Some error ->
                                let! _ = WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope attempt
                                return Contracts.Rejected(failure $"Working Directory Update could not publish prepared objects: {error}")
                            | None when cancellationToken.IsCancellationRequested ->
                                let! _ = WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope attempt
                                return Contracts.Rejected(failure "Working Directory Update was cancelled before mutation.")
                            | None ->
                                let! finalRevision = LocalStateDb.readLocalStatusRevision configuration.GraceStatusFile
                                let! finalStatus = LocalStateDb.readStatusSnapshot configuration.GraceStatusFile

                                if finalRevision
                                   <> Contracts.AcceptedBranchPhase.localStatusRevision acceptedPhase
                                   || statusFingerprint finalStatus
                                      <> Contracts.AcceptedBranchPhase.statusFingerprint acceptedPhase then
                                    let! _ = WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope attempt
                                    return Contracts.Rejected(failure "Working Directory Update rejected stale local status immediately before mutation.")
                                else
                                    try
                                        let targetDirectories =
                                            targetStatus.Index.Values
                                            |> Seq.map (fun directory -> directory.RelativePath)
                                            |> Seq.sortBy (fun path -> (string path).Length)
                                            |> Seq.toArray

                                        for directoryPath in targetDirectories do
                                            let fullPath =
                                                if directoryPath = RootDirectoryPath then
                                                    configuration.RootDirectory
                                                else
                                                    pathUnderRoot configuration.RootDirectory directoryPath

                                            if File.Exists(fullPath) then File.Delete(fullPath)

                                            Directory.CreateDirectory(fullPath) |> ignore

                                        let targetPaths = HashSet<string>(files |> Seq.map (fun (path, _, _) -> string path), StringComparer.OrdinalIgnoreCase)

                                        let staleFiles =
                                            currentStatus.Index.Values
                                            |> Seq.collect (fun directory -> directory.Files)
                                            |> Seq.filter (fun file -> not (targetPaths.Contains(string file.RelativePath)))
                                            |> Seq.sortByDescending (fun file -> string file.RelativePath)
                                            |> Seq.toArray

                                        for file in staleFiles do
                                            let fullPath = pathUnderRoot configuration.RootDirectory file.RelativePath

                                            if File.Exists(fullPath) then
                                                mutationStarted <- true
                                                File.Delete(fullPath)

                                        for path, sha256Hash, blake3Hash in files do
                                            let destination = pathUnderRoot configuration.RootDirectory path
                                            let destinationDirectory = Path.GetDirectoryName(destination)

                                            Directory.CreateDirectory(destinationDirectory)
                                            |> ignore

                                            match! verifyFile destination sha256Hash blake3Hash with
                                            | Ok () -> ()
                                            | Error _ ->
                                                mutationStarted <- true
                                                File.Copy(objectPaths[string path], destination, true)
                                                let! copied = verifyFile destination sha256Hash blake3Hash

                                                match copied with
                                                | Ok () -> ()
                                                | Error error -> invalidOp error

                                        let targetDirectoryPaths = HashSet<string>(targetDirectories |> Seq.map string, StringComparer.OrdinalIgnoreCase)

                                        currentStatus.Index.Values
                                        |> Seq.map (fun directory -> directory.RelativePath)
                                        |> Seq.filter (fun path ->
                                            path <> RootDirectoryPath
                                            && not (targetDirectoryPaths.Contains(string path)))
                                        |> Seq.sortByDescending (fun path -> (string path).Length)
                                        |> Seq.iter (fun path ->
                                            let fullPath = pathUnderRoot configuration.RootDirectory path

                                            if
                                                Directory.Exists(fullPath)
                                                && (Directory.EnumerateFileSystemEntries(fullPath)
                                                    |> Seq.isEmpty)
                                            then
                                                mutationStarted <- true
                                                Directory.Delete(fullPath))

                                        match! verifySelectedRoot configuration.RootDirectory scanInput target targetStatus with
                                        | Error error ->
                                            return
                                                Contracts.UpdateIncomplete(
                                                    failure $"Working Directory Update changed files but final verification failed: {error}"
                                                )
                                        | Ok () ->
                                            let! _ =
                                                LocalStateDb.commitWorkingDirectoryUpdateCompletion
                                                    configuration.GraceStatusFile
                                                    targetStatus
                                                    targetStatus.Index.Values
                                                    (LocalStateDb.WorkingDirectoryUpdateCompletionDetails.BranchDirectoryVersionFinalization
                                                        configuration.BranchId)
                                                    target
                                                    operation

                                            let! cleanup = WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope attempt

                                            match cleanup with
                                            | WorkingDirectoryUpdateCoordination.MarkerCleanup.ExactMatchCleaned
                                            | WorkingDirectoryUpdateCoordination.MarkerCleanup.NoMarker ->
                                                do! LocalStateDb.finalizeWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target operation
                                                do! WorkingDirectoryUpdateCoordination.Sidecar.write scope operation
                                                return Contracts.Updated(receipt target operation mutationStarted)
                                            | _ ->
                                                return
                                                    Contracts.FinalizationIncomplete(
                                                        receipt target operation mutationStarted,
                                                        failure "Working Directory Update retained marker evidence; run `grace doctor --repair-local-state`."
                                                    )
                                    with
                                    | ex when mutationStarted -> return Contracts.UpdateIncomplete(failure ex.Message)
                                    | ex ->
                                        let! _ = WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope attempt
                                        return Contracts.Rejected(failure ex.Message)
            with
            | :? OperationCanceledException -> return Contracts.Rejected(failure "Working Directory Update was cancelled before mutation.")
            | ex when mutationStarted -> return Contracts.UpdateIncomplete(failure ex.Message)
            | ex -> return Contracts.Rejected(failure ex.Message)
        }
