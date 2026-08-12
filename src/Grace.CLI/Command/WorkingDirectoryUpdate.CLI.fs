namespace Grace.CLI.Command

open Grace.CLI
open Grace.Shared.Services
open Grace.Shared.Utilities
open Grace.Types.Common
open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks

/// Owns the verified local working-directory update transaction after its private contracts and storage seams exist.
module internal WorkingDirectoryUpdate =
    module Contracts = WorkingDirectoryUpdateContracts
    open Contracts

    /// Names bounded, internal interruption points used only by the integration fixture.
    type internal FailurePoint =
        | BeforeObjectPublication
        | AfterWorkingMutation
        | BeforeLocalCompletion
        | AfterLocalCompletionBeforeSidecar
        | BeforeFinalization
        | BeforeTerminalCompletion

    /// Captures the caller-approved configuration and repository scan inputs without exposing mutable configuration objects.
    type internal AcceptedConfiguration = private AcceptedConfiguration of canonical: string * scanInput: Services.WorkingTreeScanInput

    /// Binds the selected root, stable local-root scope, and exact SQLite revision that must remain current after leasing.
    type internal AcceptedSelection =
        private | AcceptedSelection of
            target: Contracts.Target *
            localRootScope: Contracts.LocalRootScope *
            configuration: AcceptedConfiguration *
            localStatusRevision: int64

    /// Carries defensively copied local paths and complete target status facts required by the one atomic completion write.
    type internal ApplicationFacts =
        private | ApplicationFacts of
            localRoot: string *
            objectRoot: string *
            localStateDbPath: string *
            priorStatus: GraceStatus *
            targetStatus: GraceStatus *
            objectMetadata: LocalDirectoryVersion array

    /// Clones one directory and its mutable child collections so an accepted status cannot change through caller aliases.
    let private copyDirectory (directory: LocalDirectoryVersion) =
        let copy = LocalDirectoryVersion()
        copy.Class <- directory.Class
        copy.DirectoryVersionId <- directory.DirectoryVersionId
        copy.OwnerId <- directory.OwnerId
        copy.OrganizationId <- directory.OrganizationId
        copy.RepositoryId <- directory.RepositoryId
        copy.RelativePath <- directory.RelativePath
        copy.Sha256Hash <- directory.Sha256Hash
        copy.Blake3Hash <- directory.Blake3Hash
        copy.Directories <- List<DirectoryVersionId>(directory.Directories)
        copy.Files <- List<LocalFileVersion>(directory.Files)
        copy.Size <- directory.Size
        copy.CreatedAt <- directory.CreatedAt
        copy.LastWriteTimeUtc <- directory.LastWriteTimeUtc
        copy

    /// Copies a status graph into a private index before request construction can expose it to caller mutation.
    let private freezeStatus (status: GraceStatus) =
        if isNull (box status) || isNull status.Index then
            Error "Working Directory Update requires a complete target status graph."
        else
            try
                let index = GraceIndex()

                let directories =
                    status.Index.Values
                    |> Seq.map copyDirectory
                    |> Seq.toArray

                let mutable duplicate = false
                let mutable directoryIndex = 0

                while directoryIndex < directories.Length do
                    let directory = directories[directoryIndex]

                    if not (index.TryAdd(directory.DirectoryVersionId, directory)) then
                        duplicate <- true

                    directoryIndex <- directoryIndex + 1

                if duplicate then
                    Error "Working Directory Update target status graph contains duplicate directory identities."
                else
                    Ok({ status with Index = index })
            with
            | ex -> Error $"Working Directory Update could not freeze the target status graph: {ex.Message}"

    /// Creates immutable application facts before leasing and rejects incomplete or cross-inconsistent root facts.
    module ApplicationFacts =
        /// Creates facts only when all paths are absolute and exactly one target root metadata record agrees with status.
        let create
            (target: Contracts.Target)
            localRoot
            objectRoot
            localStateDbPath
            (priorStatus: GraceStatus)
            (targetStatus: GraceStatus)
            (objectMetadata: LocalDirectoryVersion array)
            =
            let requiredPath name path =
                if
                    String.IsNullOrWhiteSpace(path)
                    || not (Path.IsPathFullyQualified(path))
                then
                    Error $"Working Directory Update requires an absolute {name}."
                else
                    Ok(Path.GetFullPath(path))

            match requiredPath "local root" localRoot,
                  requiredPath "object root" objectRoot,
                  requiredPath "local-state database path" localStateDbPath,
                  freezeStatus priorStatus,
                  freezeStatus targetStatus
                with
            | Ok normalizedLocalRoot, Ok normalizedObjectRoot, Ok normalizedDatabasePath, Ok frozenPriorStatus, Ok frozenTargetStatus when
                not (isNull (box objectMetadata))
                ->
                let copiedMetadata = objectMetadata |> Array.map copyDirectory

                let matchesTargetStatus status =
                    status.RootDirectoryId = Contracts.Target.rootDirectoryVersionId target
                    && status.RootDirectorySha256Hash = Contracts.Target.sha256Hash target
                    && status.RootDirectoryBlake3Hash = Contracts.Target.blake3Hash target

                let matchingRoots =
                    copiedMetadata
                    |> Array.filter (fun (directory: LocalDirectoryVersion) ->
                        directory.DirectoryVersionId = Contracts.Target.rootDirectoryVersionId target
                        && directory.RelativePath = Grace.Shared.Constants.RootDirectoryPath
                        && directory.Sha256Hash = Contracts.Target.sha256Hash target
                        && directory.Blake3Hash = Contracts.Target.blake3Hash target
                        && directory.RepositoryId = Contracts.Target.repositoryId target)

                let metadataMatchesTargetGraph =
                    copiedMetadata.Length = frozenTargetStatus.Index.Count
                    && copiedMetadata
                       |> Array.forall (fun directory ->
                           let mutable expected = LocalDirectoryVersion.Default

                           frozenTargetStatus.Index.TryGetValue(directory.DirectoryVersionId, &expected)
                           && expected.Equals(directory))

                if not (matchesTargetStatus frozenTargetStatus) then
                    Error "Working Directory Update target status does not match the selected target."
                elif LocalStateDb.validateCompleteStatusTree frozenTargetStatus
                     |> Result.isError then
                    Error "Working Directory Update target status must be one complete canonical root graph."
                elif matchingRoots.Length <> 1 then
                    Error "Working Directory Update requires exactly one target root metadata record."
                elif not metadataMatchesTargetGraph then
                    Error "Working Directory Update object metadata must exactly match the frozen target status graph."
                else
                    Ok(
                        ApplicationFacts(
                            normalizedLocalRoot,
                            normalizedObjectRoot,
                            normalizedDatabasePath,
                            frozenPriorStatus,
                            frozenTargetStatus,
                            copiedMetadata
                        )
                    )
            | Error error, _, _, _, _ -> Error error
            | _, Error error, _, _, _ -> Error error
            | _, _, Error error, _, _ -> Error error
            | _, _, _, Error error, _ -> Error error
            | _, _, _, _, Error error -> Error error
            | _ -> Error "Working Directory Update requires complete object metadata."

    /// Supplies construction and comparison functions for the immutable selected-state snapshot.
    module AcceptedConfiguration =
        /// Copies configuration and scan arrays so later caller mutation cannot change a leased plan.
        let create canonical (scanInput: Services.WorkingTreeScanInput) =
            if
                String.IsNullOrWhiteSpace(canonical)
                || isNull (box scanInput)
            then
                Error "Working Directory Update requires a canonical configuration snapshot."
            elif not (Path.IsPathFullyQualified(scanInput.RootDirectory)) then
                Error "Working Directory Update scan root must be absolute."
            else
                Ok(
                    AcceptedConfiguration(
                        canonical,
                        { scanInput with
                            RootDirectory = Path.GetFullPath(scanInput.RootDirectory)
                            GraceDirectory = Path.GetFullPath(scanInput.GraceDirectory)
                            GraceStatusFile = Path.GetFullPath(scanInput.GraceStatusFile)
                            DirectoryIgnoreEntries = Array.copy scanInput.DirectoryIgnoreEntries
                            FileIgnoreEntries = Array.copy scanInput.FileIgnoreEntries
                        }
                    )
                )

    /// Supplies construction functions for accepted state facts.
    module AcceptedSelection =
        /// Captures exactly the target, local-root scope, configuration, and status revision accepted by a caller.
        let create target localRootScope (configuration as AcceptedConfiguration (_, scanInput)) localStatusRevision =
            if
                isNull (box target) || isNull (box localRootScope)
                || isNull (box configuration)
            then
                Error "Working Directory Update requires complete selected target, root scope, and configuration facts."
            elif localStatusRevision < 0L then
                Error "Working Directory Update local status revision must be non-negative."
            else
                match Contracts.LocalRootScope.create scanInput.RootDirectory with
                | Ok expectedScope when expectedScope = localRootScope -> Ok(AcceptedSelection(target, localRootScope, configuration, localStatusRevision))
                | _ -> Error "Working Directory Update selected root scope does not match the frozen scan root."

        /// Returns the frozen local revision for focused stale-selection proof without exposing mutable caller state.
        let internal localStatusRevision (AcceptedSelection (_, _, _, localStatusRevision)) = localStatusRevision

    /// Rejects a request whose selected scope or frozen scan root could target a different local transaction location.
    let private selectionMatchesApplicationFacts
        (AcceptedSelection (_, selectedScope, AcceptedConfiguration (_, scanInput), _))
        (ApplicationFacts (localRoot, _, _, _, _, _))
        =
        match Contracts.LocalRootScope.create localRoot with
        | Ok localRootScope ->
            localRootScope = selectedScope
            && String.Equals(Path.GetFullPath(scanInput.RootDirectory), localRoot, StringComparison.OrdinalIgnoreCase)
        | Error _ -> false

    /// Validates the branch tuple by regenerating its deterministic operation identity from every repeated caller fact.
    let private matchesBranchOperation target operation previousBranchId selectedReferenceId =
        match Contracts.Operation.branchSwitch previousBranchId selectedReferenceId target with
        | Ok expected -> Contracts.Operation.value expected = Contracts.Operation.value operation
        | Error _ -> false

    /// Validates the Watch tuple by regenerating its deterministic operation identity from its exact replay cursor.
    let private matchesWatchOperation target operation eventCursor =
        match Contracts.Operation.watchReplay (Contracts.Target.repositoryId target) (Contracts.Target.branchId target) eventCursor with
        | Ok expected -> Contracts.Operation.value expected = Contracts.Operation.value operation
        | Error _ -> false

    /// Validates the Connect tuple by regenerating its deterministic operation identity from the selected scope and cursor.
    let private matchesConnectOperation target operation localRootScope initialCursor =
        match Contracts.Operation.connectBootstrap target initialCursor localRootScope with
        | Ok expected -> Contracts.Operation.value expected = Contracts.Operation.value operation
        | Error _ -> false

    /// Verifies that frozen prepared content declares exactly the target status graph before the lease can be acquired.
    let private preparedContentMatchesTargetStatus (ApplicationFacts (_, _, _, _, targetStatus, _)) preparedContent =
        let expectedDirectories = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let expectedFiles = Dictionary<string, Sha256Hash * Blake3Hash>(StringComparer.OrdinalIgnoreCase)
        let mutable valid = true

        targetStatus.Index.Values
        |> Seq.iter (fun directory ->
            if directory.RelativePath
               <> Grace.Shared.Constants.RootDirectoryPath then
                valid <-
                    valid
                    && expectedDirectories.Add(string directory.RelativePath)

            directory.Files
            |> Seq.iter (fun file ->
                valid <-
                    valid
                    && not (expectedFiles.ContainsKey(string file.RelativePath))

                expectedFiles[string file.RelativePath] <- (file.Sha256Hash, file.Blake3Hash)))

        let manifestDirectories = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let manifestFiles = Dictionary<string, Sha256Hash * Blake3Hash>(StringComparer.OrdinalIgnoreCase)

        Contracts.PreparedManifest.entries (Contracts.PreparedContent.manifest preparedContent)
        |> Seq.iter (function
            | Contracts.PreparedManifestEntry.Directory path -> valid <- valid && manifestDirectories.Add(string path)
            | Contracts.PreparedManifestEntry.File (path, sha256Hash, blake3Hash) ->
                valid <-
                    valid
                    && not (manifestFiles.ContainsKey(string path))

                manifestFiles[string path] <- (sha256Hash, blake3Hash))

        valid
        && manifestDirectories.SetEquals(expectedDirectories)
        && manifestFiles.Count = expectedFiles.Count
        && (expectedFiles
            |> Seq.forall (fun entry ->
                match manifestFiles.TryGetValue(entry.Key) with
                | true, hashes -> hashes = entry.Value
                | false, _ -> false))

    /// Returns only current immutable selection facts after the engine owns the stable repository/local-root lease.
    type internal ISelectedStateReader =
        /// Rereads caller-selected target, normalized root scope, and canonical configuration without planning or mutation access.
        abstract member ReadAsync: CancellationToken -> Task<AcceptedSelection>

    /// Carries the only caller-progress actions that may run after verified local completion.
    type internal FinalizationFacts =
        | BranchFinalization of previousBranchId: BranchId * selectedReferenceId: ReferenceId
        | WatchFinalization of eventCursor: string
        | ConnectFinalization

    /// Observes coarse engine stages without participating in update ordering or failure classification.
    type internal ProgressObserver = Progress -> unit

    /// Performs the one idempotent caller acknowledgement after the local update is durably pending.
    type internal IIdempotentFinalizer =
        /// Acknowledges only the typed Branch or Watch fact; implementations receive no working-file or SQLite access.
        abstract member FinalizeAsync: FinalizationFacts * CancellationToken -> Task

    /// Holds the private accepted request tuple while preserving caller-specific constructors and disallowing plans or writer callbacks.
    type internal Request =
        private | Request of
            selection: AcceptedSelection *
            applicationFacts: ApplicationFacts *
            operation: Contracts.Operation *
            preparedContent: Contracts.PreparedContent *
            correlationId: CorrelationId *
            selectedStateReader: ISelectedStateReader *
            completionDetails: LocalStateDb.WorkingDirectoryUpdateCompletionDetails *
            finalization: FinalizationFacts *
            finalizer: IIdempotentFinalizer option *
            progress: ProgressObserver option

    /// Holds only durable recovery facts; deliberately excludes prepared bytes, roots, and file operations.
    type internal FinalizationRequest =
        private | FinalizationRequest of
            selection: AcceptedSelection *
            operation: Contracts.Operation *
            localStateDbPath: string *
            correlationId: CorrelationId *
            selectedStateReader: ISelectedStateReader *
            finalization: FinalizationFacts *
            finalizer: IIdempotentFinalizer *
            progress: ProgressObserver option

    /// Creates caller-specific normalized requests with no generic transaction hooks.
    module Request =
        /// Returns the private prepared-snapshot lifetime count for focused engine proof without exposing its bytes or ownership.
        let internal preparedContentDisposalCountForTests (Request (_, _, _, preparedContent, _, _, _, _, _, _)) =
            Contracts.PreparedContent.disposalCount preparedContent

        /// Returns the constructed logical operation identity for caller-bound correlation proof without exposing request internals.
        let internal operationValueForTests (Request (_, _, operation, _, _, _, _, _, _, _)) = Contracts.Operation.value operation

        /// Captures a Branch-selected target and its idempotent post-completion branch facts.
        let branchSwitch selection facts operation preparedContent correlationId reader previousBranchId selectedReferenceId finalizer progress =
            if
                isNull (box preparedContent)
                || isNull (box reader)
                || isNull (box finalizer)
            then
                Error "Working Directory Update Branch request requires prepared content, selected state, and a finalizer."
            elif previousBranchId = Guid.Empty
                 || selectedReferenceId = Guid.Empty then
                Error "Working Directory Update Branch request requires non-empty branch and Reference identities."
            else
                let (AcceptedSelection (target, _, _, _)) = selection

                if
                    not (selectionMatchesApplicationFacts selection facts)
                    || not (matchesBranchOperation target operation previousBranchId selectedReferenceId)
                    || not (preparedContentMatchesTargetStatus facts preparedContent)
                then
                    Error "Working Directory Update Branch request facts do not bind one accepted operation, complete target graph, and local scope."
                else
                    Ok(
                        Request(
                            selection,
                            facts,
                            operation,
                            preparedContent,
                            correlationId,
                            reader,
                            LocalStateDb.BranchFinalization(previousBranchId, selectedReferenceId),
                            BranchFinalization(previousBranchId, selectedReferenceId),
                            Some finalizer,
                            progress
                        )
                    )

        /// Captures a Watch replay target and its exact cursor acknowledgement fact.
        let watchReplay selection facts operation preparedContent correlationId reader eventCursor finalizer progress =
            if
                isNull (box preparedContent)
                || isNull (box reader)
                || isNull (box finalizer)
                || String.IsNullOrWhiteSpace(eventCursor)
            then
                Error "Working Directory Update Watch request requires prepared content, selected state, a cursor, and a finalizer."
            else
                let (AcceptedSelection (target, _, _, _)) = selection

                if
                    not (selectionMatchesApplicationFacts selection facts)
                    || not (matchesWatchOperation target operation eventCursor)
                    || not (preparedContentMatchesTargetStatus facts preparedContent)
                then
                    Error "Working Directory Update Watch request facts do not bind one accepted operation, complete target graph, and local scope."
                else
                    Ok(
                        Request(
                            selection,
                            facts,
                            operation,
                            preparedContent,
                            correlationId,
                            reader,
                            LocalStateDb.WatchFinalization eventCursor,
                            WatchFinalization eventCursor,
                            Some finalizer,
                            progress
                        )
                    )

        /// Captures a Connect target, which has no separate finalizer once local completion commits.
        let connectBootstrap
            (selection as AcceptedSelection (_, localRootScope, _, _))
            facts
            operation
            preparedContent
            correlationId
            reader
            initialCursor
            progress
            =
            if
                isNull (box preparedContent)
                || isNull (box reader)
                || String.IsNullOrWhiteSpace(initialCursor)
            then
                Error "Working Directory Update Connect request requires prepared content, selected state, and an initial cursor."
            else
                let (AcceptedSelection (target, _, _, _)) = selection

                if
                    not (selectionMatchesApplicationFacts selection facts)
                    || not (matchesConnectOperation target operation localRootScope initialCursor)
                    || not (preparedContentMatchesTargetStatus facts preparedContent)
                then
                    Error "Working Directory Update Connect request facts do not bind one accepted operation, complete target graph, and local scope."
                else
                    Ok(
                        Request(
                            selection,
                            facts,
                            operation,
                            preparedContent,
                            correlationId,
                            reader,
                            LocalStateDb.ConnectCompletion(initialCursor, localRootScope),
                            ConnectFinalization,
                            None,
                            progress
                        )
                    )

    /// Creates recovery-only requests after the caller has retained exact Branch or Watch finalization facts.
    module FinalizationRequest =
        /// Captures a Branch retry without allowing the retry path to access prepared or working-file state.
        let branchSwitch selection operation localStateDbPath correlationId reader previousBranchId selectedReferenceId finalizer progress =
            if isNull (box reader)
               || isNull (box finalizer)
               || String.IsNullOrWhiteSpace(localStateDbPath)
               || previousBranchId = Guid.Empty
               || selectedReferenceId = Guid.Empty then
                Error "Working Directory Update Branch finalization requires selected state, database path, and finalizer."
            else
                let (AcceptedSelection (target, _, _, _)) = selection

                if not (matchesBranchOperation target operation previousBranchId selectedReferenceId) then
                    Error "Working Directory Update Branch finalization facts do not bind the recorded operation."
                else
                    Ok(
                        FinalizationRequest(
                            selection,
                            operation,
                            Path.GetFullPath(localStateDbPath),
                            correlationId,
                            reader,
                            BranchFinalization(previousBranchId, selectedReferenceId),
                            finalizer,
                            progress
                        )
                    )

        /// Captures a Watch retry without allowing the retry path to access prepared or working-file state.
        let watchReplay selection operation localStateDbPath correlationId reader eventCursor finalizer progress =
            if
                isNull (box reader)
                || isNull (box finalizer)
                || String.IsNullOrWhiteSpace(localStateDbPath)
                || String.IsNullOrWhiteSpace(eventCursor)
            then
                Error "Working Directory Update Watch finalization requires selected state, database path, cursor, and finalizer."
            else
                let (AcceptedSelection (target, _, _, _)) = selection

                if not (matchesWatchOperation target operation eventCursor) then
                    Error "Working Directory Update Watch finalization facts do not bind the recorded operation."
                else
                    Ok(
                        FinalizationRequest(
                            selection,
                            operation,
                            Path.GetFullPath(localStateDbPath),
                            correlationId,
                            reader,
                            WatchFinalization eventCursor,
                            finalizer,
                            progress
                        )
                    )

    /// Holds the process-local finite seam selected by an integration test, never by a caller request.
    let mutable private failurePointForTests = None

    /// Selects an internal finite seam for a serialized integration test.
    let internal setFailurePointForTests failurePoint = failurePointForTests <- failurePoint

    /// Reports progress best-effort so observers cannot change transaction ordering or outcomes.
    let private report progress stage =
        match progress with
        | Some observer ->
            try
                observer stage
            with
            | _ -> ()
        | None -> ()

    /// Produces a classified failure without leaking mutable engine state to callers.
    let private failure reason =
        Contracts.Failure.create reason
        |> function
            | Ok value -> value
            | Error error -> invalidOp error

    /// Produces a receipt only after the operation and target are known to match.
    let private receipt target operation bytesChanged =
        Contracts.Receipt.create target operation bytesChanged
        |> function
            | Ok value -> value
            | Error error -> invalidOp error

    /// Throws at exactly one finite integration-test seam without providing a generic failure callback.
    let private throwAt expected =
        match failurePointForTests with
        | Some actual when actual = expected -> invalidOp $"Injected Working Directory Update failure at {expected}."
        | _ -> ()

    /// Resolves a relative path below a normalized root and rejects traversal before any file operation.
    let private pathUnderRoot root (relativePath: RelativePath) =
        let fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
        let candidate = Path.GetFullPath(Path.Combine(fullRoot, string relativePath))
        let prefix = fullRoot + string Path.DirectorySeparatorChar

        if candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then
            candidate
        else
            invalidOp "Working Directory Update rejected a path outside its local root."

    /// Computes required content hashes from a real file immediately before crossing an object or working-file boundary.
    let private computeHashes path =
        task {
            use stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)
            return! computeHashesForFile stream (RelativePath(Path.GetFileName(path)))
        }

    /// Requires a real file to retain its exact prepared SHA-256 and BLAKE3 bytes.
    let private verifyFile path sha256Hash blake3Hash =
        task {
            if not (File.Exists(path)) then
                return Error "Required prepared file is missing."
            else
                let! actualSha256Hash, actualBlake3Hash = computeHashes path

                if actualSha256Hash <> sha256Hash then
                    return Error "File bytes do not match the prepared SHA-256 hash."
                elif actualBlake3Hash <> blake3Hash then
                    return Error "File bytes do not match the prepared BLAKE3 hash."
                else
                    return Ok()
        }

    /// Avoids a working-file rewrite when its actual bytes already equal the verified object declaration.
    let private requiresWorkingCopy path sha256Hash blake3Hash =
        task {
            match! verifyFile path sha256Hash blake3Hash with
            | Ok () -> return false
            | Error _ -> return true
        }

    /// Publishes one verified object atomically before any working-file mutation and re-verifies the final object path.
    let private publishObject preparedContent objectRoot relativePath sha256Hash blake3Hash =
        task {
            let objectFileName = Services.getLocalObjectCacheFileName relativePath sha256Hash blake3Hash
            let objectPath = pathUnderRoot objectRoot (RelativePath(Path.Combine(string relativePath, objectFileName)))
            let objectDirectory = Path.GetDirectoryName(objectPath)

            Directory.CreateDirectory(objectDirectory)
            |> ignore

            let temporaryPath = Path.Combine(objectDirectory, $".{objectFileName}.{Guid.NewGuid():N}.tmp")

            try
                match! verifyFile objectPath sha256Hash blake3Hash with
                | Ok () -> return Ok()
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
                            return! verifyFile objectPath sha256Hash blake3Hash
            finally
                if File.Exists(temporaryPath) then File.Delete(temporaryPath)
        }

    /// Builds a target-only plan only after supported ignore-aware scanning has verified the accepted baseline.
    let private buildPlan (scanInput: Services.WorkingTreeScanInput) localRoot (baselineStatus: GraceStatus) preparedContent =
        task {
            match! Services.scanWorkingTreeForDifferencesReadOnly scanInput baselineStatus with
            | Error error -> return Error error
            | Ok differences when differences.Count > 0 ->
                let changedPaths =
                    differences
                    |> Seq.map (fun difference -> string difference.RelativePath)
                    |> Seq.distinct
                    |> String.concat ", "

                return Error $"Working Directory Update rejected relevant working-tree content that changed after selection: {changedPaths}."
            | Ok _ ->
                let targetFiles =
                    Contracts.PreparedManifest.entries (Contracts.PreparedContent.manifest preparedContent)
                    |> Seq.choose (function
                        | Contracts.PreparedManifestEntry.File (path, sha256Hash, blake3Hash) -> Some(path, sha256Hash, blake3Hash)
                        | Contracts.PreparedManifestEntry.Directory _ -> None)
                    |> Seq.toArray

                let targetPaths =
                    HashSet<string>(
                        targetFiles
                        |> Seq.map (fun (path, _, _) -> string path),
                        StringComparer.OrdinalIgnoreCase
                    )

                let deletions =
                    baselineStatus.Index.Values
                    |> Seq.collect (fun directory -> directory.Files)
                    |> Seq.map (fun file -> file.RelativePath)
                    |> Seq.filter (fun path -> not (targetPaths.Contains(string path)))
                    |> Seq.toArray

                return Ok(targetFiles, deletions)
        }

    /// Resolves a status-directory path under the working root, including the canonical root-directory entry.
    let private directoryPathUnderRoot localRoot (relativePath: RelativePath) =
        if relativePath = Grace.Shared.Constants.RootDirectoryPath then
            Path.GetFullPath(localRoot)
        else
            pathUnderRoot localRoot relativePath

    /// Creates every directory declared by the frozen target graph before final-root verification.
    let private materializeTargetDirectories localRoot (statusToCommit: GraceStatus) =
        task {
            let directories =
                statusToCommit.Index.Values
                |> Seq.map (fun directory -> directory.RelativePath)
                |> Seq.sortBy (fun relativePath -> string relativePath)
                |> Seq.toArray

            let mutable directoryIndex = 0
            let mutable created = false

            while directoryIndex < directories.Length do
                let directoryPath = directoryPathUnderRoot localRoot directories[directoryIndex]

                if not (Directory.Exists(directoryPath)) then
                    Directory.CreateDirectory(directoryPath) |> ignore

                    created <- true

                directoryIndex <- directoryIndex + 1

            return created
        }

    /// Recomputes one on-disk directory identity from verified direct files and recursively verified children.
    let rec private verifyCompleteDirectory localRoot (statusToCommit: GraceStatus) directoryId =
        task {
            let mutable directory = LocalDirectoryVersion.Default

            if not (statusToCommit.Index.TryGetValue(directoryId, &directory)) then
                return Error $"Working Directory Update final verification is missing directory {directoryId}."
            elif not (Directory.Exists(directoryPathUnderRoot localRoot directory.RelativePath)) then
                return Error $"Working Directory Update final verification is missing directory '{directory.RelativePath}'."
            else
                let entries = ResizeArray<Grace.Shared.Services.DirectoryVersionPreimageEntry>()
                let files = directory.Files |> Seq.toArray
                let mutable fileIndex = 0
                let mutable fileError = None
                let mutable directSize = 0L

                while fileIndex < files.Length
                      && Option.isNone fileError do
                    let file = files[fileIndex]
                    let filePath = pathUnderRoot localRoot file.RelativePath

                    match! verifyFile filePath file.Sha256Hash file.Blake3Hash with
                    | Error error -> fileError <- Some error
                    | Ok () ->
                        let actualSize = FileInfo(filePath).Length

                        if actualSize <> file.Size then
                            fileError <- Some $"File '{file.RelativePath}' has an unexpected size."
                        else
                            directSize <- directSize + actualSize

                            entries.Add(Grace.Shared.Services.DirectoryVersionPreimageEntry.File file.RelativePath actualSize file.Blake3Hash file.Sha256Hash)

                    fileIndex <- fileIndex + 1

                match fileError with
                | Some error -> return Error error
                | None when directSize <> directory.Size -> return Error $"Directory '{directory.RelativePath}' has an unexpected direct-file size."
                | None ->
                    let childIds = directory.Directories |> Seq.toArray
                    let mutable childIndex = 0
                    let mutable childError = None

                    while childIndex < childIds.Length
                          && Option.isNone childError do
                        let! child = verifyCompleteDirectory localRoot statusToCommit childIds[childIndex]

                        match child with
                        | Error error -> childError <- Some error
                        | Ok (childPath, childSize, childSha256Hash, childBlake3Hash) ->
                            entries.Add(Grace.Shared.Services.DirectoryVersionPreimageEntry.Directory childPath childSize childBlake3Hash childSha256Hash)

                        childIndex <- childIndex + 1

                    match childError with
                    | Some error -> return Error error
                    | None ->
                        let actualSha256Hash = Grace.Shared.Services.computeSha256ForDirectoryEntries directory.RelativePath entries

                        let actualBlake3Hash = Grace.Shared.Services.computeBlake3ForDirectory directory.RelativePath entries

                        if actualSha256Hash <> directory.Sha256Hash then
                            return Error $"Directory '{directory.RelativePath}' does not match its SHA-256 hash."
                        elif actualBlake3Hash <> directory.Blake3Hash then
                            return Error $"Directory '{directory.RelativePath}' does not match its BLAKE3 hash."
                        else
                            return Ok(directory.RelativePath, directSize, actualSha256Hash, actualBlake3Hash)
        }

    /// Verifies the full eligible final tree and exact target tuple before the atomic local completion commit.
    let private verifySelectedRoot localRoot scanInput (statusToCommit: GraceStatus) target =
        task {
            if LocalStateDb.validateCompleteStatusTree statusToCommit
               |> Result.isError then
                return Error "Working Directory Update final status is not one complete canonical root graph."
            elif statusToCommit.RootDirectoryId
                 <> Contracts.Target.rootDirectoryVersionId target
                 || statusToCommit.RootDirectorySha256Hash
                    <> Contracts.Target.sha256Hash target
                 || statusToCommit.RootDirectoryBlake3Hash
                    <> Contracts.Target.blake3Hash target then
                return Error "Working Directory Update final status does not match the selected target."
            else
                match! Services.scanWorkingTreeForDifferencesReadOnly scanInput statusToCommit with
                | Error error -> return Error $"Working Directory Update final tree scan failed: {error}"
                | Ok differences when differences.Count > 0 ->
                    return Error "Working Directory Update final tree contains missing or unexpected eligible entries."
                | Ok _ ->
                    match! verifyCompleteDirectory localRoot statusToCommit statusToCommit.RootDirectoryId with
                    | Error error -> return Error $"Working Directory Update final verification failed: {error}"
                    | Ok (_, _, rootSha256Hash, rootBlake3Hash) when
                        rootSha256Hash = Contracts.Target.sha256Hash target
                        && rootBlake3Hash = Contracts.Target.blake3Hash target
                        ->
                        return Ok()
                    | Ok _ -> return Error "Working Directory Update final root does not match the selected target."
        }

    /// Validates both accepted status revision and baseline root tuple after lease acquisition.
    let private matchesBaseline actual expected =
        actual.RootDirectoryId = expected.RootDirectoryId
        && actual.RootDirectorySha256Hash = expected.RootDirectorySha256Hash
        && actual.RootDirectoryBlake3Hash = expected.RootDirectoryBlake3Hash

    /// Runs the typed finalizer before the only pending-to-terminal SQLite transition.
    let private finalize localStateDbPath target operation (finalization: FinalizationFacts) (finalizer: IIdempotentFinalizer) cancellationToken =
        task {
            throwAt BeforeFinalization
            do! finalizer.FinalizeAsync(finalization, cancellationToken)

            do!
                LocalStateDb.finalizeWorkingDirectoryUpdateCompletionWithBeforeTerminalUpdate localStateDbPath target operation (fun () ->
                    throwAt BeforeTerminalCompletion)
        }

    /// Reconciles a same-operation pending completion without touching working files.
    let private finishPending localStateDbPath target operation finalization finalizer cancellationToken =
        task {
            try
                do! finalize localStateDbPath target operation finalization finalizer cancellationToken
                return Unchanged(receipt target operation false)
            with
            | ex -> return FinalizationIncomplete(receipt target operation false, failure ex.Message)
        }

    /// Applies one verified local update under the repository/local-root lease and returns only the five terminal outcomes.
    let run
        (Request (selection,
                  ApplicationFacts (localRoot, objectRoot, localStateDbPath, priorStatus, targetStatus, objectMetadata),
                  operation,
                  preparedContent,
                  _,
                  selectedStateReader,
                  completionDetails,
                  finalization,
                  finalizer,
                  progress))
        cancellationToken
        =
        task {
            use preparedContentLifetime =
                { new IDisposable with
                    member _.Dispose() = Contracts.PreparedContent.dispose preparedContent
                }

            let (AcceptedSelection (target, localRootScope, AcceptedConfiguration (_, scanInput), acceptedRevision)) = selection
            let mutable mutated = false
            let mutable locallyCompleted = false
            let mutable ownedToken = None

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create (Contracts.Target.repositoryId target) localRoot
                |> function
                    | Ok value -> value
                    | Error error -> invalidOp error

            /// Clears only the current attempt's exact marker while the scope lease remains held.
            let complete outcome =
                task {
                    match ownedToken with
                    | Some token ->
                        let! _ = WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope token
                        ownedToken <- None
                    | None -> ()

                    return outcome
                }

            try
                if not (Contracts.Operation.matchesTarget target operation) then
                    return! complete (Rejected(failure "Working Directory Update operation does not match its accepted target."))
                else
                    report progress Preparing

                    report progress Waiting
                    use! heldLease = WorkingDirectoryUpdateCoordination.Lease.acquire scope cancellationToken
                    cancellationToken.ThrowIfCancellationRequested()
                    let! selectedAfterLease = selectedStateReader.ReadAsync(cancellationToken)
                    let! currentRevision = LocalStateDb.readLocalStatusRevision localStateDbPath
                    let! currentStatus = LocalStateDb.readStatusSnapshot localStateDbPath
                    let! pending = LocalStateDb.readPendingWorkingDirectoryUpdateFinalization localStateDbPath
                    let! completion = LocalStateDb.readWorkingDirectoryUpdateCompletion localStateDbPath target operation

                    if selectedAfterLease <> selection then
                        return! complete (Rejected(failure "Working Directory Update selected state changed while waiting for the lease."))
                    else
                        match pending, completion with
                        | Some _, Some LocalStateDb.WorkingDirectoryUpdateCompletion.Pending ->
                            let! _ = WorkingDirectoryUpdateCoordination.Marker.tryRemoveMatchingCompletion scope target operation

                            match finalizer with
                            | Some value ->
                                let! outcome = finishPending localStateDbPath target operation finalization value cancellationToken
                                return! complete outcome
                            | None -> return! complete (Rejected(failure "Connect cannot retain a pending finalization."))
                        | Some _, _ -> return! complete (Rejected(failure "A different Working Directory Update finalization is pending."))
                        | None, Some LocalStateDb.WorkingDirectoryUpdateCompletion.Terminal ->
                            let! _ = WorkingDirectoryUpdateCoordination.Marker.tryRemoveMatchingCompletion scope target operation
                            return! complete (Unchanged(receipt target operation false))
                        | None, Some LocalStateDb.WorkingDirectoryUpdateCompletion.Pending ->
                            match finalizer with
                            | Some value ->
                                let! outcome = finishPending localStateDbPath target operation finalization value cancellationToken
                                return! complete outcome
                            | None -> return! complete (Rejected(failure "Connect cannot retain a pending finalization."))
                        | None, None when
                            currentRevision <> acceptedRevision
                            || not (matchesBaseline currentStatus priorStatus)
                            ->
                            return! complete (Rejected(failure "Working Directory Update local status changed while waiting for the lease."))
                        | None, None ->
                            match! WorkingDirectoryUpdateCoordination.Marker.inspect scope target operation with
                            | WorkingDirectoryUpdateCoordination.RequiresDoctor ->
                                return! complete (Rejected(failure "Working Directory Update marker requires Doctor recovery."))
                            | WorkingDirectoryUpdateCoordination.Missing
                            | WorkingDirectoryUpdateCoordination.Adopt ->
                                match! buildPlan scanInput localRoot priorStatus preparedContent with
                                | Error error -> return! complete (Rejected(failure error))
                                | Ok (targetFiles, deletions) ->
                                    let token = Contracts.AttemptToken.create ()

                                    let marker =
                                        WorkingDirectoryUpdateCoordination.Marker.create scope token target operation
                                        |> function
                                            | Ok value -> value
                                            | Error error -> invalidOp error

                                    do! WorkingDirectoryUpdateCoordination.Marker.write scope marker
                                    ownedToken <- Some token
                                    cancellationToken.ThrowIfCancellationRequested()
                                    throwAt BeforeObjectPublication

                                    let mutable objectIndex = 0

                                    while objectIndex < targetFiles.Length do
                                        let relativePath, sha256Hash, blake3Hash = targetFiles[objectIndex]

                                        match! publishObject preparedContent objectRoot relativePath sha256Hash blake3Hash with
                                        | Ok () -> ()
                                        | Error error -> invalidOp error

                                        objectIndex <- objectIndex + 1

                                    report progress Applying

                                    let mutable applicationIndex = 0

                                    while applicationIndex < targetFiles.Length do
                                        let relativePath, sha256Hash, blake3Hash = targetFiles[applicationIndex]
                                        let objectFileName = Services.getLocalObjectCacheFileName relativePath sha256Hash blake3Hash
                                        let objectPath = pathUnderRoot objectRoot (RelativePath(Path.Combine(string relativePath, objectFileName)))

                                        match! verifyFile objectPath sha256Hash blake3Hash with
                                        | Error error -> invalidOp error
                                        | Ok () ->
                                            let workingPath = pathUnderRoot localRoot relativePath
                                            let! workingCopyRequired = requiresWorkingCopy workingPath sha256Hash blake3Hash

                                            if workingCopyRequired then
                                                Directory.CreateDirectory(Path.GetDirectoryName(workingPath))
                                                |> ignore

                                                mutated <- true
                                                File.Copy(objectPath, workingPath, true)

                                        applicationIndex <- applicationIndex + 1

                                    let! createdDirectories = materializeTargetDirectories localRoot targetStatus

                                    if createdDirectories then mutated <- true

                                    let mutable deletionIndex = 0

                                    while deletionIndex < deletions.Length do
                                        let relativePath = deletions[deletionIndex]
                                        let path = pathUnderRoot localRoot relativePath

                                        if File.Exists(path) then
                                            mutated <- true
                                            File.Delete(path)

                                        deletionIndex <- deletionIndex + 1

                                    throwAt AfterWorkingMutation
                                    report progress Verifying

                                    match! verifySelectedRoot localRoot scanInput targetStatus target with
                                    | Error error -> return! complete (UpdateIncomplete(failure error))
                                    | Ok () ->
                                        throwAt BeforeLocalCompletion
                                        report progress Committing

                                        let! _ =
                                            LocalStateDb.commitWorkingDirectoryUpdateCompletion
                                                localStateDbPath
                                                targetStatus
                                                objectMetadata
                                                completionDetails
                                                target
                                                operation

                                        locallyCompleted <- true
                                        throwAt AfterLocalCompletionBeforeSidecar

                                        do! WorkingDirectoryUpdateCoordination.Sidecar.write scope operation

                                        match ownedToken with
                                        | Some token ->
                                            let! _ = WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope token
                                            ownedToken <- None
                                            ()
                                        | None -> ()

                                        match finalizer with
                                        | None ->
                                            return!
                                                complete (
                                                    if mutated then
                                                        Updated(receipt target operation true)
                                                    else
                                                        Unchanged(receipt target operation false)
                                                )
                                        | Some value ->
                                            try
                                                do! finalize localStateDbPath target operation finalization value cancellationToken

                                                return!
                                                    complete (
                                                        if mutated then
                                                            Updated(receipt target operation true)
                                                        else
                                                            Unchanged(receipt target operation false)
                                                    )
                                            with
                                            | ex -> return! complete (FinalizationIncomplete(receipt target operation mutated, failure ex.Message))
            with
            | :? OperationCanceledException when not mutated ->
                return! complete (Rejected(failure "Working Directory Update was cancelled before working-file mutation."))
            | ex when locallyCompleted ->
                match finalizer with
                | Some _ -> return! complete (FinalizationIncomplete(receipt target operation mutated, failure ex.Message))
                | None ->
                    return!
                        complete (
                            if mutated then
                                Updated(receipt target operation true)
                            else
                                Unchanged(receipt target operation false)
                        )
            | ex when mutated -> return! complete (UpdateIncomplete(failure ex.Message))
            | ex -> return! complete (Rejected(failure ex.Message))
        }

    /// Retries only recorded typed finalization after lease and selected-state revalidation; it never accesses working files.
    let retryFinalization
        (FinalizationRequest (selection, operation, localStateDbPath, _, selectedStateReader, finalization, finalizer, progress))
        cancellationToken
        =
        task {
            let (AcceptedSelection (target, localRootScope, _, _)) = selection

            try
                report progress Waiting

                let scope =
                    WorkingDirectoryUpdateCoordination.Scope.createFromLocalRootScope (Contracts.Target.repositoryId target) localRootScope
                    |> function
                        | Ok value -> value
                        | Error error -> invalidOp error

                use! heldLease = WorkingDirectoryUpdateCoordination.Lease.acquire scope cancellationToken
                cancellationToken.ThrowIfCancellationRequested()
                let! selectedAfterLease = selectedStateReader.ReadAsync(cancellationToken)

                if selectedAfterLease <> selection then
                    return Rejected(failure "Working Directory Update selected state changed before finalization retry.")
                else
                    let! completion = LocalStateDb.readWorkingDirectoryUpdateCompletion localStateDbPath target operation

                    match completion with
                    | Some LocalStateDb.WorkingDirectoryUpdateCompletion.Pending ->
                        return! finishPending localStateDbPath target operation finalization finalizer cancellationToken
                    | Some LocalStateDb.WorkingDirectoryUpdateCompletion.Terminal -> return Unchanged(receipt target operation false)
                    | None -> return Rejected(failure "Working Directory Update finalization is not pending for this operation.")
            with
            | :? OperationCanceledException -> return Rejected(failure "Working Directory Update finalization retry was cancelled.")
            | ex -> return Rejected(failure ex.Message)
        }
