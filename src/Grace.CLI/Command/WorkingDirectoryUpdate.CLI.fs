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
