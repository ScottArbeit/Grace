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

/// Exercises the production five-input local transaction against real temporary files and SQLite state.
module WorkingDirectoryUpdateLocalTransactionTests =
    /// Extracts a successful private contract construction or reports the exact failure.
    let private required =
        function
        | Ok value -> value
        | Error error -> failwith error

    /// Provides immutable in-memory prepared bytes while recording disposal by the production preparation contract.
    type private Reader(entries: (string * byte array) list) =
        let values = Dictionary<string, byte array>(StringComparer.Ordinal)

        do
            entries
            |> Seq.iter (fun (path, bytes) -> values[path] <- bytes)

        interface WorkingDirectoryUpdateContracts.IPreparedContentReader with
            member _.FilePaths = values.Keys :> seq<string>

            member _.OpenReadAsync(path, _) =
                match values.TryGetValue(string path) with
                | true, bytes -> Task.FromResult(new MemoryStream(bytes, writable = false) :> Stream)
                | false, _ -> Task.FromException<Stream>(FileNotFoundException(string path))

            member _.Dispose() = ()

    /// Configures one isolated repository root using the production local configuration location and paths.
    let private configure root repositoryId branchId =
        let configuration = GraceConfiguration()
        configuration.OwnerId <- Guid.NewGuid()
        configuration.OrganizationId <- Guid.NewGuid()
        configuration.RepositoryId <- repositoryId
        configuration.BranchId <- branchId
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
        Current()

    /// Runs a real temporary filesystem and SQLite scenario without preserving process-static CLI configuration.
    let private withTempRepository action =
        let root = Path.Combine(Path.GetTempPath(), $"grace-wdu-local-transaction-{Guid.NewGuid():N}")
        let originalDirectory = Environment.CurrentDirectory
        let originalParseResult = Services.parseResult

        try
            Directory.CreateDirectory(root) |> ignore
            Environment.CurrentDirectory <- root
            Services.parseResult <- GraceCommand.rootCommand.Parse(Array.empty<string>)
            action root
        finally
            WorkingDirectoryUpdate.LocalTransactionTesting.reset ()
            Services.clearShouldIgnoreCache ()
            Services.parseResult <- originalParseResult
            Environment.CurrentDirectory <- originalDirectory
            resetConfiguration ()
            SqliteConnection.ClearAllPools()

            if Directory.Exists(root) then Directory.Delete(root, true)

    /// Builds one dual-hashed file with deterministic status metadata.
    let private localFile (path: string) (bytes: byte array) : LocalFileVersion =
        LocalFileVersion.CreateWithHashes
            (RelativePath path)
            (Sha256Hash(
                Convert
                    .ToHexString(SHA256.HashData(bytes))
                    .ToLowerInvariant()
            ))
            (Blake3Hash(ContentAddress.computeBlake3Hex bytes))
            false
            (int64 bytes.Length)
            (Grace.Shared.Utilities.getCurrentInstant ())
            true
            (DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc))

    /// Returns the direct parent path of a normalized repository path.
    let private parentPath (path: string) =
        if path = Constants.RootDirectoryPath then
            None
        else
            let separator = path.LastIndexOf('/')

            if separator < 0 then
                Some Constants.RootDirectoryPath
            else
                Some(path.Substring(0, separator))

    /// Builds a complete rooted graph whose directory hashes cover exact supplied files and explicitly selected empty directories.
    let private completeStatusWithDirectories (explicitDirectories: string array) (files: LocalFileVersion array) =
        let paths =
            seq {
                yield Constants.RootDirectoryPath

                for directory in explicitDirectories do
                    let mutable current = Some directory

                    while Option.isSome current do
                        let path = Option.get current
                        yield path
                        current <- parentPath path

                for (file: LocalFileVersion) in files do
                    let mutable current = parentPath (string file.RelativePath)

                    while Option.isSome current do
                        let path = Option.get current
                        yield path
                        current <- parentPath path
            }
            |> Seq.distinct
            |> Seq.sortByDescending (fun path -> if path = Constants.RootDirectoryPath then 0 else path.Split('/').Length)
            |> Seq.toArray

        let ids = Dictionary<string, DirectoryVersionId>(StringComparer.Ordinal)

        paths
        |> Seq.iter (fun path -> ids[path] <- Guid.NewGuid())

        let directories = Dictionary<string, LocalDirectoryVersion>(StringComparer.Ordinal)
        let current = Current()

        paths
        |> Seq.iter (fun path ->
            let childDirectories =
                paths
                |> Seq.filter (fun candidate -> parentPath candidate = Some path)
                |> Seq.map (fun child -> directories[child])
                |> Seq.toArray

            let directFiles =
                files
                |> Seq.filter (fun (file: LocalFileVersion) -> parentPath (string file.RelativePath) = Some path)
                |> Seq.toArray

            let entries =
                seq {
                    yield!
                        childDirectories
                        |> Seq.map (fun child ->
                            Services.DirectoryVersionPreimageEntry.Directory child.RelativePath child.Size child.Blake3Hash child.Sha256Hash)

                    yield!
                        directFiles
                        |> Seq.map (fun (file: LocalFileVersion) ->
                            Services.DirectoryVersionPreimageEntry.File file.RelativePath file.Size file.Blake3Hash file.Sha256Hash)
                }
                |> Seq.toArray

            directories[path] <- LocalDirectoryVersion.CreateWithHashes
                                     ids[path]
                                     current.OwnerId
                                     current.OrganizationId
                                     current.RepositoryId
                                     (RelativePath path)
                                     (Services.computeSha256ForDirectoryEntries (RelativePath path) entries)
                                     (Services.computeBlake3ForDirectory (RelativePath path) entries)
                                     (List<DirectoryVersionId>(
                                         childDirectories
                                         |> Seq.map (fun child -> child.DirectoryVersionId)
                                     ))
                                     (List<LocalFileVersion>(directFiles: LocalFileVersion array))
                                     (entries |> Seq.sumBy (fun entry -> entry.Size))
                                     (DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc)))

        let root = directories[Constants.RootDirectoryPath]
        let index = GraceIndex()

        directories.Values
        |> Seq.iter (fun directory -> index[directory.DirectoryVersionId] <- directory)

        let status =
            { GraceStatus.Default with
                Index = index
                RootDirectoryId = root.DirectoryVersionId
                RootDirectorySha256Hash = root.Sha256Hash
                RootDirectoryBlake3Hash = root.Blake3Hash
            }

        LocalStateDb.validateCompleteStatusTree status
        |> required

        status

    /// Builds a complete rooted graph whose directories are implied solely by its supplied files.
    let private completeStatus (files: LocalFileVersion array) = completeStatusWithDirectories Array.empty files

    /// Prepares one immutable manifest/byte pair matching the complete target graph's sole file.
    let private preparedContent (file: LocalFileVersion) (bytes: byte array) =
        let manifest =
            WorkingDirectoryUpdateContracts.PreparedManifest.create [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.File(
                                                                          file.RelativePath,
                                                                          file.Sha256Hash,
                                                                          file.Blake3Hash
                                                                      ) ]
            |> required

        WorkingDirectoryUpdateContracts.PreparedContent.create manifest (new Reader([ string file.RelativePath, bytes ])) CancellationToken.None
        |> fun task -> task.GetAwaiter().GetResult()
        |> required

    /// Prepares immutable bytes and explicit empty-directory topology for one direct production transaction.
    let private preparedContentWithDirectories (directories: string array) (file: LocalFileVersion) (bytes: byte array) =
        let entries =
            seq {
                yield!
                    directories
                    |> Seq.map (fun directory -> WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath directory))

                yield WorkingDirectoryUpdateContracts.PreparedManifestEntry.File(file.RelativePath, file.Sha256Hash, file.Blake3Hash)
            }

        let manifest =
            WorkingDirectoryUpdateContracts.PreparedManifest.create entries
            |> required

        WorkingDirectoryUpdateContracts.PreparedContent.create manifest (new Reader([ string file.RelativePath, bytes ])) CancellationToken.None
        |> fun task -> task.GetAwaiter().GetResult()
        |> required

    /// Prepares an exact multi-file immutable manifest for direct transaction compositions.
    let private preparedContentForFiles (directories: string array) (files: (LocalFileVersion * byte array) array) =
        let entries =
            seq {
                yield!
                    directories
                    |> Seq.map (fun directory -> WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath directory))

                yield!
                    files
                    |> Seq.map (fun (file, _) -> WorkingDirectoryUpdateContracts.PreparedManifestEntry.File(file.RelativePath, file.Sha256Hash, file.Blake3Hash))
            }

        let manifest =
            WorkingDirectoryUpdateContracts.PreparedManifest.create entries
            |> required

        let readerEntries =
            files
            |> Seq.map (fun (file, bytes) -> string file.RelativePath, bytes)
            |> Seq.toList

        WorkingDirectoryUpdateContracts.PreparedContent.create manifest (new Reader(readerEntries)) CancellationToken.None
        |> fun task -> task.GetAwaiter().GetResult()
        |> required

    /// Rewrites only the persisted revision so a direct regression can isolate an accepted-fact guard.
    let private rewriteLocalStatusRevision dbPath revision =
        SqliteConnection.ClearAllPools()

        use connection = new SqliteConnection($"Data Source={dbPath}")
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <- "UPDATE meta SET value = $value WHERE key = 'LocalStatusRevision';"

        command.Parameters.AddWithValue("$value", string revision)
        |> ignore

        command.ExecuteNonQuery() |> should equal 1

    /// Asserts that a direct transaction rejection identifies the single global guard that observed the changed fact.
    let private assertRejectedFor expectedReason outcome =
        match outcome with
        | Error (WorkingDirectoryUpdateContracts.Outcome.Rejected failure) ->
            WorkingDirectoryUpdateContracts.Failure.reason failure
            |> should contain expectedReason
        | _ -> Assert.Fail($"Expected rejected outcome for '{expectedReason}', got {outcome}.")

    /// Saves a direct configuration change without resetting the process cache that the transaction must not trust.
    let private saveConfigurationChange (current: GraceConfiguration) change =
        change current
        saveConfigFile (Path.Combine(current.ConfigurationDirectory, GraceConfigFileName)) current

    /// Verifies that the local transaction has not recorded any pending or terminal completion for a rejected or incomplete attempt.
    let private assertNoCompletion (current: GraceConfiguration) target branchId selection =
        let operation =
            WorkingDirectoryUpdateContracts.Operation.branchSwitchWithSelection branchId selection target
            |> required

        LocalStateDb.readWorkingDirectoryUpdateCompletion current.GraceStatusFile target operation
        |> fun task -> task.GetAwaiter().GetResult()
        |> should equal None

        LocalStateDb.readPendingWorkingDirectoryUpdateFinalization current.GraceStatusFile
        |> fun task -> task.GetAwaiter().GetResult()
        |> should equal None

    /// Reacquires the exact WDU scope to prove every direct transaction outcome has released its local lease.
    let private assertLeaseReacquirable repositoryId root =
        let scope =
            WorkingDirectoryUpdateCoordination.Scope.create repositoryId root
            |> required

        use acquired =
            WorkingDirectoryUpdateCoordination.Lease.acquire scope CancellationToken.None
            |> fun task -> task.GetAwaiter().GetResult()

        acquired |> should not' (be Null)

    /// Proves the five-input owner clears one prepared-content buffer exactly once after every terminal local result.
    let private assertPreparedDisposed prepared path =
        WorkingDirectoryUpdateContracts.PreparedContent.disposalCount prepared
        |> should equal 1

        WorkingDirectoryUpdateContracts.PreparedContent.openRead prepared path
        |> Result.isError
        |> should equal true

    /// Builds the resolved target tuple required by the production five-input transaction for one complete status graph.
    let private targetGraphForStatus repositoryId branchId status =
        let target =
            WorkingDirectoryUpdateContracts.Target.create
                repositoryId
                branchId
                status.RootDirectoryId
                status.RootDirectorySha256Hash
                status.RootDirectoryBlake3Hash
            |> required

        target,
        status,
        WorkingDirectoryUpdate.ResolvedTargetGraph.create target status
        |> required

    /// Builds the single-file resolved target tuple required by the production five-input transaction.
    let private targetGraph repositoryId branchId (file: LocalFileVersion) =
        let status = completeStatus [| file |]
        targetGraphForStatus repositoryId branchId status

    /// Creates one phase from the exact current SQLite snapshot and an optional distinct cancellation token.
    let private acceptedPhase (current: GraceConfiguration) (cancellationToken: CancellationToken) =
        let revision =
            LocalStateDb.readLocalStatusRevisionReadOnly current.GraceStatusFile
            |> fun task -> task.GetAwaiter().GetResult()

        let status =
            LocalStateDb.readCompleteStatusSnapshotReadOnly current.GraceStatusFile current.OwnerId current.OrganizationId current.RepositoryId
            |> fun task -> task.GetAwaiter().GetResult()
            |> required

        WorkingDirectoryUpdateContracts.AcceptedBranchPhase.create revision (WorkingDirectoryUpdate.LocalTransaction.statusFingerprint status) cancellationToken
        |> required

    /// Executes both typed Branch selections through the same production transaction and verifies pending SQLite facts.
    [<TestCase(true)>]
    [<TestCase(false)>]
    let ``five-input transaction writes verified bytes and typed pending Branch completion`` referenceSelection =
        withTempRepository (fun root ->
            let repositoryId = Guid.NewGuid()
            let currentBranchId = Guid.NewGuid()
            let targetBranchId = if referenceSelection then Guid.NewGuid() else currentBranchId
            let current = configure root repositoryId currentBranchId
            let currentStatus = completeStatus Array.empty<LocalFileVersion>

            LocalStateDb.replaceStatusSnapshotWithRevision current.GraceStatusFile currentStatus
            |> fun task -> task.GetAwaiter().GetResult()
            |> ignore

            let targetFile = localFile "nested/target.txt" (Encoding.UTF8.GetBytes("selected target bytes"))
            let target, _, resolvedGraph = targetGraph repositoryId targetBranchId targetFile
            let prepared = preparedContent targetFile (Encoding.UTF8.GetBytes("selected target bytes"))
            let phase = acceptedPhase current CancellationToken.None

            let selection =
                if referenceSelection then
                    WorkingDirectoryUpdateContracts.BranchSelection.Reference(Guid.NewGuid())
                else
                    WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion

            let correlation =
                WorkingDirectoryUpdate.DiagnosticCorrelation.create "direct-runtime"
                |> required

            let outcome =
                WorkingDirectoryUpdate.LocalTransaction.run phase selection resolvedGraph prepared correlation
                |> fun task -> task.GetAwaiter().GetResult()

            match outcome with
            | Ok completion ->
                WorkingDirectoryUpdate.LocalCompletion.target completion
                |> should equal target

                WorkingDirectoryUpdate.LocalCompletion.operation completion
                |> should
                    equal
                    (WorkingDirectoryUpdateContracts.Operation.branchSwitchWithSelection currentBranchId selection target
                     |> required)
            | Error result -> Assert.Fail($"Expected verified pending completion, got {result}.")

            File.ReadAllText(Path.Combine(root, "nested", "target.txt"))
            |> should equal "selected target bytes"

            match LocalStateDb.readPendingWorkingDirectoryUpdateFinalization current.GraceStatusFile
                  |> fun task -> task.GetAwaiter().GetResult()
                with
            | Some (LocalStateDb.PendingWorkingDirectoryUpdateFinalization.PendingBranchFinalization (persistedTarget, _, previousBranchId, persistedSelection)) ->
                persistedTarget |> should equal target
                previousBranchId |> should equal currentBranchId
                persistedSelection |> should equal selection
            | _ -> Assert.Fail("Expected one typed pending Branch finalization after verified local completion.")

            Current().BranchId |> should equal currentBranchId

            assertPreparedDisposed prepared (RelativePath "nested/target.txt")
            assertLeaseReacquirable repositoryId root)

    /// Proves a stale accepted revision rejects before object or working-tree mutation and disposes immutable prepared bytes.
    [<Test>]
    let ``five-input transaction rejects a stale accepted phase before mutation`` () =
        withTempRepository (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let current = configure root repositoryId branchId
            let baseline = completeStatus Array.empty<LocalFileVersion>

            LocalStateDb.replaceStatusSnapshotWithRevision current.GraceStatusFile baseline
            |> fun task -> task.GetAwaiter().GetResult()
            |> ignore

            let stalePhase = acceptedPhase current CancellationToken.None

            LocalStateDb.replaceStatusSnapshotWithRevision current.GraceStatusFile baseline
            |> fun task -> task.GetAwaiter().GetResult()
            |> ignore

            let selected = localFile "selected.txt" (Encoding.UTF8.GetBytes("selected target bytes"))
            let target, _, graph = targetGraph repositoryId branchId selected
            let prepared = preparedContent selected (Encoding.UTF8.GetBytes("selected target bytes"))

            let outcome =
                WorkingDirectoryUpdate.LocalTransaction.run
                    stalePhase
                    WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion
                    graph
                    prepared
                    (WorkingDirectoryUpdate.DiagnosticCorrelation.create "stale"
                     |> required)
                |> fun task -> task.GetAwaiter().GetResult()

            match outcome with
            | Error (WorkingDirectoryUpdateContracts.Outcome.Rejected _) -> ()
            | _ -> Assert.Fail($"Expected stale phase rejection, got {outcome}.")

            File.Exists(Path.Combine(root, "selected.txt"))
            |> should equal false

            Directory.Exists(current.ObjectDirectory)
            |> should equal false

            LocalStateDb.readWorkingDirectoryUpdateCompletion
                current.GraceStatusFile
                target
                (WorkingDirectoryUpdateContracts.Operation.branchSwitchWithSelection
                    branchId
                    WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion
                    target
                 |> required)
            |> fun task -> task.GetAwaiter().GetResult()
            |> should equal None

            assertPreparedDisposed prepared (RelativePath "selected.txt"))

    /// Proves graph/manifest topology disagreement rejects before #898 planning, objects, or working bytes change.
    [<Test>]
    let ``five-input transaction rejects graph manifest topology mismatch before mutation`` () =
        withTempRepository (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let current = configure root repositoryId branchId
            let baseline = completeStatus Array.empty<LocalFileVersion>

            LocalStateDb.replaceStatusSnapshotWithRevision current.GraceStatusFile baseline
            |> fun task -> task.GetAwaiter().GetResult()
            |> ignore

            let selected = localFile "selected.txt" (Encoding.UTF8.GetBytes("selected target bytes"))
            let _, _, graph = targetGraph repositoryId branchId selected
            let different = localFile "different.txt" (Encoding.UTF8.GetBytes("different target bytes"))
            let prepared = preparedContent different (Encoding.UTF8.GetBytes("different target bytes"))

            let outcome =
                WorkingDirectoryUpdate.LocalTransaction.run
                    (acceptedPhase current CancellationToken.None)
                    WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion
                    graph
                    prepared
                    (WorkingDirectoryUpdate.DiagnosticCorrelation.create "mismatch"
                     |> required)
                |> fun task -> task.GetAwaiter().GetResult()

            match outcome with
            | Error (WorkingDirectoryUpdateContracts.Outcome.Rejected _) -> ()
            | _ -> Assert.Fail($"Expected graph/manifest mismatch rejection, got {outcome}.")

            File.Exists(Path.Combine(root, "selected.txt"))
            |> should equal false

            File.Exists(Path.Combine(root, "different.txt"))
            |> should equal false

            Directory.Exists(current.ObjectDirectory)
            |> should equal false)

    /// Proves a configuration change while the WDU lease is held rejects at the mandatory post-lease disk reread.
    [<Test>]
    let ``five-input transaction rejects configuration changed while waiting for the lease`` () =
        withTempRepository (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let current = configure root repositoryId branchId
            let baseline = completeStatus Array.empty<LocalFileVersion>

            LocalStateDb.replaceStatusSnapshotWithRevision current.GraceStatusFile baseline
            |> fun task -> task.GetAwaiter().GetResult()
            |> ignore

            let selected = localFile "selected.txt" (Encoding.UTF8.GetBytes("selected target bytes"))
            let target, _, graph = targetGraph repositoryId branchId selected
            let selection = WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion
            let phase = acceptedPhase current CancellationToken.None
            let prepared = preparedContent selected (Encoding.UTF8.GetBytes("selected target bytes"))

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create repositoryId root
                |> required

            use sealedConfiguration = new ManualResetEventSlim(false)

            use heldLease =
                WorkingDirectoryUpdateCoordination.Lease.acquire scope CancellationToken.None
                |> fun task -> task.GetAwaiter().GetResult()

            WorkingDirectoryUpdate.LocalTransactionTesting.installAfterSealedConfiguration (fun () -> sealedConfiguration.Set())

            let running =
                Task.Run (fun () ->
                    WorkingDirectoryUpdate.LocalTransaction.run
                        phase
                        selection
                        graph
                        prepared
                        (WorkingDirectoryUpdate.DiagnosticCorrelation.create "configuration-while-waiting"
                         |> required)
                    |> fun task -> task.GetAwaiter().GetResult())

            sealedConfiguration.Wait(TimeSpan.FromSeconds(5.0))
            |> should equal true

            saveConfigurationChange current (fun configuration -> configuration.BranchId <- Guid.NewGuid())
            WorkingDirectoryUpdateCoordination.Lease.dispose heldLease
            let outcome = running.GetAwaiter().GetResult()

            match outcome with
            | Error (WorkingDirectoryUpdateContracts.Outcome.Rejected _) -> ()
            | _ -> Assert.Fail($"Expected fresh configuration rejection, got {outcome}.")

            File.Exists(Path.Combine(root, "selected.txt"))
            |> should equal false

            assertNoCompletion current target branchId selection
            assertPreparedDisposed prepared (RelativePath "selected.txt")
            assertLeaseReacquirable repositoryId root)

    /// Proves final pre-mutation validation rereads disk configuration after object publication instead of trusting a cached object.
    [<Test>]
    let ``five-input transaction rejects configuration changed after object publication`` () =
        withTempRepository (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let current = configure root repositoryId branchId
            let baseline = completeStatus Array.empty<LocalFileVersion>

            LocalStateDb.replaceStatusSnapshotWithRevision current.GraceStatusFile baseline
            |> fun task -> task.GetAwaiter().GetResult()
            |> ignore

            let selected = localFile "selected.txt" (Encoding.UTF8.GetBytes("selected target bytes"))
            let target, _, graph = targetGraph repositoryId branchId selected
            let selection = WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion
            let phase = acceptedPhase current CancellationToken.None
            let prepared = preparedContent selected (Encoding.UTF8.GetBytes("selected target bytes"))

            WorkingDirectoryUpdate.LocalTransactionTesting.installAfterObjectPublication (fun () ->
                saveConfigurationChange current (fun configuration -> configuration.BranchId <- Guid.NewGuid()))

            let outcome =
                WorkingDirectoryUpdate.LocalTransaction.run
                    phase
                    selection
                    graph
                    prepared
                    (WorkingDirectoryUpdate.DiagnosticCorrelation.create "configuration-after-objects"
                     |> required)
                |> fun task -> task.GetAwaiter().GetResult()

            match outcome with
            | Error (WorkingDirectoryUpdateContracts.Outcome.Rejected _) -> ()
            | _ -> Assert.Fail($"Expected final fresh configuration rejection, got {outcome}.")

            File.Exists(Path.Combine(root, "selected.txt"))
            |> should equal false

            assertNoCompletion current target branchId selection
            assertPreparedDisposed prepared (RelativePath "selected.txt")
            assertLeaseReacquirable repositoryId root)

    /// Proves independent final-root verification requires nested empty directories and leaves no completion after their loss.
    [<Test>]
    let ``five-input transaction detects a removed expected empty directory before SQLite completion`` () =
        withTempRepository (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let current = configure root repositoryId branchId
            let baseline = completeStatus Array.empty<LocalFileVersion>

            LocalStateDb.replaceStatusSnapshotWithRevision current.GraceStatusFile baseline
            |> fun task -> task.GetAwaiter().GetResult()
            |> ignore

            let selected = localFile "nested/target.txt" (Encoding.UTF8.GetBytes("selected target bytes"))

            let targetStatus =
                completeStatusWithDirectories [| "nested"; "nested/empty" |] [|
                    selected
                |]

            let target, _, graph = targetGraphForStatus repositoryId branchId targetStatus
            let selection = WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion
            let prepared = preparedContentWithDirectories [| "nested/empty" |] selected (Encoding.UTF8.GetBytes("selected target bytes"))

            WorkingDirectoryUpdate.LocalTransactionTesting.installAfterPlannedActions (fun () -> Directory.Delete(Path.Combine(root, "nested", "empty"), false))

            let outcome =
                WorkingDirectoryUpdate.LocalTransaction.run
                    (acceptedPhase current CancellationToken.None)
                    selection
                    graph
                    prepared
                    (WorkingDirectoryUpdate.DiagnosticCorrelation.create "missing-empty-directory"
                     |> required)
                |> fun task -> task.GetAwaiter().GetResult()

            match outcome with
            | Error (WorkingDirectoryUpdateContracts.Outcome.UpdateIncomplete _) -> ()
            | _ -> Assert.Fail($"Expected incomplete topology verification, got {outcome}.")

            Directory.Exists(Path.Combine(root, "nested", "empty"))
            |> should equal false

            File.Exists(Path.Combine(root, "nested", "target.txt"))
            |> should equal true

            assertNoCompletion current target branchId selection
            assertPreparedDisposed prepared (RelativePath "nested/target.txt")
            assertLeaseReacquirable repositoryId root)

    /// Proves an injected failure immediately before the first mutable filesystem call remains a clean pre-mutation rejection.
    [<Test>]
    let ``five-input transaction rejects an injected failure immediately before first mutation`` () =
        withTempRepository (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let current = configure root repositoryId branchId
            let baseline = completeStatus Array.empty<LocalFileVersion>

            LocalStateDb.replaceStatusSnapshotWithRevision current.GraceStatusFile baseline
            |> fun task -> task.GetAwaiter().GetResult()
            |> ignore

            let selected = localFile "nested/target.txt" (Encoding.UTF8.GetBytes("selected target bytes"))
            let target, _, graph = targetGraph repositoryId branchId selected
            let selection = WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion
            WorkingDirectoryUpdate.LocalTransactionTesting.installBeforeFirstMutation (fun () -> raise (IOException("before first mutation")))

            let prepared = preparedContent selected (Encoding.UTF8.GetBytes("selected target bytes"))

            let outcome =
                WorkingDirectoryUpdate.LocalTransaction.run
                    (acceptedPhase current CancellationToken.None)
                    selection
                    graph
                    prepared
                    (WorkingDirectoryUpdate.DiagnosticCorrelation.create "before-first-mutation"
                     |> required)
                |> fun task -> task.GetAwaiter().GetResult()

            match outcome with
            | Error (WorkingDirectoryUpdateContracts.Outcome.Rejected _) -> ()
            | _ -> Assert.Fail($"Expected pre-mutation rejection, got {outcome}.")

            File.Exists(Path.Combine(root, "nested", "target.txt"))
            |> should equal false

            assertNoCompletion current target branchId selection
            assertPreparedDisposed prepared (RelativePath "nested/target.txt")
            assertLeaseReacquirable repositoryId root)

    /// Proves a failure after a filesystem action begins preserves marker evidence and reports incomplete without completion.
    [<Test>]
    let ``five-input transaction reports incomplete for injected failure after first mutation begins`` () =
        withTempRepository (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let current = configure root repositoryId branchId
            let baseline = completeStatus Array.empty<LocalFileVersion>

            LocalStateDb.replaceStatusSnapshotWithRevision current.GraceStatusFile baseline
            |> fun task -> task.GetAwaiter().GetResult()
            |> ignore

            let selected = localFile "nested/target.txt" (Encoding.UTF8.GetBytes("selected target bytes"))
            let target, _, graph = targetGraph repositoryId branchId selected
            let selection = WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create repositoryId root
                |> required

            WorkingDirectoryUpdate.LocalTransactionTesting.installAfterFirstMutationBegan (fun () -> raise (IOException("after first mutation began")))

            let prepared = preparedContent selected (Encoding.UTF8.GetBytes("selected target bytes"))

            let outcome =
                WorkingDirectoryUpdate.LocalTransaction.run
                    (acceptedPhase current CancellationToken.None)
                    selection
                    graph
                    prepared
                    (WorkingDirectoryUpdate.DiagnosticCorrelation.create "after-first-mutation"
                     |> required)
                |> fun task -> task.GetAwaiter().GetResult()

            match outcome with
            | Error (WorkingDirectoryUpdateContracts.Outcome.UpdateIncomplete _) -> ()
            | _ -> Assert.Fail($"Expected post-mutation incomplete outcome, got {outcome}.")

            File.Exists(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
            |> should equal true

            assertNoCompletion current target branchId selection
            assertPreparedDisposed prepared (RelativePath "nested/target.txt")
            assertLeaseReacquirable repositoryId root)

    /// Proves cancellation during final planning remains pre-mutation, while cancellation after the first action begins remains incomplete.
    [<Test>]
    let ``five-input transaction honors cancellation only before first working-tree mutation`` () =
        let runCase cancelDuringFinalPlanning cancelBeforeFirstMutation cancelAfterMutationBegins expectedIncomplete =
            withTempRepository (fun root ->
                let repositoryId = Guid.NewGuid()
                let branchId = Guid.NewGuid()
                let current = configure root repositoryId branchId
                let baseline = completeStatus Array.empty<LocalFileVersion>

                LocalStateDb.replaceStatusSnapshotWithRevision current.GraceStatusFile baseline
                |> fun task -> task.GetAwaiter().GetResult()
                |> ignore

                let selected = localFile "nested/target.txt" (Encoding.UTF8.GetBytes("selected target bytes"))
                let target, _, graph = targetGraph repositoryId branchId selected
                let selection = WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion

                let scope =
                    WorkingDirectoryUpdateCoordination.Scope.create repositoryId root
                    |> required

                use cancellation = new CancellationTokenSource()

                if cancelDuringFinalPlanning then
                    WorkingDirectoryUpdate.LocalTransactionTesting.installBeforeFinalPlanning (fun () -> cancellation.Cancel())

                if cancelBeforeFirstMutation then
                    WorkingDirectoryUpdate.LocalTransactionTesting.installBeforeFirstMutation (fun () -> cancellation.Cancel())

                if cancelAfterMutationBegins then
                    WorkingDirectoryUpdate.LocalTransactionTesting.installAfterFirstMutationBegan (fun () ->
                        cancellation.Cancel()
                        raise (OperationCanceledException(cancellation.Token)))

                let prepared = preparedContent selected (Encoding.UTF8.GetBytes("selected target bytes"))

                let outcome =
                    WorkingDirectoryUpdate.LocalTransaction.run
                        (acceptedPhase current cancellation.Token)
                        selection
                        graph
                        prepared
                        (WorkingDirectoryUpdate.DiagnosticCorrelation.create "cancellation-boundary"
                         |> required)
                    |> fun task -> task.GetAwaiter().GetResult()

                if expectedIncomplete then
                    match outcome with
                    | Error (WorkingDirectoryUpdateContracts.Outcome.UpdateIncomplete _) -> ()
                    | _ -> Assert.Fail($"Expected post-mutation incomplete cancellation outcome, got {outcome}.")
                else
                    match outcome with
                    | Error (WorkingDirectoryUpdateContracts.Outcome.Rejected _) -> ()
                    | _ -> Assert.Fail($"Expected pre-mutation rejected cancellation outcome, got {outcome}.")

                if expectedIncomplete then
                    File.Exists(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
                    |> should equal true

                assertNoCompletion current target branchId selection
                assertPreparedDisposed prepared (RelativePath "nested/target.txt")
                assertLeaseReacquirable repositoryId root)

        runCase true false false false
        runCase false true false false
        runCase false false true true

    /// Proves a file that appears after final planning cannot be overwritten by a previously safe absent-target copy.
    [<Test>]
    let ``five-input transaction preserves late untracked target bytes at the action boundary`` () =
        withTempRepository (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let current = configure root repositoryId branchId
            let baseline = completeStatus Array.empty<LocalFileVersion>

            LocalStateDb.replaceStatusSnapshotWithRevision current.GraceStatusFile baseline
            |> fun task -> task.GetAwaiter().GetResult()
            |> ignore

            let selected = localFile "target.txt" (Encoding.UTF8.GetBytes("selected target bytes"))
            let target, _, graph = targetGraph repositoryId branchId selected
            let prepared = preparedContent selected (Encoding.UTF8.GetBytes("selected target bytes"))

            WorkingDirectoryUpdate.LocalTransactionTesting.installAfterFinalGlobalFactGate (fun () ->
                File.WriteAllText(Path.Combine(root, "target.txt"), "late untracked bytes"))

            let outcome =
                WorkingDirectoryUpdate.LocalTransaction.run
                    (acceptedPhase current CancellationToken.None)
                    WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion
                    graph
                    prepared
                    (WorkingDirectoryUpdate.DiagnosticCorrelation.create "late-target"
                     |> required)
                |> fun task -> task.GetAwaiter().GetResult()

            match outcome with
            | Error (WorkingDirectoryUpdateContracts.Outcome.Rejected _) -> ()
            | _ -> Assert.Fail($"Expected action-time target rejection, got {outcome}.")

            File.ReadAllText(Path.Combine(root, "target.txt"))
            |> should equal "late untracked bytes"

            assertNoCompletion current target branchId WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion
            assertPreparedDisposed prepared (RelativePath "target.txt")
            assertLeaseReacquirable repositoryId root)

    /// Proves a tracked file with its accepted old dual hashes is the only existing copy target that may be replaced.
    [<Test>]
    let ``five-input transaction replaces a verified tracked file and records pending completion`` () =
        withTempRepository (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let current = configure root repositoryId branchId
            let oldBytes = Encoding.UTF8.GetBytes("accepted old bytes")
            let newBytes = Encoding.UTF8.GetBytes("selected replacement bytes")
            let oldFile = localFile "target.txt" oldBytes
            let newFile = localFile "target.txt" newBytes
            let baseline = completeStatus [| oldFile |]
            let target, targetStatus, graph = targetGraph repositoryId branchId newFile
            let selection = WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion

            LocalStateDb.replaceStatusSnapshotWithRevision current.GraceStatusFile baseline
            |> fun task -> task.GetAwaiter().GetResult()
            |> ignore

            File.WriteAllBytes(Path.Combine(root, "target.txt"), oldBytes)
            let prepared = preparedContent newFile newBytes

            let outcome =
                WorkingDirectoryUpdate.LocalTransaction.run
                    (acceptedPhase current CancellationToken.None)
                    selection
                    graph
                    prepared
                    (WorkingDirectoryUpdate.DiagnosticCorrelation.create "tracked-replacement"
                     |> required)
                |> fun task -> task.GetAwaiter().GetResult()

            match outcome with
            | Ok completion ->
                WorkingDirectoryUpdate.LocalCompletion.target completion
                |> should equal target
            | Error result -> Assert.Fail($"Expected tracked replacement completion, got {result}.")

            File.ReadAllBytes(Path.Combine(root, "target.txt"))
            |> should equal newBytes

            LocalStateDb.readCompleteStatusSnapshotReadOnly current.GraceStatusFile current.OwnerId current.OrganizationId current.RepositoryId
            |> fun task -> task.GetAwaiter().GetResult()
            |> required
            |> WorkingDirectoryUpdate.LocalTransaction.statusFingerprint
            |> should equal (WorkingDirectoryUpdate.LocalTransaction.statusFingerprint targetStatus)

            LocalStateDb.readPendingWorkingDirectoryUpdateFinalization current.GraceStatusFile
            |> fun task -> task.GetAwaiter().GetResult()
            |> Option.isSome
            |> should equal true

            assertPreparedDisposed prepared (RelativePath "target.txt")
            assertLeaseReacquirable repositoryId root)

    /// Proves a tracked replacement rechecks the accepted old identity at its action boundary before overwriting bytes.
    [<Test>]
    let ``five-input transaction rejects a late tracked replacement identity change before mutation`` () =
        withTempRepository (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let current = configure root repositoryId branchId
            let oldBytes = Encoding.UTF8.GetBytes("accepted old bytes")
            let newBytes = Encoding.UTF8.GetBytes("selected replacement bytes")
            let oldFile = localFile "target.txt" oldBytes
            let newFile = localFile "target.txt" newBytes
            let baseline = completeStatus [| oldFile |]
            let target, _, graph = targetGraph repositoryId branchId newFile
            let selection = WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion

            LocalStateDb.replaceStatusSnapshotWithRevision current.GraceStatusFile baseline
            |> fun task -> task.GetAwaiter().GetResult()
            |> ignore

            File.WriteAllBytes(Path.Combine(root, "target.txt"), oldBytes)
            let prepared = preparedContent newFile newBytes

            WorkingDirectoryUpdate.LocalTransactionTesting.installAfterFinalGlobalFactGate (fun () ->
                File.WriteAllText(Path.Combine(root, "target.txt"), "late tracked bytes"))

            let outcome =
                WorkingDirectoryUpdate.LocalTransaction.run
                    (acceptedPhase current CancellationToken.None)
                    selection
                    graph
                    prepared
                    (WorkingDirectoryUpdate.DiagnosticCorrelation.create "late-tracked-replacement"
                     |> required)
                |> fun task -> task.GetAwaiter().GetResult()

            assertRejectedFor "Tracked file 'target.txt' changed" outcome

            File.ReadAllText(Path.Combine(root, "target.txt"))
            |> should equal "late tracked bytes"

            assertNoCompletion current target branchId selection
            assertPreparedDisposed prepared (RelativePath "target.txt")
            assertLeaseReacquirable repositoryId root)

    /// Proves a retained tracked file is part of the whole final-plan applicability check before an unrelated copy begins.
    [<Test>]
    let ``five-input transaction rejects a late retained tracked file change before unrelated mutation`` () =
        withTempRepository (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let current = configure root repositoryId branchId
            let retainedBytes = Encoding.UTF8.GetBytes("retained bytes")
            let addedBytes = Encoding.UTF8.GetBytes("new bytes")
            let retained = localFile "retained.txt" retainedBytes
            let added = localFile "new.txt" addedBytes
            let baseline = completeStatus [| retained |]
            let targetStatus = completeStatus [| retained; added |]
            let target, _, graph = targetGraphForStatus repositoryId branchId targetStatus
            let selection = WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion

            LocalStateDb.replaceStatusSnapshotWithRevision current.GraceStatusFile baseline
            |> fun task -> task.GetAwaiter().GetResult()
            |> ignore

            File.WriteAllBytes(Path.Combine(root, "retained.txt"), retainedBytes)

            let prepared =
                preparedContentForFiles
                    Array.empty
                    [|
                        retained, retainedBytes
                        added, addedBytes
                    |]

            WorkingDirectoryUpdate.LocalTransactionTesting.installAfterFinalGlobalFactGate (fun () ->
                File.WriteAllText(Path.Combine(root, "retained.txt"), "late retained bytes"))

            let outcome =
                WorkingDirectoryUpdate.LocalTransaction.run
                    (acceptedPhase current CancellationToken.None)
                    selection
                    graph
                    prepared
                    (WorkingDirectoryUpdate.DiagnosticCorrelation.create "late-retained-file"
                     |> required)
                |> fun task -> task.GetAwaiter().GetResult()

            assertRejectedFor "complete Working Directory Update plan changed" outcome

            File.ReadAllText(Path.Combine(root, "retained.txt"))
            |> should equal "late retained bytes"

            File.Exists(Path.Combine(root, "new.txt"))
            |> should equal false

            assertNoCompletion current target branchId selection
            assertPreparedDisposed prepared (RelativePath "retained.txt")
            assertLeaseReacquirable repositoryId root)

    /// Proves a retained tracked directory's newly eligible descendant invalidates the entire final plan before another action mutates.
    [<Test>]
    let ``five-input transaction rejects a late eligible descendant under a retained directory`` () =
        withTempRepository (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let current = configure root repositoryId branchId
            let addedBytes = Encoding.UTF8.GetBytes("new bytes")
            let added = localFile "new.txt" addedBytes
            let baseline = completeStatusWithDirectories [| "retained" |] Array.empty<LocalFileVersion>

            let targetStatus =
                completeStatusWithDirectories [| "retained" |] [|
                    added
                |]

            let target, _, graph = targetGraphForStatus repositoryId branchId targetStatus
            let selection = WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion

            LocalStateDb.replaceStatusSnapshotWithRevision current.GraceStatusFile baseline
            |> fun task -> task.GetAwaiter().GetResult()
            |> ignore

            Directory.CreateDirectory(Path.Combine(root, "retained"))
            |> ignore

            let prepared =
                preparedContentForFiles [| "retained" |] [|
                    added, addedBytes
                |]

            WorkingDirectoryUpdate.LocalTransactionTesting.installAfterFinalGlobalFactGate (fun () ->
                File.WriteAllText(Path.Combine(root, "retained", "late.txt"), "late eligible descendant"))

            let outcome =
                WorkingDirectoryUpdate.LocalTransaction.run
                    (acceptedPhase current CancellationToken.None)
                    selection
                    graph
                    prepared
                    (WorkingDirectoryUpdate.DiagnosticCorrelation.create "late-retained-directory"
                     |> required)
                |> fun task -> task.GetAwaiter().GetResult()

            assertRejectedFor "topology rejected 'retained/late.txt'" outcome

            File.ReadAllText(Path.Combine(root, "retained", "late.txt"))
            |> should equal "late eligible descendant"

            File.Exists(Path.Combine(root, "new.txt"))
            |> should equal false

            assertNoCompletion current target branchId selection
            assertPreparedDisposed prepared (RelativePath "new.txt")
            assertLeaseReacquirable repositoryId root)

    /// Proves an accepted tracked file can be removed before its path becomes a target directory.
    [<Test>]
    let ``five-input transaction applies a tracked file to directory composition`` () =
        withTempRepository (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let current = configure root repositoryId branchId
            let oldBytes = Encoding.UTF8.GetBytes("tracked file")
            let newBytes = Encoding.UTF8.GetBytes("nested target")
            let oldFile = localFile "switch" oldBytes
            let newFile = localFile "switch/new.txt" newBytes
            let baseline = completeStatus [| oldFile |]
            let targetStatus = completeStatus [| newFile |]
            let _, _, graph = targetGraphForStatus repositoryId branchId targetStatus

            LocalStateDb.replaceStatusSnapshotWithRevision current.GraceStatusFile baseline
            |> fun task -> task.GetAwaiter().GetResult()
            |> ignore

            File.WriteAllBytes(Path.Combine(root, "switch"), oldBytes)
            let prepared = preparedContent newFile newBytes

            let outcome =
                WorkingDirectoryUpdate.LocalTransaction.run
                    (acceptedPhase current CancellationToken.None)
                    WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion
                    graph
                    prepared
                    (WorkingDirectoryUpdate.DiagnosticCorrelation.create "file-to-directory"
                     |> required)
                |> fun task -> task.GetAwaiter().GetResult()

            match outcome with
            | Ok _ -> ()
            | Error result -> Assert.Fail($"Expected file-to-directory completion, got {result}.")

            Directory.Exists(Path.Combine(root, "switch"))
            |> should equal true

            File.ReadAllBytes(Path.Combine(root, "switch", "new.txt"))
            |> should equal newBytes

            assertPreparedDisposed prepared (RelativePath "switch/new.txt")
            assertLeaseReacquirable repositoryId root)

    /// Proves tracked directory descendants are removed before the exact path becomes a target file.
    [<Test>]
    let ``five-input transaction applies a tracked directory to file composition`` () =
        withTempRepository (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let current = configure root repositoryId branchId
            let oldBytes = Encoding.UTF8.GetBytes("tracked nested file")
            let newBytes = Encoding.UTF8.GetBytes("target file")
            let oldFile = localFile "switch/old.txt" oldBytes
            let newFile = localFile "switch" newBytes
            let baseline = completeStatus [| oldFile |]
            let target, _, graph = targetGraph repositoryId branchId newFile
            let selection = WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion

            LocalStateDb.replaceStatusSnapshotWithRevision current.GraceStatusFile baseline
            |> fun task -> task.GetAwaiter().GetResult()
            |> ignore

            Directory.CreateDirectory(Path.Combine(root, "switch"))
            |> ignore

            File.WriteAllBytes(Path.Combine(root, "switch", "old.txt"), oldBytes)
            let prepared = preparedContent newFile newBytes

            let outcome =
                WorkingDirectoryUpdate.LocalTransaction.run
                    (acceptedPhase current CancellationToken.None)
                    selection
                    graph
                    prepared
                    (WorkingDirectoryUpdate.DiagnosticCorrelation.create "directory-to-file"
                     |> required)
                |> fun task -> task.GetAwaiter().GetResult()

            match outcome with
            | Ok _ -> ()
            | Error result -> Assert.Fail($"Expected directory-to-file completion, got {result}.")

            File.ReadAllBytes(Path.Combine(root, "switch"))
            |> should equal newBytes

            assertPreparedDisposed prepared (RelativePath "switch")
            assertLeaseReacquirable repositoryId root)

    /// Proves a source object replaced after publication rejects before the first filesystem mutation and cleans owned evidence.
    [<Test>]
    let ``five-input transaction rejects a replaced source object before mutation`` () =
        withTempRepository (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let current = configure root repositoryId branchId
            let baseline = completeStatus Array.empty<LocalFileVersion>

            LocalStateDb.replaceStatusSnapshotWithRevision current.GraceStatusFile baseline
            |> fun task -> task.GetAwaiter().GetResult()
            |> ignore

            let selected = localFile "target.txt" (Encoding.UTF8.GetBytes("selected target bytes"))
            let target, _, graph = targetGraph repositoryId branchId selected
            let prepared = preparedContent selected (Encoding.UTF8.GetBytes("selected target bytes"))

            WorkingDirectoryUpdate.LocalTransactionTesting.installAfterObjectPublication (fun () ->
                let objectFile =
                    Path.Combine(
                        current.ObjectDirectory,
                        string selected.RelativePath,
                        Services.getLocalObjectCacheFileName selected.RelativePath selected.Sha256Hash selected.Blake3Hash
                    )

                File.WriteAllText(objectFile, "replaced object bytes"))

            let outcome =
                WorkingDirectoryUpdate.LocalTransaction.run
                    (acceptedPhase current CancellationToken.None)
                    WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion
                    graph
                    prepared
                    (WorkingDirectoryUpdate.DiagnosticCorrelation.create "replaced-object"
                     |> required)
                |> fun task -> task.GetAwaiter().GetResult()

            match outcome with
            | Error (WorkingDirectoryUpdateContracts.Outcome.Rejected _) -> ()
            | _ -> Assert.Fail($"Expected pre-mutation object rejection, got {outcome}.")

            File.Exists(Path.Combine(root, "target.txt"))
            |> should equal false

            assertNoCompletion current target branchId WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion
            assertPreparedDisposed prepared (RelativePath "target.txt")
            assertLeaseReacquirable repositoryId root)

    /// Proves corrupt immutable bytes already present at the object path reject before marker-backed working-tree mutation.
    [<Test>]
    let ``five-input transaction rejects an initially corrupt existing source object before mutation`` () =
        withTempRepository (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let current = configure root repositoryId branchId
            let baseline = completeStatus Array.empty<LocalFileVersion>

            LocalStateDb.replaceStatusSnapshotWithRevision current.GraceStatusFile baseline
            |> fun task -> task.GetAwaiter().GetResult()
            |> ignore

            let selected = localFile "target.txt" (Encoding.UTF8.GetBytes("selected target bytes"))
            let target, _, graph = targetGraph repositoryId branchId selected
            let selection = WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create repositoryId root
                |> required

            let objectDirectory = Path.Combine(current.ObjectDirectory, string selected.RelativePath)

            Directory.CreateDirectory(objectDirectory)
            |> ignore

            File.WriteAllText(
                Path.Combine(objectDirectory, Services.getLocalObjectCacheFileName selected.RelativePath selected.Sha256Hash selected.Blake3Hash),
                "initially corrupt object bytes"
            )

            let prepared = preparedContent selected (Encoding.UTF8.GetBytes("selected target bytes"))

            let outcome =
                WorkingDirectoryUpdate.LocalTransaction.run
                    (acceptedPhase current CancellationToken.None)
                    selection
                    graph
                    prepared
                    (WorkingDirectoryUpdate.DiagnosticCorrelation.create "initially-corrupt-object"
                     |> required)
                |> fun task -> task.GetAwaiter().GetResult()

            match outcome with
            | Error (WorkingDirectoryUpdateContracts.Outcome.Rejected _) -> ()
            | _ -> Assert.Fail($"Expected initially corrupt object rejection, got {outcome}.")

            File.Exists(Path.Combine(root, "target.txt"))
            |> should equal false

            File.Exists(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
            |> should equal false

            assertNoCompletion current target branchId selection
            assertPreparedDisposed prepared (RelativePath "target.txt")
            assertLeaseReacquirable repositoryId root)

    /// Proves every post-plan global fact is reread before the first action can mutate the working tree.
    [<TestCase("revision")>]
    [<TestCase("fingerprint")>]
    [<TestCase("pending")>]
    [<TestCase("marker-attempt")>]
    let ``five-input transaction independently rejects each post-plan global fact change`` changedFact =
        withTempRepository (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let current = configure root repositoryId branchId
            let baseline = completeStatus Array.empty<LocalFileVersion>

            LocalStateDb.replaceStatusSnapshotWithRevision current.GraceStatusFile baseline
            |> fun task -> task.GetAwaiter().GetResult()
            |> ignore

            let selected = localFile "target.txt" (Encoding.UTF8.GetBytes("selected target bytes"))
            let target, targetStatus, graph = targetGraph repositoryId branchId selected
            let selection = WorkingDirectoryUpdateContracts.BranchSelection.DirectoryVersion

            let operation =
                WorkingDirectoryUpdateContracts.Operation.branchSwitchWithSelection branchId selection target
                |> required

            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create repositoryId root
                |> required

            let prepared = preparedContent selected (Encoding.UTF8.GetBytes("selected target bytes"))
            let accepted = acceptedPhase current CancellationToken.None

            let acceptedRevision =
                LocalStateDb.readLocalStatusRevisionReadOnly current.GraceStatusFile
                |> fun task -> task.GetAwaiter().GetResult()

            WorkingDirectoryUpdate.LocalTransactionTesting.installBeforeFinalGlobalFactGate (fun () ->
                match changedFact with
                | "revision" -> rewriteLocalStatusRevision current.GraceStatusFile (acceptedRevision + 1L)
                | "fingerprint" ->
                    let changed = completeStatus [| localFile "different.txt" (Encoding.UTF8.GetBytes("different status bytes")) |]

                    LocalStateDb.replaceStatusSnapshotWithRevision current.GraceStatusFile changed
                    |> fun task -> task.GetAwaiter().GetResult()
                    |> ignore

                    rewriteLocalStatusRevision current.GraceStatusFile acceptedRevision
                | "pending" ->
                    LocalStateDb.commitWorkingDirectoryUpdateCompletion
                        current.GraceStatusFile
                        targetStatus
                        (targetStatus.Index.Values :> IEnumerable<LocalDirectoryVersion>)
                        (LocalStateDb.WorkingDirectoryUpdateCompletionDetails.BranchDirectoryVersionFinalization branchId)
                        target
                        operation
                    |> fun task -> task.GetAwaiter().GetResult()
                    |> ignore

                    LocalStateDb.replaceStatusSnapshotWithRevision current.GraceStatusFile baseline
                    |> fun task -> task.GetAwaiter().GetResult()
                    |> ignore

                    rewriteLocalStatusRevision current.GraceStatusFile acceptedRevision
                | "marker-attempt" ->
                    let differentAttempt = WorkingDirectoryUpdateContracts.AttemptToken.create ()

                    WorkingDirectoryUpdateCoordination.Marker.create scope differentAttempt target operation
                    |> required
                    |> WorkingDirectoryUpdateCoordination.Marker.write scope
                    |> fun task -> task.GetAwaiter().GetResult()
                | value -> Assert.Fail($"Unexpected global fact '{value}'."))

            let outcome =
                WorkingDirectoryUpdate.LocalTransaction.run
                    accepted
                    selection
                    graph
                    prepared
                    (WorkingDirectoryUpdate.DiagnosticCorrelation.create $"post-plan-{changedFact}"
                     |> required)
                |> fun task -> task.GetAwaiter().GetResult()

            match changedFact with
            | "revision" -> assertRejectedFor "Accepted local-status revision changed after final planning." outcome
            | "fingerprint" -> assertRejectedFor "Accepted complete local-status fingerprint changed after final planning." outcome
            | "pending" -> assertRejectedFor "Pending Working Directory Update finalization changed after final planning." outcome
            | "marker-attempt" ->
                match outcome with
                | Error (WorkingDirectoryUpdateContracts.Outcome.Rejected _) -> ()
                | _ -> Assert.Fail($"Expected post-plan marker-attempt rejection, got {outcome}.")
            | value -> Assert.Fail($"Unexpected global fact '{value}'.")

            File.Exists(Path.Combine(root, "target.txt"))
            |> should equal false

            if changedFact <> "pending" then
                assertNoCompletion current target branchId selection

            assertPreparedDisposed prepared (RelativePath "target.txt")
            assertLeaseReacquirable repositoryId root)
