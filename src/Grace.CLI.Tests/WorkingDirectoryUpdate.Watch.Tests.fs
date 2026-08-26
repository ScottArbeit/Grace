namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Constants
open Grace.Types.Common
open Microsoft.Data.Sqlite
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

/// Exercises Watch replay completion through the shared Working Directory Update boundary.
module WorkingDirectoryUpdateWatchTests =
    /// Returns a private-contract value or fails at the construction boundary.
    let private required =
        function
        | Ok value -> value
        | Error error -> invalidOp error

    /// Computes both supported content hashes for deterministic fixture bytes.
    let private hashes (bytes: byte array) =
        let sha256 =
            SHA256.HashData(bytes)
            |> Convert.ToHexString
            |> fun value -> Sha256Hash(value.ToLowerInvariant())

        sha256, Blake3Hash(ContentAddress.computeBlake3Hex bytes)

    /// Supplies one immutable in-memory prepared-content reader.
    type private ByteReader(path: string, bytes: byte array) =
        interface WorkingDirectoryUpdateContracts.IPreparedContentReader with
            member _.FilePaths = [ path ]
            member _.OpenReadAsync(_, _) = Task.FromResult(new MemoryStream(bytes, writable = false) :> Stream)
            member _.Dispose() = ()

    /// Creates one complete root status containing a single direct file.
    let private status (configuration: GraceConfiguration) (rootId: DirectoryVersionId) (path: string) (bytes: byte array) =
        let sha256, blake3 = hashes bytes

        let file =
            LocalFileVersion.CreateWithHashes
                (RelativePath path)
                sha256
                blake3
                false
                (int64 bytes.Length)
                (Grace.Shared.Utilities.getCurrentInstant ())
                true
                DateTime.UtcNow

        let entries =
            [|
                Services.DirectoryVersionPreimageEntry.File file.RelativePath file.Size file.Blake3Hash file.Sha256Hash
            |]

        let root =
            LocalDirectoryVersion.CreateWithHashes
                rootId
                configuration.OwnerId
                configuration.OrganizationId
                configuration.RepositoryId
                (RelativePath RootDirectoryPath)
                (Services.computeSha256ForDirectoryEntries (RelativePath RootDirectoryPath) entries)
                (Services.computeBlake3ForDirectory (RelativePath RootDirectoryPath) entries)
                (List<DirectoryVersionId>())
                (List<LocalFileVersion>([| file |]))
                file.Size
                DateTime.UtcNow

        let index = GraceIndex()
        index[root.DirectoryVersionId] <- root

        { GraceStatus.Default with
            Index = index
            RootDirectoryId = root.DirectoryVersionId
            RootDirectorySha256Hash = root.Sha256Hash
            RootDirectoryBlake3Hash = root.Blake3Hash
        },
        root

    /// Builds exact prepared content for one single-file target status.
    let private preparedContent (path: string) bytes =
        let sha256, blake3 = hashes bytes

        let manifest =
            WorkingDirectoryUpdateContracts.PreparedManifest.create [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.File(
                                                                          RelativePath path,
                                                                          sha256,
                                                                          blake3
                                                                      ) ]
            |> required

        WorkingDirectoryUpdateContracts.PreparedContent.create manifest (new ByteReader(path, bytes)) CancellationToken.None
        |> fun task -> task.GetAwaiter().GetResult()
        |> required

    /// Persists one exact pending Watch completion and returns its retry identity.
    let private seedPendingWatchCompletion (configuration: GraceConfiguration) (targetStatus: GraceStatus) (targetRoot: LocalDirectoryVersion) eventCursor =
        let target =
            WorkingDirectoryUpdateContracts.Target.create
                configuration.RepositoryId
                configuration.BranchId
                targetStatus.RootDirectoryId
                targetStatus.RootDirectorySha256Hash
                targetStatus.RootDirectoryBlake3Hash
            |> required

        let operation =
            WorkingDirectoryUpdateContracts.Operation.watchReplay configuration.RepositoryId configuration.BranchId eventCursor
            |> required

        LocalStateDb.commitWorkingDirectoryUpdateCompletion
            configuration.GraceStatusFile
            targetStatus
            [| targetRoot |]
            (LocalStateDb.WorkingDirectoryUpdateCompletionDetails.WatchFinalization eventCursor)
            target
            operation
        |> fun task -> task.GetAwaiter().GetResult()
        |> ignore

        target, operation

    /// Writes exact WDU marker evidence for one pending Watch completion.
    let private writeExactMarker (configuration: GraceConfiguration) root target operation =
        let scope =
            WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId root
            |> required

        let marker =
            WorkingDirectoryUpdateCoordination.Marker.create scope (WorkingDirectoryUpdateContracts.AttemptToken.create ()) target operation
            |> required

        WorkingDirectoryUpdateCoordination.Marker.write scope marker
        |> fun task -> task.GetAwaiter().GetResult()

        scope

    /// Configures one disposable Product V1 repository and restores process-global configuration afterward.
    let private withRepo action =
        let root = Path.Combine(Path.GetTempPath(), $"grace-wdu-watch-{Guid.NewGuid():N}")
        let originalDirectory = Environment.CurrentDirectory
        let originalParseResult = Services.parseResult

        try
            Directory.CreateDirectory(root) |> ignore
            Environment.CurrentDirectory <- root
            let configuration = GraceConfiguration()
            configuration.OwnerId <- Guid.NewGuid()
            configuration.OrganizationId <- Guid.NewGuid()
            configuration.RepositoryId <- Guid.NewGuid()
            configuration.BranchId <- Guid.NewGuid()
            configuration.RootDirectory <- root
            configuration.StandardizedRootDirectory <- Grace.Shared.Utilities.normalizeFilePath root
            configuration.GraceDirectory <- Path.Combine(root, GraceConfigDirectory)
            configuration.ObjectDirectory <- Path.Combine(configuration.GraceDirectory, GraceObjectsDirectory)
            configuration.GraceStatusFile <- Path.Combine(configuration.GraceDirectory, GraceLocalStateDbFileName)
            configuration.GraceObjectCacheFile <- configuration.GraceStatusFile
            configuration.ConfigurationDirectory <- configuration.GraceDirectory

            Directory.CreateDirectory(configuration.ConfigurationDirectory)
            |> ignore

            saveConfigFile (Path.Combine(configuration.ConfigurationDirectory, GraceConfigFileName)) configuration
            resetConfiguration ()
            Services.parseResult <- GraceCommand.rootCommand.Parse(Array.empty<string>)
            action root (Current())
        finally
            Services.clearShouldIgnoreCache ()
            Services.parseResult <- originalParseResult
            Environment.CurrentDirectory <- originalDirectory
            resetConfiguration ()
            SqliteConnection.ClearAllPools()

            if Directory.Exists(root) then Directory.Delete(root, true)

    /// Proves restart consumes only pending Watch completion facts and cannot rewrite already-verified working bytes.
    [<Test>]
    let ``Watch restart terminalizes pending local completion without rewriting working files`` () =
        withRepo (fun root configuration ->
            let path = "watched.txt"
            let bytes = Encoding.UTF8.GetBytes("verified Watch replay bytes")
            let targetStatus, targetRoot = status configuration (DirectoryVersionId.NewGuid()) path bytes
            let eventCursor = "watch-cursor-0001"

            let target =
                WorkingDirectoryUpdateContracts.Target.create
                    configuration.RepositoryId
                    configuration.BranchId
                    targetStatus.RootDirectoryId
                    targetStatus.RootDirectorySha256Hash
                    targetStatus.RootDirectoryBlake3Hash
                |> required

            let operation =
                WorkingDirectoryUpdateContracts.Operation.watchReplay configuration.RepositoryId configuration.BranchId eventCursor
                |> required

            LocalStateDb.commitWorkingDirectoryUpdateCompletion
                configuration.GraceStatusFile
                targetStatus
                [| targetRoot |]
                (LocalStateDb.WorkingDirectoryUpdateCompletionDetails.WatchFinalization eventCursor)
                target
                operation
            |> fun task -> task.GetAwaiter().GetResult()
            |> ignore

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId root
                |> required

            let marker =
                WorkingDirectoryUpdateCoordination.Marker.create scope (WorkingDirectoryUpdateContracts.AttemptToken.create ()) target operation
                |> required

            WorkingDirectoryUpdateCoordination.Marker.write scope marker
            |> fun task -> task.GetAwaiter().GetResult()

            let workingPath = Path.Combine(root, path)
            File.WriteAllBytes(workingPath, bytes)

            let revisionBefore =
                LocalStateDb.readLocalStatusRevisionReadOnly configuration.GraceStatusFile
                |> fun task -> task.Result

            use workingFileLock = new FileStream(workingPath, FileMode.Open, FileAccess.Read, FileShare.Read)

            Grace.CLI.Command.WorkingDirectoryUpdate.Watch.resumePendingFinalization CancellationToken.None Grace.CLI.Command.WorkingDirectoryUpdate.Watch.none
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | Some (actualCursor, WorkingDirectoryUpdateContracts.Outcome.Updated _) -> actualCursor |> should equal eventCursor
                | outcome -> Assert.Fail($"Expected resumed Watch completion, got {outcome}.")

            File.Exists(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
            |> should equal false

            LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target operation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal (Some LocalStateDb.WorkingDirectoryUpdateCompletion.Terminal)

            LocalStateDb.readLocalStatusRevisionReadOnly configuration.GraceStatusFile
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal revisionBefore)

    /// Proves failure after local completion resumes at terminal recording without a second mutation or status rewrite.
    [<Test>]
    let ``Watch local completion survives restart before terminal recording without reapplication`` () =
        withRepo (fun root configuration ->
            let path = "watched.txt"
            let currentBytes = Encoding.UTF8.GetBytes("current bytes")
            let targetBytes = Encoding.UTF8.GetBytes("accepted replay bytes")
            let currentStatus, _ = status configuration (DirectoryVersionId.NewGuid()) path currentBytes
            let targetStatus, targetRoot = status configuration (DirectoryVersionId.NewGuid()) path targetBytes
            let eventCursor = "watch-cursor-after-local-completion"

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile currentStatus
            |> fun task -> task.GetAwaiter().GetResult()
            |> ignore

            let workingPath = Path.Combine(root, path)
            File.WriteAllBytes(workingPath, currentBytes)

            let acceptedRevision =
                LocalStateDb.readLocalStatusRevisionReadOnly configuration.GraceStatusFile
                |> fun task -> task.Result

            let injection =
                { Grace.CLI.Command.WorkingDirectoryUpdate.Watch.none with
                    ThrowAt =
                        fun point ->
                            if point = Grace.CLI.Command.WorkingDirectoryUpdate.Watch.AfterCommit then
                                raise (IOException("injected exit after Watch local completion"))
                }

            Grace.CLI.Command.WorkingDirectoryUpdate.Watch.runAtRevision
                currentStatus
                targetStatus
                [| targetRoot |]
                (preparedContent path targetBytes)
                eventCursor
                "watch-after-local-completion"
                acceptedRevision
                CancellationToken.None
                injection
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | WorkingDirectoryUpdateContracts.Outcome.FinalizationIncomplete _ -> ()
                | outcome -> Assert.Fail($"Expected pending Watch completion, got {outcome}.")

            File.ReadAllBytes(workingPath)
            |> should equal targetBytes

            let pendingRevision =
                LocalStateDb.readLocalStatusRevisionReadOnly configuration.GraceStatusFile
                |> fun task -> task.Result

            use workingFileLock = new FileStream(workingPath, FileMode.Open, FileAccess.Read, FileShare.Read)

            Grace.CLI.Command.WorkingDirectoryUpdate.Watch.resumePendingFinalization CancellationToken.None Grace.CLI.Command.WorkingDirectoryUpdate.Watch.none
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | Some (actualCursor, WorkingDirectoryUpdateContracts.Outcome.Updated _) -> actualCursor |> should equal eventCursor
                | outcome -> Assert.Fail($"Expected resumed Watch terminal completion, got {outcome}.")

            LocalStateDb.readLocalStatusRevisionReadOnly configuration.GraceStatusFile
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal pendingRevision)

    /// Proves a Watch SQLite failure after verified local application remains truthfully incomplete and retryable.
    [<Test>]
    let ``Watch pre-commit failure after verified root returns update incomplete`` () =
        withRepo (fun root configuration ->
            let path = "watch-pre-commit.txt"
            let currentBytes = Encoding.UTF8.GetBytes("current Watch bytes")
            let targetBytes = Encoding.UTF8.GetBytes("verified Watch target bytes")
            let currentStatus, _ = status configuration (DirectoryVersionId.NewGuid()) path currentBytes
            let targetStatus, targetRoot = status configuration (DirectoryVersionId.NewGuid()) path targetBytes
            let eventCursor = "watch-cursor-before-local-commit"

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile currentStatus
            |> fun task -> task.GetAwaiter().GetResult()
            |> ignore

            let workingPath = Path.Combine(root, path)
            File.WriteAllBytes(workingPath, currentBytes)

            let acceptedRevision =
                LocalStateDb.readLocalStatusRevisionReadOnly configuration.GraceStatusFile
                |> fun task -> task.Result

            let injection =
                { Grace.CLI.Command.WorkingDirectoryUpdate.Watch.none with
                    ThrowAt =
                        fun point ->
                            if point = Grace.CLI.Command.WorkingDirectoryUpdate.Watch.BeforeCommit then
                                raise (IOException("injected Watch local completion failure"))
                }

            Grace.CLI.Command.WorkingDirectoryUpdate.Watch.runAtRevision
                currentStatus
                targetStatus
                [| targetRoot |]
                (preparedContent path targetBytes)
                eventCursor
                "watch-before-local-commit"
                acceptedRevision
                CancellationToken.None
                injection
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | WorkingDirectoryUpdateContracts.Outcome.UpdateIncomplete _ -> ()
                | outcome -> Assert.Fail($"Expected incomplete Watch local completion, got {outcome}.")

            File.ReadAllBytes(workingPath)
            |> should equal targetBytes

            let target =
                WorkingDirectoryUpdateContracts.Target.create
                    configuration.RepositoryId
                    configuration.BranchId
                    targetStatus.RootDirectoryId
                    targetStatus.RootDirectorySha256Hash
                    targetStatus.RootDirectoryBlake3Hash
                |> required

            let operation =
                WorkingDirectoryUpdateContracts.Operation.watchReplay configuration.RepositoryId configuration.BranchId eventCursor
                |> required

            LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target operation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal None

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId root
                |> required

            WorkingDirectoryUpdateCoordination.Marker.inspect scope target operation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal WorkingDirectoryUpdateCoordination.MarkerInspection.ExactMatch)

    /// Proves a newer cursor for the same accepted root records its own terminal completion without touching working bytes.
    [<Test>]
    let ``Watch same root newer cursor completes without rewriting working files`` () =
        withRepo (fun root configuration ->
            let path = "watched.txt"
            let bytes = Encoding.UTF8.GetBytes("same accepted root")
            let currentStatus, currentRoot = status configuration (DirectoryVersionId.NewGuid()) path bytes

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile currentStatus
            |> fun task -> task.GetAwaiter().GetResult()
            |> ignore

            let workingPath = Path.Combine(root, path)
            File.WriteAllBytes(workingPath, bytes)
            use workingFileLock = new FileStream(workingPath, FileMode.Open, FileAccess.Read, FileShare.Read)

            [
                "watch-cursor-same-root-1"
                "watch-cursor-same-root-2"
            ]
            |> List.iter (fun eventCursor ->
                let acceptedRevision =
                    LocalStateDb.readLocalStatusRevisionReadOnly configuration.GraceStatusFile
                    |> fun task -> task.Result

                Grace.CLI.Command.WorkingDirectoryUpdate.Watch.runAtRevision
                    currentStatus
                    currentStatus
                    [| currentRoot |]
                    (preparedContent path bytes)
                    eventCursor
                    eventCursor
                    acceptedRevision
                    CancellationToken.None
                    Grace.CLI.Command.WorkingDirectoryUpdate.Watch.none
                |> fun task -> task.GetAwaiter().GetResult()
                |> function
                    | WorkingDirectoryUpdateContracts.Outcome.Unchanged _ -> ()
                    | outcome -> Assert.Fail($"Expected same-root Watch completion, got {outcome}."))

            let finalCursor = "watch-cursor-same-root-2"

            let finalTarget =
                WorkingDirectoryUpdateContracts.Target.create
                    configuration.RepositoryId
                    configuration.BranchId
                    currentStatus.RootDirectoryId
                    currentStatus.RootDirectorySha256Hash
                    currentStatus.RootDirectoryBlake3Hash
                |> required

            let finalOperation =
                WorkingDirectoryUpdateContracts.Operation.watchReplay configuration.RepositoryId configuration.BranchId finalCursor
                |> required

            LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile finalTarget finalOperation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal (Some LocalStateDb.WorkingDirectoryUpdateCompletion.Terminal))

    /// Proves malformed marker evidence retains pending Watch completion and never falls back to file mutation.
    [<Test>]
    let ``Watch finalizer retains pending completion for malformed marker evidence`` () =
        withRepo (fun root configuration ->
            let path = "watched.txt"
            let bytes = Encoding.UTF8.GetBytes("malformed marker bytes")
            let targetStatus, targetRoot = status configuration (DirectoryVersionId.NewGuid()) path bytes
            let target, operation = seedPendingWatchCompletion configuration targetStatus targetRoot "watch-cursor-malformed-marker"

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId root
                |> required

            let markerPath = WorkingDirectoryUpdateCoordination.Scope.markerPath scope

            Directory.CreateDirectory(Path.GetDirectoryName(markerPath))
            |> ignore

            File.WriteAllText(markerPath, "malformed marker evidence")
            let workingPath = Path.Combine(root, path)
            File.WriteAllBytes(workingPath, bytes)
            use workingFileLock = new FileStream(workingPath, FileMode.Open, FileAccess.Read, FileShare.Read)

            Grace.CLI.Command.WorkingDirectoryUpdate.Watch.resumePendingFinalization CancellationToken.None Grace.CLI.Command.WorkingDirectoryUpdate.Watch.none
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | Some (_, WorkingDirectoryUpdateContracts.Outcome.FinalizationIncomplete (_, failure)) ->
                    WorkingDirectoryUpdateContracts.Failure.reason failure
                    |> should contain "retained marker evidence"
                | outcome -> Assert.Fail($"Expected malformed-marker Watch completion, got {outcome}.")

            LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target operation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal (Some LocalStateDb.WorkingDirectoryUpdateCompletion.Pending))

    /// Proves cancellation before retry writes retains exact marker and pending Watch completion.
    [<Test>]
    let ``Watch finalizer cancellation before cleanup retains exact pending evidence`` () =
        withRepo (fun root configuration ->
            let bytes = Encoding.UTF8.GetBytes("cancel before cleanup")
            let targetStatus, targetRoot = status configuration (DirectoryVersionId.NewGuid()) "watched.txt" bytes
            let target, operation = seedPendingWatchCompletion configuration targetStatus targetRoot "watch-cursor-cancel-before-cleanup"
            let scope = writeExactMarker configuration root target operation
            use cancellation = new CancellationTokenSource()
            cancellation.Cancel()

            Grace.CLI.Command.WorkingDirectoryUpdate.Watch.resumePendingFinalization cancellation.Token Grace.CLI.Command.WorkingDirectoryUpdate.Watch.none
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | Some (_, WorkingDirectoryUpdateContracts.Outcome.FinalizationIncomplete _) -> ()
                | outcome -> Assert.Fail($"Expected canceled Watch completion, got {outcome}.")

            File.Exists(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
            |> should equal true

            LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target operation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal (Some LocalStateDb.WorkingDirectoryUpdateCompletion.Pending))

    /// Proves cancellation after exact marker cleanup begins cannot interrupt terminal recording.
    [<Test>]
    let ``Watch finalizer ignores cancellation after exact cleanup begins`` () =
        withRepo (fun root configuration ->
            let bytes = Encoding.UTF8.GetBytes("cancel after cleanup begins")
            let targetStatus, targetRoot = status configuration (DirectoryVersionId.NewGuid()) "watched.txt" bytes
            let target, operation = seedPendingWatchCompletion configuration targetStatus targetRoot "watch-cursor-cancel-after-cleanup"
            let scope = writeExactMarker configuration root target operation
            use cancellation = new CancellationTokenSource()

            let injection =
                { Grace.CLI.Command.WorkingDirectoryUpdate.Watch.none with
                    DeleteMarker =
                        fun markerPath ->
                            cancellation.Cancel()
                            File.Delete(markerPath)
                }

            Grace.CLI.Command.WorkingDirectoryUpdate.Watch.resumePendingFinalization cancellation.Token injection
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | Some (_, WorkingDirectoryUpdateContracts.Outcome.Updated _) -> ()
                | outcome -> Assert.Fail($"Expected terminal Watch completion after cleanup began, got {outcome}.")

            File.Exists(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
            |> should equal false

            LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target operation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal (Some LocalStateDb.WorkingDirectoryUpdateCompletion.Terminal))
