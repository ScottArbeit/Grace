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

    /// Builds a complete rooted graph whose directory hashes cover the exact supplied files.
    let private completeStatus (files: LocalFileVersion array) =
        let paths =
            seq {
                yield Constants.RootDirectoryPath

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

    /// Builds the single-file resolved target tuple required by the production five-input transaction.
    let private targetGraph repositoryId branchId (file: LocalFileVersion) =
        let status = completeStatus [| file |]

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
            | WorkingDirectoryUpdateContracts.Outcome.Updated _ -> ()
            | _ -> Assert.Fail($"Expected verified pending completion, got {outcome}.")

            File.ReadAllText(Path.Combine(root, "nested", "target.txt"))
            |> should equal "selected target bytes"

            match LocalStateDb.readPendingWorkingDirectoryUpdateFinalization current.GraceStatusFile
                  |> fun task -> task.GetAwaiter().GetResult()
                with
            | Some (LocalStateDb.PendingWorkingDirectoryUpdateFinalization.PendingBranchFinalization (persistedTarget, _, previousBranchId, persistedSelection)) ->
                persistedTarget |> should equal target
                previousBranchId |> should equal currentBranchId
                persistedSelection |> should equal selection
            | _ -> Assert.Fail("Expected one typed pending Branch finalization after verified local completion."))

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
            | WorkingDirectoryUpdateContracts.Outcome.Rejected _ -> ()
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

            WorkingDirectoryUpdateContracts.PreparedContent.openRead prepared (RelativePath "selected.txt")
            |> ignore)

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
            | WorkingDirectoryUpdateContracts.Outcome.Rejected _ -> ()
            | _ -> Assert.Fail($"Expected graph/manifest mismatch rejection, got {outcome}.")

            File.Exists(Path.Combine(root, "selected.txt"))
            |> should equal false

            File.Exists(Path.Combine(root, "different.txt"))
            |> should equal false

            Directory.Exists(current.ObjectDirectory)
            |> should equal false)
