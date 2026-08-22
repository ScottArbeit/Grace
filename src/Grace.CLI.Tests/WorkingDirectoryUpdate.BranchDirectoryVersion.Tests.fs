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

    /// Reads the sealed local-status revision accepted by one prepared test request.
    let private revision (configuration: GraceConfiguration) =
        LocalStateDb.readLocalStatusRevisionReadOnly configuration.GraceStatusFile
        |> fun task -> task.GetAwaiter().GetResult()

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

            let markerBeforeReplay = File.ReadAllText(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)

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
            |> should equal true

            File.ReadAllText(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
            |> should equal markerBeforeReplay

            Current().BranchId |> should equal branchBefore)

    /// Proves a failure after the first working-file effect retains evidence and never commits terminal SQLite truth.
    [<Test>]
    let ``DirectoryVersion Branch mid-application failure returns UpdateIncomplete without completion`` () =
        withRepo (fun root configuration ->
            let branchBefore = Current().BranchId
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
            |> should equal true

            let retryRequest, retryManifest, retryMetadata = request configuration targetStatus targetRoot "selected.txt" selectedBytes

            WorkingDirectoryUpdate.BranchDirectoryVersion.run
                retryRequest
                currentStatus
                targetStatus
                retryMetadata
                retryManifest
                root
                configuration.GraceStatusFile
                CancellationToken.None
                WorkingDirectoryUpdate.BranchDirectoryVersion.none
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | WorkingDirectoryUpdateContracts.Outcome.Updated _ -> ()
                | outcome -> Assert.Fail($"Expected resumed Updated, got {outcome}.")

            LocalStateDb.readStatusSnapshot configuration.GraceStatusFile
            |> fun task -> task.GetAwaiter().GetResult()
            |> fun status -> status.RootDirectoryId
            |> should equal targetStatus.RootDirectoryId

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId root
                |> required

            File.Exists(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
            |> should equal false

            Current().BranchId |> should equal branchBefore)

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

    /// Proves a later hash switch replaces only the prior terminal Branch row and adopts its owned marker residue.
    [<Test>]
    let ``later DirectoryVersion Branch operation replaces prior terminal row and marker residue`` () =
        withRepo (fun root configuration ->
            let firstBytes = Encoding.UTF8.GetBytes("first selected hash version")
            let secondBytes = Encoding.UTF8.GetBytes("second selected hash version")
            let initialStatus, _ = status configuration (Guid.NewGuid()) None
            let firstStatus, firstRoot = status configuration (Guid.NewGuid()) (Some("first.txt", firstBytes))
            let secondStatus, secondRoot = status configuration (Guid.NewGuid()) (Some("second.txt", secondBytes))

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile initialStatus
            |> fun task -> task.GetAwaiter().GetResult() |> ignore

            let firstRequest, firstManifest, firstMetadata = request configuration firstStatus firstRoot "first.txt" firstBytes
            let firstTarget = WorkingDirectoryUpdateContracts.Request.target firstRequest
            let firstOperation = WorkingDirectoryUpdateContracts.Request.operation firstRequest

            let cleanupFailure =
                { WorkingDirectoryUpdate.BranchDirectoryVersion.none with DeleteMarker = fun _ -> raise (IOException("retain first terminal marker")) }

            WorkingDirectoryUpdate.BranchDirectoryVersion.run
                firstRequest
                initialStatus
                firstStatus
                firstMetadata
                firstManifest
                root
                configuration.GraceStatusFile
                CancellationToken.None
                cleanupFailure
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | WorkingDirectoryUpdateContracts.Outcome.Updated _ -> ()
                | outcome -> Assert.Fail($"Expected first Updated, got {outcome}.")

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId root
                |> required

            File.Exists(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
            |> should equal true

            let secondRequest, secondManifest, secondMetadata = request configuration secondStatus secondRoot "second.txt" secondBytes
            let secondTarget = WorkingDirectoryUpdateContracts.Request.target secondRequest
            let secondOperation = WorkingDirectoryUpdateContracts.Request.operation secondRequest

            WorkingDirectoryUpdate.BranchDirectoryVersion.run
                secondRequest
                firstStatus
                secondStatus
                secondMetadata
                secondManifest
                root
                configuration.GraceStatusFile
                CancellationToken.None
                WorkingDirectoryUpdate.BranchDirectoryVersion.none
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | WorkingDirectoryUpdateContracts.Outcome.Updated _ -> ()
                | outcome -> Assert.Fail($"Expected later Updated, got {outcome}.")

            File.Exists(Path.Combine(root, "first.txt"))
            |> should equal false

            File.ReadAllBytes(Path.Combine(root, "second.txt"))
            |> should equal secondBytes

            File.Exists(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
            |> should equal false

            LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile firstTarget firstOperation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal None

            LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile secondTarget secondOperation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal (Some LocalStateDb.WorkingDirectoryUpdateCompletion.Terminal))

    /// Proves exact incomplete-operation evidence is retokened and cleaned by a pre-mutation cancellation.
    [<Test>]
    let ``exact marker adoption retokens and cleans pre-mutation cancellation`` () =
        withRepo (fun root configuration ->
            let bytes = Encoding.UTF8.GetBytes("selected hash version")
            let currentStatus, _ = status configuration (Guid.NewGuid()) None
            let targetStatus, targetRoot = status configuration (Guid.NewGuid()) (Some("selected.txt", bytes))

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile currentStatus
            |> fun task -> task.GetAwaiter().GetResult() |> ignore

            let updateRequest, manifest, metadata = request configuration targetStatus targetRoot "selected.txt" bytes
            let target = WorkingDirectoryUpdateContracts.Request.target updateRequest
            let operation = WorkingDirectoryUpdateContracts.Request.operation updateRequest

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId root
                |> required

            let oldMarker =
                WorkingDirectoryUpdateCoordination.Marker.create scope (WorkingDirectoryUpdateContracts.AttemptToken.create ()) target operation
                |> required

            WorkingDirectoryUpdateCoordination.Marker.write scope oldMarker
            |> fun task -> task.GetAwaiter().GetResult()

            let injection =
                { WorkingDirectoryUpdate.BranchDirectoryVersion.none with
                    ThrowAt =
                        fun point ->
                            if point = WorkingDirectoryUpdate.BranchDirectoryVersion.BeforeMutation then
                                raise (OperationCanceledException("injected pre-mutation cancellation"))
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
                | WorkingDirectoryUpdateContracts.Outcome.Rejected _ -> ()
                | outcome -> Assert.Fail($"Expected pre-mutation Rejected, got {outcome}.")

            File.Exists(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
            |> should equal false

            File.Exists(Path.Combine(root, "selected.txt"))
            |> should equal false)

    /// Proves same-kind tracked byte drift is rejected by accepted dual-hash prefix identity before overwrite.
    [<Test>]
    let ``tracked file byte drift rejects before overwrite and cleans marker`` () =
        withRepo (fun root configuration ->
            let acceptedBytes = Encoding.UTF8.GetBytes("accepted tracked bytes")
            let selectedBytes = Encoding.UTF8.GetBytes("selected target bytes")
            let driftBytes = Encoding.UTF8.GetBytes("drifted tracked bytes")
            let currentStatus, _ = status configuration (Guid.NewGuid()) (Some("selected.txt", acceptedBytes))
            let targetStatus, targetRoot = status configuration (Guid.NewGuid()) (Some("selected.txt", selectedBytes))
            File.WriteAllBytes(Path.Combine(root, "selected.txt"), acceptedBytes)

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile currentStatus
            |> fun task -> task.GetAwaiter().GetResult() |> ignore

            let updateRequest, manifest, metadata = request configuration targetStatus targetRoot "selected.txt" selectedBytes

            let injection =
                { WorkingDirectoryUpdate.BranchDirectoryVersion.none with
                    BeforeAction =
                        fun index ->
                            if index = 0 then
                                File.WriteAllBytes(Path.Combine(root, "selected.txt"), driftBytes)
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
                | WorkingDirectoryUpdateContracts.Outcome.Rejected _ -> ()
                | outcome -> Assert.Fail($"Expected same-kind byte-drift Rejected, got {outcome}.")

            File.ReadAllBytes(Path.Combine(root, "selected.txt"))
            |> should equal driftBytes

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId root
                |> required

            File.Exists(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
            |> should equal false)

    /// Proves a lost response after commit preserves terminal truth and exact replay changes neither completion nor marker evidence.
    [<Test>]
    let ``AfterCommit lost response returns success and exact replay is residue-preserving Unchanged`` () =
        withRepo (fun root configuration ->
            let bytes = Encoding.UTF8.GetBytes("selected hash version")
            let currentStatus, _ = status configuration (Guid.NewGuid()) None
            let targetStatus, targetRoot = status configuration (Guid.NewGuid()) (Some("selected.txt", bytes))

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile currentStatus
            |> fun task -> task.GetAwaiter().GetResult() |> ignore

            let updateRequest, manifest, metadata = request configuration targetStatus targetRoot "selected.txt" bytes
            let target = WorkingDirectoryUpdateContracts.Request.target updateRequest
            let operation = WorkingDirectoryUpdateContracts.Request.operation updateRequest

            let lostResponse =
                { WorkingDirectoryUpdate.BranchDirectoryVersion.none with
                    ThrowAt =
                        fun point ->
                            if point = WorkingDirectoryUpdate.BranchDirectoryVersion.AfterCommit then
                                raise (IOException("injected lost response"))
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
                lostResponse
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | WorkingDirectoryUpdateContracts.Outcome.Updated _ -> ()
                | outcome -> Assert.Fail($"Expected truthful committed Updated, got {outcome}.")

            let persisted =
                LocalStateDb.readStatusSnapshot configuration.GraceStatusFile
                |> fun task -> task.GetAwaiter().GetResult()

            persisted.RootDirectoryId
            |> should equal targetStatus.RootDirectoryId

            LocalStateDb.isDirectoryVersionInObjectCache configuration.GraceStatusFile targetStatus.RootDirectoryId
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal true

            LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target operation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal (Some LocalStateDb.WorkingDirectoryUpdateCompletion.Terminal)

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId root
                |> required

            let markerBeforeReplay = File.ReadAllText(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
            use connection = new SqliteConnection($"Data Source={configuration.GraceStatusFile}")
            connection.Open()
            use timestampCommand = connection.CreateCommand()
            timestampCommand.CommandText <- "SELECT completed_at_unix_ticks FROM working_directory_update_completions WHERE operation_value = $operation;"

            timestampCommand.Parameters.AddWithValue("$operation", WorkingDirectoryUpdateContracts.Operation.value operation)
            |> ignore

            let completedBeforeReplay = timestampCommand.ExecuteScalar() :?> int64
            let replayRequest, replayManifest, replayMetadata = request configuration targetStatus targetRoot "selected.txt" bytes

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
                | WorkingDirectoryUpdateContracts.Outcome.Unchanged _ -> ()
                | outcome -> Assert.Fail($"Expected lost-response replay Unchanged, got {outcome}.")

            let completedAfterReplay = timestampCommand.ExecuteScalar() :?> int64

            completedAfterReplay
            |> should equal completedBeforeReplay

            File.ReadAllText(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
            |> should equal markerBeforeReplay)

    /// Proves preparation against an older local-status revision cannot plan or mutate after the lease is acquired.
    [<Test>]
    let ``stale accepted status revision rejects before marker and mutation`` () =
        withRepo (fun root configuration ->
            let bytes = Encoding.UTF8.GetBytes("selected hash version")
            let acceptedStatus, _ = status configuration (Guid.NewGuid()) None
            let targetStatus, targetRoot = status configuration (Guid.NewGuid()) (Some("selected.txt", bytes))

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile acceptedStatus
            |> fun task -> task.GetAwaiter().GetResult() |> ignore

            let acceptedRevision = revision configuration
            let updateRequest, manifest, metadata = request configuration targetStatus targetRoot "selected.txt" bytes
            let newerStatus, _ = status configuration (Guid.NewGuid()) None

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile newerStatus
            |> fun task -> task.GetAwaiter().GetResult() |> ignore

            WorkingDirectoryUpdate.BranchDirectoryVersion.runAtRevision
                updateRequest
                acceptedStatus
                targetStatus
                metadata
                manifest
                root
                configuration.GraceStatusFile
                acceptedRevision
                CancellationToken.None
                WorkingDirectoryUpdate.BranchDirectoryVersion.none
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | WorkingDirectoryUpdateContracts.Outcome.Rejected _ -> ()
                | outcome -> Assert.Fail($"Expected stale-status Rejected, got {outcome}.")

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId root
                |> required

            File.Exists(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
            |> should equal false

            File.Exists(Path.Combine(root, "selected.txt"))
            |> should equal false

            let persisted =
                LocalStateDb.readStatusSnapshot configuration.GraceStatusFile
                |> fun task -> task.GetAwaiter().GetResult()

            persisted.RootDirectoryId
            |> should equal newerStatus.RootDirectoryId)

    /// Proves a relevant path-kind drift after planning is detected by the complete prefix check before its action.
    [<Test>]
    let ``planned file path becoming a directory rejects before first mutation and cleans marker`` () =
        withRepo (fun root configuration ->
            let bytes = Encoding.UTF8.GetBytes("selected hash version")
            let currentStatus, _ = status configuration (Guid.NewGuid()) None
            let targetStatus, targetRoot = status configuration (Guid.NewGuid()) (Some("selected.txt", bytes))

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile currentStatus
            |> fun task -> task.GetAwaiter().GetResult() |> ignore

            let updateRequest, manifest, metadata = request configuration targetStatus targetRoot "selected.txt" bytes

            let injection =
                { WorkingDirectoryUpdate.BranchDirectoryVersion.none with
                    BeforeAction =
                        fun index ->
                            if index = 0 then
                                Directory.CreateDirectory(Path.Combine(root, "selected.txt"))
                                |> ignore
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
                | WorkingDirectoryUpdateContracts.Outcome.Rejected _ -> ()
                | outcome -> Assert.Fail($"Expected pre-mutation prefix-drift Rejected, got {outcome}.")

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId root
                |> required

            File.Exists(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
            |> should equal false

            Directory.Exists(Path.Combine(root, "selected.txt"))
            |> should equal true

            File.Exists(Path.Combine(root, "selected.txt"))
            |> should equal false)

    /// Proves terminal replay waits for an active mutation and cannot return stale terminal facts after the lease advances.
    [<Test>]
    let ``terminal replay serializes behind active DirectoryVersion mutation`` () =
        withRepo (fun root configuration ->
            let firstBytes = Encoding.UTF8.GetBytes("first selected hash version")
            let secondBytes = Encoding.UTF8.GetBytes("second selected hash version")
            let initialStatus, _ = status configuration (Guid.NewGuid()) None
            let firstStatus, firstRoot = status configuration (Guid.NewGuid()) (Some("first.txt", firstBytes))
            let secondStatus, secondRoot = status configuration (Guid.NewGuid()) (Some("second.txt", secondBytes))

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile initialStatus
            |> fun task -> task.GetAwaiter().GetResult() |> ignore

            let firstRequest, firstManifest, firstMetadata = request configuration firstStatus firstRoot "first.txt" firstBytes

            WorkingDirectoryUpdate.BranchDirectoryVersion.run
                firstRequest
                initialStatus
                firstStatus
                firstMetadata
                firstManifest
                root
                configuration.GraceStatusFile
                CancellationToken.None
                WorkingDirectoryUpdate.BranchDirectoryVersion.none
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | WorkingDirectoryUpdateContracts.Outcome.Updated _ -> ()
                | outcome -> Assert.Fail($"Expected first Updated, got {outcome}.")

            let secondRequest, secondManifest, secondMetadata = request configuration secondStatus secondRoot "second.txt" secondBytes
            use mutationBlocked = new ManualResetEventSlim(false)
            use releaseMutation = new ManualResetEventSlim(false)

            let injection =
                { WorkingDirectoryUpdate.BranchDirectoryVersion.none with
                    BeforeAction =
                        fun index ->
                            if index = 1 then
                                mutationBlocked.Set()
                                releaseMutation.Wait()
                }

            let secondTask =
                WorkingDirectoryUpdate.BranchDirectoryVersion.run
                    secondRequest
                    firstStatus
                    secondStatus
                    secondMetadata
                    secondManifest
                    root
                    configuration.GraceStatusFile
                    CancellationToken.None
                    injection

            mutationBlocked.Wait(TimeSpan.FromSeconds(5.0))
            |> should equal true

            let replayRequest, replayManifest, replayMetadata = request configuration firstStatus firstRoot "first.txt" firstBytes

            let replayTask =
                WorkingDirectoryUpdate.BranchDirectoryVersion.run
                    replayRequest
                    firstStatus
                    firstStatus
                    replayMetadata
                    replayManifest
                    root
                    configuration.GraceStatusFile
                    CancellationToken.None
                    WorkingDirectoryUpdate.BranchDirectoryVersion.none

            replayTask.Wait(200) |> should equal false
            releaseMutation.Set()

            secondTask.GetAwaiter().GetResult()
            |> function
                | WorkingDirectoryUpdateContracts.Outcome.Updated _ -> ()
                | outcome -> Assert.Fail($"Expected second Updated, got {outcome}.")

            replayTask.GetAwaiter().GetResult()
            |> function
                | WorkingDirectoryUpdateContracts.Outcome.Unchanged _ -> Assert.Fail("Replay returned Unchanged from stale terminal facts.")
                | WorkingDirectoryUpdateContracts.Outcome.Rejected _ -> ()
                | outcome -> Assert.Fail($"Expected superseded replay Rejected, got {outcome}."))

    /// Proves cancellation while waiting on the real WDU lease is a non-mutating Rejected outcome with no marker evidence.
    [<Test>]
    let ``lease-wait cancellation rejects without files SQLite or marker`` () =
        withRepo (fun root configuration ->
            let bytes = Encoding.UTF8.GetBytes("selected hash version")
            let currentStatus, _ = status configuration (Guid.NewGuid()) None
            let targetStatus, targetRoot = status configuration (Guid.NewGuid()) (Some("selected.txt", bytes))

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile currentStatus
            |> fun task -> task.GetAwaiter().GetResult() |> ignore

            let acceptedRevision = revision configuration
            let updateRequest, manifest, metadata = request configuration targetStatus targetRoot "selected.txt" bytes

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId root
                |> required

            use heldLease =
                WorkingDirectoryUpdateCoordination.Lease.acquire scope CancellationToken.None
                |> fun task -> task.GetAwaiter().GetResult()

            use cancellation = new CancellationTokenSource()

            let updateTask =
                WorkingDirectoryUpdate.BranchDirectoryVersion.runAtRevision
                    updateRequest
                    currentStatus
                    targetStatus
                    metadata
                    manifest
                    root
                    configuration.GraceStatusFile
                    acceptedRevision
                    cancellation.Token
                    WorkingDirectoryUpdate.BranchDirectoryVersion.none

            Thread.Sleep(100)
            updateTask.IsCompleted |> should equal false
            cancellation.Cancel()

            updateTask.GetAwaiter().GetResult()
            |> function
                | WorkingDirectoryUpdateContracts.Outcome.Rejected _ -> ()
                | outcome -> Assert.Fail($"Expected lease-wait Rejected, got {outcome}.")

            File.Exists(Path.Combine(root, "selected.txt"))
            |> should equal false

            File.Exists(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
            |> should equal false

            LocalStateDb.readStatusSnapshot configuration.GraceStatusFile
            |> fun task -> task.GetAwaiter().GetResult()
            |> fun persisted -> persisted.RootDirectoryId
            |> should equal currentStatus.RootDirectoryId)
