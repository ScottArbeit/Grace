namespace Grace.CLI.Command

open System
open System.Collections.Generic
open System.IO
open System.Threading.Tasks
open Grace.CLI
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Types.Common

/// Owns the verified local working-directory update transaction after its private contracts and storage seams exist.
module internal WorkingDirectoryUpdate =
    /// Classifies one immutable topology result before a later transaction can mutate the working tree.
    module Topology =
        /// Identifies why a local path cannot safely participate in a tracked-only topology plan.
        type RejectionClassification =
            | Ignored
            | Untracked
            | AmbiguousTarget
            | EscapesLocalRoot

        /// Carries the stable repository-relative path and classification that prevented a pre-mutation plan.
        type Rejection = private { Path: RelativePath; Classification: RejectionClassification }

        /// Describes one tracked later transaction step without carrying a writer, callback, or mutable filesystem handle.
        type Action =
            | RemoveTrackedFile of RelativePath
            | RemoveTrackedDirectory of RelativePath
            | EnsureDirectory of RelativePath
            | CopyVerifiedFile of RelativePath

        /// Represents the complete immutable topology plan that a later transaction may apply without discovering blockers.
        type Plan = private Plan of Action list

        /// Represents either the complete immutable plan or one stable pre-mutation conflict.
        type Result =
            | Planned of Plan
            | Rejected of Rejection

        /// Returns the conflict path from a rejected topology decision.
        module Rejection =
            /// Gets the repository-relative path that prevents safe planning.
            let path rejection = rejection.Path

            /// Returns the safety classification that caused the topology decision to reject.
            let classification rejection = rejection.Classification

        /// Returns every deterministic action in a successful topology plan.
        module Plan =
            /// Gets the complete immutable action sequence in transaction order.
            let actions (Plan actions) = actions

        /// Represents the filesystem shape observed for one candidate repository path.
        type private ActualKind =
            | Missing
            | File
            | Directory

        /// Returns the Windows-normalized comparison key for one already-normalized relative path.
        let private pathKey (path: RelativePath) =
            string path
            |> fun value -> value.ToUpperInvariant()

        /// Counts path components so tracked removals and target directory creation have explicit stable ordering.
        let private depth (path: RelativePath) =
            if string path = Constants.RootDirectoryPath then
                0
            else
                (string path).Split(
                    '/',
                    StringSplitOptions.RemoveEmptyEntries
                )
                    .Length

        /// Compares two paths using the exact case-insensitive behavior required for Windows target shapes.
        let private comparePaths left right = StringComparer.OrdinalIgnoreCase.Compare((string left), (string right))

        /// Orders tracked directories by Windows comparison path so rejection selection never depends on dictionary enumeration.
        let private orderedTrackedDirectories (entries: seq<LocalDirectoryVersion>) =
            entries
            |> Seq.sortWith (fun left right -> comparePaths left.RelativePath right.RelativePath)

        /// Orders tracked files by Windows comparison path so rejection selection never depends on dictionary enumeration.
        let private orderedTrackedFiles (entries: seq<LocalFileVersion>) =
            entries
            |> Seq.sortWith (fun left right -> comparePaths left.RelativePath right.RelativePath)

        /// Orders target directories by Windows comparison path so rejection selection never depends on dictionary enumeration.
        let private orderedTargetDirectories (entries: seq<RelativePath>) = entries |> Seq.sortWith comparePaths

        /// Orders target files by Windows comparison path so rejection selection never depends on dictionary enumeration.
        let private orderedTargetFiles (entries: seq<Sha256Hash * Blake3Hash * RelativePath>) =
            entries
            |> Seq.sortWith (fun (_, _, left) (_, _, right) -> comparePaths left right)

        /// Returns the parent directories implied by one target file or directory path.
        let private parentDirectories (path: RelativePath) =
            let segments =
                (string path)
                    .Split('/', StringSplitOptions.RemoveEmptyEntries)

            [
                for index in 1 .. segments.Length - 1 do
                    RelativePath(String.Join('/', segments[0 .. index - 1]))
            ]

        /// Converts a repository-relative path into one fully-qualified candidate beneath the configured local root.
        let private fullPathUnderRoot localRoot (path: RelativePath) =
            let relative = string path

            let candidate =
                if relative = Constants.RootDirectoryPath then
                    localRoot
                else
                    Path.Combine(localRoot, relative.Replace('/', Path.DirectorySeparatorChar))
                    |> Path.GetFullPath

            if Services.isPathWithinDirectoryWithComparison StringComparison.OrdinalIgnoreCase localRoot candidate then
                Ok candidate
            else
                Error { Path = path; Classification = EscapesLocalRoot }

        /// Returns the actual filesystem kind without mutating the path.
        let private actualKind fullPath =
            if File.Exists(fullPath) then File
            elif Directory.Exists(fullPath) then Directory
            else Missing

        /// Builds the immutable current configuration snapshot consumed by the repository scan/ignore classifier.
        let private currentScanInput () : Services.WorkingTreeScanInput =
            let current = Current()

            {
                RootDirectory = current.RootDirectory
                GraceDirectory = current.GraceDirectory
                GraceStatusFile = current.GraceStatusFile
                DirectoryIgnoreEntries = current.GraceDirectoryIgnoreEntries
                FileIgnoreEntries = current.GraceFileIgnoreEntries
            }

        /// Builds the supported repository classifier input from one immutable scan snapshot.
        let private classifierInput (scanInput: Services.WorkingTreeScanInput) : Services.RepositoryPathClassifierInput =
            {
                RootDirectory = scanInput.RootDirectory
                GraceDirectory = scanInput.GraceDirectory
                GraceStatusFile = scanInput.GraceStatusFile
                DirectoryIgnoreEntries = scanInput.DirectoryIgnoreEntries
                FileIgnoreEntries = scanInput.FileIgnoreEntries
                PathComparison = StringComparison.OrdinalIgnoreCase
            }

        /// Returns whether a tracked file already contains the exact selected target bytes.
        let private hasVerifiedTargetBytes fullPath targetSha256 targetBlake3 =
            Services.createLocalFileVersion (FileInfo(fullPath))
            |> fun task -> task.GetAwaiter().GetResult()
            |> Option.exists (fun actual ->
                actual.Sha256Hash = targetSha256
                && actual.Blake3Hash = targetBlake3)

        /// Builds the complete tracked file and directory maps from one status snapshot while rejecting Windows collisions.
        let private trackedTopology (status: GraceStatus) =
            let files = Dictionary<string, LocalFileVersion>(StringComparer.Ordinal)
            let directories = Dictionary<string, LocalDirectoryVersion>(StringComparer.Ordinal)
            let mutable rejection = None

            for directoryVersion in orderedTrackedDirectories status.Index.Values do
                let directoryKey = pathKey directoryVersion.RelativePath

                if
                    directories.ContainsKey(directoryKey)
                    || files.ContainsKey(directoryKey)
                then
                    rejection <- Some { Path = directoryVersion.RelativePath; Classification = AmbiguousTarget }
                else
                    directories[directoryKey] <- directoryVersion

                for fileVersion in orderedTrackedFiles directoryVersion.Files do
                    let fileKey = pathKey fileVersion.RelativePath

                    if
                        files.ContainsKey(fileKey)
                        || directories.ContainsKey(fileKey)
                    then
                        rejection <- Some { Path = fileVersion.RelativePath; Classification = AmbiguousTarget }
                    else
                        files[fileKey] <- fileVersion

            rejection, files, directories

        /// Derives complete target file and directory topology from the immutable prepared manifest, including file ancestors.
        let private targetTopology manifest =
            let files = Dictionary<string, Sha256Hash * Blake3Hash * RelativePath>(StringComparer.Ordinal)
            let directories = Dictionary<string, RelativePath>(StringComparer.Ordinal)
            directories[pathKey (RelativePath Constants.RootDirectoryPath)] <- RelativePath Constants.RootDirectoryPath
            let mutable rejection = None

            for entry in WorkingDirectoryUpdateContracts.PreparedManifest.entries manifest do
                match entry with
                | WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory path ->
                    let key = pathKey path

                    if
                        files.ContainsKey(key)
                        || directories.ContainsKey(key)
                    then
                        rejection <- Some { Path = path; Classification = AmbiguousTarget }
                    else
                        directories[key] <- path

                    for parent in parentDirectories path do
                        let parentKey = pathKey parent

                        if files.ContainsKey(parentKey) then
                            rejection <- Some { Path = parent; Classification = AmbiguousTarget }
                        else
                            directories[parentKey] <- parent
                | WorkingDirectoryUpdateContracts.PreparedManifestEntry.File (path, sha256Hash, blake3Hash) ->
                    let key = pathKey path

                    if
                        files.ContainsKey(key)
                        || directories.ContainsKey(key)
                    then
                        rejection <- Some { Path = path; Classification = AmbiguousTarget }
                    else
                        files[key] <- (sha256Hash, blake3Hash, path)

                    for parent in parentDirectories path do
                        let parentKey = pathKey parent

                        if files.ContainsKey(parentKey) then
                            rejection <- Some { Path = parent; Classification = AmbiguousTarget }
                        else
                            directories[parentKey] <- parent

            rejection, files, directories

        /// Classifies an actual local entry through the supported ignore rules before it can be treated as tracked.
        let private localClassification
            (classifierInput: Services.RepositoryPathClassifierInput)
            (files: Dictionary<string, LocalFileVersion>)
            (directories: Dictionary<string, LocalDirectoryVersion>)
            (fullPath: string)
            (relativePath: RelativePath)
            kind
            =
            let pathKind =
                match kind with
                | File -> Services.RepositoryPathKind.FilePath
                | Directory -> Services.RepositoryPathKind.DirectoryPath
                | Missing -> Services.RepositoryPathKind.UnknownPath

            match Services.classifyRepositoryPath classifierInput pathKind fullPath with
            | Services.RepositoryPathClassification.Eligible ->
                let key = pathKey relativePath

                match kind, files.TryGetValue(key), directories.TryGetValue(key) with
                | File, (true, _), _ -> Ok "tracked-file"
                | Directory, _, (true, _) -> Ok "tracked-directory"
                | Missing, _, _ -> Ok "missing"
                | _ -> Error { Path = relativePath; Classification = Untracked }
            | _ -> Error { Path = relativePath; Classification = Ignored }

        /// Finds the first ignored or untracked descendant that would make a tracked directory unsafe to remove.
        let private firstUnsafeDescendant classifierInput files directories localRoot rootDirectory relativePath =
            let rec visit fullPath currentRelative =
                let children =
                    DirectoryInfo(fullPath).GetFileSystemInfos()
                    |> Array.sortWith (fun left right -> StringComparer.OrdinalIgnoreCase.Compare(left.FullName, right.FullName))

                let mutable conflict = None
                let mutable index = 0

                while index < children.Length && Option.isNone conflict do
                    let child = children[index]

                    let childRelative =
                        Path.GetRelativePath(localRoot, child.FullName)
                        |> Grace.Shared.Utilities.normalizeFilePath
                        |> RelativePath

                    let childKind = if child :? DirectoryInfo then Directory else File

                    match localClassification classifierInput files directories child.FullName childRelative childKind with
                    | Error rejection -> conflict <- Some rejection
                    | Ok "tracked-directory" -> conflict <- visit child.FullName childRelative
                    | Ok _ -> ()

                    index <- index + 1

                conflict

            visit rootDirectory relativePath

        /// Classifies every target and removable tracked blocker without entering the asynchronous transaction workflow.
        let private planSynchronously (currentStatus: GraceStatus) manifest =
            let scanInput = currentScanInput ()
            let classifier = classifierInput scanInput
            let trackedRejection, trackedFiles, trackedDirectories = trackedTopology currentStatus
            let targetRejection, targetFiles, targetDirectories = targetTopology manifest

            match trackedRejection, targetRejection with
            | Some rejection, _
            | _, Some rejection -> Rejected rejection
            | None, None ->
                let mutable rejection = None
                let removals = Dictionary<string, Action>(StringComparer.Ordinal)
                let creates = Dictionary<string, Action>(StringComparer.Ordinal)
                let copies = Dictionary<string, Action>(StringComparer.Ordinal)

                let addRemoval path action = removals[pathKey path] <- action
                let addCreate path = creates[pathKey path] <- EnsureDirectory path
                let addCopy path = copies[pathKey path] <- CopyVerifiedFile path

                for targetDirectory in orderedTargetDirectories targetDirectories.Values do
                    if Option.isNone rejection
                       && string targetDirectory
                          <> Constants.RootDirectoryPath then
                        match fullPathUnderRoot scanInput.RootDirectory targetDirectory with
                        | Error value -> rejection <- Some value
                        | Ok fullPath ->
                            match actualKind fullPath with
                            | Missing -> addCreate targetDirectory
                            | File ->
                                match localClassification classifier trackedFiles trackedDirectories fullPath targetDirectory File with
                                | Error value -> rejection <- Some value
                                | Ok "tracked-file" ->
                                    addRemoval targetDirectory (RemoveTrackedFile targetDirectory)
                                    addCreate targetDirectory
                                | Ok _ -> rejection <- Some { Path = targetDirectory; Classification = Untracked }
                            | Directory ->
                                match localClassification classifier trackedFiles trackedDirectories fullPath targetDirectory Directory with
                                | Error value -> rejection <- Some value
                                | Ok "tracked-directory" ->
                                    match firstUnsafeDescendant classifier trackedFiles trackedDirectories scanInput.RootDirectory fullPath targetDirectory with
                                    | Some value -> rejection <- Some value
                                    | None -> ()
                                | Ok _ -> rejection <- Some { Path = targetDirectory; Classification = Untracked }

                for targetFile in orderedTargetFiles targetFiles.Values do
                    if Option.isNone rejection then
                        let _, _, targetPath = targetFile

                        match fullPathUnderRoot scanInput.RootDirectory targetPath with
                        | Error value -> rejection <- Some value
                        | Ok fullPath ->
                            match actualKind fullPath with
                            | Missing -> addCopy targetPath
                            | File ->
                                match localClassification classifier trackedFiles trackedDirectories fullPath targetPath File with
                                | Error value -> rejection <- Some value
                                | Ok "tracked-file" ->
                                    let targetSha256, targetBlake3, _ = targetFile
                                    let tracked = trackedFiles[pathKey targetPath]

                                    if
                                        tracked.Sha256Hash <> targetSha256
                                        || tracked.Blake3Hash <> targetBlake3
                                        || not (hasVerifiedTargetBytes fullPath targetSha256 targetBlake3)
                                    then
                                        addCopy targetPath
                                | Ok _ -> rejection <- Some { Path = targetPath; Classification = Untracked }
                            | Directory ->
                                match localClassification classifier trackedFiles trackedDirectories fullPath targetPath Directory with
                                | Error value -> rejection <- Some value
                                | Ok "tracked-directory" ->
                                    match firstUnsafeDescendant classifier trackedFiles trackedDirectories scanInput.RootDirectory fullPath targetPath with
                                    | Some value -> rejection <- Some value
                                    | None ->
                                        for directoryVersion in orderedTrackedDirectories trackedDirectories.Values do
                                            let directoryPath = directoryVersion.RelativePath

                                            if string directoryPath
                                               <> Constants.RootDirectoryPath
                                               && (pathKey directoryPath = pathKey targetPath
                                                   || (string directoryPath)
                                                       .StartsWith(string targetPath + "/", StringComparison.OrdinalIgnoreCase)) then
                                                addRemoval directoryPath (RemoveTrackedDirectory directoryPath)

                                        for fileVersion in orderedTrackedFiles trackedFiles.Values do
                                            let filePath = fileVersion.RelativePath

                                            if (string filePath)
                                                .StartsWith(string targetPath + "/", StringComparison.OrdinalIgnoreCase) then
                                                addRemoval filePath (RemoveTrackedFile filePath)

                                        addCopy targetPath
                                | Ok _ -> rejection <- Some { Path = targetPath; Classification = Untracked }

                for fileVersion in orderedTrackedFiles trackedFiles.Values do
                    if
                        Option.isNone rejection
                        && not (targetFiles.ContainsKey(pathKey fileVersion.RelativePath))
                    then
                        match fullPathUnderRoot scanInput.RootDirectory fileVersion.RelativePath with
                        | Error value -> rejection <- Some value
                        | Ok fullPath ->
                            match actualKind fullPath with
                            | Missing -> ()
                            | File ->
                                match localClassification classifier trackedFiles trackedDirectories fullPath fileVersion.RelativePath File with
                                | Ok "tracked-file" -> addRemoval fileVersion.RelativePath (RemoveTrackedFile fileVersion.RelativePath)
                                | Error value -> rejection <- Some value
                                | Ok _ -> rejection <- Some { Path = fileVersion.RelativePath; Classification = Untracked }
                            | Directory ->
                                match localClassification classifier trackedFiles trackedDirectories fullPath fileVersion.RelativePath Directory with
                                | Error value -> rejection <- Some value
                                | Ok _ -> rejection <- Some { Path = fileVersion.RelativePath; Classification = Untracked }

                for directoryVersion in orderedTrackedDirectories trackedDirectories.Values do
                    let directoryPath = directoryVersion.RelativePath

                    if
                        Option.isNone rejection
                        && string directoryPath
                           <> Constants.RootDirectoryPath
                        && not (targetDirectories.ContainsKey(pathKey directoryPath))
                    then
                        match fullPathUnderRoot scanInput.RootDirectory directoryPath with
                        | Error value -> rejection <- Some value
                        | Ok fullPath ->
                            match actualKind fullPath with
                            | Missing -> ()
                            | Directory ->
                                match localClassification classifier trackedFiles trackedDirectories fullPath directoryPath Directory with
                                | Error value -> rejection <- Some value
                                | Ok "tracked-directory" ->
                                    match firstUnsafeDescendant classifier trackedFiles trackedDirectories scanInput.RootDirectory fullPath directoryPath with
                                    | Some value -> rejection <- Some value
                                    | None -> addRemoval directoryPath (RemoveTrackedDirectory directoryPath)
                                | Ok _ -> rejection <- Some { Path = directoryPath; Classification = Untracked }
                            | File ->
                                match localClassification classifier trackedFiles trackedDirectories fullPath directoryPath File with
                                | Error value -> rejection <- Some value
                                | Ok _ -> rejection <- Some { Path = directoryPath; Classification = Untracked }

                match rejection with
                | Some value -> Rejected value
                | None ->
                    let orderedRemovals =
                        removals.Values
                        |> Seq.sortWith (fun left right ->
                            let leftPath =
                                match left with
                                | RemoveTrackedFile path
                                | RemoveTrackedDirectory path -> path
                                | _ -> RelativePath Constants.RootDirectoryPath

                            let rightPath =
                                match right with
                                | RemoveTrackedFile path
                                | RemoveTrackedDirectory path -> path
                                | _ -> RelativePath Constants.RootDirectoryPath

                            let byDepth = compare (depth rightPath) (depth leftPath)
                            if byDepth <> 0 then byDepth else comparePaths leftPath rightPath)
                        |> Seq.toList

                    let orderedCreates =
                        creates.Values
                        |> Seq.sortWith (fun left right ->
                            let leftPath =
                                match left with
                                | EnsureDirectory path -> path
                                | _ -> RelativePath Constants.RootDirectoryPath

                            let rightPath =
                                match right with
                                | EnsureDirectory path -> path
                                | _ -> RelativePath Constants.RootDirectoryPath

                            let byDepth = compare (depth leftPath) (depth rightPath)
                            if byDepth <> 0 then byDepth else comparePaths leftPath rightPath)
                        |> Seq.toList

                    let orderedCopies =
                        copies.Values
                        |> Seq.sortWith (fun left right ->
                            let leftPath =
                                match left with
                                | CopyVerifiedFile path -> path
                                | _ -> RelativePath Constants.RootDirectoryPath

                            let rightPath =
                                match right with
                                | CopyVerifiedFile path -> path
                                | _ -> RelativePath Constants.RootDirectoryPath

                            comparePaths leftPath rightPath)
                        |> Seq.toList

                    Planned(Plan(orderedRemovals @ orderedCreates @ orderedCopies))

        /// Produces one complete pre-mutation action list after classifying every target and removable tracked blocker.
        let plan (currentStatus: GraceStatus) manifest = task { return planSynchronously currentStatus manifest }

    /// Carries the one complete target status graph and its exact selected-root identity through the local transaction.
    type ResolvedTargetGraph = private ResolvedTargetGraph of WorkingDirectoryUpdateContracts.Target * GraceStatus

    /// Carries correlation used only to identify diagnostics from one invocation; it never changes the update identity.
    type DiagnosticCorrelation = private DiagnosticCorrelation of string

    /// Holds the private local-completion result that a later finalization leaf may consume without reconstructing facts.
    type LocalCompletion = private LocalCompletion of WorkingDirectoryUpdateContracts.Target * WorkingDirectoryUpdateContracts.Operation

    /// Builds the only accepted resolved graph when its complete rooted status and selected-root identity agree.
    module ResolvedTargetGraph =
        /// Creates the runtime target graph after proving its root matches the selected target exactly.
        let create target (status: GraceStatus) =
            match LocalStateDb.validateCompleteStatusTree status with
            | Error error -> Error $"Resolved target graph must be complete: {error}"
            | Ok () ->
                match
                    WorkingDirectoryUpdateContracts.Target.create
                        (WorkingDirectoryUpdateContracts.Target.repositoryId target)
                        (WorkingDirectoryUpdateContracts.Target.branchId target)
                        status.RootDirectoryId
                        status.RootDirectorySha256Hash
                        status.RootDirectoryBlake3Hash
                    with
                | Ok graphTarget when graphTarget = target -> Ok(ResolvedTargetGraph(target, status))
                | Ok _ -> Error "Resolved target graph root does not exactly match the selected target."
                | Error error -> Error $"Resolved target graph has an invalid root: {error}"

        /// Returns the exact selected-root identity retained by this immutable graph.
        let internal target (ResolvedTargetGraph (target, _)) = target

        /// Returns the complete rooted status graph retained by this immutable graph.
        let internal status (ResolvedTargetGraph (_, status)) = status

    /// Supplies construction for non-empty diagnostic correlation values without making them part of operation identity.
    module DiagnosticCorrelation =
        /// Creates correlation text that can distinguish simultaneous local attempts in diagnostics.
        let create value =
            if String.IsNullOrWhiteSpace(value) then
                Error "Working Directory Update diagnostic correlation must not be empty."
            else
                Ok(DiagnosticCorrelation value)

        /// Returns diagnostic-only correlation text to the local transaction implementation.
        let internal value (DiagnosticCorrelation value) = value

    /// Supplies the narrow immutable result consumed by later pending-finalization routing.
    module LocalCompletion =
        /// Returns the selected target preserved by verified SQLite local completion.
        let internal target (LocalCompletion (target, _)) = target

        /// Returns the exact operation whose pending local completion was committed.
        let internal operation (LocalCompletion (_, operation)) = operation

    /// Owns application of the private five-input Branch transaction through verified pending local completion.
    module LocalTransaction =
        /// Produces a deterministic complete-status fingerprint used to reject stale Branch admission after preparation.
        let statusFingerprint (status: GraceStatus) =
            let canonical =
                status.Index.Values
                |> Seq.sortBy (fun directory -> string directory.RelativePath)
                |> Seq.collect (fun directory ->
                    seq {
                        yield $"D|{directory.RelativePath}|{directory.DirectoryVersionId:N}|{directory.Sha256Hash}|{directory.Blake3Hash}"

                        yield!
                            directory.Files
                            |> Seq.sortBy (fun file -> string file.RelativePath)
                            |> Seq.map (fun file -> $"F|{file.RelativePath}|{file.Sha256Hash}|{file.Blake3Hash}|{file.Size}")
                    })
                |> String.concat "\n"

            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))
            |> Convert.ToHexString
            |> fun value -> value.ToLowerInvariant()

        /// Computes a stable Windows comparison key for graph and manifest topology proof.
        let private pathKey (path: RelativePath) =
            string path
            |> fun value -> value.ToUpperInvariant()

        /// Enumerates all parent directories implied by one target path, including the root directory.
        let private parentDirectories (path: RelativePath) =
            let segments =
                (string path)
                    .Split('/', StringSplitOptions.RemoveEmptyEntries)

            seq {
                yield RelativePath Constants.RootDirectoryPath

                for index in 1 .. segments.Length - 1 do
                    yield RelativePath(String.Join('/', segments[0 .. index - 1]))
            }

        /// Compares the full normalized directory and dual-hash file topology before any planner can consume it.
        let private graphMatchesManifest (graph: GraceStatus) manifest =
            let graphDirectories = Dictionary<string, RelativePath>(StringComparer.Ordinal)
            let graphFiles = Dictionary<string, Sha256Hash * Blake3Hash>(StringComparer.Ordinal)
            let manifestDirectories = Dictionary<string, RelativePath>(StringComparer.Ordinal)
            let manifestFiles = Dictionary<string, Sha256Hash * Blake3Hash>(StringComparer.Ordinal)
            let mutable valid = true

            graph.Index.Values
            |> Seq.iter (fun directory ->
                let directoryKey = pathKey directory.RelativePath

                if
                    graphDirectories.ContainsKey(directoryKey)
                    || graphFiles.ContainsKey(directoryKey)
                then
                    valid <- false
                else
                    graphDirectories[directoryKey] <- directory.RelativePath

                directory.Files
                |> Seq.iter (fun file ->
                    let fileKey = pathKey file.RelativePath

                    if
                        graphFiles.ContainsKey(fileKey)
                        || graphDirectories.ContainsKey(fileKey)
                    then
                        valid <- false
                    else
                        graphFiles[fileKey] <- (file.Sha256Hash, file.Blake3Hash)))

            WorkingDirectoryUpdateContracts.PreparedManifest.entries manifest
            |> Seq.iter (function
                | WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory path ->
                    let key = pathKey path

                    if
                        manifestDirectories.ContainsKey(key)
                        || manifestFiles.ContainsKey(key)
                    then
                        valid <- false
                    else
                        manifestDirectories[key] <- path

                    parentDirectories path
                    |> Seq.iter (fun parent ->
                        let parentKey = pathKey parent

                        if manifestFiles.ContainsKey(parentKey) then
                            valid <- false
                        else
                            manifestDirectories[parentKey] <- parent)
                | WorkingDirectoryUpdateContracts.PreparedManifestEntry.File (path, sha256Hash, blake3Hash) ->
                    let key = pathKey path

                    if
                        manifestFiles.ContainsKey(key)
                        || manifestDirectories.ContainsKey(key)
                    then
                        valid <- false
                    else
                        manifestFiles[key] <- (sha256Hash, blake3Hash)

                    parentDirectories path
                    |> Seq.iter (fun parent ->
                        let parentKey = pathKey parent

                        if manifestFiles.ContainsKey(parentKey) then
                            valid <- false
                        else
                            manifestDirectories[parentKey] <- parent))

            valid
            && graphDirectories.Count = manifestDirectories.Count
            && graphFiles.Count = manifestFiles.Count
            && graphDirectories.Keys
               |> Seq.forall manifestDirectories.ContainsKey
            && graphFiles
               |> Seq.forall (fun pair ->
                   match manifestFiles.TryGetValue(pair.Key) with
                   | true, hashes -> hashes = pair.Value
                   | false, _ -> false)

        /// Maps one prepared path and hashes to its immutable local object-cache path.
        let private objectPath objectDirectory path sha256Hash blake3Hash =
            Path.Combine(objectDirectory, string path, Services.getLocalObjectCacheFileName path sha256Hash blake3Hash)

        /// Verifies one filesystem file against both selected content hashes without trusting its name or metadata.
        let private hasExpectedBytes path sha256Hash blake3Hash =
            Services.createLocalFileVersion (FileInfo(path))
            |> fun task -> task.GetAwaiter().GetResult()
            |> Option.exists (fun file ->
                file.Sha256Hash = sha256Hash
                && file.Blake3Hash = blake3Hash)

        /// Publishes one verified prepared file exactly once and rejects a mismatching immutable object already at its final path.
        let private publishObject preparedContent objectDirectory path sha256Hash blake3Hash =
            task {
                let finalPath = objectPath objectDirectory path sha256Hash blake3Hash

                if File.Exists(finalPath) then
                    if not (hasExpectedBytes finalPath sha256Hash blake3Hash) then
                        return Error $"Immutable object bytes are corrupt or were replaced for '{path}'."
                    else
                        return Ok()
                else
                    match WorkingDirectoryUpdateContracts.PreparedContent.openRead preparedContent path with
                    | Error error -> return Error error
                    | Ok source ->
                        use source = source
                        let directory = Path.GetDirectoryName(finalPath)
                        Directory.CreateDirectory(directory) |> ignore

                        let temporaryPath =
                            finalPath
                            + "."
                            + Guid.NewGuid().ToString("N")
                            + ".tmp"

                        use destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                        do! source.CopyToAsync(destination)
                        destination.Flush(true)
                        destination.Dispose()

                        if not (hasExpectedBytes temporaryPath sha256Hash blake3Hash) then
                            File.Delete(temporaryPath)
                            return Error $"Prepared object bytes do not match their declared hashes for '{path}'."
                        else
                            try
                                File.Move(temporaryPath, finalPath)
                            with
                            | :? IOException when File.Exists(finalPath) -> File.Delete(temporaryPath)

                            if hasExpectedBytes finalPath sha256Hash blake3Hash then
                                return Ok()
                            else
                                return Error $"Immutable object bytes are corrupt or were replaced for '{path}'."
            }

        /// Publishes and rechecks every prepared immutable object before working-directory mutation is considered.
        let private publishObjects preparedContent objectDirectory manifest =
            task {
                let files =
                    WorkingDirectoryUpdateContracts.PreparedManifest.entries manifest
                    |> Seq.choose (function
                        | WorkingDirectoryUpdateContracts.PreparedManifestEntry.File (path, sha256Hash, blake3Hash) -> Some(path, sha256Hash, blake3Hash)
                        | WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory _ -> None)
                    |> Seq.toArray

                let mutable failure = None
                let mutable index = 0

                while index < files.Length && Option.isNone failure do
                    let path, sha256Hash, blake3Hash = files[index]
                    let! result = publishObject preparedContent objectDirectory path sha256Hash blake3Hash

                    match result with
                    | Ok () -> ()
                    | Error error -> failure <- Some error

                    index <- index + 1

                return
                    match failure with
                    | Some error -> Error error
                    | None -> Ok()
            }

        /// Converts a planned repository-relative path to a root-contained filesystem path.
        let private workingPath root (path: RelativePath) =
            if string path = Constants.RootDirectoryPath then
                root
            else
                let fullPath =
                    Path.GetFullPath(
                        Path.Combine(
                            root,
                            (string path)
                                .Replace('/', Path.DirectorySeparatorChar)
                        )
                    )

                if Services.isPathWithinDirectoryWithComparison StringComparison.OrdinalIgnoreCase root fullPath then
                    fullPath
                else
                    invalidOp $"Working Directory Update plan path escapes the local root: {path}"

        /// Applies one preplanned action using only already-verified immutable objects.
        let private applyAction root objectDirectory (objectHashes: Dictionary<string, Sha256Hash * Blake3Hash>) =
            function
            | Topology.RemoveTrackedFile path ->
                let fullPath = workingPath root path

                if File.Exists(fullPath) then
                    File.Delete(fullPath)
                    true
                else
                    false
            | Topology.RemoveTrackedDirectory path ->
                let fullPath = workingPath root path

                if Directory.Exists(fullPath) then
                    Directory.Delete(fullPath, false)
                    true
                else
                    false
            | Topology.EnsureDirectory path ->
                let fullPath = workingPath root path

                if Directory.Exists(fullPath) then
                    false
                else
                    Directory.CreateDirectory(fullPath) |> ignore
                    true
            | Topology.CopyVerifiedFile path ->
                let key = pathKey path

                match objectHashes.TryGetValue(key) with
                | false, _ -> invalidOp $"Planned file '{path}' has no immutable object declaration."
                | true, (sha256Hash, blake3Hash) ->
                    let objectFile = objectPath objectDirectory path sha256Hash blake3Hash

                    if not (hasExpectedBytes objectFile sha256Hash blake3Hash) then
                        invalidOp $"Immutable object bytes are corrupt or were replaced for '{path}'."

                    let finalPath = workingPath root path
                    let directory = Path.GetDirectoryName(finalPath)
                    Directory.CreateDirectory(directory) |> ignore
                    File.Copy(objectFile, finalPath, true)
                    true

        /// Returns every target file declared by the complete selected graph.
        let private targetFiles (status: GraceStatus) =
            status.Index.Values
            |> Seq.collect (fun directory -> directory.Files)
            |> Seq.map (fun file -> file.RelativePath, file.Sha256Hash, file.Blake3Hash)
            |> Seq.toArray

        /// Independently verifies every target path, file hash, directory kind, and absence of extra working-tree entries.
        let private verifyCompleteTargetRoot root graceDirectory (status: GraceStatus) =
            let expectedDirectories =
                status.Index.Values
                |> Seq.map (fun directory -> pathKey directory.RelativePath)
                |> HashSet

            let expectedFiles =
                targetFiles status
                |> Seq.map (fun (path, sha256Hash, blake3Hash) -> pathKey path, (sha256Hash, blake3Hash))
                |> dict

            let targetFilesMatch =
                expectedFiles
                |> Seq.forall (fun pair ->
                    let relativePath =
                        status.Index.Values
                        |> Seq.collect (fun directory -> directory.Files)
                        |> Seq.find (fun file -> pathKey file.RelativePath = pair.Key)
                        |> fun file -> file.RelativePath

                    let fullPath = workingPath root relativePath

                    File.Exists(fullPath)
                    && hasExpectedBytes fullPath (fst pair.Value) (snd pair.Value))

            let actualEntries =
                DirectoryInfo(root)
                    .GetFileSystemInfos("*", SearchOption.AllDirectories)
                |> Array.filter (fun entry ->
                    not (Services.isPathWithinDirectoryWithComparison StringComparison.OrdinalIgnoreCase graceDirectory entry.FullName))

            let actualTopologyMatches =
                actualEntries
                |> Array.forall (fun entry ->
                    let relativePath =
                        Path.GetRelativePath(root, entry.FullName)
                        |> Grace.Shared.Utilities.normalizeFilePath
                        |> RelativePath

                    let key = pathKey relativePath

                    if entry :? DirectoryInfo then
                        expectedDirectories.Contains(key)
                    else
                        expectedFiles.ContainsKey(key))

            let actualDirectoryHashesMatch =
                if not targetFilesMatch || not actualTopologyMatches then
                    false
                else
                    let computedDirectories = Dictionary<DirectoryVersionId, Sha256Hash * Blake3Hash>()

                    let orderedDirectories =
                        status.Index.Values
                        |> Seq.sortByDescending (fun directory -> string directory.RelativePath)
                        |> Seq.toArray

                    let mutable matches = true

                    orderedDirectories
                    |> Seq.iter (fun directory ->
                        let childDirectories =
                            directory.Directories
                            |> Seq.map (fun childId ->
                                match computedDirectories.TryGetValue(childId) with
                                | true, (sha256Hash, blake3Hash) ->
                                    let child = status.Index[childId]

                                    Services.DirectoryVersionPreimageEntry.Directory child.RelativePath child.Size blake3Hash sha256Hash
                                | false, _ ->
                                    matches <- false

                                    Services.DirectoryVersionPreimageEntry.Directory
                                        (RelativePath "invalid")
                                        0L
                                        (Blake3Hash String.Empty)
                                        (Sha256Hash String.Empty))

                        let files =
                            directory.Files
                            |> Seq.map (fun file -> Services.DirectoryVersionPreimageEntry.File file.RelativePath file.Size file.Blake3Hash file.Sha256Hash)

                        let entries = Seq.append childDirectories files |> Seq.toArray
                        let sha256Hash = Services.computeSha256ForDirectoryEntries directory.RelativePath entries
                        let blake3Hash = Services.computeBlake3ForDirectory directory.RelativePath entries

                        if directory.Sha256Hash <> sha256Hash
                           || directory.Blake3Hash <> blake3Hash then
                            matches <- false

                        computedDirectories[directory.DirectoryVersionId] <- (sha256Hash, blake3Hash))

                    matches
                    && match computedDirectories.TryGetValue(status.RootDirectoryId) with
                       | true, (sha256Hash, blake3Hash) ->
                           sha256Hash = status.RootDirectorySha256Hash
                           && blake3Hash = status.RootDirectoryBlake3Hash
                       | false, _ -> false

            targetFilesMatch
            && actualTopologyMatches
            && actualDirectoryHashesMatch

        /// Forms the exact Branch operation and pending details from typed selection and current configuration only.
        let private branchOperation currentBranchId selection target =
            match selection with
            | WorkingDirectoryUpdateContracts.BranchSelection.Reference referenceId ->
                WorkingDirectoryUpdateContracts.Operation.branchSwitchWithSelection currentBranchId selection target
                |> Result.map (fun operation -> operation, LocalStateDb.WorkingDirectoryUpdateCompletionDetails.BranchFinalization(currentBranchId, referenceId))
            | WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion ->
                WorkingDirectoryUpdateContracts.Operation.branchSwitchWithSelection currentBranchId selection target
                |> Result.map (fun operation ->
                    operation, LocalStateDb.WorkingDirectoryUpdateCompletionDetails.BranchDirectoryVersionFinalization currentBranchId)

        /// Produces a classified rejected outcome while retaining the exact runtime reason.
        let private rejected reason =
            WorkingDirectoryUpdateContracts.Failure.create reason
            |> Result.map WorkingDirectoryUpdateContracts.Outcome.Rejected
            |> Result.defaultWith invalidOp

        /// Produces a classified incomplete outcome after actual working-tree mutation begins.
        let private incomplete reason =
            WorkingDirectoryUpdateContracts.Failure.create reason
            |> Result.map WorkingDirectoryUpdateContracts.Outcome.UpdateIncomplete
            |> Result.defaultWith invalidOp

        /// Applies the only Branch five-input local transaction through verified pending SQLite completion.
        let private runCore
            (acceptedPhase: WorkingDirectoryUpdateContracts.AcceptedBranchPhase)
            (selection: WorkingDirectoryUpdateContracts.BranchSelection)
            (resolvedGraph: ResolvedTargetGraph)
            (preparedContent: WorkingDirectoryUpdateContracts.PreparedContent)
            (correlation: DiagnosticCorrelation)
            =
            task {
                let target = ResolvedTargetGraph.target resolvedGraph
                let targetStatus = ResolvedTargetGraph.status resolvedGraph
                let manifest = WorkingDirectoryUpdateContracts.PreparedContent.manifest preparedContent
                let actionToken = WorkingDirectoryUpdateContracts.AcceptedBranchPhase.actionToken acceptedPhase
                let correlationValue = DiagnosticCorrelation.value correlation
                let mutable mutationStarted = false
                let mutable ownedMarker = None

                try
                    actionToken.ThrowIfCancellationRequested()
                    let current = Current()

                    if current.RepositoryId
                       <> WorkingDirectoryUpdateContracts.Target.repositoryId target then
                        return rejected $"[{correlationValue}] Local configuration repository changed before Working Directory Update admission."
                    else
                        match WorkingDirectoryUpdateCoordination.Scope.create current.RepositoryId current.RootDirectory with
                        | Error error -> return rejected $"[{correlationValue}] {error}"
                        | Ok scope ->
                            let! acquiredLease = WorkingDirectoryUpdateCoordination.Lease.acquire scope actionToken
                            use acquiredLease = acquiredLease
                            let dbPath = current.GraceStatusFile
                            let! revision = LocalStateDb.readLocalStatusRevisionReadOnly dbPath

                            let! currentStatusResult =
                                LocalStateDb.readCompleteStatusSnapshotReadOnly dbPath current.OwnerId current.OrganizationId current.RepositoryId

                            let currentStatus =
                                match currentStatusResult with
                                | Ok status -> status
                                | Error error -> invalidOp error

                            let! pending = LocalStateDb.readPendingWorkingDirectoryUpdateFinalization dbPath

                            if revision
                               <> WorkingDirectoryUpdateContracts.AcceptedBranchPhase.localStatusRevision acceptedPhase then
                                return rejected $"[{correlationValue}] Accepted local-status revision changed before Working Directory Update mutation."
                            elif statusFingerprint currentStatus
                                 <> WorkingDirectoryUpdateContracts.AcceptedBranchPhase.statusFingerprint acceptedPhase then
                                return
                                    rejected
                                        $"[{correlationValue}] Accepted complete local-status fingerprint changed before Working Directory Update mutation."
                            elif Option.isSome pending then
                                return rejected $"[{correlationValue}] A pending Working Directory Update finalization blocks this local transaction."
                            elif not (graphMatchesManifest targetStatus manifest) then
                                return rejected $"[{correlationValue}] Resolved target graph does not match immutable prepared-content topology."
                            else
                                match branchOperation current.BranchId selection target with
                                | Error error -> return rejected $"[{correlationValue}] {error}"
                                | Ok (operation, completionDetails) ->
                                    let! markerInspection = WorkingDirectoryUpdateCoordination.Marker.inspect scope target operation

                                    match markerInspection with
                                    | WorkingDirectoryUpdateCoordination.MarkerInspection.DifferentOperation
                                    | WorkingDirectoryUpdateCoordination.MarkerInspection.MalformedOrUnsupported
                                    | WorkingDirectoryUpdateCoordination.MarkerInspection.Unreadable ->
                                        return rejected $"[{correlationValue}] Existing Working Directory Update marker is not exact owned admission evidence."
                                    | WorkingDirectoryUpdateCoordination.MarkerInspection.Missing
                                    | WorkingDirectoryUpdateCoordination.MarkerInspection.ExactMatch ->
                                        let attempt = WorkingDirectoryUpdateContracts.AttemptToken.create ()

                                        match WorkingDirectoryUpdateCoordination.Marker.create scope attempt target operation with
                                        | Error error -> return rejected $"[{correlationValue}] {error}"
                                        | Ok marker ->
                                            do! WorkingDirectoryUpdateCoordination.Marker.write scope marker
                                            ownedMarker <- Some(scope, attempt)
                                            let! objectResult = publishObjects preparedContent current.ObjectDirectory manifest

                                            match objectResult with
                                            | Error error ->
                                                let! cleanup = WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope attempt

                                                return
                                                    match cleanup with
                                                    | WorkingDirectoryUpdateCoordination.MarkerCleanup.ExactMatchCleaned
                                                    | WorkingDirectoryUpdateCoordination.MarkerCleanup.NoMarker -> rejected $"[{correlationValue}] {error}"
                                                    | _ -> rejected $"[{correlationValue}] {error}; exact marker cleanup failed."
                                            | Ok () ->
                                                let! initialPlan = Topology.plan currentStatus manifest

                                                match initialPlan with
                                                | Topology.Rejected rejection ->
                                                    invalidOp $"Initial Working Directory Update topology rejected '{Topology.Rejection.path rejection}'."
                                                | Topology.Planned _ -> ()

                                                let finalCurrent = Current()
                                                let! finalRevision = LocalStateDb.readLocalStatusRevisionReadOnly dbPath

                                                let! finalStatusResult =
                                                    LocalStateDb.readCompleteStatusSnapshotReadOnly
                                                        dbPath
                                                        current.OwnerId
                                                        current.OrganizationId
                                                        current.RepositoryId

                                                let finalStatus =
                                                    match finalStatusResult with
                                                    | Ok status -> status
                                                    | Error error -> invalidOp error

                                                let! finalPending = LocalStateDb.readPendingWorkingDirectoryUpdateFinalization dbPath
                                                let! finalMarker = WorkingDirectoryUpdateCoordination.Marker.inspect scope target operation

                                                let! finalMarkerAttempt =
                                                    WorkingDirectoryUpdateCoordination.Marker.inspectExactAttempt scope target operation attempt

                                                if
                                                    finalCurrent.RepositoryId <> current.RepositoryId
                                                    || finalCurrent.RootDirectory
                                                       <> current.RootDirectory
                                                    || finalCurrent.GraceStatusFile
                                                       <> current.GraceStatusFile
                                                    || finalCurrent.ObjectDirectory
                                                       <> current.ObjectDirectory
                                                    || finalCurrent.GraceDirectory
                                                       <> current.GraceDirectory
                                                    || finalCurrent.BranchId <> current.BranchId
                                                    || finalRevision
                                                       <> WorkingDirectoryUpdateContracts.AcceptedBranchPhase.localStatusRevision acceptedPhase
                                                    || statusFingerprint finalStatus
                                                       <> WorkingDirectoryUpdateContracts.AcceptedBranchPhase.statusFingerprint acceptedPhase
                                                    || Option.isSome finalPending
                                                    || finalMarker
                                                       <> WorkingDirectoryUpdateCoordination.MarkerInspection.ExactMatch
                                                    || not finalMarkerAttempt
                                                    || not (graphMatchesManifest targetStatus manifest)
                                                then
                                                    let! cleanup = WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope attempt

                                                    return
                                                        match cleanup with
                                                        | WorkingDirectoryUpdateCoordination.MarkerCleanup.ExactMatchCleaned
                                                        | WorkingDirectoryUpdateCoordination.MarkerCleanup.NoMarker ->
                                                            rejected $"[{correlationValue}] Working Directory Update facts changed before the first mutation."
                                                        | _ ->
                                                            rejected
                                                                $"[{correlationValue}] Working Directory Update facts changed before mutation and exact marker cleanup failed."
                                                else
                                                    actionToken.ThrowIfCancellationRequested()
                                                    let! planResult = Topology.plan finalStatus manifest

                                                    match planResult with
                                                    | Topology.Rejected rejection ->
                                                        let! cleanup = WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope attempt
                                                        let path = Topology.Rejection.path rejection

                                                        return
                                                            match cleanup with
                                                            | WorkingDirectoryUpdateCoordination.MarkerCleanup.ExactMatchCleaned
                                                            | WorkingDirectoryUpdateCoordination.MarkerCleanup.NoMarker ->
                                                                rejected $"[{correlationValue}] Working Directory Update topology rejected '{path}'."
                                                            | _ ->
                                                                rejected
                                                                    $"[{correlationValue}] Working Directory Update topology rejected '{path}' and exact marker cleanup failed."
                                                    | Topology.Planned plan ->
                                                        let actions = Topology.Plan.actions plan |> List.toArray
                                                        let objectHashes = Dictionary<string, Sha256Hash * Blake3Hash>(StringComparer.Ordinal)

                                                        WorkingDirectoryUpdateContracts.PreparedManifest.entries manifest
                                                        |> Seq.iter (function
                                                            | WorkingDirectoryUpdateContracts.PreparedManifestEntry.File (path, sha256Hash, blake3Hash) ->
                                                                objectHashes[pathKey path] <- (sha256Hash, blake3Hash)
                                                            | WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory _ -> ())

                                                        actions
                                                        |> Array.iter (fun action ->
                                                            if applyAction current.RootDirectory current.ObjectDirectory objectHashes action then
                                                                mutationStarted <- true)

                                                        if not (verifyCompleteTargetRoot current.RootDirectory current.GraceDirectory targetStatus) then
                                                            return
                                                                incomplete
                                                                    $"[{correlationValue}] Complete target-root verification failed after working-tree mutation."
                                                        else
                                                            let! _ =
                                                                LocalStateDb.commitWorkingDirectoryUpdateCompletion
                                                                    dbPath
                                                                    targetStatus
                                                                    (targetStatus.Index.Values :> IEnumerable<LocalDirectoryVersion>)
                                                                    completionDetails
                                                                    target
                                                                    operation

                                                            match WorkingDirectoryUpdateContracts.Receipt.create target operation mutationStarted with
                                                            | Ok receipt -> return WorkingDirectoryUpdateContracts.Outcome.Updated receipt
                                                            | Error error -> return incomplete $"[{correlationValue}] {error}"
                with
                | :? OperationCanceledException when mutationStarted ->
                    return incomplete $"[{correlationValue}] Cancellation arrived after the first working-tree mutation."
                | :? OperationCanceledException ->
                    match ownedMarker with
                    | Some (scope, attempt) ->
                        let! cleanup = WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope attempt

                        return
                            match cleanup with
                            | WorkingDirectoryUpdateCoordination.MarkerCleanup.ExactMatchCleaned
                            | WorkingDirectoryUpdateCoordination.MarkerCleanup.NoMarker ->
                                rejected $"[{correlationValue}] Working Directory Update was cancelled before the first working-tree mutation."
                            | _ -> rejected $"[{correlationValue}] Working Directory Update was cancelled before mutation and exact marker cleanup failed."
                    | None -> return rejected $"[{correlationValue}] Working Directory Update was cancelled before the first working-tree mutation."
                | ex when mutationStarted -> return incomplete $"[{correlationValue}] {ex.Message}"
                | ex ->
                    match ownedMarker with
                    | Some (scope, attempt) ->
                        let! cleanup = WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope attempt

                        return
                            match cleanup with
                            | WorkingDirectoryUpdateCoordination.MarkerCleanup.ExactMatchCleaned
                            | WorkingDirectoryUpdateCoordination.MarkerCleanup.NoMarker -> rejected $"[{correlationValue}] {ex.Message}"
                            | _ -> rejected $"[{correlationValue}] {ex.Message}; exact marker cleanup failed."
                    | None -> return rejected $"[{correlationValue}] {ex.Message}"
            }

        /// Disposes immutable prepared bytes exactly once after every five-input transaction path completes.
        let run acceptedPhase selection resolvedGraph preparedContent correlation =
            task {
                let! outcome = runCore acceptedPhase selection resolvedGraph preparedContent correlation
                WorkingDirectoryUpdateContracts.PreparedContent.dispose preparedContent
                return outcome
            }
