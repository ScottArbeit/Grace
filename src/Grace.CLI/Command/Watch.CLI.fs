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

    let mutable private enumerateFilesForDirectoryUpload = fun directoryPath -> Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)

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

    /// Coordinates set grace status for watch tests behavior for this CLI command path.
    let internal setGraceStatusForWatchTests status = graceStatus <- status
    /// Checks whether set grace status has changed for watch tests is true for the parsed command input.
    let internal setGraceStatusHasChangedForWatchTests hasChanged = graceStatusHasChanged <- hasChanged
    /// Coordinates set read grace status file for watch tests behavior for this CLI command path.
    let internal setReadGraceStatusFileForWatchTests readStatusFile = readGraceStatusFileForDeletedPathClassification <- readStatusFile
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

    /// Lists missing parent directories that must be added before a new uploaded file can link into GraceStatus.
    let private deriveParentDirectoryAddDifferences (status: GraceStatus) (relativePath: RelativePath) =
        let differences = List<FileSystemDifference>()
        let normalizedRelativePath = normalizeRelativePath relativePath
        let segments = normalizedRelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)

        if segments.Length > 1 then
            for index in 0 .. segments.Length - 2 do
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

    /// Reads the best available GraceStatus snapshot for classifying directory-create observations.
    let private readGraceStatusForDirectoryAddClassification () =
        if (not graceStatusHasChanged)
           && graceStatus.Index.Count > 0 then
            graceStatus
        else
            try
                (readGraceStatusFileForDeletedPathClassification ())
                    .GetAwaiter()
                    .GetResult()
            with
            | _ -> GraceStatus.Default

    /// Queues a directory-only structural add when final path state contains a new non-ignored directory.
    let private enqueueDirectoryStatusAdd fullPath =
        if
            Directory.Exists(fullPath)
            && shouldNotIgnoreDirectory fullPath
        then
            match repositoryRelativePath fullPath with
            | Some relativePath ->
                let relativePath = RelativePath relativePath
                let status = readGraceStatusForDirectoryAddClassification ()

                let canonicalRelativePath = canonicalizeTrackedAncestorCasing status relativePath

                if not (isTrackedDirectory status canonicalRelativePath) then
                    addPendingStatusDifference (FileSystemDifference.Create Add FileSystemEntryType.Directory canonicalRelativePath)
                    true
                else
                    false
            | None -> false
        else
            false

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

        Interlocked.Exchange(&statusUpdateTriggerGeneration, 0L)
        |> ignore

        Interlocked.Exchange(&fileUploadWorkGeneration, 0L)
        |> ignore

        graceStatus <- GraceStatus.Default
        graceStatusHasChanged <- false
        readGraceStatusFileForDeletedPathClassification <- readGraceStatusFile
        enumerateFilesForDirectoryUpload <- fun directoryPath -> Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)
        watchPathComparison <- defaultWatchPathComparison ()
        watchPathComparisonOverride <- None
        watchPathComparisonConfiguredRoot <- None
        repositoryPathCaseInsensitiveLookupForWatch <- detectRepositoryPathCaseInsensitiveLookup
        fileExistsForWatchFinalPath <- File.Exists
        directoryExistsForWatchFinalPath <- Directory.Exists
        createLocalFileVersionForWatchStatus <- createLocalFileVersion
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
        if
            updateNotInProgress ()
            && Directory.Exists(args.FullPath)
        then
            if enqueueDirectoryStatusAdd args.FullPath then
                logToAnsiConsole Colors.Added $"I saw that directory {args.FullPath} was created."
        elif updateNotInProgress ()
             && isNotDirectory args.FullPath then
            if enqueueFileUpload args.FullPath then
                logToAnsiConsole Colors.Added $"I saw that {args.FullPath} was created."

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
    let private enqueueDirectoryContentsForUpload directoryPath = tryEnqueueDirectoryContentsForUpload directoryPath

    /// Retries directory content enumeration before status application consumes directory rename-old triggers.
    let private retryPendingDirectoryUploads () =
        let mutable unitValue = ()

        for directoryPath in directoriesToProcess.Keys.ToArray() do
            if enqueueDirectoryContentsForUpload directoryPath then
                directoriesToProcess.TryRemove(directoryPath, &unitValue)
                |> ignore

    /// Coordinates on deleted behavior for this CLI command path.
    let OnDeleted (args: FileSystemEventArgs) =
        if updateNotInProgress () then
            let canceledFileUpload = cancelPendingUploadsForDeletedPath args.FullPath

            if enqueueStatusUpdateTrigger args.FullPath then
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

            if (isGraceStatusArtifact args.FullPath)
               && (not <| graceStatusHasChanged) then
                graceStatusHasChanged <- true

    /// Coordinates on renamed behavior for this CLI command path.
    let OnRenamed (args: RenamedEventArgs) =
        if updateNotInProgress () then
            let newPathIsDirectory = Directory.Exists(args.FullPath)

            let canceledFileUpload = cancelPendingUploadsForDeletedPath args.OldFullPath

            let oldPathStatusQueued = enqueueStatusUpdateTrigger args.OldFullPath

            if oldPathStatusQueued then
                if canceledFileUpload then
                    match repositoryRelativePath args.OldFullPath with
                    | Some relativePath -> addCanceledFileUploadDeleteRelativePath (RelativePath relativePath)
                    | None -> ()

                logToAnsiConsole Colors.Changed $"I saw that {args.OldFullPath} was renamed to {args.FullPath}."

            if newPathIsDirectory then
                if not (enqueueDirectoryContentsForUpload args.FullPath) then
                    enqueueDirectoryUploadRetry args.FullPath
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
        _scanForDifferencesClient
        updateGraceStatusFromDifferencesClient
        applyGraceStatusIncrementalClient
        updateGraceWatchInterprocessFileClient
        =
        task {
            configureWatchPathComparisonForCurrentRepository ()

            // First, check if there's anything to process.
            if hasPendingWatchWork () then
                try
                    let correlationId = generateCorrelationId ()
                    let! graceStatusFromDisk = readGraceStatusMetaClient ()
                    graceStatus <- graceStatusFromDisk

                    let mutable lastFileUploadInstant = graceStatus.LastSuccessfulFileUpload
                    let mutable processedAnyFile = false

                    retryPendingDirectoryUploads ()

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
                            | Some relativePath -> addProcessedFileRelativePathPendingStatus (RelativePath relativePath)
                            | None -> ()

                            processedAnyFile <- true
                            lastFileUploadInstant <- getCurrentInstant ()

                    if processedAnyFile then
                        graceStatus <- { graceStatus with LastSuccessfulFileUpload = lastFileUploadInstant }
                        do! applyGraceStatusIncrementalClient graceStatus Seq.empty Seq.empty

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
                            elif statusDifferencesForApply.Applicable.Count > 0 then
                                updateGraceStatusFromDifferencesClient graceStatus statusDifferencesForApply.Applicable correlationId
                            else
                                Task.FromResult(Some graceStatus)

                        match statusUpdateResult with
                        | Some newGraceStatus ->
                            let statusUpdateCanCommit =
                                filesToProcess.IsEmpty
                                && Volatile.Read(&fileUploadWorkGeneration) = fileWorkGenerationBeforeStatusUpdate

                            if statusUpdateCanCommit then
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
                                clearProcessedFileRelativePathsPendingStatus processedFileRelativePathsForStatus
                                removeUploadedFileVersionsForPaths processedFileRelativePathsForStatus
                            else
                                clearPendingStatusDifferences pendingDifferencesToClear
                                clearProcessedFileRelativePathsPendingStatus processedFileRelativePathsForStatus
                                removeUploadedFileVersionsForPaths processedFileRelativePathsForStatus

                                logToAnsiConsole
                                    Colors.Important
                                    $"Grace Status file update completed while newer file upload work was pending; status-only triggers will retry."
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
