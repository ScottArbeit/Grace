namespace Grace.CLI.Command

open System
open System.Collections.Generic
open System.IO
open System.Threading
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

        /// Returns whether one existing file has the prepared BLAKE3 bytes without recomputing SHA-256 metadata.
        let private hasVerifiedTargetBytes fullPath targetBlake3 =
            File.ReadAllBytes(fullPath)
            |> ContentAddress.computeBlake3Hex
            |> Blake3Hash
            |> (=) targetBlake3

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

        /// Finds the first descendant that is not an exact prepared target entry during retained-operation adoption.
        let private firstUnsafeExactTargetDescendant
            classifierInput
            (targetFiles: Dictionary<string, Sha256Hash * Blake3Hash * RelativePath>)
            (targetDirectories: Dictionary<string, RelativePath>)
            localRoot
            rootDirectory
            =
            let rec visit fullPath =
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

                    let key = pathKey childRelative

                    if child :? DirectoryInfo then
                        match Services.classifyRepositoryPath classifierInput Services.RepositoryPathKind.DirectoryPath child.FullName with
                        | Services.RepositoryPathClassification.Eligible when targetDirectories.ContainsKey key -> conflict <- visit child.FullName
                        | Services.RepositoryPathClassification.Eligible -> conflict <- Some { Path = childRelative; Classification = Untracked }
                        | _ -> conflict <- Some { Path = childRelative; Classification = Ignored }
                    else
                        match Services.classifyRepositoryPath classifierInput Services.RepositoryPathKind.FilePath child.FullName with
                        | Services.RepositoryPathClassification.Eligible ->
                            match targetFiles.TryGetValue key with
                            | true, (_, blake3Hash, _) when hasVerifiedTargetBytes child.FullName blake3Hash -> ()
                            | _ -> conflict <- Some { Path = childRelative; Classification = Untracked }
                        | _ -> conflict <- Some { Path = childRelative; Classification = Ignored }

                    index <- index + 1

                conflict

            visit rootDirectory

        /// Classifies every target and removable tracked blocker without entering the asynchronous transaction workflow.
        let private planSynchronously allowExactAdoption (currentStatus: GraceStatus) manifest =
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
                                | Error value when
                                    allowExactAdoption
                                    && value.Classification = Untracked
                                    ->
                                    match firstUnsafeExactTargetDescendant classifier targetFiles targetDirectories scanInput.RootDirectory fullPath with
                                    | Some descendant -> rejection <- Some descendant
                                    | None -> ()
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
                                | Error value when
                                    allowExactAdoption
                                    && value.Classification = Untracked
                                    ->
                                    let _, targetBlake3, _ = targetFile

                                    if not (hasVerifiedTargetBytes fullPath targetBlake3) then
                                        rejection <- Some value
                                | Error value -> rejection <- Some value
                                | Ok "tracked-file" ->
                                    let targetSha256, targetBlake3, _ = targetFile
                                    let tracked = trackedFiles[pathKey targetPath]

                                    if
                                        tracked.Sha256Hash <> targetSha256
                                        || tracked.Blake3Hash <> targetBlake3
                                        || not (hasVerifiedTargetBytes fullPath targetBlake3)
                                    then
                                        addCopy targetPath
                                | Ok _ when allowExactAdoption ->
                                    let _, targetBlake3, _ = targetFile

                                    if not (hasVerifiedTargetBytes fullPath targetBlake3) then
                                        rejection <- Some { Path = targetPath; Classification = Untracked }
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
        let plan (currentStatus: GraceStatus) manifest = task { return planSynchronously false currentStatus manifest }

        /// Reconciles exact target bytes produced by a retained exact-operation marker without weakening fresh admission.
        let planExactAdoption (currentStatus: GraceStatus) manifest = task { return planSynchronously true currentStatus manifest }

    /// Applies immutable prepared content through an opaque verified root without deciding selection finalization.
    module internal LocalApplication =
        /// Injects deterministic failures at the finite effect boundaries owned by the tracer tests.
        type FailurePoint =
            | BeforeMutation
            | AfterObjectPublication
            | DuringApplication
            | BeforeCommit
            | AfterCommit
            | MarkerCleanup
            | BeforeTerminalRecording

        /// Supplies only deterministic test controls; production uses `none`.
        type FailureInjection = { ThrowAt: FailurePoint -> unit; DeleteMarker: string -> unit; BeforeAction: int -> unit }

        /// Uses normal production effects without injected failures.
        let none = { ThrowAt = ignore; DeleteMarker = File.Delete; BeforeAction = ignore }

        /// Converts a non-empty reason into the private failure contract.
        let failure reason =
            WorkingDirectoryUpdateContracts.Failure.create reason
            |> function
                | Ok value -> value
                | Error error -> invalidOp error

        /// Maps a normalized repository-relative path beneath the configured working root.
        let private workingPath root (relativePath: RelativePath) =
            Path.Combine(
                root,
                (string relativePath)
                    .Replace('/', Path.DirectorySeparatorChar)
            )

        /// Atomically publishes verified prepared bytes at one destination.
        let private publishPreparedFile preparedContent relativePath (destination: string) =
            task {
                match WorkingDirectoryUpdateContracts.PreparedContent.openRead preparedContent relativePath with
                | Error error -> return invalidOp error
                | Ok source ->
                    use source = source

                    Directory.CreateDirectory(Path.GetDirectoryName(destination))
                    |> ignore

                    let temporary = destination + $".wdu-{Guid.NewGuid():N}.tmp"

                    try
                        do!
                            task {
                                use output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                                do! source.CopyToAsync(output)
                                do! output.FlushAsync()
                                output.Flush(true)
                            }

                        File.Move(temporary, destination, true)
                    finally
                        if File.Exists(temporary) then File.Delete(temporary)
            }

        /// Verifies every target path and file BLAKE3 against the complete prepared manifest.
        let verifyTarget root manifest =
            task {
                let mutable error = None

                for entry in WorkingDirectoryUpdateContracts.PreparedManifest.entries manifest do
                    if Option.isNone error then
                        match entry with
                        | WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory path ->
                            if not (Directory.Exists(workingPath root path)) then
                                error <- Some $"Target directory '{path}' is missing after application."
                        | WorkingDirectoryUpdateContracts.PreparedManifestEntry.File (path, _, blake3Hash) ->
                            let fullPath = workingPath root path

                            if not (File.Exists(fullPath)) then
                                error <- Some $"Target file '{path}' is missing after application."
                            else
                                let bytes = File.ReadAllBytes(fullPath)

                                let actualBlake3 = Blake3Hash(ContentAddress.computeBlake3Hex bytes)

                                if actualBlake3 <> blake3Hash then
                                    error <- Some $"Target file '{path}' failed final BLAKE3 verification."

                return error
            }

        /// Verifies the complete relevant topology expected at one prefix of the ordered action sequence.
        let private verifyPlanPrefix root manifest (acceptedStatus: GraceStatus) plan completedCount =
            let actions = Topology.Plan.actions plan |> List.toArray
            let mutable error = None

            let verifyFile path blake3Hash =
                let fullPath = workingPath root path

                if
                    not (File.Exists(fullPath))
                    || Directory.Exists(fullPath)
                then
                    Some $"Expected verified file '{path}' is not present."
                else
                    let bytes = File.ReadAllBytes(fullPath)

                    let actualBlake3 = Blake3Hash(ContentAddress.computeBlake3Hex bytes)

                    if actualBlake3 = blake3Hash then
                        None
                    else
                        Some $"Expected verified file '{path}' changed during application."

            let targetFiles = Dictionary<string, Blake3Hash>(StringComparer.OrdinalIgnoreCase)
            let acceptedFiles = Dictionary<string, Blake3Hash>(StringComparer.OrdinalIgnoreCase)
            let actionPaths = HashSet<string>(StringComparer.OrdinalIgnoreCase)

            for directory in acceptedStatus.Index.Values do
                for file in directory.Files do
                    acceptedFiles[string file.RelativePath] <- file.Blake3Hash

            for entry in WorkingDirectoryUpdateContracts.PreparedManifest.entries manifest do
                match entry with
                | WorkingDirectoryUpdateContracts.PreparedManifestEntry.File (path, _, blake3Hash) -> targetFiles[string path] <- blake3Hash
                | _ -> ()

            actions
            |> Array.iter (function
                | Topology.RemoveTrackedFile path
                | Topology.RemoveTrackedDirectory path
                | Topology.EnsureDirectory path
                | Topology.CopyVerifiedFile path -> actionPaths.Add(string path) |> ignore)

            for index = 0 to actions.Length - 1 do
                if Option.isNone error then
                    let completed = index < completedCount

                    let pathOf =
                        function
                        | Topology.RemoveTrackedFile path
                        | Topology.RemoveTrackedDirectory path
                        | Topology.EnsureDirectory path
                        | Topology.CopyVerifiedFile path -> path

                    let supersededByCompletedAction path =
                        actions
                        |> Array.mapi (fun candidateIndex candidate -> candidateIndex, candidate)
                        |> Array.exists (fun (candidateIndex, candidate) ->
                            candidateIndex > index
                            && candidateIndex < completedCount
                            && String.Equals(string (pathOf candidate), string path, StringComparison.OrdinalIgnoreCase))

                    match actions[index] with
                    | Topology.RemoveTrackedFile path ->
                        let fullPath = workingPath root path

                        if completed
                           && not (supersededByCompletedAction path)
                           && (File.Exists(fullPath)
                               || Directory.Exists(fullPath)) then
                            error <- Some $"Removed predecessor '{path}' reappeared."
                        elif not completed
                             && (not (File.Exists(fullPath))
                                 || Directory.Exists(fullPath)) then
                            error <- Some $"Tracked file '{path}' changed before removal."
                        elif not completed then
                            error <- verifyFile path acceptedFiles[string path]
                    | Topology.RemoveTrackedDirectory path ->
                        let fullPath = workingPath root path

                        if completed
                           && not (supersededByCompletedAction path)
                           && (File.Exists(fullPath)
                               || Directory.Exists(fullPath)) then
                            error <- Some $"Removed predecessor '{path}' reappeared."
                        elif not completed
                             && (not (Directory.Exists(fullPath))
                                 || File.Exists(fullPath)) then
                            error <- Some $"Tracked directory '{path}' changed before removal."
                    | Topology.EnsureDirectory path ->
                        let fullPath = workingPath root path

                        if completed
                           && (not (Directory.Exists(fullPath))
                               || File.Exists(fullPath)) then
                            error <- Some $"Created directory '{path}' changed during application."
                        elif not completed && File.Exists(fullPath) then
                            error <- Some $"Directory target '{path}' became a file before creation."
                    | Topology.CopyVerifiedFile path ->
                        let fullPath = workingPath root path

                        if completed then
                            error <- verifyFile path targetFiles[string path]
                        elif Directory.Exists(fullPath) then
                            error <- Some $"File target '{path}' became a directory before copy."
                        elif
                            File.Exists(fullPath)
                            && acceptedFiles.ContainsKey(string path)
                        then
                            error <- verifyFile path acceptedFiles[string path]

            for entry in WorkingDirectoryUpdateContracts.PreparedManifest.entries manifest do
                if Option.isNone error then
                    match entry with
                    | WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory path when not (actionPaths.Contains(string path)) ->
                        let fullPath = workingPath root path

                        if
                            not (Directory.Exists(fullPath))
                            || File.Exists(fullPath)
                        then
                            error <- Some $"Retained target directory '{path}' changed during application."
                    | WorkingDirectoryUpdateContracts.PreparedManifestEntry.File (path, _, blake3Hash) when not (actionPaths.Contains(string path)) ->
                        error <- verifyFile path blake3Hash
                    | _ -> ()

            error

        /// Applies one already ordered topology plan while checking the expected path kind before each effect.
        let private applyPlan root preparedContent acceptedStatus failureInjection beginMutation plan =
            task {
                let mutable actionIndex = 0

                for action in Topology.Plan.actions plan do
                    failureInjection.BeforeAction actionIndex

                    match verifyPlanPrefix root (WorkingDirectoryUpdateContracts.PreparedContent.manifest preparedContent) acceptedStatus plan actionIndex with
                    | Some error -> invalidOp error
                    | None -> ()

                    beginMutation ()

                    match action with
                    | Topology.RemoveTrackedFile path ->
                        let fullPath = workingPath root path

                        if
                            not (File.Exists(fullPath))
                            || Directory.Exists(fullPath)
                        then
                            invalidOp $"Expected tracked file '{path}' changed before removal."

                        File.Delete(fullPath)
                    | Topology.RemoveTrackedDirectory path ->
                        let fullPath = workingPath root path

                        if
                            not (Directory.Exists(fullPath))
                            || File.Exists(fullPath)
                        then
                            invalidOp $"Expected tracked directory '{path}' changed before removal."

                        Directory.Delete(fullPath, false)
                    | Topology.EnsureDirectory path ->
                        let fullPath = workingPath root path

                        if File.Exists(fullPath) then
                            invalidOp $"Expected directory path '{path}' became a file before creation."

                        Directory.CreateDirectory(fullPath) |> ignore
                    | Topology.CopyVerifiedFile path ->
                        let fullPath = workingPath root path

                        if Directory.Exists(fullPath) then
                            invalidOp $"Expected file path '{path}' became a directory before copy."

                        do! publishPreparedFile preparedContent path fullPath

                    actionIndex <- actionIndex + 1
                    failureInjection.ThrowAt DuringApplication

                match verifyPlanPrefix root (WorkingDirectoryUpdateContracts.PreparedContent.manifest preparedContent) acceptedStatus plan actionIndex with
                | Some error -> invalidOp error
                | None -> ()
            }

        /// Carries the terminal SQLite inputs after the working tree has been fully verified under the held lease.
        type VerifiedLocalRoot = private VerifiedLocalRoot of targetStatus: GraceStatus * objectMetadata: LocalDirectoryVersion array * bytesChanged: bool

        /// Names the only outcomes permitted before caller-specific SQLite completion.
        type Outcome =
            | Rejected of WorkingDirectoryUpdateContracts.Failure
            | UpdateIncomplete of WorkingDirectoryUpdateContracts.Failure
            | Verified of VerifiedLocalRoot

        /// Exposes verified-root facts only to the caller that retains the matching WDU lease.
        module VerifiedLocalRoot =
            /// Gets the complete target status accepted for the caller's later local completion transaction.
            let targetStatus (VerifiedLocalRoot (targetStatus, _, _)) = targetStatus

            /// Gets the verified object metadata for the caller's later local completion transaction.
            let objectMetadata (VerifiedLocalRoot (_, objectMetadata, _)) = objectMetadata

            /// Gets whether application changed bytes or adopted a retained exact operation.
            let bytesChanged (VerifiedLocalRoot (_, _, bytesChanged)) = bytesChanged

        /// Verifies one atomically published object-cache file against its prepared BLAKE3 bytes.
        let private verifyPublishedObject path blake3Hash =
            if not (File.Exists(path)) || Directory.Exists(path) then
                Error $"Published object '{path}' is missing or is not a file."
            else
                let bytes = File.ReadAllBytes(path)

                let actualBlake3 = Blake3Hash(ContentAddress.computeBlake3Hex bytes)

                if actualBlake3 = blake3Hash then
                    Ok()
                else
                    Error $"Published object '{path}' failed BLAKE3 verification."

        /// Publishes every required prepared object before any mutable local admission fact can authorize application.
        let private publishObjects
            (preparedContent: WorkingDirectoryUpdateContracts.PreparedContent)
            (objectMetadata: LocalDirectoryVersion array)
            (failureInjection: FailureInjection)
            =
            task {
                let mutable error = None
                let mutable directoryIndex = 0

                while directoryIndex < objectMetadata.Length
                      && Option.isNone error do
                    let directory = objectMetadata[directoryIndex]
                    let files = directory.Files |> Seq.toArray
                    let mutable fileIndex = 0

                    while fileIndex < files.Length && Option.isNone error do
                        let file = files[fileIndex]

                        let objectPath =
                            Path.Combine(
                                Current().ObjectDirectory,
                                string file.RelativePath,
                                Services.getLocalObjectCacheFileName file.RelativePath file.Sha256Hash file.Blake3Hash
                            )

                        try
                            do! publishPreparedFile preparedContent file.RelativePath objectPath
                            failureInjection.ThrowAt AfterObjectPublication

                            match verifyPublishedObject objectPath file.Blake3Hash with
                            | Ok () -> ()
                            | Error publishError -> error <- Some publishError
                        with
                        | ex -> error <- Some ex.Message

                        fileIndex <- fileIndex + 1

                    directoryIndex <- directoryIndex + 1

                return error
            }

        /// Compares the full rooted status identity used to reject stale post-publication planning facts.
        let internal statusFingerprintMatches (accepted: GraceStatus) (fresh: GraceStatus) =
            let fileMatches (left: LocalFileVersion) (right: LocalFileVersion) =
                left.RelativePath = right.RelativePath
                && left.Sha256Hash = right.Sha256Hash
                && left.Blake3Hash = right.Blake3Hash
                && left.IsBinary = right.IsBinary
                && left.Size = right.Size
                && left.UploadedToObjectStorage = right.UploadedToObjectStorage

            let directoryMatches (left: LocalDirectoryVersion) (right: LocalDirectoryVersion) =
                left.DirectoryVersionId = right.DirectoryVersionId
                && left.OwnerId = right.OwnerId
                && left.OrganizationId = right.OrganizationId
                && left.RepositoryId = right.RepositoryId
                && left.RelativePath = right.RelativePath
                && left.Sha256Hash = right.Sha256Hash
                && left.Blake3Hash = right.Blake3Hash
                && left.Size = right.Size
                && Seq.forall2 (=) left.Directories right.Directories
                && left.Files.Count = right.Files.Count
                && Seq.forall2 fileMatches left.Files right.Files

            accepted.RootDirectoryId = fresh.RootDirectoryId
            && accepted.RootDirectorySha256Hash = fresh.RootDirectorySha256Hash
            && accepted.RootDirectoryBlake3Hash = fresh.RootDirectoryBlake3Hash
            && accepted.Index.Count = fresh.Index.Count
            && (accepted.Index
                |> Seq.forall (fun entry ->
                    match fresh.Index.TryGetValue entry.Key with
                    | true, directory -> directoryMatches entry.Value directory
                    | false, _ -> false))

        /// Removes only the marker token created for this invocation before any verified-root transition.
        let private rejectAndClean scope attemptToken reason =
            task {
                let! _ = WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope attemptToken
                return Rejected(failure reason)
            }

        /// Performs final admission and application from only facts reread after object publication.
        let run
            request
            acceptedStatus
            targetStatus
            (objectMetadata: LocalDirectoryVersion array)
            (manifest: WorkingDirectoryUpdateContracts.PreparedManifest)
            (root: string)
            (dbPath: string)
            (acceptedRevision: int64)
            (scope: WorkingDirectoryUpdateCoordination.Scope)
            (attemptToken: WorkingDirectoryUpdateContracts.AttemptToken)
            (exactAdoption: bool)
            (cancellationToken: CancellationToken)
            (failureInjection: FailureInjection)
            =
            task {
                let target = WorkingDirectoryUpdateContracts.Request.target request
                let operation = WorkingDirectoryUpdateContracts.Request.operation request
                let preparedContent = WorkingDirectoryUpdateContracts.Request.preparedContent request
                let mutable mutationStarted = false

                try
                    match! publishObjects preparedContent objectMetadata failureInjection with
                    | Some publishError -> return! rejectAndClean scope attemptToken publishError
                    | None ->
                        let! revisionBefore = LocalStateDb.readLocalStatusRevisionReadOnly dbPath

                        let! freshStatusResult =
                            LocalStateDb.readCompleteStatusSnapshotReadOnly
                                dbPath
                                (Current().OwnerId)
                                (Current().OrganizationId)
                                (WorkingDirectoryUpdateContracts.Target.repositoryId target)

                        let! revisionAfter = LocalStateDb.readLocalStatusRevisionReadOnly dbPath
                        let! completion = LocalStateDb.readWorkingDirectoryUpdateCompletion dbPath target operation
                        let! markerInspection = WorkingDirectoryUpdateCoordination.Marker.inspect scope target operation
                        let! markerEvidence = WorkingDirectoryUpdateCoordination.Marker.readEvidence scope

                        let markerMatchesAttempt =
                            match markerInspection, markerEvidence with
                            | WorkingDirectoryUpdateCoordination.MarkerInspection.ExactMatch, Some evidence ->
                                evidence.AttemptToken = WorkingDirectoryUpdateContracts.AttemptToken.value attemptToken
                            | _ -> false

                        match freshStatusResult with
                        | Error error -> return! rejectAndClean scope attemptToken error
                        | Ok freshStatus when
                            revisionBefore <> acceptedRevision
                            || revisionAfter <> acceptedRevision
                            ->
                            return! rejectAndClean scope attemptToken "Local status changed while prepared objects were being published."
                        | Ok freshStatus when not (statusFingerprintMatches acceptedStatus freshStatus) ->
                            return! rejectAndClean scope attemptToken "Local status fingerprint changed while prepared objects were being published."
                        | Ok _ when completion.IsSome ->
                            return! rejectAndClean scope attemptToken "Working Directory Update completion changed while prepared objects were being published."
                        | Ok _ when not markerMatchesAttempt ->
                            return!
                                rejectAndClean
                                    scope
                                    attemptToken
                                    "Working Directory Update marker evidence changed while prepared objects were being published."
                        | Ok freshStatus ->
                            let planning =
                                if exactAdoption then
                                    Topology.planExactAdoption freshStatus manifest
                                else
                                    Topology.plan freshStatus manifest

                            match! planning with
                            | Topology.Rejected rejection ->
                                return!
                                    rejectAndClean
                                        scope
                                        attemptToken
                                        $"Path '{Topology.Rejection.path rejection}' is {Topology.Rejection.classification rejection}."
                            | Topology.Planned plan ->
                                failureInjection.ThrowAt BeforeMutation
                                cancellationToken.ThrowIfCancellationRequested()
                                do! applyPlan root preparedContent freshStatus failureInjection (fun () -> mutationStarted <- true) plan

                                match! verifyTarget root manifest with
                                | Some verifyError -> return UpdateIncomplete(failure verifyError)
                                | None ->
                                    let changed =
                                        exactAdoption
                                        || not (Topology.Plan.actions plan |> List.isEmpty)

                                    return Verified(VerifiedLocalRoot(targetStatus, objectMetadata, changed))
                with
                | :? OperationCanceledException as ex when not mutationStarted -> return! rejectAndClean scope attemptToken ex.Message
                | ex when mutationStarted -> return UpdateIncomplete(failure ex.Message)
                | ex -> return! rejectAndClean scope attemptToken ex.Message
            }

    /// Holds the established DirectoryVersion transaction behind the five-input Branch composition seam.
    module private BranchTransaction =
        /// Preserves the existing Branch test-facing effect-boundary type while the application stage remains private.
        type FailurePoint = LocalApplication.FailurePoint

        /// Preserves the existing Branch test-facing deterministic effect controls.
        type FailureInjection = LocalApplication.FailureInjection

        /// Uses normal effects while retaining the Branch test-facing injection name.
        let none = LocalApplication.none

        /// Preserves existing Branch test-facing failure-point names.
        let BeforeMutation = LocalApplication.BeforeMutation

        /// Preserves existing Branch test-facing failure-point names.
        let AfterObjectPublication = LocalApplication.AfterObjectPublication

        /// Preserves existing Branch test-facing failure-point names.
        let DuringApplication = LocalApplication.DuringApplication

        /// Preserves existing Branch test-facing failure-point names.
        let BeforeCommit = LocalApplication.BeforeCommit

        /// Preserves existing Branch test-facing failure-point names.
        let AfterCommit = LocalApplication.AfterCommit

        /// Preserves existing Branch test-facing failure-point names.
        let MarkerCleanup = LocalApplication.MarkerCleanup

        /// Injects a failure immediately before Reference terminal recording in focused tests.
        let BeforeTerminalRecording = LocalApplication.BeforeTerminalRecording

        /// Creates the private outcome failure consumed by the preserved DirectoryVersion terminal behavior.
        let private failure = LocalApplication.failure

        /// Reuses complete target verification for terminal exact replay without changing its public outcome.
        let private verifyTarget = LocalApplication.verifyTarget

        /// Runs one complete DirectoryVersion-selected Branch update without changing Branch identity.
        let private runAtRevisionCoreImpl
            (request: WorkingDirectoryUpdateContracts.Request)
            (currentStatus: GraceStatus)
            (targetStatus: GraceStatus)
            (objectMetadata: LocalDirectoryVersion array)
            (manifest: WorkingDirectoryUpdateContracts.PreparedManifest)
            (root: string)
            (dbPath: string)
            (acceptedRevision: int64)
            (cancellationToken: CancellationToken)
            (failureInjection: FailureInjection)
            =
            task {
                let target = WorkingDirectoryUpdateContracts.Request.target request
                let operation = WorkingDirectoryUpdateContracts.Request.operation request
                let preparedContent = WorkingDirectoryUpdateContracts.Request.preparedContent request
                let attemptToken = WorkingDirectoryUpdateContracts.AttemptToken.create ()
                let mutable mutationStarted = false
                let mutable verifiedRoot = false
                let mutable verifiedBytesChanged = false
                let mutable committed = false

                try
                    match! LocalStateDb.readWorkingDirectoryUpdateCompletion dbPath target operation with
                    | Some LocalStateDb.WorkingDirectoryUpdateCompletion.Terminal ->
                        match WorkingDirectoryUpdateCoordination.Scope.create (WorkingDirectoryUpdateContracts.Target.repositoryId target) root with
                        | Error error -> return WorkingDirectoryUpdateContracts.Outcome.Rejected(failure error)
                        | Ok scope ->
                            try
                                use! lease = WorkingDirectoryUpdateCoordination.Lease.acquire scope cancellationToken

                                match! LocalStateDb.readWorkingDirectoryUpdateCompletion dbPath target operation with
                                | Some LocalStateDb.WorkingDirectoryUpdateCompletion.Terminal ->
                                    let! freshStatusResult =
                                        LocalStateDb.readCompleteStatusSnapshotReadOnly
                                            dbPath
                                            (Current().OwnerId)
                                            (Current().OrganizationId)
                                            (WorkingDirectoryUpdateContracts.Target.repositoryId target)

                                    match freshStatusResult with
                                    | Error error -> return WorkingDirectoryUpdateContracts.Outcome.Rejected(failure error)
                                    | Ok freshStatus when
                                        freshStatus.Index.Count
                                        <> targetStatus.Index.Count
                                        ->
                                        return
                                            WorkingDirectoryUpdateContracts.Outcome.Rejected(
                                                failure "Terminal update facts no longer describe the complete local status."
                                            )
                                    | Ok freshStatus ->
                                        let statusMatchesTarget =
                                            targetStatus.Index
                                            |> Seq.forall (fun pair ->
                                                match freshStatus.Index.TryGetValue pair.Key with
                                                | true, actual -> actual.DirectoryVersionId = pair.Value.DirectoryVersionId
                                                | _ -> false)

                                        if not statusMatchesTarget then
                                            return
                                                WorkingDirectoryUpdateContracts.Outcome.Rejected(
                                                    failure "Terminal update facts no longer describe the complete local status."
                                                )
                                        else
                                            match! verifyTarget root manifest with
                                            | Some error -> return WorkingDirectoryUpdateContracts.Outcome.Rejected(failure error)
                                            | None ->
                                                let receipt =
                                                    WorkingDirectoryUpdateContracts.Receipt.create target operation false
                                                    |> Result.defaultWith invalidOp

                                                return WorkingDirectoryUpdateContracts.Outcome.Unchanged receipt
                                | _ ->
                                    return
                                        WorkingDirectoryUpdateContracts.Outcome.Rejected(
                                            failure "Terminal update facts were superseded while waiting for the Working Directory Update lease."
                                        )
                            with
                            | :? OperationCanceledException as ex -> return WorkingDirectoryUpdateContracts.Outcome.Rejected(failure ex.Message)
                    | Some LocalStateDb.WorkingDirectoryUpdateCompletion.Pending ->
                        return WorkingDirectoryUpdateContracts.Outcome.Rejected(failure "DirectoryVersion selection cannot adopt pending finalization state.")
                    | None ->
                        match WorkingDirectoryUpdateCoordination.Scope.create (WorkingDirectoryUpdateContracts.Target.repositoryId target) root with
                        | Error error -> return WorkingDirectoryUpdateContracts.Outcome.Rejected(failure error)
                        | Ok scope ->
                            use! lease = WorkingDirectoryUpdateCoordination.Lease.acquire scope cancellationToken
                            let! revisionBeforeRead = LocalStateDb.readLocalStatusRevisionReadOnly dbPath

                            let! freshStatusResult =
                                LocalStateDb.readCompleteStatusSnapshotReadOnly
                                    dbPath
                                    (Current().OwnerId)
                                    (Current().OrganizationId)
                                    (WorkingDirectoryUpdateContracts.Target.repositoryId target)

                            let! revisionAfterRead = LocalStateDb.readLocalStatusRevisionReadOnly dbPath

                            let gateError =
                                if revisionBeforeRead <> acceptedRevision
                                   || revisionAfterRead <> acceptedRevision then
                                    Some "Local status changed while the selected DirectoryVersion was being prepared."
                                else
                                    match freshStatusResult with
                                    | Ok _ -> None
                                    | Error error -> Some error

                            let freshStatus =
                                freshStatusResult
                                |> Result.defaultValue currentStatus

                            let! markerInspection =
                                match gateError with
                                | Some _ -> Task.FromResult WorkingDirectoryUpdateCoordination.MarkerInspection.MalformedOrUnsupported
                                | None -> WorkingDirectoryUpdateCoordination.Marker.inspect scope target operation

                            let! admittedMarkerInspection =
                                task {
                                    match markerInspection with
                                    | WorkingDirectoryUpdateCoordination.MarkerInspection.DifferentOperation ->
                                        match! WorkingDirectoryUpdateCoordination.Marker.readEvidence scope with
                                        | Some evidence ->
                                            let! isTerminal = LocalStateDb.hasTerminalWorkingDirectoryUpdateEvidence dbPath evidence.OperationId evidence.Target

                                            if isTerminal then
                                                let! cleanup =
                                                    WorkingDirectoryUpdateCoordination.Marker.tryRemoveTerminalEvidenceWithDelete
                                                        scope
                                                        evidence.OperationId
                                                        evidence.Target
                                                        File.Delete

                                                return
                                                    if cleanup = WorkingDirectoryUpdateCoordination.MarkerCleanup.ExactMatchCleaned then
                                                        WorkingDirectoryUpdateCoordination.MarkerInspection.Missing
                                                    else
                                                        markerInspection
                                            else
                                                return markerInspection
                                        | None -> return markerInspection
                                    | _ -> return markerInspection
                                }

                            match admittedMarkerInspection with
                            | WorkingDirectoryUpdateCoordination.MarkerInspection.MalformedOrUnsupported
                            | WorkingDirectoryUpdateCoordination.MarkerInspection.Unreadable
                            | WorkingDirectoryUpdateCoordination.MarkerInspection.DifferentOperation ->
                                return
                                    WorkingDirectoryUpdateContracts.Outcome.Rejected(
                                        failure (
                                            gateError
                                            |> Option.defaultValue $"Working Directory Update marker evidence is {markerInspection}."
                                        )
                                    )
                            | WorkingDirectoryUpdateCoordination.MarkerInspection.Missing
                            | WorkingDirectoryUpdateCoordination.MarkerInspection.ExactMatch ->
                                match LocalStateDb.validateCompleteStatusTree freshStatus, LocalStateDb.validateCompleteStatusTree targetStatus with
                                | Error error, _
                                | _, Error error -> return WorkingDirectoryUpdateContracts.Outcome.Rejected(failure error)
                                | Ok (), Ok () ->
                                    let marker =
                                        WorkingDirectoryUpdateCoordination.Marker.create scope attemptToken target operation
                                        |> Result.defaultWith invalidOp

                                    do! WorkingDirectoryUpdateCoordination.Marker.write scope marker

                                    let exactAdoption = admittedMarkerInspection = WorkingDirectoryUpdateCoordination.MarkerInspection.ExactMatch

                                    match!
                                        LocalApplication.run
                                            request
                                            freshStatus
                                            targetStatus
                                            objectMetadata
                                            manifest
                                            root
                                            dbPath
                                            acceptedRevision
                                            scope
                                            attemptToken
                                            exactAdoption
                                            cancellationToken
                                            failureInjection
                                        with
                                    | LocalApplication.Rejected error -> return WorkingDirectoryUpdateContracts.Outcome.Rejected error
                                    | LocalApplication.UpdateIncomplete error -> return WorkingDirectoryUpdateContracts.Outcome.UpdateIncomplete error
                                    | LocalApplication.Verified localRoot ->
                                        verifiedRoot <- true
                                        verifiedBytesChanged <- LocalApplication.VerifiedLocalRoot.bytesChanged localRoot

                                        let! _ =
                                            LocalStateDb.commitWorkingDirectoryUpdateCompletionWithBeforeCommit
                                                dbPath
                                                (LocalApplication.VerifiedLocalRoot.targetStatus localRoot)
                                                (LocalApplication.VerifiedLocalRoot.objectMetadata localRoot)
                                                (LocalStateDb.WorkingDirectoryUpdateCompletionDetails.BranchDirectoryVersionFinalization(
                                                    WorkingDirectoryUpdateContracts.Target.branchId target
                                                ))
                                                target
                                                operation
                                                (fun () -> failureInjection.ThrowAt BeforeCommit)

                                        committed <- true
                                        failureInjection.ThrowAt AfterCommit
                                        do! WorkingDirectoryUpdateCoordination.Sidecar.write scope operation
                                        failureInjection.ThrowAt MarkerCleanup

                                        let! _ =
                                            WorkingDirectoryUpdateCoordination.Marker.tryRemoveTerminalEvidenceWithDelete
                                                scope
                                                (WorkingDirectoryUpdateContracts.Operation.value operation)
                                                (WorkingDirectoryUpdateContracts.Target.canonical target)
                                                failureInjection.DeleteMarker

                                        let receipt =
                                            WorkingDirectoryUpdateContracts.Receipt.create
                                                target
                                                operation
                                                (LocalApplication.VerifiedLocalRoot.bytesChanged localRoot)
                                            |> Result.defaultWith invalidOp

                                        return
                                            if WorkingDirectoryUpdateContracts.Receipt.bytesChanged receipt then
                                                WorkingDirectoryUpdateContracts.Outcome.Updated receipt
                                            else
                                                WorkingDirectoryUpdateContracts.Outcome.Unchanged receipt

                with
                | ex when committed ->
                    let receipt =
                        WorkingDirectoryUpdateContracts.Receipt.create target operation verifiedBytesChanged
                        |> Result.defaultWith invalidOp

                    return
                        if verifiedBytesChanged then
                            WorkingDirectoryUpdateContracts.Outcome.Updated receipt
                        else
                            WorkingDirectoryUpdateContracts.Outcome.Unchanged receipt
                | ex when verifiedRoot -> return WorkingDirectoryUpdateContracts.Outcome.UpdateIncomplete(failure ex.Message)
                | ex when mutationStarted -> return WorkingDirectoryUpdateContracts.Outcome.UpdateIncomplete(failure ex.Message)
                | ex -> return WorkingDirectoryUpdateContracts.Outcome.Rejected(failure ex.Message)
            }

        /// Disposes immutable prepared bytes after every terminal outcome without changing the local-application result.
        let private runAtRevisionCore
            request
            currentStatus
            targetStatus
            objectMetadata
            manifest
            root
            dbPath
            acceptedRevision
            cancellationToken
            failureInjection
            =
            task {
                let preparedContent = WorkingDirectoryUpdateContracts.Request.preparedContent request

                try
                    return!
                        runAtRevisionCoreImpl
                            request
                            currentStatus
                            targetStatus
                            objectMetadata
                            manifest
                            root
                            dbPath
                            acceptedRevision
                            cancellationToken
                            failureInjection
                finally
                    WorkingDirectoryUpdateContracts.PreparedContent.dispose preparedContent
            }

        /// Projects cancellation before lease admission as a non-mutating rejected Working Directory Update.
        let runAtRevision request currentStatus targetStatus objectMetadata manifest root dbPath acceptedRevision cancellationToken failureInjection =
            task {
                try
                    return!
                        runAtRevisionCore
                            request
                            currentStatus
                            targetStatus
                            objectMetadata
                            manifest
                            root
                            dbPath
                            acceptedRevision
                            cancellationToken
                            failureInjection
                with
                | :? OperationCanceledException as ex -> return WorkingDirectoryUpdateContracts.Outcome.Rejected(failure ex.Message)
            }

        /// Runs a prepared update against the local-status revision current at direct invocation time.
        let run request currentStatus targetStatus objectMetadata manifest root dbPath cancellationToken failureInjection =
            task {
                let! acceptedRevision = LocalStateDb.readLocalStatusRevisionReadOnly dbPath

                return! runAtRevision request currentStatus targetStatus objectMetadata manifest root dbPath acceptedRevision cancellationToken failureInjection
            }

    /// Names the private non-durable local completion evidence returned by the sole Branch transaction.
    type internal LocalCompletion =
        | DirectoryVersionTerminal of WorkingDirectoryUpdateContracts.Receipt
        | ReferencePending of WorkingDirectoryUpdateContracts.Receipt

    /// Names the only Reference publication outcomes permitted after verified local completion has been persisted.
    type internal ReferenceFinalization =
        | ReferenceTerminal of WorkingDirectoryUpdateContracts.Receipt
        | ReferenceIncomplete of WorkingDirectoryUpdateContracts.Receipt * WorkingDirectoryUpdateContracts.Failure

    /// Names the only private outcomes returned by the five-input Branch transaction.
    type internal RunOutcome =
        | Completed of LocalCompletion
        | Finalized of ReferenceFinalization
        | Rejected of WorkingDirectoryUpdateContracts.Failure
        | UpdateIncomplete of WorkingDirectoryUpdateContracts.Failure

    /// Creates a classified transaction failure without exposing a caller-provided completion callback.
    let private transactionFailure reason =
        WorkingDirectoryUpdateContracts.Failure.create reason
        |> Result.defaultWith invalidOp

    /// Projects the preserved DirectoryVersion transaction result into the private five-input transaction result.
    let private directoryVersionResult =
        function
        | WorkingDirectoryUpdateContracts.Outcome.Updated receipt -> Completed(DirectoryVersionTerminal receipt)
        | WorkingDirectoryUpdateContracts.Outcome.Unchanged receipt -> Completed(DirectoryVersionTerminal receipt)
        | WorkingDirectoryUpdateContracts.Outcome.Rejected error -> Rejected error
        | WorkingDirectoryUpdateContracts.Outcome.UpdateIncomplete error -> UpdateIncomplete error
        | WorkingDirectoryUpdateContracts.Outcome.FinalizationIncomplete (_, error) -> UpdateIncomplete error

    /// Converts a Reference finalization result into the established CLI outcome without widening its internal state space.
    let private projectReferenceFinalization =
        function
        | ReferenceTerminal receipt ->
            if WorkingDirectoryUpdateContracts.Receipt.bytesChanged receipt then
                WorkingDirectoryUpdateContracts.Outcome.Updated receipt
            else
                WorkingDirectoryUpdateContracts.Outcome.Unchanged receipt
        | ReferenceIncomplete (receipt, failure) -> WorkingDirectoryUpdateContracts.Outcome.FinalizationIncomplete(receipt, failure)

    /// Projects the private five-input result to the established Branch command outcome after Reference finalization is complete.
    let internal projectRunOutcome =
        function
        | Completed (DirectoryVersionTerminal receipt) ->
            if WorkingDirectoryUpdateContracts.Receipt.bytesChanged receipt then
                WorkingDirectoryUpdateContracts.Outcome.Updated receipt
            else
                WorkingDirectoryUpdateContracts.Outcome.Unchanged receipt
        | Completed (ReferencePending receipt) ->
            WorkingDirectoryUpdateContracts.Outcome.FinalizationIncomplete(
                receipt,
                transactionFailure "Reference completion remained pending without a finalization result."
            )
        | Finalized finalization -> projectReferenceFinalization finalization
        | Rejected failure -> WorkingDirectoryUpdateContracts.Outcome.Rejected failure
        | UpdateIncomplete failure -> WorkingDirectoryUpdateContracts.Outcome.UpdateIncomplete failure

    /// Adds repair guidance without reporting an unterminalized Reference completion as success.
    let private referenceIncomplete receipt reason =
        ReferenceIncomplete(receipt, transactionFailure $"{reason} Run `grace doctor --repair-local-state` before retrying the Branch switch.")

    /// Reads Branch configuration from disk so stale process state cannot decide Reference publication.
    let private readDurableBranchConfiguration () =
        try
            resetConfiguration ()
            Ok(Current())
        with
        | ex -> Error ex.Message

    /// Publishes only the selected Branch identity and clears the display name that pending facts cannot reconstruct.
    let private publishSelectedBranch selectedBranchId =
        try
            let configuration = Current()
            configuration.BranchId <- selectedBranchId
            configuration.BranchName <- String.Empty
            updateConfiguration configuration
            resetConfiguration ()

            let reread = Current()

            if reread.BranchId = selectedBranchId then
                Ok()
            else
                Error "Branch configuration did not retain the selected Branch identity."
        with
        | ex -> Error ex.Message

    /// Finalizes one Reference pending row while its caller retains the matching WDU lease.
    let private finalizeReferenceCompletionUnderLease scope receipt (cancellationToken: CancellationToken) beforeTerminalRecording =
        task {
            let target = WorkingDirectoryUpdateContracts.Receipt.target receipt
            let operation = WorkingDirectoryUpdateContracts.Receipt.operation receipt
            let mutable firstWriteStarted = false

            let throwIfCancellationStillControls () = if not firstWriteStarted then cancellationToken.ThrowIfCancellationRequested()

            let unchangedTerminal () =
                WorkingDirectoryUpdateContracts.Receipt.create target operation false
                |> Result.defaultWith invalidOp
                |> ReferenceTerminal

            try
                match! LocalStateDb.readWorkingDirectoryUpdateCompletion (Current().GraceStatusFile) target operation with
                | Some LocalStateDb.WorkingDirectoryUpdateCompletion.Terminal -> return unchangedTerminal ()
                | Some LocalStateDb.WorkingDirectoryUpdateCompletion.Pending ->
                    match! LocalStateDb.readPendingWorkingDirectoryUpdateFinalization (Current().GraceStatusFile) with
                    | Some (LocalStateDb.PendingWorkingDirectoryUpdateFinalization.PendingBranchFinalization (persistedTarget,
                                                                                                              persistedOperation,
                                                                                                              previousBranchId,
                                                                                                              WorkingDirectoryUpdateContracts.BranchSelection.Reference _)) when
                        WorkingDirectoryUpdateContracts.Target.canonical persistedTarget = WorkingDirectoryUpdateContracts.Target.canonical target
                        && WorkingDirectoryUpdateContracts.Operation.value persistedOperation = WorkingDirectoryUpdateContracts.Operation.value operation
                        ->
                        let! marker = WorkingDirectoryUpdateCoordination.Marker.inspect scope target operation

                        let! markerResult =
                            task {
                                match marker with
                                | WorkingDirectoryUpdateCoordination.MarkerInspection.DifferentOperation
                                | WorkingDirectoryUpdateCoordination.MarkerInspection.MalformedOrUnsupported
                                | WorkingDirectoryUpdateCoordination.MarkerInspection.Unreadable ->
                                    return Error $"Reference finalization retained marker evidence: {marker}."
                                | WorkingDirectoryUpdateCoordination.MarkerInspection.ExactMatch ->
                                    throwIfCancellationStillControls ()
                                    firstWriteStarted <- true

                                    let! cleanup =
                                        WorkingDirectoryUpdateCoordination.Marker.tryRemoveTerminalEvidenceWithDelete
                                            scope
                                            (WorkingDirectoryUpdateContracts.Operation.value operation)
                                            (WorkingDirectoryUpdateContracts.Target.canonical target)
                                            File.Delete

                                    match cleanup with
                                    | WorkingDirectoryUpdateCoordination.MarkerCleanup.ExactMatchCleaned
                                    | WorkingDirectoryUpdateCoordination.MarkerCleanup.NoMarker -> return Ok()
                                    | result -> return Error $"Reference finalization could not clean its exact marker evidence: {result}."
                                | WorkingDirectoryUpdateCoordination.MarkerInspection.Missing -> return Ok()
                            }

                        match markerResult with
                        | Error error -> return referenceIncomplete receipt error
                        | Ok () ->
                            let branchResult =
                                match readDurableBranchConfiguration () with
                                | Error error -> Error $"Reference finalization could not read Branch configuration: {error}."
                                | Ok configuration when configuration.BranchId = WorkingDirectoryUpdateContracts.Target.branchId target -> Ok()
                                | Ok configuration when configuration.BranchId = previousBranchId ->
                                    throwIfCancellationStillControls ()
                                    firstWriteStarted <- true

                                    match publishSelectedBranch (WorkingDirectoryUpdateContracts.Target.branchId target) with
                                    | Ok () -> Ok()
                                    | Error error -> Error $"Reference finalization could not publish the selected Branch: {error}."
                                | Ok _ -> Error "Reference finalization found a third Branch identity and left the pending completion unchanged."

                            match branchResult with
                            | Error error -> return referenceIncomplete receipt error
                            | Ok () ->
                                match readDurableBranchConfiguration () with
                                | Ok configuration when configuration.BranchId = WorkingDirectoryUpdateContracts.Target.branchId target ->
                                    throwIfCancellationStillControls ()
                                    firstWriteStarted <- true

                                    do!
                                        LocalStateDb.finalizeWorkingDirectoryUpdateCompletionWithBeforeTerminalRecording
                                            configuration.GraceStatusFile
                                            target
                                            operation
                                            beforeTerminalRecording

                                    return ReferenceTerminal receipt
                                | Ok _ ->
                                    return
                                        referenceIncomplete
                                            receipt
                                            "Reference finalization could not durably reread the selected Branch before terminal recording."
                                | Error error ->
                                    return referenceIncomplete receipt $"Reference finalization could not durably reread Branch configuration: {error}."
                    | None ->
                        match! LocalStateDb.readWorkingDirectoryUpdateCompletion (Current().GraceStatusFile) target operation with
                        | Some LocalStateDb.WorkingDirectoryUpdateCompletion.Terminal -> return unchangedTerminal ()
                        | _ -> return referenceIncomplete receipt "The persisted Reference completion disappeared before finalization."
                    | Some _ -> return referenceIncomplete receipt "The persisted pending finalization does not match this Reference completion."
                | None -> return referenceIncomplete receipt "The persisted Reference completion is missing."
            with
            | :? OperationCanceledException -> return referenceIncomplete receipt "Reference finalization was canceled before its first applicable write."
            | ex -> return referenceIncomplete receipt $"Reference finalization failed: {ex.Message}"
        }

    /// Acquires the WDU lease before entering the shared Reference completion effect sequence.
    let private finalizeReferenceCompletion receipt (cancellationToken: CancellationToken) beforeTerminalRecording =
        task {
            let target = WorkingDirectoryUpdateContracts.Receipt.target receipt

            try
                match WorkingDirectoryUpdateCoordination.Scope.create (WorkingDirectoryUpdateContracts.Target.repositoryId target) (Current().RootDirectory)
                    with
                | Error error -> return referenceIncomplete receipt error
                | Ok scope ->
                    use! _lease = WorkingDirectoryUpdateCoordination.Lease.acquire scope cancellationToken
                    return! finalizeReferenceCompletionUnderLease scope receipt cancellationToken beforeTerminalRecording
            with
            | :? OperationCanceledException -> return referenceIncomplete receipt "Reference finalization was canceled before its first applicable write."
            | ex -> return referenceIncomplete receipt $"Reference finalization failed: {ex.Message}"
        }

    /// Verifies that SQLite still describes the complete selected root recorded by one pending Reference target.
    let private statusMatchesTarget target (status: GraceStatus) =
        status.RootDirectoryId = WorkingDirectoryUpdateContracts.Target.rootDirectoryVersionId target
        && status.RootDirectorySha256Hash = WorkingDirectoryUpdateContracts.Target.sha256Hash target
        && status.RootDirectoryBlake3Hash = WorkingDirectoryUpdateContracts.Target.blake3Hash target
        && match status.Index.TryGetValue status.RootDirectoryId with
           | true, root ->
               root.RepositoryId = WorkingDirectoryUpdateContracts.Target.repositoryId target
               && root.DirectoryVersionId = status.RootDirectoryId
               && root.Sha256Hash = status.RootDirectorySha256Hash
               && root.Blake3Hash = status.RootDirectoryBlake3Hash
           | _ -> false

    /// Completes Doctor's applicable pending Reference only after lease-held persisted-state and exact-byte validation.
    let internal repairPendingReferenceFinalization (cancellationToken: CancellationToken) =
        task {
            let initialConfiguration = Current()
            let localStatePath = initialConfiguration.GraceStatusFile

            match! LocalStateDb.readPendingWorkingDirectoryUpdateFinalization localStatePath with
            | Some (LocalStateDb.PendingWorkingDirectoryUpdateFinalization.PendingBranchFinalization (target,
                                                                                                      operation,
                                                                                                      _,
                                                                                                      WorkingDirectoryUpdateContracts.BranchSelection.Reference _)) ->
                let receipt =
                    WorkingDirectoryUpdateContracts.Receipt.create target operation true
                    |> Result.defaultWith invalidOp

                let incomplete reason =
                    referenceIncomplete receipt reason
                    |> projectReferenceFinalization
                    |> Some

                match
                    WorkingDirectoryUpdateCoordination.Scope.create
                        (WorkingDirectoryUpdateContracts.Target.repositoryId target)
                        initialConfiguration.RootDirectory
                    with
                | Error error -> return incomplete error
                | Ok scope ->
                    try
                        use! _lease = WorkingDirectoryUpdateCoordination.Lease.acquire scope cancellationToken

                        match readDurableBranchConfiguration () with
                        | Error error -> return incomplete $"Doctor could not reread durable Branch configuration: {error}."
                        | Ok configuration ->
                            let durableScope = WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId configuration.RootDirectory

                            match durableScope with
                            | Error error -> return incomplete $"Doctor found invalid durable WDU configuration: {error}."
                            | Ok durableScope when
                                configuration.RepositoryId
                                <> WorkingDirectoryUpdateContracts.Target.repositoryId target
                                || WorkingDirectoryUpdateCoordination.Scope.value durableScope
                                   <> WorkingDirectoryUpdateCoordination.Scope.value scope
                                || not (String.Equals(configuration.GraceStatusFile, localStatePath, StringComparison.OrdinalIgnoreCase))
                                ->
                                return incomplete "Doctor found configuration drift while validating pending Reference finalization."
                            | Ok _ ->
                                match! LocalStateDb.readWorkingDirectoryUpdateCompletion localStatePath target operation with
                                | Some LocalStateDb.WorkingDirectoryUpdateCompletion.Terminal ->
                                    let! finalization = finalizeReferenceCompletionUnderLease scope receipt cancellationToken ignore
                                    return Some(projectReferenceFinalization finalization)
                                | Some LocalStateDb.WorkingDirectoryUpdateCompletion.Pending ->
                                    let! revisionBeforeValidation = LocalStateDb.readLocalStatusRevisionReadOnly localStatePath

                                    match! LocalStateDb.readPendingWorkingDirectoryUpdateFinalization localStatePath with
                                    | Some (LocalStateDb.PendingWorkingDirectoryUpdateFinalization.PendingBranchFinalization (persistedTarget,
                                                                                                                              persistedOperation,
                                                                                                                              _,
                                                                                                                              WorkingDirectoryUpdateContracts.BranchSelection.Reference _)) when
                                        WorkingDirectoryUpdateContracts.Target.canonical persistedTarget = WorkingDirectoryUpdateContracts.Target.canonical
                                                                                                               target
                                        && WorkingDirectoryUpdateContracts.Operation.value persistedOperation = WorkingDirectoryUpdateContracts.Operation.value
                                                                                                                    operation
                                        ->
                                        match!
                                            LocalStateDb.readCompleteStatusSnapshotReadOnly
                                                localStatePath
                                                configuration.OwnerId
                                                configuration.OrganizationId
                                                configuration.RepositoryId
                                            with
                                        | Error error -> return incomplete $"Doctor could not read pending Reference local status: {error}."
                                        | Ok status when not (statusMatchesTarget target status) ->
                                            return incomplete "Doctor found local status that does not match the pending Reference target."
                                        | Ok status ->
                                            let! differences = Services.scanForDifferences status
                                            let! revisionAfterValidation = LocalStateDb.readLocalStatusRevisionReadOnly localStatePath

                                            if revisionBeforeValidation
                                               <> revisionAfterValidation then
                                                return incomplete "Doctor found local status changed during pending Reference validation."
                                            elif
                                                not (Services.wasLastScanForDifferencesSuccessful ())
                                                || differences.Count <> 0
                                            then
                                                return incomplete "Doctor found working-tree bytes or paths do not match the pending Reference target."
                                            else
                                                let! finalization = finalizeReferenceCompletionUnderLease scope receipt cancellationToken ignore
                                                return Some(projectReferenceFinalization finalization)
                                    | _ -> return incomplete "Doctor found pending Reference facts that changed while waiting for the WDU lease."
                                | None -> return incomplete "Doctor found the pending Reference completion missing after acquiring the WDU lease."
                    with
                    | :? OperationCanceledException -> return incomplete "Reference finalization was canceled before its first applicable write."
                    | ex -> return incomplete $"Reference finalization failed: {ex.Message}"
            | Some _ -> return None
            | None -> return None
        }

    /// Reconstructs and finalizes the sole persisted Reference row without preparing or writing working-tree content.
    let internal resumePendingReferenceFinalization (cancellationToken: CancellationToken) =
        task {
            let localStatePath =
                try
                    Current().GraceStatusFile
                with
                | _ -> Path.Combine(Environment.CurrentDirectory, Constants.GraceConfigDirectory, Constants.GraceLocalStateDbFileName)

            match! LocalStateDb.readPendingWorkingDirectoryUpdateFinalization localStatePath with
            | Some (LocalStateDb.PendingWorkingDirectoryUpdateFinalization.PendingBranchFinalization (target,
                                                                                                      operation,
                                                                                                      _,
                                                                                                      WorkingDirectoryUpdateContracts.BranchSelection.Reference _)) ->
                let receipt =
                    WorkingDirectoryUpdateContracts.Receipt.create target operation true
                    |> Result.defaultWith invalidOp

                let! finalization = finalizeReferenceCompletion receipt cancellationToken ignore
                return Some(projectReferenceFinalization finalization)
            | Some _ -> return None
            | None -> return None
        }

    /// Applies and records the Reference-pending path after the shared local application stage has verified the root.
    let private runReference acceptedPhase selection resolvedTargetGraph request cancellationToken (failureInjection: BranchTransaction.FailureInjection) =
        task {
            let target = WorkingDirectoryUpdateContracts.Request.target request
            let operation = WorkingDirectoryUpdateContracts.Request.operation request
            let preparedContent = WorkingDirectoryUpdateContracts.Request.preparedContent request
            let attemptToken = WorkingDirectoryUpdateContracts.AttemptToken.create ()
            let mutable verifiedRoot = false

            try
                match WorkingDirectoryUpdateCoordination.Scope.create (WorkingDirectoryUpdateContracts.Target.repositoryId target) (Current().RootDirectory)
                    with
                | Error error -> return Rejected(transactionFailure error)
                | Ok scope ->
                    use! lease = WorkingDirectoryUpdateCoordination.Lease.acquire scope cancellationToken

                    match! LocalStateDb.readWorkingDirectoryUpdateCompletion (Current().GraceStatusFile) target operation with
                    | Some _ ->
                        return
                            Rejected(
                                transactionFailure
                                    "Reference selection cannot replace an existing Working Directory Update completion after acquiring the Working Directory Update lease."
                            )
                    | None ->
                        let! pendingFinalization = LocalStateDb.readPendingWorkingDirectoryUpdateFinalization (Current().GraceStatusFile)

                        match pendingFinalization with
                        | Some _ ->
                            return
                                Rejected(
                                    transactionFailure
                                        "Reference selection cannot begin while a Branch finalization is pending after acquiring the Working Directory Update lease."
                                )
                        | None ->
                            let! revisionBeforeRead = LocalStateDb.readLocalStatusRevisionReadOnly (Current().GraceStatusFile)

                            let! freshStatusResult =
                                LocalStateDb.readCompleteStatusSnapshotReadOnly
                                    (Current().GraceStatusFile)
                                    (Current().OwnerId)
                                    (Current().OrganizationId)
                                    (WorkingDirectoryUpdateContracts.Target.repositoryId target)

                            let! revisionAfterRead = LocalStateDb.readLocalStatusRevisionReadOnly (Current().GraceStatusFile)

                            let gateError =
                                if revisionBeforeRead
                                   <> WorkingDirectoryUpdateContracts.AcceptedBranchPhase.acceptedRevision acceptedPhase
                                   || revisionAfterRead
                                      <> WorkingDirectoryUpdateContracts.AcceptedBranchPhase.acceptedRevision acceptedPhase then
                                    Some "Local status changed while the selected Reference was being prepared."
                                else
                                    match freshStatusResult with
                                    | Ok freshStatus when
                                        not
                                            (
                                                LocalApplication.statusFingerprintMatches
                                                    (WorkingDirectoryUpdateContracts.AcceptedBranchPhase.acceptedStatus acceptedPhase)
                                                    freshStatus
                                            )
                                        ->
                                        Some "Local status changed while the selected Reference was being prepared."
                                    | Ok _ -> None
                                    | Error error -> Some error

                            let freshStatus =
                                freshStatusResult
                                |> Result.defaultValue (WorkingDirectoryUpdateContracts.AcceptedBranchPhase.acceptedStatus acceptedPhase)

                            let! markerInspection =
                                match gateError with
                                | Some _ -> Task.FromResult WorkingDirectoryUpdateCoordination.MarkerInspection.MalformedOrUnsupported
                                | None -> WorkingDirectoryUpdateCoordination.Marker.inspect scope target operation

                            let! admittedMarkerInspection =
                                task {
                                    match markerInspection with
                                    | WorkingDirectoryUpdateCoordination.MarkerInspection.DifferentOperation ->
                                        match! WorkingDirectoryUpdateCoordination.Marker.readEvidence scope with
                                        | Some evidence ->
                                            let! isTerminal =
                                                LocalStateDb.hasTerminalWorkingDirectoryUpdateEvidence
                                                    (Current().GraceStatusFile)
                                                    evidence.OperationId
                                                    evidence.Target

                                            if isTerminal then
                                                let! cleanup =
                                                    WorkingDirectoryUpdateCoordination.Marker.tryRemoveTerminalEvidenceWithDelete
                                                        scope
                                                        evidence.OperationId
                                                        evidence.Target
                                                        File.Delete

                                                return
                                                    if cleanup = WorkingDirectoryUpdateCoordination.MarkerCleanup.ExactMatchCleaned then
                                                        WorkingDirectoryUpdateCoordination.MarkerInspection.Missing
                                                    else
                                                        markerInspection
                                            else
                                                return markerInspection
                                        | None -> return markerInspection
                                    | _ -> return markerInspection
                                }

                            match admittedMarkerInspection with
                            | WorkingDirectoryUpdateCoordination.MarkerInspection.MalformedOrUnsupported
                            | WorkingDirectoryUpdateCoordination.MarkerInspection.Unreadable
                            | WorkingDirectoryUpdateCoordination.MarkerInspection.DifferentOperation ->
                                return
                                    Rejected(
                                        transactionFailure (
                                            gateError
                                            |> Option.defaultValue $"Working Directory Update marker evidence is {markerInspection}."
                                        )
                                    )
                            | WorkingDirectoryUpdateCoordination.MarkerInspection.Missing
                            | WorkingDirectoryUpdateCoordination.MarkerInspection.ExactMatch ->
                                match LocalStateDb.validateCompleteStatusTree freshStatus,
                                      LocalStateDb.validateCompleteStatusTree (
                                          WorkingDirectoryUpdateContracts.ResolvedTargetGraph.targetStatus resolvedTargetGraph
                                      )
                                    with
                                | Error error, _
                                | _, Error error -> return Rejected(transactionFailure error)
                                | Ok (), Ok () ->
                                    let marker =
                                        WorkingDirectoryUpdateCoordination.Marker.create scope attemptToken target operation
                                        |> Result.defaultWith invalidOp

                                    do! WorkingDirectoryUpdateCoordination.Marker.write scope marker

                                    let exactAdoption = admittedMarkerInspection = WorkingDirectoryUpdateCoordination.MarkerInspection.ExactMatch

                                    match!
                                        LocalApplication.run
                                            request
                                            (WorkingDirectoryUpdateContracts.AcceptedBranchPhase.acceptedStatus acceptedPhase)
                                            (WorkingDirectoryUpdateContracts.ResolvedTargetGraph.targetStatus resolvedTargetGraph)
                                            (WorkingDirectoryUpdateContracts.ResolvedTargetGraph.objectMetadata resolvedTargetGraph)
                                            (WorkingDirectoryUpdateContracts.ResolvedTargetGraph.manifest resolvedTargetGraph)
                                            (Current().RootDirectory)
                                            (Current().GraceStatusFile)
                                            (WorkingDirectoryUpdateContracts.AcceptedBranchPhase.acceptedRevision acceptedPhase)
                                            scope
                                            attemptToken
                                            exactAdoption
                                            cancellationToken
                                            failureInjection
                                        with
                                    | LocalApplication.Rejected error -> return Rejected error
                                    | LocalApplication.UpdateIncomplete error -> return UpdateIncomplete error
                                    | LocalApplication.Verified localRoot ->
                                        verifiedRoot <- true

                                        match selection with
                                        | WorkingDirectoryUpdateContracts.BranchSelection.Reference selectedReferenceId ->
                                            let! _ =
                                                LocalStateDb.commitWorkingDirectoryUpdateCompletionWithBeforeCommit
                                                    (Current().GraceStatusFile)
                                                    (LocalApplication.VerifiedLocalRoot.targetStatus localRoot)
                                                    (LocalApplication.VerifiedLocalRoot.objectMetadata localRoot)
                                                    (LocalStateDb.WorkingDirectoryUpdateCompletionDetails.BranchFinalization(
                                                        WorkingDirectoryUpdateContracts.AcceptedBranchPhase.previousBranchId acceptedPhase,
                                                        selectedReferenceId
                                                    ))
                                                    target
                                                    operation
                                                    (fun () -> failureInjection.ThrowAt BranchTransaction.BeforeCommit)

                                            let receipt =
                                                WorkingDirectoryUpdateContracts.Receipt.create
                                                    target
                                                    operation
                                                    (LocalApplication.VerifiedLocalRoot.bytesChanged localRoot)
                                                |> Result.defaultWith invalidOp

                                            return Completed(ReferencePending receipt)
                                        | WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion ->
                                            return Rejected(transactionFailure "Reference completion received DirectoryVersion selection.")
            with
            | ex when verifiedRoot -> return UpdateIncomplete(transactionFailure ex.Message)
            | :? OperationCanceledException as ex -> return Rejected(transactionFailure ex.Message)
            | ex -> return Rejected(transactionFailure ex.Message)
        }

    /// Executes the five semantic Branch inputs while retaining cancellation and failure injection as invocation controls.
    let run acceptedPhase selection resolvedTargetGraph preparedContent correlationId cancellationToken (failureInjection: BranchTransaction.FailureInjection) =
        task {
            let reject reason =
                WorkingDirectoryUpdateContracts.PreparedContent.dispose preparedContent
                Rejected(transactionFailure reason)

            if not (WorkingDirectoryUpdateContracts.ResolvedTargetGraph.belongsTo acceptedPhase resolvedTargetGraph) then
                return reject "Resolved target graph was not prepared from the accepted Branch phase."
            elif WorkingDirectoryUpdateContracts.PreparedContent.manifest preparedContent
                 <> WorkingDirectoryUpdateContracts.ResolvedTargetGraph.manifest resolvedTargetGraph then
                return reject "Prepared content does not match the resolved target graph manifest."
            else
                let target = WorkingDirectoryUpdateContracts.ResolvedTargetGraph.target resolvedTargetGraph

                if WorkingDirectoryUpdateContracts.Target.repositoryId target
                   <> Current().RepositoryId then
                    return reject "Resolved target graph does not belong to the current repository."
                else
                    let operationResult =
                        WorkingDirectoryUpdateContracts.Operation.branchSwitchWithSelection
                            (WorkingDirectoryUpdateContracts.AcceptedBranchPhase.previousBranchId acceptedPhase)
                            selection
                            target

                    match operationResult with
                    | Error error -> return reject error
                    | Ok operation ->
                        match WorkingDirectoryUpdateContracts.Request.create target operation preparedContent correlationId with
                        | Error error -> return reject error
                        | Ok request ->
                            match selection with
                            | WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion ->
                                let! outcome =
                                    BranchTransaction.runAtRevision
                                        request
                                        (WorkingDirectoryUpdateContracts.AcceptedBranchPhase.acceptedStatus acceptedPhase)
                                        (WorkingDirectoryUpdateContracts.ResolvedTargetGraph.targetStatus resolvedTargetGraph)
                                        (WorkingDirectoryUpdateContracts.ResolvedTargetGraph.objectMetadata resolvedTargetGraph)
                                        (WorkingDirectoryUpdateContracts.ResolvedTargetGraph.manifest resolvedTargetGraph)
                                        (Current().RootDirectory)
                                        (Current().GraceStatusFile)
                                        (WorkingDirectoryUpdateContracts.AcceptedBranchPhase.acceptedRevision acceptedPhase)
                                        cancellationToken
                                        failureInjection

                                return directoryVersionResult outcome
                            | WorkingDirectoryUpdateContracts.BranchSelection.Reference _ ->
                                try
                                    match! runReference acceptedPhase selection resolvedTargetGraph request cancellationToken failureInjection with
                                    | Completed (ReferencePending receipt) ->
                                        let! finalization =
                                            finalizeReferenceCompletion receipt cancellationToken (fun () ->
                                                failureInjection.ThrowAt BranchTransaction.BeforeTerminalRecording)

                                        return Finalized finalization
                                    | outcome -> return outcome
                                finally
                                    WorkingDirectoryUpdateContracts.PreparedContent.dispose preparedContent
        }

    /// Preserves the existing DirectoryVersion test-facing adapter while routing it through the five-input transaction.
    module BranchDirectoryVersion =
        /// Preserves the established deterministic effect-boundary type for focused DirectoryVersion tests.
        type FailurePoint = BranchTransaction.FailurePoint

        /// Preserves the established deterministic effect controls for focused DirectoryVersion tests.
        type FailureInjection = BranchTransaction.FailureInjection

        /// Uses normal effects while preserving the existing DirectoryVersion test-facing name.
        let none = BranchTransaction.none

        /// Preserves existing DirectoryVersion test-facing failure-point names.
        let BeforeMutation = BranchTransaction.BeforeMutation

        /// Preserves existing DirectoryVersion test-facing failure-point names.
        let AfterObjectPublication = BranchTransaction.AfterObjectPublication

        /// Preserves existing DirectoryVersion test-facing failure-point names.
        let DuringApplication = BranchTransaction.DuringApplication

        /// Preserves existing DirectoryVersion test-facing failure-point names.
        let BeforeCommit = BranchTransaction.BeforeCommit

        /// Preserves existing DirectoryVersion test-facing failure-point names.
        let AfterCommit = BranchTransaction.AfterCommit

        /// Preserves the deterministic boundary immediately before Reference terminal recording.
        let BeforeTerminalRecording = BranchTransaction.BeforeTerminalRecording

        /// Preserves existing DirectoryVersion test-facing failure-point names.
        let MarkerCleanup = BranchTransaction.MarkerCleanup

        /// Converts the private five-input outcome back to the preserved DirectoryVersion outcome shape.
        let private project =
            function
            | Completed (DirectoryVersionTerminal receipt) ->
                if WorkingDirectoryUpdateContracts.Receipt.bytesChanged receipt then
                    WorkingDirectoryUpdateContracts.Outcome.Updated receipt
                else
                    WorkingDirectoryUpdateContracts.Outcome.Unchanged receipt
            | Completed (ReferencePending _) ->
                WorkingDirectoryUpdateContracts.Outcome.Rejected(transactionFailure "DirectoryVersion adapter cannot project Reference pending completion.")
            | Finalized finalization -> projectReferenceFinalization finalization
            | Rejected error -> WorkingDirectoryUpdateContracts.Outcome.Rejected error
            | UpdateIncomplete error -> WorkingDirectoryUpdateContracts.Outcome.UpdateIncomplete error

        /// Seals existing DirectoryVersion producer facts and delegates all transaction behavior to the five-input seam.
        let runAtRevision request currentStatus targetStatus objectMetadata manifest _root _dbPath acceptedRevision cancellationToken failureInjection =
            task {
                let target = WorkingDirectoryUpdateContracts.Request.target request
                let preparedContent = WorkingDirectoryUpdateContracts.Request.preparedContent request
                let correlationId = WorkingDirectoryUpdateContracts.Request.correlationId request

                match
                    WorkingDirectoryUpdateContracts.AcceptedBranchPhase.noSave
                        currentStatus
                        acceptedRevision
                        (WorkingDirectoryUpdateContracts.Target.branchId target)
                    with
                | Error error ->
                    WorkingDirectoryUpdateContracts.PreparedContent.dispose preparedContent
                    return WorkingDirectoryUpdateContracts.Outcome.Rejected(transactionFailure error)
                | Ok acceptedPhase ->
                    match
                        WorkingDirectoryUpdateContracts.Operation.branchSwitchWithSelection
                            (WorkingDirectoryUpdateContracts.AcceptedBranchPhase.previousBranchId acceptedPhase)
                            WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion
                            target
                        with
                    | Error error ->
                        WorkingDirectoryUpdateContracts.PreparedContent.dispose preparedContent
                        return WorkingDirectoryUpdateContracts.Outcome.Rejected(transactionFailure error)
                    | Ok expectedOperation when
                        WorkingDirectoryUpdateContracts.Operation.value expectedOperation
                        <> (WorkingDirectoryUpdateContracts.Request.operation request
                            |> WorkingDirectoryUpdateContracts.Operation.value)
                        ->
                        WorkingDirectoryUpdateContracts.PreparedContent.dispose preparedContent

                        return
                            WorkingDirectoryUpdateContracts.Outcome.Rejected(
                                transactionFailure "DirectoryVersion adapter request operation does not match its sealed selection."
                            )
                    | Ok _ ->
                        match
                            WorkingDirectoryUpdateContracts.ResolvedTargetGraph.create
                                acceptedPhase
                                WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion
                                target
                                targetStatus
                                objectMetadata
                                manifest
                            with
                        | Error error ->
                            WorkingDirectoryUpdateContracts.PreparedContent.dispose preparedContent
                            return WorkingDirectoryUpdateContracts.Outcome.Rejected(transactionFailure error)
                        | Ok resolvedTargetGraph ->
                            let! result =
                                run
                                    acceptedPhase
                                    WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion
                                    resolvedTargetGraph
                                    preparedContent
                                    correlationId
                                    cancellationToken
                                    failureInjection

                            return project result
            }

        /// Reads the current local revision before sealing the preserved DirectoryVersion producer facts.
        let run request currentStatus targetStatus objectMetadata manifest root dbPath cancellationToken failureInjection =
            task {
                let! acceptedRevision = LocalStateDb.readLocalStatusRevisionReadOnly dbPath

                return! runAtRevision request currentStatus targetStatus objectMetadata manifest root dbPath acceptedRevision cancellationToken failureInjection
            }
