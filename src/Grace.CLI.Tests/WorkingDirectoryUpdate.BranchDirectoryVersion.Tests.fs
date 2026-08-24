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

    /// Supplies prepared content for a target graph containing directories but no files.
    type private EmptyReader() =
        interface WorkingDirectoryUpdateContracts.IPreparedContentReader with
            member _.FilePaths = Seq.empty
            member _.OpenReadAsync(_, _) = Task.FromException<Stream>(InvalidOperationException("Directory-only content has no file stream."))
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

    /// Creates a production-valid root graph containing one nested directory and file.
    let private nestedDirectoryStatus (configuration: GraceConfiguration) (directoryPath: string) (filePath: string) (bytes: byte array) =
        let childId = DirectoryVersionId.NewGuid()
        let sha256, blake3 = hashes bytes

        let file =
            LocalFileVersion.CreateWithHashes
                (RelativePath filePath)
                sha256
                blake3
                false
                (int64 bytes.Length)
                (Grace.Shared.Utilities.getCurrentInstant ())
                true
                DateTime.UtcNow

        let childEntries =
            [|
                Services.DirectoryVersionPreimageEntry.File file.RelativePath file.Size file.Blake3Hash file.Sha256Hash
            |]

        let child =
            LocalDirectoryVersion.CreateWithHashes
                childId
                configuration.OwnerId
                configuration.OrganizationId
                configuration.RepositoryId
                (RelativePath directoryPath)
                (Services.computeSha256ForDirectoryEntries (RelativePath directoryPath) childEntries)
                (Services.computeBlake3ForDirectory (RelativePath directoryPath) childEntries)
                (List<DirectoryVersionId>())
                (List<LocalFileVersion>([| file |]))
                file.Size
                DateTime.UtcNow

        let rootId = DirectoryVersionId.NewGuid()
        let rootPath = RelativePath RootDirectoryPath

        let rootEntries =
            [|
                Services.DirectoryVersionPreimageEntry.Directory child.RelativePath child.Size child.Blake3Hash child.Sha256Hash
            |]

        let root =
            LocalDirectoryVersion.CreateWithHashes
                rootId
                configuration.OwnerId
                configuration.OrganizationId
                configuration.RepositoryId
                rootPath
                (Services.computeSha256ForDirectoryEntries rootPath rootEntries)
                (Services.computeBlake3ForDirectory rootPath rootEntries)
                (List<DirectoryVersionId>([| childId |]))
                (List<LocalFileVersion>())
                child.Size
                DateTime.UtcNow

        let index = GraceIndex()
        index[rootId] <- root
        index[childId] <- child

        { GraceStatus.Default with
            Index = index
            RootDirectoryId = rootId
            RootDirectorySha256Hash = root.Sha256Hash
            RootDirectoryBlake3Hash = root.Blake3Hash
        },
        root,
        child

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

    /// Persists the existing Reference-pending facts used to test restart-only finalization without a working-tree update.
    let private seedPendingReferenceFinalization
        (configuration: GraceConfiguration)
        (targetStatus: GraceStatus)
        (targetRoot: LocalDirectoryVersion)
        (selectedBranchId: BranchId)
        =
        let previousBranchId = configuration.BranchId
        let referenceId = ReferenceId.NewGuid()

        let target =
            WorkingDirectoryUpdateContracts.Target.create
                configuration.RepositoryId
                selectedBranchId
                targetStatus.RootDirectoryId
                targetStatus.RootDirectorySha256Hash
                targetStatus.RootDirectoryBlake3Hash
            |> required

        let selection = WorkingDirectoryUpdateContracts.BranchSelection.Reference referenceId

        let operation =
            WorkingDirectoryUpdateContracts.Operation.branchSwitchWithSelection previousBranchId selection target
            |> required

        LocalStateDb.commitWorkingDirectoryUpdateCompletion
            configuration.GraceStatusFile
            targetStatus
            [| targetRoot |]
            (LocalStateDb.WorkingDirectoryUpdateCompletionDetails.BranchFinalization(previousBranchId, referenceId))
            target
            operation
        |> fun task -> task.GetAwaiter().GetResult()
        |> ignore

        target, operation

    /// Writes exact marker evidence owned by a pending Reference completion.
    let private writeExactReferenceMarker (configuration: GraceConfiguration) root target operation =
        let scope =
            WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId root
            |> required

        let marker =
            WorkingDirectoryUpdateCoordination.Marker.create scope (WorkingDirectoryUpdateContracts.AttemptToken.create ()) target operation
            |> required

        WorkingDirectoryUpdateCoordination.Marker.write scope marker
        |> fun task -> task.GetAwaiter().GetResult()

        scope

    /// Creates a fully prepared request for one nested directory-and-file target graph.
    let private directoryRequest (configuration: GraceConfiguration) (targetStatus: GraceStatus) metadata (directoryPath: string) (filePath: string) bytes =
        let sha256, blake3 = hashes bytes

        let manifest =
            WorkingDirectoryUpdateContracts.PreparedManifest.create [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(
                                                                          RelativePath directoryPath
                                                                      )
                                                                      WorkingDirectoryUpdateContracts.PreparedManifestEntry.File(
                                                                          RelativePath filePath,
                                                                          sha256,
                                                                          blake3
                                                                      ) ]
            |> required

        let prepared =
            WorkingDirectoryUpdateContracts.PreparedContent.create manifest (new ByteReader(filePath, bytes)) CancellationToken.None
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
        metadata

    /// Verifies that graph construction rejects a partial or altered target without leaving working-tree or SQLite residue.
    [<Test>]
    let ``Resolved target graph rejects partial manifest and altered metadata without residue`` () =
        withRepo (fun root configuration ->
            let selectedBytes = Encoding.UTF8.GetBytes("resolved graph structural equality")
            let currentStatus, _ = status configuration (Guid.NewGuid()) None
            let targetStatus, targetRoot, targetChild = nestedDirectoryStatus configuration "selected" "selected/content.txt" selectedBytes

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile currentStatus
            |> fun task -> task.GetAwaiter().GetResult() |> ignore

            let target =
                WorkingDirectoryUpdateContracts.Target.create
                    configuration.RepositoryId
                    configuration.BranchId
                    targetStatus.RootDirectoryId
                    targetStatus.RootDirectorySha256Hash
                    targetStatus.RootDirectoryBlake3Hash
                |> required

            let selection = WorkingDirectoryUpdateContracts.BranchSelection.Reference(Guid.NewGuid())

            let acceptedPhase =
                WorkingDirectoryUpdateContracts.AcceptedBranchPhase.noSave currentStatus (revision configuration) configuration.BranchId
                |> required

            let _, matchingManifest, matchingMetadata =
                directoryRequest configuration targetStatus [| targetRoot; targetChild |] "selected" "selected/content.txt" selectedBytes

            let missingFileManifest =
                WorkingDirectoryUpdateContracts.PreparedManifest.create [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(
                                                                              RelativePath "selected"
                                                                          ) ]
                |> required

            let sha256, blake3 = hashes selectedBytes

            let extraFileManifest =
                WorkingDirectoryUpdateContracts.PreparedManifest.create [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(
                                                                              RelativePath "selected"
                                                                          )
                                                                          WorkingDirectoryUpdateContracts.PreparedManifestEntry.File(
                                                                              RelativePath "selected/content.txt",
                                                                              sha256,
                                                                              blake3
                                                                          )
                                                                          WorkingDirectoryUpdateContracts.PreparedManifestEntry.File(
                                                                              RelativePath "unexpected.txt",
                                                                              sha256,
                                                                              blake3
                                                                          ) ]
                |> required

            let alteredChild =
                LocalDirectoryVersion.CreateWithHashes
                    targetChild.DirectoryVersionId
                    targetChild.OwnerId
                    targetChild.OrganizationId
                    targetChild.RepositoryId
                    targetChild.RelativePath
                    targetChild.Sha256Hash
                    targetChild.Blake3Hash
                    (List<DirectoryVersionId>(targetChild.Directories))
                    (List<LocalFileVersion>(targetChild.Files))
                    targetChild.Size
                    (targetChild.LastWriteTimeUtc.AddSeconds(1.0))

            let alteredFile =
                { targetChild.Files[0] with
                    LastWriteTimeUtc =
                        targetChild
                            .Files[ 0 ]
                            .LastWriteTimeUtc.AddSeconds(1.0)
                }

            let childWithAlteredFile =
                LocalDirectoryVersion.CreateWithHashes
                    targetChild.DirectoryVersionId
                    targetChild.OwnerId
                    targetChild.OrganizationId
                    targetChild.RepositoryId
                    targetChild.RelativePath
                    targetChild.Sha256Hash
                    targetChild.Blake3Hash
                    (List<DirectoryVersionId>(targetChild.Directories))
                    (List<LocalFileVersion>([| alteredFile |]))
                    targetChild.Size
                    targetChild.LastWriteTimeUtc

            let rejected graph =
                WorkingDirectoryUpdateContracts.ResolvedTargetGraph.create acceptedPhase selection target targetStatus graph matchingManifest
                |> Result.isError
                |> should equal true

            rejected [| targetRoot; alteredChild |]

            rejected [| targetRoot
                        childWithAlteredFile |]

            WorkingDirectoryUpdateContracts.ResolvedTargetGraph.create acceptedPhase selection target targetStatus matchingMetadata missingFileManifest
            |> Result.isError
            |> should equal true

            WorkingDirectoryUpdateContracts.ResolvedTargetGraph.create acceptedPhase selection target targetStatus matchingMetadata extraFileManifest
            |> Result.isError
            |> should equal true

            WorkingDirectoryUpdateContracts.ResolvedTargetGraph.create acceptedPhase selection target targetStatus matchingMetadata matchingManifest
            |> Result.isOk
            |> should equal true

            File.Exists(Path.Combine(root, "selected", "content.txt"))
            |> should equal false

            LocalStateDb.readStatusSnapshot configuration.GraceStatusFile
            |> fun task -> task.GetAwaiter().GetResult()
            |> fun persisted -> persisted.RootDirectoryId
            |> should equal currentStatus.RootDirectoryId

            revision configuration |> should equal 1L

            LocalStateDb.isDirectoryVersionInObjectCache configuration.GraceStatusFile targetStatus.RootDirectoryId
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal false

            let operation =
                WorkingDirectoryUpdateContracts.Operation.branchSwitchWithSelection configuration.BranchId selection target
                |> required

            LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target operation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal None

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId root
                |> required

            File.Exists(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
            |> should equal false)

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

    /// Proves exact-marker adoption resumes a nested target directory created before an interrupted application.
    [<Test>]
    let ``exact adoption resumes previously created nested target directory`` () =
        withRepo (fun root configuration ->
            let branchBefore = Current().BranchId
            let targetBytes = Encoding.UTF8.GetBytes("nested target bytes")
            let currentStatus, _ = status configuration (Guid.NewGuid()) None
            let targetStatus, targetRoot, targetChild = nestedDirectoryStatus configuration "nested" "nested/file.txt" targetBytes

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile currentStatus
            |> fun task -> task.GetAwaiter().GetResult() |> ignore

            Directory.CreateDirectory(Path.Combine(root, "nested"))
            |> ignore

            let freshRequest, freshManifest, freshMetadata =
                directoryRequest configuration targetStatus [| targetRoot; targetChild |] "nested" "nested/file.txt" targetBytes

            WorkingDirectoryUpdate.BranchDirectoryVersion.run
                freshRequest
                currentStatus
                targetStatus
                freshMetadata
                freshManifest
                root
                configuration.GraceStatusFile
                CancellationToken.None
                WorkingDirectoryUpdate.BranchDirectoryVersion.none
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | WorkingDirectoryUpdateContracts.Outcome.Rejected _ -> ()
                | outcome -> Assert.Fail($"Expected fresh untracked-directory Rejected, got {outcome}.")

            File.WriteAllBytes(Path.Combine(root, "nested", "file.txt"), targetBytes)
            File.WriteAllText(Path.Combine(root, "nested", "extra.txt"), "not in target")

            WorkingDirectoryUpdate.Topology.planExactAdoption currentStatus freshManifest
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | WorkingDirectoryUpdate.Topology.Rejected _ -> ()
                | outcome -> Assert.Fail($"Expected non-target descendant rejection, got {outcome}.")

            File.Delete(Path.Combine(root, "nested", "extra.txt"))
            File.WriteAllText(Path.Combine(root, "nested", "file.txt"), "wrong target bytes")

            WorkingDirectoryUpdate.Topology.planExactAdoption currentStatus freshManifest
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | WorkingDirectoryUpdate.Topology.Rejected _ -> ()
                | outcome -> Assert.Fail($"Expected wrong target hash rejection, got {outcome}.")

            Directory.Delete(Path.Combine(root, "nested"), true)

            let updateRequest, manifest, metadata =
                directoryRequest configuration targetStatus [| targetRoot; targetChild |] "nested" "nested/file.txt" targetBytes

            let mutable completedActions = 0

            let injection =
                { WorkingDirectoryUpdate.BranchDirectoryVersion.none with
                    ThrowAt =
                        fun point ->
                            if point = WorkingDirectoryUpdate.BranchDirectoryVersion.DuringApplication then
                                completedActions <- completedActions + 1

                                if completedActions = 2 then
                                    raise (IOException("interrupt after nested target file creation"))
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
                | outcome -> Assert.Fail($"Expected interrupted UpdateIncomplete, got {outcome}.")

            Directory.Exists(Path.Combine(root, "nested"))
            |> should equal true

            File.ReadAllBytes(Path.Combine(root, "nested", "file.txt"))
            |> should equal targetBytes

            let retryRequest, retryManifest, retryMetadata =
                directoryRequest configuration targetStatus [| targetRoot; targetChild |] "nested" "nested/file.txt" targetBytes

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
            |> fun persisted -> persisted.RootDirectoryId
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

    /// Proves same-kind tracked byte drift is rejected by accepted BLAKE3 prefix identity before overwrite.
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

    /// Proves every exact-marker identity drift after publication rejects without removing another invocation's marker.
    [<TestCase("attempt")>]
    [<TestCase("operation")>]
    [<TestCase("target")>]
    let ``post-publication marker drift rejects before working-tree mutation`` markerDrift =
        withRepo (fun root configuration ->
            let bytes = Encoding.UTF8.GetBytes("selected object publication")
            let acceptedStatus, _ = status configuration (Guid.NewGuid()) None
            let targetStatus, targetRoot = status configuration (Guid.NewGuid()) (Some("selected.txt", bytes))

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile acceptedStatus
            |> fun task -> task.GetAwaiter().GetResult() |> ignore

            let updateRequest, manifest, metadata = request configuration targetStatus targetRoot "selected.txt" bytes

            let target = WorkingDirectoryUpdateContracts.Request.target updateRequest
            let operation = WorkingDirectoryUpdateContracts.Request.operation updateRequest

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId root
                |> required

            let injection =
                { WorkingDirectoryUpdate.BranchDirectoryVersion.none with
                    ThrowAt =
                        fun point ->
                            if point = WorkingDirectoryUpdate.BranchDirectoryVersion.AfterObjectPublication then
                                let replacementTarget, replacementOperation, replacementAttempt =
                                    match markerDrift with
                                    | "attempt" -> target, operation, WorkingDirectoryUpdateContracts.AttemptToken.create ()
                                    | "operation" ->
                                        target,
                                        (WorkingDirectoryUpdateContracts.Operation.branchSwitch configuration.BranchId (Guid.NewGuid()) target
                                         |> required),
                                        WorkingDirectoryUpdateContracts.AttemptToken.create ()
                                    | "target" ->
                                        let differentTarget =
                                            WorkingDirectoryUpdateContracts.Target.create
                                                configuration.RepositoryId
                                                configuration.BranchId
                                                (Guid.NewGuid())
                                                targetStatus.RootDirectorySha256Hash
                                                targetStatus.RootDirectoryBlake3Hash
                                            |> required

                                        differentTarget,
                                        (WorkingDirectoryUpdateContracts.Operation.branchSwitchWithSelection
                                            configuration.BranchId
                                            WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion
                                            differentTarget
                                         |> required),
                                        WorkingDirectoryUpdateContracts.AttemptToken.create ()
                                    | value -> invalidArg (nameof markerDrift) $"Unsupported marker drift '{value}'."

                                let replacement =
                                    WorkingDirectoryUpdateCoordination.Marker.create scope replacementAttempt replacementTarget replacementOperation
                                    |> required

                                WorkingDirectoryUpdateCoordination.Marker.write scope replacement
                                |> fun task -> task.GetAwaiter().GetResult()
                }

            WorkingDirectoryUpdate.BranchDirectoryVersion.run
                updateRequest
                acceptedStatus
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
                | outcome -> Assert.Fail($"Expected post-publication marker-drift Rejected, got {outcome}.")

            File.Exists(Path.Combine(root, "selected.txt"))
            |> should equal false

            let publishedFile = metadata[0].Files |> Seq.head

            let objectPath =
                Path.Combine(
                    configuration.ObjectDirectory,
                    string publishedFile.RelativePath,
                    Services.getLocalObjectCacheFileName publishedFile.RelativePath publishedFile.Sha256Hash publishedFile.Blake3Hash
                )

            File.ReadAllBytes(objectPath)
            |> should equal bytes

            File.Exists(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
            |> should equal true)

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

    /// Proves post-publication accepted revision and complete-status changes cannot reach the first working-tree mutation.
    [<TestCase("revision")>]
    [<TestCase("status")>]
    let ``post-publication accepted local facts reject before working-tree mutation`` drift =
        withRepo (fun root configuration ->
            let bytes = Encoding.UTF8.GetBytes("post-publication accepted local facts")
            let acceptedStatus, _ = status configuration (Guid.NewGuid()) None
            let targetStatus, targetRoot = status configuration (Guid.NewGuid()) (Some("selected.txt", bytes))

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile acceptedStatus
            |> fun task -> task.GetAwaiter().GetResult() |> ignore

            let updateRequest, manifest, metadata = request configuration targetStatus targetRoot "selected.txt" bytes
            let changedStatus, _ = status configuration (Guid.NewGuid()) None

            let injection =
                { WorkingDirectoryUpdate.BranchDirectoryVersion.none with
                    ThrowAt =
                        fun point ->
                            if point = WorkingDirectoryUpdate.BranchDirectoryVersion.AfterObjectPublication then
                                let replacement = if drift = "revision" then acceptedStatus else changedStatus

                                LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile replacement
                                |> fun task -> task.GetAwaiter().GetResult()
                }

            WorkingDirectoryUpdate.BranchDirectoryVersion.run
                updateRequest
                acceptedStatus
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
                | outcome -> Assert.Fail($"Expected post-publication {drift} rejection, got {outcome}.")

            File.Exists(Path.Combine(root, "selected.txt"))
            |> should equal false)

    /// Proves completion created while objects publish blocks local application before its first filesystem mutation.
    [<Test>]
    let ``post-publication completion drift rejects before working-tree mutation`` () =
        withRepo (fun root configuration ->
            let bytes = Encoding.UTF8.GetBytes("post-publication completion")
            let acceptedStatus, _ = status configuration (Guid.NewGuid()) None
            let targetStatus, targetRoot = status configuration (Guid.NewGuid()) (Some("selected.txt", bytes))

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile acceptedStatus
            |> fun task -> task.GetAwaiter().GetResult() |> ignore

            let updateRequest, manifest, metadata = request configuration targetStatus targetRoot "selected.txt" bytes
            let target = WorkingDirectoryUpdateContracts.Request.target updateRequest
            let operation = WorkingDirectoryUpdateContracts.Request.operation updateRequest

            let injection =
                { WorkingDirectoryUpdate.BranchDirectoryVersion.none with
                    ThrowAt =
                        fun point ->
                            if point = WorkingDirectoryUpdate.BranchDirectoryVersion.AfterObjectPublication then
                                LocalStateDb.commitWorkingDirectoryUpdateCompletion
                                    configuration.GraceStatusFile
                                    targetStatus
                                    metadata
                                    (LocalStateDb.WorkingDirectoryUpdateCompletionDetails.BranchDirectoryVersionFinalization(configuration.BranchId))
                                    target
                                    operation
                                |> fun task -> task.GetAwaiter().GetResult() |> ignore
                }

            WorkingDirectoryUpdate.BranchDirectoryVersion.run
                updateRequest
                acceptedStatus
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
                | outcome -> Assert.Fail($"Expected post-publication completion rejection, got {outcome}.")

            File.Exists(Path.Combine(root, "selected.txt"))
            |> should equal false)

    /// Proves corruption after atomic object publication is detected before final admission can mutate the working tree.
    [<Test>]
    let ``post-publication object corruption rejects before working-tree mutation`` () =
        withRepo (fun root configuration ->
            let bytes = Encoding.UTF8.GetBytes("post-publication object corruption")
            let acceptedStatus, _ = status configuration (Guid.NewGuid()) None
            let targetStatus, targetRoot = status configuration (Guid.NewGuid()) (Some("selected.txt", bytes))

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile acceptedStatus
            |> fun task -> task.GetAwaiter().GetResult() |> ignore

            let updateRequest, manifest, metadata = request configuration targetStatus targetRoot "selected.txt" bytes
            let publishedFile = metadata[0].Files |> Seq.head

            let objectPath =
                Path.Combine(
                    configuration.ObjectDirectory,
                    string publishedFile.RelativePath,
                    Services.getLocalObjectCacheFileName publishedFile.RelativePath publishedFile.Sha256Hash publishedFile.Blake3Hash
                )

            let injection =
                { WorkingDirectoryUpdate.BranchDirectoryVersion.none with
                    ThrowAt =
                        fun point ->
                            if point = WorkingDirectoryUpdate.BranchDirectoryVersion.AfterObjectPublication then
                                File.WriteAllBytes(objectPath, Encoding.UTF8.GetBytes("corrupt"))
                }

            WorkingDirectoryUpdate.BranchDirectoryVersion.run
                updateRequest
                acceptedStatus
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
                | outcome -> Assert.Fail($"Expected published-object corruption rejection, got {outcome}.")

            File.Exists(Path.Combine(root, "selected.txt"))
            |> should equal false)

    /// Proves a committed zero-action update retains the truthful Unchanged outcome when its response is lost.
    [<Test>]
    let ``zero-action committed lost response preserves Unchanged`` () =
        withRepo (fun root configuration ->
            let bytes = Encoding.UTF8.GetBytes("zero action selected root")
            let currentStatus, targetRoot = status configuration (Guid.NewGuid()) (Some("selected.txt", bytes))

            File.WriteAllBytes(Path.Combine(root, "selected.txt"), bytes)

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile currentStatus
            |> fun task -> task.GetAwaiter().GetResult() |> ignore

            let updateRequest, manifest, metadata = request configuration currentStatus targetRoot "selected.txt" bytes

            let injection =
                { WorkingDirectoryUpdate.BranchDirectoryVersion.none with
                    ThrowAt =
                        fun point ->
                            if point = WorkingDirectoryUpdate.BranchDirectoryVersion.AfterCommit then
                                invalidOp "lost zero-action response"
                }

            WorkingDirectoryUpdate.BranchDirectoryVersion.run
                updateRequest
                currentStatus
                currentStatus
                metadata
                manifest
                root
                configuration.GraceStatusFile
                CancellationToken.None
                injection
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | WorkingDirectoryUpdateContracts.Outcome.Unchanged _ -> ()
                | outcome -> Assert.Fail($"Expected lost zero-action response Unchanged, got {outcome}.")

            WorkingDirectoryUpdate.BranchDirectoryVersion.run
                updateRequest
                currentStatus
                currentStatus
                metadata
                manifest
                root
                configuration.GraceStatusFile
                CancellationToken.None
                WorkingDirectoryUpdate.BranchDirectoryVersion.none
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | WorkingDirectoryUpdateContracts.Outcome.Unchanged _ -> ()
                | outcome -> Assert.Fail($"Expected zero-action replay Unchanged, got {outcome}."))

    /// Proves an initial Reference run finalizes only after real local application verifies its root.
    [<Test>]
    let ``Reference five-input transaction finalizes after verified local root`` () =
        withRepo (fun root configuration ->
            let selectedBytes = Encoding.UTF8.GetBytes("reference pending selected bytes")
            let currentStatus, _ = status configuration (Guid.NewGuid()) None
            let targetStatus, targetRoot = status configuration (Guid.NewGuid()) (Some("selected.txt", selectedBytes))

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile currentStatus
            |> fun task -> task.GetAwaiter().GetResult() |> ignore

            let preparedRequest, manifest, metadata = request configuration targetStatus targetRoot "selected.txt" selectedBytes
            let target = WorkingDirectoryUpdateContracts.Request.target preparedRequest
            let preparedContent = WorkingDirectoryUpdateContracts.Request.preparedContent preparedRequest
            let referenceId = Guid.NewGuid()
            let selection = WorkingDirectoryUpdateContracts.BranchSelection.Reference referenceId

            let acceptedPhase =
                WorkingDirectoryUpdateContracts.AcceptedBranchPhase.noSave
                    currentStatus
                    (LocalStateDb.readLocalStatusRevisionReadOnly configuration.GraceStatusFile
                     |> fun task -> task.GetAwaiter().GetResult())
                    configuration.BranchId
                |> required

            let resolvedTargetGraph =
                WorkingDirectoryUpdateContracts.ResolvedTargetGraph.create acceptedPhase selection target targetStatus metadata manifest
                |> required

            Grace.CLI.Command.WorkingDirectoryUpdate.run
                acceptedPhase
                selection
                resolvedTargetGraph
                preparedContent
                "reference-pending"
                CancellationToken.None
                Grace.CLI.Command.WorkingDirectoryUpdate.BranchDirectoryVersion.none
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | Grace.CLI.Command.WorkingDirectoryUpdate.Finalized (Grace.CLI.Command.WorkingDirectoryUpdate.ReferenceTerminal receipt) ->
                    WorkingDirectoryUpdateContracts.Receipt.bytesChanged receipt
                    |> should equal true
                | outcome -> Assert.Fail($"Expected Reference finalization, got {outcome}.")

            File.ReadAllBytes(Path.Combine(root, "selected.txt"))
            |> should equal selectedBytes

            let operation =
                WorkingDirectoryUpdateContracts.Operation.branchSwitchWithSelection configuration.BranchId selection target
                |> required

            LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target operation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal (Some LocalStateDb.WorkingDirectoryUpdateCompletion.Terminal)

            LocalStateDb.readStatusSnapshot configuration.GraceStatusFile
            |> fun task -> task.GetAwaiter().GetResult()
            |> fun persisted -> persisted.RootDirectoryId
            |> should equal targetStatus.RootDirectoryId)

    /// Proves a Reference completion transaction failure keeps verified bytes but rolls back every pending SQLite fact.
    [<Test>]
    let ``Reference five-input transaction rolls back pending completion after verified local root`` () =
        withRepo (fun root configuration ->
            let selectedBytes = Encoding.UTF8.GetBytes("reference pending rollback bytes")
            let currentStatus, _ = status configuration (Guid.NewGuid()) None
            let targetStatus, targetRoot = status configuration (Guid.NewGuid()) (Some("selected.txt", selectedBytes))

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile currentStatus
            |> fun task -> task.GetAwaiter().GetResult() |> ignore

            let preparedRequest, manifest, metadata = request configuration targetStatus targetRoot "selected.txt" selectedBytes
            let target = WorkingDirectoryUpdateContracts.Request.target preparedRequest
            let preparedContent = WorkingDirectoryUpdateContracts.Request.preparedContent preparedRequest
            let referenceId = Guid.NewGuid()
            let selection = WorkingDirectoryUpdateContracts.BranchSelection.Reference referenceId

            let acceptedPhase =
                WorkingDirectoryUpdateContracts.AcceptedBranchPhase.noSave
                    currentStatus
                    (LocalStateDb.readLocalStatusRevisionReadOnly configuration.GraceStatusFile
                     |> fun task -> task.GetAwaiter().GetResult())
                    configuration.BranchId
                |> required

            let resolvedTargetGraph =
                WorkingDirectoryUpdateContracts.ResolvedTargetGraph.create acceptedPhase selection target targetStatus metadata manifest
                |> required

            let injection =
                { Grace.CLI.Command.WorkingDirectoryUpdate.BranchDirectoryVersion.none with
                    ThrowAt =
                        fun point ->
                            if point = Grace.CLI.Command.WorkingDirectoryUpdate.BranchDirectoryVersion.BeforeCommit then
                                raise (IOException("injected Reference completion rollback"))
                }

            Grace.CLI.Command.WorkingDirectoryUpdate.run
                acceptedPhase
                selection
                resolvedTargetGraph
                preparedContent
                "reference-pending-rollback"
                CancellationToken.None
                injection
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | Grace.CLI.Command.WorkingDirectoryUpdate.UpdateIncomplete _ -> ()
                | outcome -> Assert.Fail($"Expected Reference UpdateIncomplete, got {outcome}.")

            File.ReadAllBytes(Path.Combine(root, "selected.txt"))
            |> should equal selectedBytes

            let operation =
                WorkingDirectoryUpdateContracts.Operation.branchSwitchWithSelection configuration.BranchId selection target
                |> required

            LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target operation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal None

            LocalStateDb.readStatusSnapshot configuration.GraceStatusFile
            |> fun task -> task.GetAwaiter().GetResult()
            |> fun persisted -> persisted.RootDirectoryId
            |> should equal currentStatus.RootDirectoryId)

    /// Proves pre-verified cancellation releases the WDU lease, writes no completion, and disposes prepared bytes.
    [<Test>]
    let ``Reference five-input transaction cancellation rejects before local completion and disposes content`` () =
        withRepo (fun root configuration ->
            let selectedBytes = Encoding.UTF8.GetBytes("reference pending cancellation bytes")
            let currentStatus, _ = status configuration (Guid.NewGuid()) None
            let targetStatus, targetRoot = status configuration (Guid.NewGuid()) (Some("selected.txt", selectedBytes))

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile currentStatus
            |> fun task -> task.GetAwaiter().GetResult() |> ignore

            let preparedRequest, manifest, metadata = request configuration targetStatus targetRoot "selected.txt" selectedBytes
            let target = WorkingDirectoryUpdateContracts.Request.target preparedRequest
            let preparedContent = WorkingDirectoryUpdateContracts.Request.preparedContent preparedRequest
            let selection = WorkingDirectoryUpdateContracts.BranchSelection.Reference(Guid.NewGuid())

            let acceptedPhase =
                WorkingDirectoryUpdateContracts.AcceptedBranchPhase.noSave
                    currentStatus
                    (LocalStateDb.readLocalStatusRevisionReadOnly configuration.GraceStatusFile
                     |> fun task -> task.GetAwaiter().GetResult())
                    configuration.BranchId
                |> required

            let resolvedTargetGraph =
                WorkingDirectoryUpdateContracts.ResolvedTargetGraph.create acceptedPhase selection target targetStatus metadata manifest
                |> required

            use cancellation = new CancellationTokenSource()
            cancellation.Cancel()

            Grace.CLI.Command.WorkingDirectoryUpdate.run
                acceptedPhase
                selection
                resolvedTargetGraph
                preparedContent
                "reference-pending-cancellation"
                cancellation.Token
                Grace.CLI.Command.WorkingDirectoryUpdate.BranchDirectoryVersion.none
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | Grace.CLI.Command.WorkingDirectoryUpdate.Rejected _ -> ()
                | outcome -> Assert.Fail($"Expected Reference cancellation rejection, got {outcome}.")

            File.Exists(Path.Combine(root, "selected.txt"))
            |> should equal false

            let operation =
                WorkingDirectoryUpdateContracts.Operation.branchSwitchWithSelection configuration.BranchId selection target
                |> required

            LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target operation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal None

            WorkingDirectoryUpdateContracts.PreparedContent.openRead preparedContent (RelativePath "selected.txt")
            |> Result.isError
            |> should equal true

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId root
                |> required

            File.Exists(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
            |> should equal false)

    /// Proves a sealed phase whose complete status differs from the current SQLite baseline cannot enter local application.
    [<Test>]
    let ``Reference five-input transaction rejects a mismatched accepted status fingerprint before mutation`` () =
        withRepo (fun root configuration ->
            let selectedBytes = Encoding.UTF8.GetBytes("reference pending accepted-status mismatch")
            let currentStatus, _ = status configuration (Guid.NewGuid()) None
            let mismatchedAcceptedStatus, _ = status configuration (Guid.NewGuid()) None
            let targetStatus, targetRoot = status configuration (Guid.NewGuid()) (Some("selected.txt", selectedBytes))

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile currentStatus
            |> fun task -> task.GetAwaiter().GetResult() |> ignore

            let preparedRequest, manifest, metadata = request configuration targetStatus targetRoot "selected.txt" selectedBytes
            let target = WorkingDirectoryUpdateContracts.Request.target preparedRequest
            let preparedContent = WorkingDirectoryUpdateContracts.Request.preparedContent preparedRequest
            let selection = WorkingDirectoryUpdateContracts.BranchSelection.Reference(Guid.NewGuid())

            let acceptedPhase =
                WorkingDirectoryUpdateContracts.AcceptedBranchPhase.noSave
                    mismatchedAcceptedStatus
                    (LocalStateDb.readLocalStatusRevisionReadOnly configuration.GraceStatusFile
                     |> fun task -> task.GetAwaiter().GetResult())
                    configuration.BranchId
                |> required

            let resolvedTargetGraph =
                WorkingDirectoryUpdateContracts.ResolvedTargetGraph.create acceptedPhase selection target targetStatus metadata manifest
                |> required

            Grace.CLI.Command.WorkingDirectoryUpdate.run
                acceptedPhase
                selection
                resolvedTargetGraph
                preparedContent
                "reference-pending-accepted-status-mismatch"
                CancellationToken.None
                Grace.CLI.Command.WorkingDirectoryUpdate.BranchDirectoryVersion.none
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | Grace.CLI.Command.WorkingDirectoryUpdate.Rejected failure ->
                    WorkingDirectoryUpdateContracts.Failure.reason failure
                    |> should equal "Local status changed while the selected Reference was being prepared."
                | outcome -> Assert.Fail($"Expected accepted-status rejection, got {outcome}.")

            File.Exists(Path.Combine(root, "selected.txt"))
            |> should equal false

            WorkingDirectoryUpdateContracts.PreparedContent.openRead preparedContent (RelativePath "selected.txt")
            |> Result.isError
            |> should equal true)

    /// Proves restart finalization reconstructs only persisted Reference facts and does not write verified working files.
    [<Test>]
    let ``Reference finalization restart publishes the selected Branch and terminalizes without rewriting bytes`` () =
        withRepo (fun root configuration ->
            let selectedBytes = Encoding.UTF8.GetBytes("Reference finalization restart bytes")
            let targetStatus, targetRoot = status configuration (DirectoryVersionId.NewGuid()) (Some("selected.txt", selectedBytes))
            let selectedBranchId = BranchId.NewGuid()
            let target, operation = seedPendingReferenceFinalization configuration targetStatus targetRoot selectedBranchId
            let selectedPath = Path.Combine(root, "selected.txt")

            File.WriteAllBytes(selectedPath, selectedBytes)
            let beforeWrite = File.GetLastWriteTimeUtc(selectedPath)

            Grace.CLI.Command.WorkingDirectoryUpdate.resumePendingReferenceFinalization CancellationToken.None
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | Some (WorkingDirectoryUpdateContracts.Outcome.Updated _) -> ()
                | outcome -> Assert.Fail($"Expected resumed Reference finalization, got {outcome}.")

            File.ReadAllBytes(selectedPath)
            |> should equal selectedBytes

            File.GetLastWriteTimeUtc(selectedPath)
            |> should equal beforeWrite

            Current().BranchId
            |> should equal selectedBranchId

            LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target operation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal (Some LocalStateDb.WorkingDirectoryUpdateCompletion.Terminal))

    /// Proves restart records terminal completion after selected-Branch publication fails at the terminal-recording boundary.
    [<Test>]
    let ``Reference terminal recording failure retries from selected Branch without rewriting bytes`` () =
        withRepo (fun root configuration ->
            let selectedBytes = Encoding.UTF8.GetBytes("Reference terminal-recording restart bytes")
            let currentStatus, _ = status configuration (DirectoryVersionId.NewGuid()) None
            let targetStatus, targetRoot = status configuration (DirectoryVersionId.NewGuid()) (Some("selected.txt", selectedBytes))
            let selectedBranchId = BranchId.NewGuid()

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile currentStatus
            |> fun task -> task.GetAwaiter().GetResult() |> ignore

            let sourceRequest, manifest, metadata = request configuration targetStatus targetRoot "selected.txt" selectedBytes
            let preparedContent = WorkingDirectoryUpdateContracts.Request.preparedContent sourceRequest
            let selection = WorkingDirectoryUpdateContracts.BranchSelection.Reference(ReferenceId.NewGuid())

            let target =
                WorkingDirectoryUpdateContracts.Target.create
                    configuration.RepositoryId
                    selectedBranchId
                    targetStatus.RootDirectoryId
                    targetStatus.RootDirectorySha256Hash
                    targetStatus.RootDirectoryBlake3Hash
                |> required

            let operation =
                WorkingDirectoryUpdateContracts.Operation.branchSwitchWithSelection configuration.BranchId selection target
                |> required

            let acceptedPhase =
                WorkingDirectoryUpdateContracts.AcceptedBranchPhase.noSave
                    currentStatus
                    (LocalStateDb.readLocalStatusRevisionReadOnly configuration.GraceStatusFile
                     |> fun task -> task.GetAwaiter().GetResult())
                    configuration.BranchId
                |> required

            let resolvedTargetGraph =
                WorkingDirectoryUpdateContracts.ResolvedTargetGraph.create acceptedPhase selection target targetStatus metadata manifest
                |> required

            let injection =
                { Grace.CLI.Command.WorkingDirectoryUpdate.BranchDirectoryVersion.none with
                    ThrowAt =
                        fun point ->
                            if point = Grace.CLI.Command.WorkingDirectoryUpdate.BranchDirectoryVersion.BeforeTerminalRecording then
                                raise (IOException("injected terminal-recording failure"))
                }

            Grace.CLI.Command.WorkingDirectoryUpdate.run
                acceptedPhase
                selection
                resolvedTargetGraph
                preparedContent
                "reference-terminal-recording-restart"
                CancellationToken.None
                injection
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | Grace.CLI.Command.WorkingDirectoryUpdate.Finalized _ -> ()
                | outcome -> Assert.Fail($"Expected Reference terminal-recording failure, got {outcome}.")

            let selectedPath = Path.Combine(root, "selected.txt")
            let beforeRetryWrite = File.GetLastWriteTimeUtc(selectedPath)

            Current().BranchId
            |> should equal selectedBranchId

            LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target operation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal (Some LocalStateDb.WorkingDirectoryUpdateCompletion.Pending)

            resetConfiguration ()

            Grace.CLI.Command.WorkingDirectoryUpdate.resumePendingReferenceFinalization CancellationToken.None
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | Some (WorkingDirectoryUpdateContracts.Outcome.Updated _) -> ()
                | outcome -> Assert.Fail($"Expected terminal-recording restart to complete, got {outcome}.")

            File.ReadAllBytes(selectedPath)
            |> should equal selectedBytes

            File.GetLastWriteTimeUtc(selectedPath)
            |> should equal beforeRetryWrite

            LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target operation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal (Some LocalStateDb.WorkingDirectoryUpdateCompletion.Terminal))

    /// Proves a lease waiter reconciles a completed Reference row as Unchanged without repeating any completion effects.
    [<Test>]
    let ``Reference finalization lease waiter returns Unchanged after another completion terminalizes`` () =
        withRepo (fun root configuration ->
            let selectedBytes = Encoding.UTF8.GetBytes("Reference finalization lease waiter bytes")
            let targetStatus, targetRoot = status configuration (DirectoryVersionId.NewGuid()) (Some("selected.txt", selectedBytes))
            let selectedBranchId = BranchId.NewGuid()
            let target, operation = seedPendingReferenceFinalization configuration targetStatus targetRoot selectedBranchId
            let selectedPath = Path.Combine(root, "selected.txt")

            File.WriteAllBytes(selectedPath, selectedBytes)

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId root
                |> required

            let heldLease =
                WorkingDirectoryUpdateCoordination.Lease.acquire scope CancellationToken.None
                |> fun task -> task.GetAwaiter().GetResult()

            try
                let waitingResume = Grace.CLI.Command.WorkingDirectoryUpdate.resumePendingReferenceFinalization CancellationToken.None

                let selectedConfiguration = Current()
                selectedConfiguration.BranchId <- selectedBranchId
                selectedConfiguration.BranchName <- String.Empty
                updateConfiguration selectedConfiguration
                resetConfiguration ()

                LocalStateDb.finalizeWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target operation
                |> fun task -> task.GetAwaiter().GetResult()
                |> ignore

                let beforeWaiterWrite = File.GetLastWriteTimeUtc(selectedPath)

                WorkingDirectoryUpdateCoordination.Lease.dispose heldLease

                waitingResume
                |> fun task -> task.GetAwaiter().GetResult()
                |> function
                    | Some (WorkingDirectoryUpdateContracts.Outcome.Unchanged _) -> ()
                    | outcome -> Assert.Fail($"Expected terminal Reference waiter to return Unchanged, got {outcome}.")

                Current().BranchId
                |> should equal selectedBranchId

                File.ReadAllBytes(selectedPath)
                |> should equal selectedBytes

                File.GetLastWriteTimeUtc(selectedPath)
                |> should equal beforeWaiterWrite
            finally
                WorkingDirectoryUpdateCoordination.Lease.dispose heldLease)

    /// Proves exact marker evidence is cleaned before the selected Branch is published and the pending row becomes terminal.
    [<Test>]
    let ``Reference finalization cleans exact marker before publishing selected Branch`` () =
        withRepo (fun root configuration ->
            let selectedBytes = Encoding.UTF8.GetBytes("Reference exact marker cleanup bytes")
            let targetStatus, targetRoot = status configuration (DirectoryVersionId.NewGuid()) (Some("selected.txt", selectedBytes))
            let selectedBranchId = BranchId.NewGuid()
            let target, operation = seedPendingReferenceFinalization configuration targetStatus targetRoot selectedBranchId
            let scope = writeExactReferenceMarker configuration root target operation

            File.WriteAllBytes(Path.Combine(root, "selected.txt"), selectedBytes)

            Grace.CLI.Command.WorkingDirectoryUpdate.resumePendingReferenceFinalization CancellationToken.None
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | Some (WorkingDirectoryUpdateContracts.Outcome.Updated _) -> ()
                | outcome -> Assert.Fail($"Expected exact-marker Reference finalization, got {outcome}.")

            File.Exists(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
            |> should equal false

            Current().BranchId
            |> should equal selectedBranchId

            LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target operation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal (Some LocalStateDb.WorkingDirectoryUpdateCompletion.Terminal))

    /// Proves malformed marker evidence retains the pending Reference completion without writing verified working bytes.
    [<Test>]
    let ``Reference finalization retains pending state for malformed marker evidence`` () =
        withRepo (fun root configuration ->
            let selectedBytes = Encoding.UTF8.GetBytes("Reference malformed marker bytes")
            let targetStatus, targetRoot = status configuration (DirectoryVersionId.NewGuid()) (Some("selected.txt", selectedBytes))
            let selectedBranchId = BranchId.NewGuid()
            let target, operation = seedPendingReferenceFinalization configuration targetStatus targetRoot selectedBranchId

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId root
                |> required

            let markerPath = WorkingDirectoryUpdateCoordination.Scope.markerPath scope

            Directory.CreateDirectory(Path.GetDirectoryName(markerPath))
            |> ignore

            File.WriteAllText(markerPath, "malformed marker")
            File.WriteAllBytes(Path.Combine(root, "selected.txt"), selectedBytes)

            Grace.CLI.Command.WorkingDirectoryUpdate.resumePendingReferenceFinalization CancellationToken.None
            |> fun task -> task.GetAwaiter().GetResult()
            |> function
                | Some (WorkingDirectoryUpdateContracts.Outcome.FinalizationIncomplete (_, failure)) ->
                    WorkingDirectoryUpdateContracts.Failure.reason failure
                    |> should contain "retained marker evidence"
                | outcome -> Assert.Fail($"Expected malformed-marker finalization result, got {outcome}.")

            Current().BranchId
            |> should equal configuration.BranchId

            File.ReadAllBytes(Path.Combine(root, "selected.txt"))
            |> should equal selectedBytes

            LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target operation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal (Some LocalStateDb.WorkingDirectoryUpdateCompletion.Pending))

    /// Proves an unreadable Branch configuration retains pending completion and leaves verified working bytes unchanged.
    [<Test>]
    let ``Reference finalization retains pending state when Branch configuration becomes unreadable`` () =
        withRepo (fun root configuration ->
            let selectedBytes = Encoding.UTF8.GetBytes("Reference finalization unreadable configuration")
            let targetStatus, targetRoot = status configuration (DirectoryVersionId.NewGuid()) (Some("selected.txt", selectedBytes))
            let selectedBranchId = BranchId.NewGuid()
            let target, operation = seedPendingReferenceFinalization configuration targetStatus targetRoot selectedBranchId
            let selectedPath = Path.Combine(root, "selected.txt")
            let configurationFile = Path.Combine(configuration.ConfigurationDirectory, GraceConfigFileName)
            let originalConfiguration = File.ReadAllText(configurationFile)

            File.WriteAllBytes(selectedPath, selectedBytes)
            resetConfiguration ()
            File.WriteAllText(configurationFile, "not valid configuration json")

            try
                Grace.CLI.Command.WorkingDirectoryUpdate.resumePendingReferenceFinalization CancellationToken.None
                |> fun task -> task.GetAwaiter().GetResult()
                |> function
                    | Some (WorkingDirectoryUpdateContracts.Outcome.FinalizationIncomplete (_, failure)) ->
                        WorkingDirectoryUpdateContracts.Failure.reason failure
                        |> should contain "could not read Branch configuration"
                    | outcome -> Assert.Fail($"Expected unreadable-configuration finalization result, got {outcome}.")
            finally
                File.WriteAllText(configurationFile, originalConfiguration)
                resetConfiguration ()

            File.ReadAllBytes(selectedPath)
            |> should equal selectedBytes

            LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target operation
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal (Some LocalStateDb.WorkingDirectoryUpdateCompletion.Pending))

    /// Proves a pending Branch completion written while this Reference waits for the lease blocks admission before mutation.
    [<Test>]
    let ``Reference five-input transaction rechecks pending finalization after lease acquisition`` () =
        withRepo (fun root configuration ->
            let selectedBytes = Encoding.UTF8.GetBytes("reference pending lease revalidation")
            let currentStatus, _ = status configuration (Guid.NewGuid()) None
            let targetStatus, targetRoot = status configuration (Guid.NewGuid()) (Some("selected.txt", selectedBytes))

            LocalStateDb.replaceStatusSnapshot configuration.GraceStatusFile currentStatus
            |> fun task -> task.GetAwaiter().GetResult() |> ignore

            let preparedRequest, manifest, metadata = request configuration targetStatus targetRoot "selected.txt" selectedBytes
            let target = WorkingDirectoryUpdateContracts.Request.target preparedRequest
            let preparedContent = WorkingDirectoryUpdateContracts.Request.preparedContent preparedRequest
            let selection = WorkingDirectoryUpdateContracts.BranchSelection.Reference(Guid.NewGuid())

            let acceptedPhase =
                WorkingDirectoryUpdateContracts.AcceptedBranchPhase.noSave
                    currentStatus
                    (LocalStateDb.readLocalStatusRevisionReadOnly configuration.GraceStatusFile
                     |> fun task -> task.GetAwaiter().GetResult())
                    configuration.BranchId
                |> required

            let resolvedTargetGraph =
                WorkingDirectoryUpdateContracts.ResolvedTargetGraph.create acceptedPhase selection target targetStatus metadata manifest
                |> required

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create configuration.RepositoryId root
                |> required

            let heldLease =
                WorkingDirectoryUpdateCoordination.Lease.acquire scope CancellationToken.None
                |> fun task -> task.GetAwaiter().GetResult()

            try
                let waitingRun =
                    Grace.CLI.Command.WorkingDirectoryUpdate.run
                        acceptedPhase
                        selection
                        resolvedTargetGraph
                        preparedContent
                        "reference-pending-lease-revalidation"
                        CancellationToken.None
                        Grace.CLI.Command.WorkingDirectoryUpdate.BranchDirectoryVersion.none

                let pendingReferenceId = Guid.NewGuid()
                let pendingSelection = WorkingDirectoryUpdateContracts.BranchSelection.Reference pendingReferenceId

                let pendingOperation =
                    WorkingDirectoryUpdateContracts.Operation.branchSwitchWithSelection configuration.BranchId pendingSelection target
                    |> required

                LocalStateDb.commitWorkingDirectoryUpdateCompletion
                    configuration.GraceStatusFile
                    targetStatus
                    metadata
                    (LocalStateDb.WorkingDirectoryUpdateCompletionDetails.BranchFinalization(configuration.BranchId, pendingReferenceId))
                    target
                    pendingOperation
                |> fun task -> task.GetAwaiter().GetResult() |> ignore

                WorkingDirectoryUpdateCoordination.Lease.dispose heldLease

                waitingRun
                |> fun task -> task.GetAwaiter().GetResult()
                |> function
                    | Grace.CLI.Command.WorkingDirectoryUpdate.Rejected failure ->
                        WorkingDirectoryUpdateContracts.Failure.reason failure
                        |> should
                            equal
                            "Reference selection cannot begin while a Branch finalization is pending after acquiring the Working Directory Update lease."
                    | outcome -> Assert.Fail($"Expected pending-finalization rejection, got {outcome}.")
            finally
                WorkingDirectoryUpdateCoordination.Lease.dispose heldLease

            File.Exists(Path.Combine(root, "selected.txt"))
            |> should equal false

            WorkingDirectoryUpdateContracts.PreparedContent.openRead preparedContent (RelativePath "selected.txt")
            |> Result.isError
            |> should equal true)
