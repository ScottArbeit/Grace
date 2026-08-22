namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Constants
open Grace.Types.Common
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite

/// Exercises the complete hash-selected Branch update against a real working tree and SQLite database.
module WorkingDirectoryUpdateBranchDirectoryVersionTests =
    /// Returns a private-contract value or fails at the construction boundary.
    let private required =
        function
        | Ok value -> value
        | Error error -> invalidOp error

    /// Supplies one immutable in-memory prepared-content reader.
    type private ByteReader(path: string, bytes: byte array) =
        interface WorkingDirectoryUpdateContracts.IPreparedContentReader with
            member _.FilePaths = [ path ]
            member _.OpenReadAsync(_, _) = Task.FromResult(new MemoryStream(bytes, writable = false) :> Stream)
            member _.Dispose() = ()

    /// Computes both supported content hashes for deterministic fixture bytes.
    let private hashes (bytes: byte array) =
        let sha256 =
            SHA256.HashData(bytes)
            |> Convert.ToHexString
            |> fun value -> Sha256Hash(value.ToLowerInvariant())

        sha256, Blake3Hash(ContentAddress.computeBlake3Hex bytes)

    /// Creates one complete root status, optionally containing a single direct file.
    let private status (configuration: GraceConfiguration) (rootId: DirectoryVersionId) (file: (string * byte array) option) =
        let files =
            match file with
            | Some (path, bytes) ->
                let sha256, blake3 = hashes bytes

                [|
                    LocalFileVersion.CreateWithHashes
                        (RelativePath path)
                        sha256
                        blake3
                        false
                        (int64 bytes.Length)
                        (Grace.Shared.Utilities.getCurrentInstant ())
                        true
                        DateTime.UtcNow
                |]
            | None -> Array.empty

        let entries =
            files
            |> Array.map (fun item -> Services.DirectoryVersionPreimageEntry.File item.RelativePath item.Size item.Blake3Hash item.Sha256Hash)

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
                (List<LocalFileVersion>(files))
                (entries |> Array.sumBy (fun entry -> entry.Size))
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

    /// Configures one disposable Product V1 repository and restores process-global configuration afterward.
    let private withRepo action =
        let root = Path.Combine(Path.GetTempPath(), $"grace-wdu-branch-dv-{Guid.NewGuid():N}")
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

    /// Creates a fully prepared request for the target status and one-file manifest.
    let private request (configuration: GraceConfiguration) (targetStatus: GraceStatus) (targetRoot: LocalDirectoryVersion) (path: string) (bytes: byte array) =
        let sha256, blake3 = hashes bytes

        let manifest =
            WorkingDirectoryUpdateContracts.PreparedManifest.create [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.File(
                                                                          RelativePath path,
                                                                          sha256,
                                                                          blake3
                                                                      ) ]
            |> required

        let prepared =
            WorkingDirectoryUpdateContracts.PreparedContent.create manifest (new ByteReader(path, bytes)) CancellationToken.None
            |> fun task -> task.GetAwaiter().GetResult()
            |> required

        let target =
            WorkingDirectoryUpdateContracts.Target.create
                configuration.RepositoryId
                configuration.BranchId
                targetStatus.RootDirectoryId
                targetStatus.RootDirectorySha256Hash
                targetStatus.RootDirectoryBlake3Hash
            |> required

        let operation =
            WorkingDirectoryUpdateContracts.Operation.branchSwitchWithSelection
                configuration.BranchId
                WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion
                target
            |> required

        WorkingDirectoryUpdateContracts.Request.create target operation prepared $"{Guid.NewGuid():N}"
        |> required,
        manifest,
        [| targetRoot |]

    /// Proves success commits exact bytes and replay returns Unchanged without changing Branch identity.
    [<Test>]
    let ``DirectoryVersion Branch update commits Updated then replays Unchanged without changing Branch identity`` () =
        withRepo (fun root configuration ->
            let selectedBytes = Encoding.UTF8.GetBytes("selected hash version")
            let currentStatus, _ = status configuration (Guid.NewGuid()) None
            let targetStatus, targetRoot = status configuration (Guid.NewGuid()) (Some("selected.txt", selectedBytes))

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile currentStatus
            |> fun task -> task.GetAwaiter().GetResult() |> ignore

            let branchBefore = configuration.BranchId
            let firstRequest, manifest, metadata = request configuration targetStatus targetRoot "selected.txt" selectedBytes

            let cleanupFailure =
                { WorkingDirectoryUpdate.BranchDirectoryVersion.none with DeleteMarker = fun _ -> raise (IOException("injected marker cleanup failure")) }

            WorkingDirectoryUpdate.BranchDirectoryVersion.run
                firstRequest
                currentStatus
                targetStatus
                metadata
                manifest
                root
                configuration.GraceStatusFile
                CancellationToken.None
                cleanupFailure
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | WorkingDirectoryUpdateContracts.Outcome.Updated receipt ->
                    WorkingDirectoryUpdateContracts.Receipt.bytesChanged receipt
                    |> should equal true
                | outcome -> Assert.Fail($"Expected Updated, got {outcome}.")

            File.ReadAllBytes(Path.Combine(root, "selected.txt"))
            |> should equal selectedBytes

            Current().BranchId |> should equal branchBefore

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId root
                |> required

            File.Exists(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
            |> should equal true

            let replayRequest, replayManifest, replayMetadata = request configuration targetStatus targetRoot "selected.txt" selectedBytes

            WorkingDirectoryUpdate.BranchDirectoryVersion.run
                replayRequest
                targetStatus
                targetStatus
                replayMetadata
                replayManifest
                root
                configuration.GraceStatusFile
                CancellationToken.None
                WorkingDirectoryUpdate.BranchDirectoryVersion.none
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | WorkingDirectoryUpdateContracts.Outcome.Unchanged receipt ->
                    WorkingDirectoryUpdateContracts.Receipt.bytesChanged receipt
                    |> should equal false
                | outcome -> Assert.Fail($"Expected replay Unchanged, got {outcome}.")

            File.Exists(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
            |> should equal false

            Current().BranchId |> should equal branchBefore)

    /// Proves a failure after the first working-file effect retains evidence and never commits terminal SQLite truth.
    [<Test>]
    let ``DirectoryVersion Branch mid-application failure returns UpdateIncomplete without completion`` () =
        withRepo (fun root configuration ->
            let selectedBytes = Encoding.UTF8.GetBytes("selected hash version")
            let currentStatus, _ = status configuration (Guid.NewGuid()) None
            let targetStatus, targetRoot = status configuration (Guid.NewGuid()) (Some("selected.txt", selectedBytes))

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile currentStatus
            |> fun task -> task.GetAwaiter().GetResult() |> ignore

            let updateRequest, manifest, metadata = request configuration targetStatus targetRoot "selected.txt" selectedBytes

            let injection =
                { WorkingDirectoryUpdate.BranchDirectoryVersion.none with
                    ThrowAt =
                        fun point ->
                            if point = WorkingDirectoryUpdate.BranchDirectoryVersion.DuringApplication then
                                raise (IOException("injected mid-application"))
                }

            WorkingDirectoryUpdate.BranchDirectoryVersion.run
                updateRequest
                currentStatus
                targetStatus
                metadata
                manifest
                root
                configuration.GraceStatusFile
                CancellationToken.None
                injection
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | WorkingDirectoryUpdateContracts.Outcome.UpdateIncomplete _ -> ()
                | outcome -> Assert.Fail($"Expected UpdateIncomplete, got {outcome}.")

            File.Exists(Path.Combine(root, "selected.txt"))
            |> should equal true)

    /// Proves contradictory valid marker evidence is rejected and retained without working-tree mutation.
    [<Test>]
    let ``DirectoryVersion Branch contradictory marker is rejected and retained`` () =
        withRepo (fun root configuration ->
            let selectedBytes = Encoding.UTF8.GetBytes("selected hash version")
            let currentStatus, _ = status configuration (Guid.NewGuid()) None
            let targetStatus, targetRoot = status configuration (Guid.NewGuid()) (Some("selected.txt", selectedBytes))

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile currentStatus
            |> fun task -> task.GetAwaiter().GetResult() |> ignore

            let updateRequest, manifest, metadata = request configuration targetStatus targetRoot "selected.txt" selectedBytes
            let target = WorkingDirectoryUpdateContracts.Request.target updateRequest

            let contradictoryTarget =
                WorkingDirectoryUpdateContracts.Target.create
                    configuration.RepositoryId
                    configuration.BranchId
                    (Guid.NewGuid())
                    targetStatus.RootDirectorySha256Hash
                    targetStatus.RootDirectoryBlake3Hash
                |> required

            let contradictoryOperation =
                WorkingDirectoryUpdateContracts.Operation.branchSwitchWithSelection
                    configuration.BranchId
                    WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion
                    contradictoryTarget
                |> required

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId root
                |> required

            let marker =
                WorkingDirectoryUpdateCoordination.Marker.create
                    scope
                    (WorkingDirectoryUpdateContracts.AttemptToken.create ())
                    contradictoryTarget
                    contradictoryOperation
                |> required

            WorkingDirectoryUpdateCoordination.Marker.write scope marker
            |> fun task -> task.GetAwaiter().GetResult()

            let markerBefore = File.ReadAllText(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)

            WorkingDirectoryUpdate.BranchDirectoryVersion.run
                updateRequest
                currentStatus
                targetStatus
                metadata
                manifest
                root
                configuration.GraceStatusFile
                CancellationToken.None
                WorkingDirectoryUpdate.BranchDirectoryVersion.none
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | WorkingDirectoryUpdateContracts.Outcome.Rejected _ -> ()
                | outcome -> Assert.Fail($"Expected contradictory marker rejection, got {outcome}.")

            File.Exists(Path.Combine(root, "selected.txt"))
            |> should equal false

            File.ReadAllText(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
            |> should equal markerBefore)

    /// Proves SQLite failure at the real pre-commit callback rolls back status, metadata, and terminal completion together.
    [<Test>]
    let ``DirectoryVersion Branch SQLite pre-commit failure rolls back terminal completion`` () =
        withRepo (fun root configuration ->
            let selectedBytes = Encoding.UTF8.GetBytes("selected hash version")
            let currentStatus, _ = status configuration (Guid.NewGuid()) None
            let targetStatus, targetRoot = status configuration (Guid.NewGuid()) (Some("selected.txt", selectedBytes))

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile currentStatus
            |> fun task -> task.GetAwaiter().GetResult() |> ignore

            let updateRequest, manifest, metadata = request configuration targetStatus targetRoot "selected.txt" selectedBytes
            let target = WorkingDirectoryUpdateContracts.Request.target updateRequest
            let operation = WorkingDirectoryUpdateContracts.Request.operation updateRequest

            let injection =
                { WorkingDirectoryUpdate.BranchDirectoryVersion.none with
                    ThrowAt =
                        fun point ->
                            if point = WorkingDirectoryUpdate.BranchDirectoryVersion.BeforeCommit then
                                raise (IOException("injected transaction rollback"))
                }

            WorkingDirectoryUpdate.BranchDirectoryVersion.run
                updateRequest
                currentStatus
                targetStatus
                metadata
                manifest
                root
                configuration.GraceStatusFile
                CancellationToken.None
                injection
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | WorkingDirectoryUpdateContracts.Outcome.UpdateIncomplete _ -> ()
                | outcome -> Assert.Fail($"Expected transaction UpdateIncomplete, got {outcome}.")

            LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target operation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal None

            let persisted =
                LocalStateDb.readStatusSnapshot configuration.GraceStatusFile
                |> fun task -> task.GetAwaiter().GetResult()

            persisted.RootDirectoryId
            |> should equal currentStatus.RootDirectoryId)
