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

        /// States whether an immutable object may create a new file or replace one exact accepted tracked file.
        type CopyPrecondition =
            | MustBeAbsent
            | ReplaceVerifiedTrackedFile of Sha256Hash * Blake3Hash

        /// Describes one tracked later transaction step without carrying a writer, callback, or mutable filesystem handle.
        type Action =
            | RemoveTrackedFile of RelativePath
            | RemoveTrackedDirectory of RelativePath
            | EnsureDirectory of RelativePath
            | CopyVerifiedFile of RelativePath * CopyPrecondition

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
        let private planSynchronously (scanInput: Services.WorkingTreeScanInput) (currentStatus: GraceStatus) manifest =
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
                let addCopy path precondition = copies[pathKey path] <- CopyVerifiedFile(path, precondition)

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
                            | Missing -> addCopy targetPath MustBeAbsent
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
                                        addCopy targetPath (ReplaceVerifiedTrackedFile(tracked.Sha256Hash, tracked.Blake3Hash))
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

                                        addCopy targetPath MustBeAbsent
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
                                | CopyVerifiedFile (path, _) -> path
                                | _ -> RelativePath Constants.RootDirectoryPath

                            let rightPath =
                                match right with
                                | CopyVerifiedFile (path, _) -> path
                                | _ -> RelativePath Constants.RootDirectoryPath

                            comparePaths leftPath rightPath)
                        |> Seq.toList

                    Planned(Plan(orderedRemovals @ orderedCreates @ orderedCopies))

        /// Produces one complete pre-mutation action list after classifying every target and removable tracked blocker.
        let plan (currentStatus: GraceStatus) manifest = task { return planSynchronously (currentScanInput ()) currentStatus manifest }

        /// Plans from one already-reread configuration snapshot so a transaction never falls back to cached configuration.
        let internal planWithScanInput (scanInput: Services.WorkingTreeScanInput) (currentStatus: GraceStatus) manifest =
            task { return planSynchronously scanInput currentStatus manifest }

        /// Re-evaluates the complete relevant filesystem without a task boundary before the first mutable action.
        let internal planSynchronouslyWithScanInput (scanInput: Services.WorkingTreeScanInput) (currentStatus: GraceStatus) manifest =
            planSynchronously scanInput currentStatus manifest

    /// Carries the one complete target status graph and its exact selected-root identity through the local transaction.
    type ResolvedTargetGraph = private ResolvedTargetGraph of WorkingDirectoryUpdateContracts.Target * GraceStatus

    /// Carries correlation used only to identify diagnostics from one invocation; it never changes the update identity.
    type DiagnosticCorrelation = private DiagnosticCorrelation of string

    /// Holds the immutable configuration facts that must remain unchanged from admission through first working-tree mutation.
    type private CanonicalConfiguration =
        {
            OwnerId: OwnerId
            OrganizationId: OrganizationId
            RepositoryId: RepositoryId
            BranchId: BranchId
            RootDirectory: string
            StandardizedRootDirectory: string
            GraceDirectory: string
            ObjectDirectory: string
            GraceStatusFile: string
            GraceObjectCacheFile: string
            DirectoryVersionCache: string
            ConfigurationDirectory: string
            ConfigurationFile: string
            GraceFileIgnoreEntries: string list
            GraceDirectoryIgnoreEntries: string list
        }

    /// Loads immutable local configuration facts directly from disk instead of reusing the process cache.
    module private CanonicalConfiguration =
        /// Converts a deserialized configuration into the exact local facts consumed by this transaction.
        let private fromInspection (inspection: GraceConfigurationInspection) =
            let configuration = inspection.Configuration

            {
                OwnerId = configuration.OwnerId
                OrganizationId = configuration.OrganizationId
                RepositoryId = configuration.RepositoryId
                BranchId = configuration.BranchId
                RootDirectory = configuration.RootDirectory
                StandardizedRootDirectory = configuration.StandardizedRootDirectory
                GraceDirectory = configuration.GraceDirectory
                ObjectDirectory = configuration.ObjectDirectory
                GraceStatusFile = configuration.GraceStatusFile
                GraceObjectCacheFile = configuration.GraceObjectCacheFile
                DirectoryVersionCache = configuration.DirectoryVersionCache
                ConfigurationDirectory = configuration.ConfigurationDirectory
                ConfigurationFile = inspection.Path
                GraceFileIgnoreEntries =
                    configuration.GraceFileIgnoreEntries
                    |> Array.toList
                GraceDirectoryIgnoreEntries =
                    configuration.GraceDirectoryIgnoreEntries
                    |> Array.toList
            }

        /// Rereads the configured repository file and its derived ignore facts for a transaction boundary.
        let loadFresh () =
            match tryInspectCurrentDirectoryConfiguration () with
            | Ok inspection -> Ok(fromInspection inspection)
            | Error (ConfigurationFileNotFound error) -> Error error
            | Error (ConfigurationFileMalformed (_, error)) -> Error error

        /// Compares every configuration fact that controls local identity, paths, status, objects, and topology classification.
        let matches left right = left = right

        /// Builds the planner input without accessing Configuration.Current().
        let scanInput configuration : Services.WorkingTreeScanInput =
            {
                RootDirectory = configuration.RootDirectory
                GraceDirectory = configuration.GraceDirectory
                GraceStatusFile = configuration.GraceStatusFile
                DirectoryIgnoreEntries =
                    configuration.GraceDirectoryIgnoreEntries
                    |> List.toArray
                FileIgnoreEntries =
                    configuration.GraceFileIgnoreEntries
                    |> List.toArray
            }

    /// Exposes precise test-only timing points without adding callers, request bags, or production transaction options.
    module internal LocalTransactionTesting =
        let mutable private afterSealedConfiguration: (unit -> unit) option = None
        let mutable private afterLeaseAcquired: (unit -> unit) option = None
        let mutable private afterObjectPublication: (unit -> unit) option = None
        let mutable private beforeFinalPlanning: (unit -> unit) option = None
        let mutable private beforeFinalGlobalFactGate: (unit -> unit) option = None
        let mutable private afterFinalGlobalFactGate: (unit -> unit) option = None
        let mutable private beforeFirstMutation: (unit -> unit) option = None
        let mutable private afterFirstMutationBegan: (unit -> unit) option = None
        let mutable private afterPlannedActions: (unit -> unit) option = None

        /// Removes all deterministic timing actions after a direct-runtime test completes.
        let reset () =
            afterSealedConfiguration <- None
            afterLeaseAcquired <- None
            afterObjectPublication <- None
            beforeFinalPlanning <- None
            beforeFinalGlobalFactGate <- None
            afterFinalGlobalFactGate <- None
            beforeFirstMutation <- None
            afterFirstMutationBegan <- None
            afterPlannedActions <- None

        /// Installs one action after the immutable configuration baseline is sealed and before WDU lease acquisition.
        let installAfterSealedConfiguration action = afterSealedConfiguration <- Some action

        /// Installs one action after the WDU lease is acquired and before the mandatory fresh configuration reread.
        let installAfterLeaseAcquired action = afterLeaseAcquired <- Some action

        /// Installs one action immediately after immutable objects are published and before final configuration reread.
        let installAfterObjectPublication action = afterObjectPublication <- Some action

        /// Installs one action immediately before final planning begins.
        let installBeforeFinalPlanning action = beforeFinalPlanning <- Some action

        /// Installs one action after final planning and immediately before the final global-fact reread.
        let installBeforeFinalGlobalFactGate action = beforeFinalGlobalFactGate <- Some action

        /// Installs one action after the final global-fact gate and before an action-time precondition is reread.
        let installAfterFinalGlobalFactGate action = afterFinalGlobalFactGate <- Some action

        /// Installs one action between the final cancellation check and the first possible working-tree mutation.
        let installBeforeFirstMutation action = beforeFirstMutation <- Some action

        /// Installs one action after a filesystem mutation begins but before the action returns to the transaction loop.
        let installAfterFirstMutationBegan action = afterFirstMutationBegan <- Some action

        /// Installs one action after all planned working-tree actions and before independent final-root verification.
        let installAfterPlannedActions action = afterPlannedActions <- Some action

        /// Executes and clears one deterministic action so later actions in the same transaction remain production-shaped.
        let private invoke hook =
            match hook with
            | Some action -> action ()
            | None -> ()

        /// Executes the post-sealed-configuration test action once.
        let afterSealedConfigurationNow () =
            let hook = afterSealedConfiguration
            afterSealedConfiguration <- None
            invoke hook

        /// Executes the post-lease-acquisition test action once.
        let afterLeaseAcquiredNow () =
            let hook = afterLeaseAcquired
            afterLeaseAcquired <- None
            invoke hook

        /// Executes the post-object-publication test action once.
        let afterObjectPublicationNow () =
            let hook = afterObjectPublication
            afterObjectPublication <- None
            invoke hook

        /// Executes the pre-final-planning test action once.
        let beforeFinalPlanningNow () =
            let hook = beforeFinalPlanning
            beforeFinalPlanning <- None
            invoke hook

        /// Executes the pre-final-global-fact test action once.
        let beforeFinalGlobalFactGateNow () =
            let hook = beforeFinalGlobalFactGate
            beforeFinalGlobalFactGate <- None
            invoke hook

        /// Executes the post-final-global-fact test action once.
        let afterFinalGlobalFactGateNow () =
            let hook = afterFinalGlobalFactGate
            afterFinalGlobalFactGate <- None
            invoke hook

        /// Executes the pre-first-mutation test action once.
        let beforeFirstMutationNow () =
            let hook = beforeFirstMutation
            beforeFirstMutation <- None
            invoke hook

        /// Executes the in-first-mutation test action once.
        let afterFirstMutationBeganNow () =
            let hook = afterFirstMutationBegan
            afterFirstMutationBegan <- None
            invoke hook

        /// Executes the post-action test action once.
        let afterPlannedActionsNow () =
            let hook = afterPlannedActions
            afterPlannedActions <- None
            invoke hook

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

        /// Finds the exact tracked file identity for a planned removal from the last complete local status reread.
        let private trackedFile (status: GraceStatus) path =
            status.Index.Values
            |> Seq.collect (fun directory -> directory.Files)
            |> Seq.tryFind (fun file -> pathKey file.RelativePath = pathKey path)

        /// Finds whether the last complete local status reread contains a tracked directory at the planned path.
        let private hasTrackedDirectory (status: GraceStatus) path =
            status.Index.Values
            |> Seq.exists (fun directory -> pathKey directory.RelativePath = pathKey path)

        /// Rereads the exact action-time filesystem fact that made one immutable plan action safe.
        let private verifyActionPrecondition root (currentStatus: GraceStatus) action =
            let fullPath path = workingPath root path

            match action with
            | Topology.RemoveTrackedFile path ->
                match trackedFile currentStatus path with
                | Some expected when
                    File.Exists(fullPath path)
                    && hasExpectedBytes (fullPath path) expected.Sha256Hash expected.Blake3Hash
                    ->
                    Ok()
                | _ -> Error $"Tracked file '{path}' changed before its planned removal."
            | Topology.RemoveTrackedDirectory path ->
                let candidate = fullPath path

                if
                    Directory.Exists(candidate)
                    && not (
                        Directory
                            .EnumerateFileSystemEntries(candidate)
                            .GetEnumerator()
                            .MoveNext()
                    )
                then
                    Ok()
                else
                    Error $"Tracked directory '{path}' changed before its planned removal."
            | Topology.EnsureDirectory path ->
                let candidate = fullPath path

                if hasTrackedDirectory currentStatus path then
                    if Directory.Exists(candidate) then
                        Ok()
                    else
                        Error $"Tracked directory '{path}' changed before it could be retained."
                elif
                    not
                        (
                            File.Exists(candidate)
                            || Directory.Exists(candidate)
                        )
                then
                    Ok()
                else
                    Error $"New directory '{path}' appeared or changed kind before it could be created."
            | Topology.CopyVerifiedFile (path, Topology.MustBeAbsent) ->
                let candidate = fullPath path

                if
                    not
                        (
                            File.Exists(candidate)
                            || Directory.Exists(candidate)
                        )
                then
                    Ok()
                else
                    Error $"New file '{path}' appeared or changed kind before immutable bytes could be copied."
            | Topology.CopyVerifiedFile (path, Topology.ReplaceVerifiedTrackedFile (sha256Hash, blake3Hash)) ->
                let candidate = fullPath path

                if
                    File.Exists(candidate)
                    && hasExpectedBytes candidate sha256Hash blake3Hash
                then
                    Ok()
                else
                    Error $"Tracked file '{path}' changed before immutable bytes could replace it."

        /// Rereads the source object before the mutation boundary so a corrupt or replaced object remains pre-mutation.
        let private verifyActionObject objectDirectory (objectHashes: Dictionary<string, Sha256Hash * Blake3Hash>) =
            function
            | Topology.CopyVerifiedFile (path, _) ->
                match objectHashes.TryGetValue(pathKey path) with
                | true, (sha256Hash, blake3Hash) when hasExpectedBytes (objectPath objectDirectory path sha256Hash blake3Hash) sha256Hash blake3Hash -> Ok()
                | _ -> Error $"Immutable object bytes are corrupt or were replaced for '{path}'."
            | _ -> Ok()

        /// Identifies whether a conditionally validated action will issue a filesystem call that can partially succeed.
        let private actionCanMutate (currentStatus: GraceStatus) =
            function
            | Topology.EnsureDirectory path when hasTrackedDirectory currentStatus path -> false
            | _ -> true

        /// Applies one action only after its filesystem and immutable-object facts were reread at the mutation boundary.
        let private applyAction root objectDirectory (objectHashes: Dictionary<string, Sha256Hash * Blake3Hash>) =
            function
            | Topology.RemoveTrackedFile path ->
                let fullPath = workingPath root path

                if File.Exists(fullPath) then
                    File.Delete(fullPath)
                    LocalTransactionTesting.afterFirstMutationBeganNow ()
                    true
                else
                    false
            | Topology.RemoveTrackedDirectory path ->
                let fullPath = workingPath root path

                if Directory.Exists(fullPath) then
                    Directory.Delete(fullPath, false)
                    LocalTransactionTesting.afterFirstMutationBeganNow ()
                    true
                else
                    false
            | Topology.EnsureDirectory path ->
                let fullPath = workingPath root path

                if Directory.Exists(fullPath) then
                    false
                else
                    Directory.CreateDirectory(fullPath) |> ignore
                    LocalTransactionTesting.afterFirstMutationBeganNow ()
                    true
            | Topology.CopyVerifiedFile (path, copyPrecondition) ->
                let key = pathKey path

                match objectHashes.TryGetValue(key) with
                | false, _ -> invalidOp $"Planned file '{path}' has no immutable object declaration."
                | true, (sha256Hash, blake3Hash) ->
                    let objectFile = objectPath objectDirectory path sha256Hash blake3Hash

                    let finalPath = workingPath root path
                    let directory = Path.GetDirectoryName(finalPath)
                    Directory.CreateDirectory(directory) |> ignore

                    let overwrite =
                        match copyPrecondition with
                        | Topology.MustBeAbsent -> false
                        | Topology.ReplaceVerifiedTrackedFile _ -> true

                    File.Copy(objectFile, finalPath, overwrite)
                    LocalTransactionTesting.afterFirstMutationBeganNow ()
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

            let actualDirectories = HashSet<string>(StringComparer.Ordinal)
            let actualFiles = HashSet<string>(StringComparer.Ordinal)

            actualDirectories.Add(pathKey (RelativePath Constants.RootDirectoryPath))
            |> ignore

            actualEntries
            |> Seq.iter (fun entry ->
                let relativePath =
                    Path.GetRelativePath(root, entry.FullName)
                    |> Grace.Shared.Utilities.normalizeFilePath
                    |> RelativePath

                if entry :? DirectoryInfo then
                    actualDirectories.Add(pathKey relativePath)
                    |> ignore
                else
                    actualFiles.Add(pathKey relativePath) |> ignore)

            let actualTopologyMatches =
                actualDirectories.Count = expectedDirectories.Count
                && actualFiles.Count = expectedFiles.Count
                && expectedDirectories
                   |> Seq.forall actualDirectories.Contains
                && expectedFiles.Keys
                   |> Seq.forall actualFiles.Contains

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
            |> Error

        /// Produces a classified incomplete outcome after actual working-tree mutation begins.
        let private incomplete reason =
            WorkingDirectoryUpdateContracts.Failure.create reason
            |> Result.map WorkingDirectoryUpdateContracts.Outcome.UpdateIncomplete
            |> Result.defaultWith invalidOp
            |> Error

        /// Rereads every global admission fact after final planning and immediately before the first working-tree action.
        let private verifyFinalGlobalFacts dbPath sealedConfiguration acceptedPhase targetStatus manifest scope target operation attempt =
            task {
                match CanonicalConfiguration.loadFresh () with
                | Error error -> return Error error
                | Ok currentConfiguration when not (CanonicalConfiguration.matches sealedConfiguration currentConfiguration) ->
                    return Error "Local configuration changed after final planning."
                | Ok currentConfiguration ->
                    let! revision = LocalStateDb.readLocalStatusRevisionReadOnly dbPath

                    let! statusResult =
                        LocalStateDb.readCompleteStatusSnapshotReadOnly
                            dbPath
                            currentConfiguration.OwnerId
                            currentConfiguration.OrganizationId
                            currentConfiguration.RepositoryId

                    let currentStatus =
                        match statusResult with
                        | Ok status -> status
                        | Error error -> invalidOp error

                    let! pending = LocalStateDb.readPendingWorkingDirectoryUpdateFinalization dbPath
                    let! marker = WorkingDirectoryUpdateCoordination.Marker.inspect scope target operation

                    let! markerAttempt = WorkingDirectoryUpdateCoordination.Marker.inspectExactAttempt scope target operation attempt

                    if revision
                       <> WorkingDirectoryUpdateContracts.AcceptedBranchPhase.localStatusRevision acceptedPhase then
                        return Error "Accepted local-status revision changed after final planning."
                    elif statusFingerprint currentStatus
                         <> WorkingDirectoryUpdateContracts.AcceptedBranchPhase.statusFingerprint acceptedPhase then
                        return Error "Accepted complete local-status fingerprint changed after final planning."
                    elif Option.isSome pending then
                        return Error "Pending Working Directory Update finalization changed after final planning."
                    elif marker
                         <> WorkingDirectoryUpdateCoordination.MarkerInspection.ExactMatch then
                        return Error "Working Directory Update marker changed after final planning."
                    elif not markerAttempt then
                        return Error "Working Directory Update marker attempt changed after final planning."
                    elif not (graphMatchesManifest targetStatus manifest) then
                        return Error "Resolved target graph changed after final planning."
                    else
                        return Ok currentConfiguration
            }

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

                    match CanonicalConfiguration.loadFresh () with
                    | Error error -> return rejected $"[{correlationValue}] {error}"
                    | Ok sealedConfiguration ->
                        LocalTransactionTesting.afterSealedConfigurationNow ()

                        if sealedConfiguration.RepositoryId
                           <> WorkingDirectoryUpdateContracts.Target.repositoryId target then
                            return rejected $"[{correlationValue}] Local configuration repository changed before Working Directory Update admission."
                        else
                            match WorkingDirectoryUpdateCoordination.Scope.create sealedConfiguration.RepositoryId sealedConfiguration.RootDirectory with
                            | Error error -> return rejected $"[{correlationValue}] {error}"
                            | Ok scope ->
                                let! acquiredLease = WorkingDirectoryUpdateCoordination.Lease.acquire scope actionToken
                                use acquiredLease = acquiredLease

                                LocalTransactionTesting.afterLeaseAcquiredNow ()

                                match CanonicalConfiguration.loadFresh () with
                                | Error error -> return rejected $"[{correlationValue}] {error}"
                                | Ok admissionConfiguration when not (CanonicalConfiguration.matches sealedConfiguration admissionConfiguration) ->
                                    return rejected $"[{correlationValue}] Local configuration changed while waiting for the Working Directory Update lease."
                                | Ok admissionConfiguration ->
                                    let dbPath = admissionConfiguration.GraceStatusFile
                                    let! revision = LocalStateDb.readLocalStatusRevisionReadOnly dbPath

                                    let! currentStatusResult =
                                        LocalStateDb.readCompleteStatusSnapshotReadOnly
                                            dbPath
                                            admissionConfiguration.OwnerId
                                            admissionConfiguration.OrganizationId
                                            admissionConfiguration.RepositoryId

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
                                        match branchOperation admissionConfiguration.BranchId selection target with
                                        | Error error -> return rejected $"[{correlationValue}] {error}"
                                        | Ok (operation, completionDetails) ->
                                            let! markerInspection = WorkingDirectoryUpdateCoordination.Marker.inspect scope target operation

                                            match markerInspection with
                                            | WorkingDirectoryUpdateCoordination.MarkerInspection.DifferentOperation
                                            | WorkingDirectoryUpdateCoordination.MarkerInspection.MalformedOrUnsupported
                                            | WorkingDirectoryUpdateCoordination.MarkerInspection.Unreadable ->
                                                return
                                                    rejected
                                                        $"[{correlationValue}] Existing Working Directory Update marker is not exact owned admission evidence."
                                            | WorkingDirectoryUpdateCoordination.MarkerInspection.Missing
                                            | WorkingDirectoryUpdateCoordination.MarkerInspection.ExactMatch ->
                                                let attempt = WorkingDirectoryUpdateContracts.AttemptToken.create ()

                                                match WorkingDirectoryUpdateCoordination.Marker.create scope attempt target operation with
                                                | Error error -> return rejected $"[{correlationValue}] {error}"
                                                | Ok marker ->
                                                    do! WorkingDirectoryUpdateCoordination.Marker.write scope marker
                                                    ownedMarker <- Some(scope, attempt)
                                                    let! objectResult = publishObjects preparedContent admissionConfiguration.ObjectDirectory manifest

                                                    match objectResult with
                                                    | Error error ->
                                                        let! cleanup = WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope attempt

                                                        return
                                                            match cleanup with
                                                            | WorkingDirectoryUpdateCoordination.MarkerCleanup.ExactMatchCleaned
                                                            | WorkingDirectoryUpdateCoordination.MarkerCleanup.NoMarker ->
                                                                rejected $"[{correlationValue}] {error}"
                                                            | _ -> rejected $"[{correlationValue}] {error}; exact marker cleanup failed."
                                                    | Ok () ->
                                                        let! initialPlan =
                                                            Topology.planWithScanInput
                                                                (CanonicalConfiguration.scanInput admissionConfiguration)
                                                                currentStatus
                                                                manifest

                                                        match initialPlan with
                                                        | Topology.Rejected rejection ->
                                                            invalidOp
                                                                $"Initial Working Directory Update topology rejected '{Topology.Rejection.path rejection}'."
                                                        | Topology.Planned _ -> ()

                                                        LocalTransactionTesting.afterObjectPublicationNow ()

                                                        match CanonicalConfiguration.loadFresh () with
                                                        | Error error ->
                                                            let! cleanup = WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope attempt

                                                            return
                                                                match cleanup with
                                                                | WorkingDirectoryUpdateCoordination.MarkerCleanup.ExactMatchCleaned
                                                                | WorkingDirectoryUpdateCoordination.MarkerCleanup.NoMarker ->
                                                                    rejected $"[{correlationValue}] {error}"
                                                                | _ -> rejected $"[{correlationValue}] {error}; exact marker cleanup failed."
                                                        | Ok finalConfiguration when not (CanonicalConfiguration.matches sealedConfiguration finalConfiguration) ->
                                                            let! cleanup = WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope attempt

                                                            return
                                                                match cleanup with
                                                                | WorkingDirectoryUpdateCoordination.MarkerCleanup.ExactMatchCleaned
                                                                | WorkingDirectoryUpdateCoordination.MarkerCleanup.NoMarker ->
                                                                    rejected $"[{correlationValue}] Local configuration changed before the first mutation."
                                                                | _ ->
                                                                    rejected
                                                                        $"[{correlationValue}] Local configuration changed before mutation and exact marker cleanup failed."
                                                        | Ok finalConfiguration ->
                                                            let! finalRevision = LocalStateDb.readLocalStatusRevisionReadOnly dbPath

                                                            let! finalStatusResult =
                                                                LocalStateDb.readCompleteStatusSnapshotReadOnly
                                                                    dbPath
                                                                    finalConfiguration.OwnerId
                                                                    finalConfiguration.OrganizationId
                                                                    finalConfiguration.RepositoryId

                                                            let finalStatus =
                                                                match finalStatusResult with
                                                                | Ok status -> status
                                                                | Error error -> invalidOp error

                                                            let! finalPending = LocalStateDb.readPendingWorkingDirectoryUpdateFinalization dbPath
                                                            let! finalMarker = WorkingDirectoryUpdateCoordination.Marker.inspect scope target operation

                                                            let! finalMarkerAttempt =
                                                                WorkingDirectoryUpdateCoordination.Marker.inspectExactAttempt scope target operation attempt

                                                            if
                                                                finalRevision
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
                                                                        rejected
                                                                            $"[{correlationValue}] Working Directory Update facts changed before the first mutation."
                                                                    | _ ->
                                                                        rejected
                                                                            $"[{correlationValue}] Working Directory Update facts changed before mutation and exact marker cleanup failed."
                                                            else
                                                                actionToken.ThrowIfCancellationRequested()
                                                                LocalTransactionTesting.beforeFinalPlanningNow ()

                                                                let! planResult =
                                                                    Topology.planWithScanInput
                                                                        (CanonicalConfiguration.scanInput finalConfiguration)
                                                                        finalStatus
                                                                        manifest

                                                                actionToken.ThrowIfCancellationRequested()

                                                                match planResult with
                                                                | Topology.Rejected rejection ->
                                                                    let! cleanup = WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope attempt
                                                                    let path = Topology.Rejection.path rejection

                                                                    return
                                                                        match cleanup with
                                                                        | WorkingDirectoryUpdateCoordination.MarkerCleanup.ExactMatchCleaned
                                                                        | WorkingDirectoryUpdateCoordination.MarkerCleanup.NoMarker ->
                                                                            rejected
                                                                                $"[{correlationValue}] Working Directory Update topology rejected '{path}'."
                                                                        | _ ->
                                                                            rejected
                                                                                $"[{correlationValue}] Working Directory Update topology rejected '{path}' and exact marker cleanup failed."
                                                                | Topology.Planned plan ->
                                                                    let actions = Topology.Plan.actions plan |> List.toArray
                                                                    let objectHashes = Dictionary<string, Sha256Hash * Blake3Hash>(StringComparer.Ordinal)

                                                                    WorkingDirectoryUpdateContracts.PreparedManifest.entries manifest
                                                                    |> Seq.iter (function
                                                                        | WorkingDirectoryUpdateContracts.PreparedManifestEntry.File (path,
                                                                                                                                      sha256Hash,
                                                                                                                                      blake3Hash) ->
                                                                            objectHashes[pathKey path] <- (sha256Hash, blake3Hash)
                                                                        | WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory _ -> ())

                                                                    LocalTransactionTesting.beforeFinalGlobalFactGateNow ()

                                                                    let! globalFacts =
                                                                        verifyFinalGlobalFacts
                                                                            dbPath
                                                                            sealedConfiguration
                                                                            acceptedPhase
                                                                            targetStatus
                                                                            manifest
                                                                            scope
                                                                            target
                                                                            operation
                                                                            attempt

                                                                    match globalFacts with
                                                                    | Error error ->
                                                                        let! cleanup = WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope attempt

                                                                        return
                                                                            match cleanup with
                                                                            | WorkingDirectoryUpdateCoordination.MarkerCleanup.ExactMatchCleaned
                                                                            | WorkingDirectoryUpdateCoordination.MarkerCleanup.NoMarker ->
                                                                                rejected $"[{correlationValue}] {error}"
                                                                            | _ -> rejected $"[{correlationValue}] {error}; exact marker cleanup failed."
                                                                    | Ok executionConfiguration ->
                                                                        LocalTransactionTesting.afterFinalGlobalFactGateNow ()

                                                                        let mutable actionFailure =
                                                                            match
                                                                                Topology.planSynchronouslyWithScanInput
                                                                                    (CanonicalConfiguration.scanInput executionConfiguration)
                                                                                    finalStatus
                                                                                    manifest
                                                                                with
                                                                            | Topology.Planned revalidatedPlan when
                                                                                (Topology.Plan.actions revalidatedPlan
                                                                                 |> List.toArray) = actions
                                                                                ->
                                                                                None
                                                                            | Topology.Planned _ ->
                                                                                Some
                                                                                    "The complete Working Directory Update plan changed before the first mutation."
                                                                            | Topology.Rejected rejection ->
                                                                                Some
                                                                                    $"Working Directory Update topology rejected '{Topology.Rejection.path rejection}' before the first mutation."

                                                                        let mutable index = 0

                                                                        while index < actions.Length
                                                                              && Option.isNone actionFailure do
                                                                            let action = actions[index]

                                                                            match verifyActionPrecondition
                                                                                      executionConfiguration.RootDirectory
                                                                                      finalStatus
                                                                                      action,
                                                                                  verifyActionObject executionConfiguration.ObjectDirectory objectHashes action
                                                                                with
                                                                            | Ok (), Ok () ->
                                                                                let actionMutates = actionCanMutate finalStatus action

                                                                                if not mutationStarted && actionMutates then
                                                                                    actionToken.ThrowIfCancellationRequested()
                                                                                    LocalTransactionTesting.beforeFirstMutationNow ()
                                                                                    actionToken.ThrowIfCancellationRequested()

                                                                                if actionMutates then mutationStarted <- true

                                                                                applyAction
                                                                                    executionConfiguration.RootDirectory
                                                                                    executionConfiguration.ObjectDirectory
                                                                                    objectHashes
                                                                                    action
                                                                                |> ignore
                                                                            | Error error, _
                                                                            | _, Error error -> actionFailure <- Some error

                                                                            index <- index + 1

                                                                        match actionFailure with
                                                                        | Some error when mutationStarted -> return incomplete $"[{correlationValue}] {error}"
                                                                        | Some error ->
                                                                            let! cleanup =
                                                                                WorkingDirectoryUpdateCoordination.Marker.tryRemoveOwned scope attempt

                                                                            return
                                                                                match cleanup with
                                                                                | WorkingDirectoryUpdateCoordination.MarkerCleanup.ExactMatchCleaned
                                                                                | WorkingDirectoryUpdateCoordination.MarkerCleanup.NoMarker ->
                                                                                    rejected $"[{correlationValue}] {error}"
                                                                                | _ -> rejected $"[{correlationValue}] {error}; exact marker cleanup failed."
                                                                        | None ->
                                                                            LocalTransactionTesting.afterPlannedActionsNow ()

                                                                            if
                                                                                not
                                                                                    (
                                                                                        verifyCompleteTargetRoot
                                                                                            executionConfiguration.RootDirectory
                                                                                            executionConfiguration.GraceDirectory
                                                                                            targetStatus
                                                                                    )
                                                                            then
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

                                                                                return Ok(LocalCompletion(target, operation))
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
                try
                    return! runCore acceptedPhase selection resolvedGraph preparedContent correlation
                finally
                    WorkingDirectoryUpdateContracts.PreparedContent.dispose preparedContent
            }
