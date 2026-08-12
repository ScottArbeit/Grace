namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.CLI.Services
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Constants
open Grace.Shared.Services
open Grace.Shared.Utilities
open Grace.Types.Common
open Microsoft.Data.Sqlite
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Exercises the private update transaction with real working files, object files, and SQLite state.
[<NonParallelizable>]
module WorkingDirectoryUpdateEngineTests =
    /// Fails the scenario immediately when a private contract constructor rejects valid fixture data.
    let private required =
        function
        | Ok value -> value
        | Error error -> failwith error

    /// Owns independent in-memory streams for one exact prepared-content manifest.
    type private Reader(files: IReadOnlyDictionary<string, byte array>) =
        interface WorkingDirectoryUpdateContracts.IPreparedContentReader with
            member _.FilePaths = files.Keys

            member _.OpenReadAsync(relativePath, _) =
                let bytes = files[string relativePath]
                Task.FromResult(new MemoryStream(bytes, writable = false) :> Stream)

            member _.Dispose() = ()

    /// Returns the immutable selected-state tuple supplied by a test scenario after lease acquisition.
    type private SelectedStateReader(selection: WorkingDirectoryUpdate.AcceptedSelection) =
        interface WorkingDirectoryUpdate.ISelectedStateReader with
            member _.ReadAsync(_) = Task.FromResult(selection)

    /// Mutates a real fixture boundary while the engine holds its lease, then returns the selected snapshot.
    type private MutatingSelectedStateReader(selection: WorkingDirectoryUpdate.AcceptedSelection, mutate: unit -> unit) =
        interface WorkingDirectoryUpdate.ISelectedStateReader with
            member _.ReadAsync(_) =
                mutate ()
                Task.FromResult(selection)

    /// Returns no accepted state to model a caller selection that changed while the engine was waiting for its lease.
    type private ChangedSelectedStateReader() =
        interface WorkingDirectoryUpdate.ISelectedStateReader with
            member _.ReadAsync(_) = Task.FromResult(Unchecked.defaultof<WorkingDirectoryUpdate.AcceptedSelection>)

    /// Records an idempotent finalization without receiving filesystem or SQLite inputs.
    type private Finalizer() =
        let mutable invocationCount = 0

        member _.InvocationCount = invocationCount

        interface WorkingDirectoryUpdate.IIdempotentFinalizer with
            member _.FinalizeAsync(_, _) =
                invocationCount <- invocationCount + 1
                Task.CompletedTask

    /// Proves the finalizer sees one pending completion only after matching status and object-cache facts are durable.
    type private PendingCompletionFinalizer(databasePath: string, target, operation, rootMetadata: LocalDirectoryVersion) =
        let mutable invocationCount = 0

        member _.InvocationCount = invocationCount

        interface WorkingDirectoryUpdate.IIdempotentFinalizer with
            member _.FinalizeAsync(_, _) =
                task {
                    let! completion = LocalStateDb.readWorkingDirectoryUpdateCompletion databasePath target operation

                    completion
                    |> should equal (Some LocalStateDb.WorkingDirectoryUpdateCompletion.Pending)

                    let! status = LocalStateDb.readStatusSnapshot databasePath

                    status.RootDirectoryId
                    |> should equal (WorkingDirectoryUpdateContracts.Target.rootDirectoryVersionId target)

                    status.RootDirectorySha256Hash
                    |> should equal (WorkingDirectoryUpdateContracts.Target.sha256Hash target)

                    status.RootDirectoryBlake3Hash
                    |> should equal (WorkingDirectoryUpdateContracts.Target.blake3Hash target)

                    use connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly")
                    connection.Open()
                    use command = connection.CreateCommand()

                    command.CommandText <-
                        "SELECT COUNT(*) FROM object_cache_directories WHERE directory_version_id = $id AND sha256_hash = $sha256 AND blake3_hash = $blake3;"

                    command.Parameters.AddWithValue("$id", rootMetadata.DirectoryVersionId.ToString())
                    |> ignore

                    command.Parameters.AddWithValue("$sha256", string rootMetadata.Sha256Hash)
                    |> ignore

                    command.Parameters.AddWithValue("$blake3", string rootMetadata.Blake3Hash)
                    |> ignore

                    Convert.ToInt64(command.ExecuteScalar())
                    |> should equal 1L

                    invocationCount <- invocationCount + 1
                }

    /// Creates a disposable real filesystem and SQLite scope for one independent engine scenario.
    let private withScenario operation =
        task {
            let root = Path.Combine(Path.GetTempPath(), "Grace", "WorkingDirectoryUpdateEngineTests", Guid.NewGuid().ToString("N"))
            let workingRoot = Path.Combine(root, "working")
            let objectRoot = Path.Combine(root, "objects")
            let databasePath = Path.Combine(root, "state", "local.db")
            Directory.CreateDirectory(workingRoot) |> ignore
            Directory.CreateDirectory(objectRoot) |> ignore

            Directory.CreateDirectory(Path.GetDirectoryName(databasePath))
            |> ignore

            let previousDirectory = Environment.CurrentDirectory
            Environment.CurrentDirectory <- workingRoot

            let configuration = GraceConfiguration()
            configuration.RootDirectory <- workingRoot
            configuration.StandardizedRootDirectory <- normalizeFilePath workingRoot
            configuration.GraceDirectory <- Path.Combine(workingRoot, ".grace")
            configuration.ObjectDirectory <- Path.Combine(configuration.GraceDirectory, "objects")
            configuration.GraceStatusFile <- Path.Combine(configuration.GraceDirectory, "local.db")
            configuration.ConfigurationDirectory <- configuration.GraceDirectory

            Directory.CreateDirectory(configuration.ConfigurationDirectory)
            |> ignore

            saveConfigFile (Path.Combine(configuration.ConfigurationDirectory, "graceconfig.json")) configuration
            resetConfiguration ()

            try
                return! operation workingRoot objectRoot databasePath
            finally
                Environment.CurrentDirectory <- previousDirectory
                resetConfiguration ()
                SqliteConnection.ClearAllPools()

                if Directory.Exists(root) then
                    try
                        Directory.Delete(root, true)
                    with
                    | :? IOException -> ()
        }

    /// Computes the actual dual hash declaration for one prepared manifest path.
    let private hashesAt (relativePath: string) (bytes: byte array) =
        task {
            use stream = new MemoryStream(bytes, writable = false)
            return! computeHashesForFile stream (RelativePath relativePath)
        }

    /// Computes the actual dual hash declaration used by the root fixture file.
    let private hashes (bytes: byte array) = hashesAt "file.txt" bytes

    /// Creates a complete canonical selected root with a root file, nested file, and empty directory.
    let private targetAndStatus () =
        task {
            let bytes = [| 1uy; 2uy; 3uy |]
            let nestedBytes = [| 4uy; 5uy; 6uy |]
            let! fileSha256Hash, fileBlake3Hash = hashes bytes
            let! nestedFileSha256Hash, nestedFileBlake3Hash = hashesAt "nested/child.txt" nestedBytes
            let rootDirectoryId = Guid.NewGuid()
            let nestedDirectoryId = Guid.NewGuid()
            let emptyDirectoryId = Guid.NewGuid()
            let repositoryId = Guid.NewGuid()

            let file =
                LocalFileVersion.CreateWithHashes
                    (RelativePath "file.txt")
                    fileSha256Hash
                    fileBlake3Hash
                    false
                    (int64 bytes.Length)
                    (getCurrentInstant ())
                    true
                    DateTime.UtcNow

            let nestedFile =
                LocalFileVersion.CreateWithHashes
                    (RelativePath "nested/child.txt")
                    nestedFileSha256Hash
                    nestedFileBlake3Hash
                    false
                    (int64 nestedBytes.Length)
                    (getCurrentInstant ())
                    true
                    DateTime.UtcNow

            let nestedEntries =
                [|
                    Grace.Shared.Services.DirectoryVersionPreimageEntry.File nestedFile.RelativePath nestedFile.Size nestedFile.Blake3Hash nestedFile.Sha256Hash
                |]

            let nestedSha256Hash = Grace.Shared.Services.computeSha256ForDirectoryEntries (RelativePath "nested") nestedEntries
            let nestedBlake3Hash = Grace.Shared.Services.computeBlake3ForDirectory (RelativePath "nested") nestedEntries
            let emptyEntries = [||]
            let emptySha256Hash = Grace.Shared.Services.computeSha256ForDirectoryEntries (RelativePath "empty") emptyEntries
            let emptyBlake3Hash = Grace.Shared.Services.computeBlake3ForDirectory (RelativePath "empty") emptyEntries

            let nestedMetadata =
                LocalDirectoryVersion.CreateWithHashes
                    nestedDirectoryId
                    Guid.Empty
                    Guid.Empty
                    repositoryId
                    (RelativePath "nested")
                    nestedSha256Hash
                    nestedBlake3Hash
                    (List<DirectoryVersionId>())
                    (List<LocalFileVersion>([| nestedFile |]))
                    nestedFile.Size
                    DateTime.UtcNow

            let emptyMetadata =
                LocalDirectoryVersion.CreateWithHashes
                    emptyDirectoryId
                    Guid.Empty
                    Guid.Empty
                    repositoryId
                    (RelativePath "empty")
                    emptySha256Hash
                    emptyBlake3Hash
                    (List<DirectoryVersionId>())
                    (List<LocalFileVersion>())
                    0L
                    DateTime.UtcNow

            let rootEntries =
                [|
                    Grace.Shared.Services.DirectoryVersionPreimageEntry.Directory
                        emptyMetadata.RelativePath
                        emptyMetadata.Size
                        emptyMetadata.Blake3Hash
                        emptyMetadata.Sha256Hash
                    Grace.Shared.Services.DirectoryVersionPreimageEntry.Directory
                        nestedMetadata.RelativePath
                        nestedMetadata.Size
                        nestedMetadata.Blake3Hash
                        nestedMetadata.Sha256Hash
                    Grace.Shared.Services.DirectoryVersionPreimageEntry.File file.RelativePath file.Size file.Blake3Hash file.Sha256Hash
                |]

            let rootSha256Hash = Grace.Shared.Services.computeSha256ForDirectoryEntries RootDirectoryPath rootEntries
            let rootBlake3Hash = Grace.Shared.Services.computeBlake3ForDirectory RootDirectoryPath rootEntries

            let target =
                WorkingDirectoryUpdateContracts.Target.create repositoryId (Guid.NewGuid()) rootDirectoryId rootSha256Hash rootBlake3Hash
                |> required

            let rootMetadata =
                LocalDirectoryVersion.CreateWithHashes
                    rootDirectoryId
                    Guid.Empty
                    Guid.Empty
                    repositoryId
                    RootDirectoryPath
                    rootSha256Hash
                    rootBlake3Hash
                    (List<DirectoryVersionId>(
                        [|
                            nestedDirectoryId
                            emptyDirectoryId
                        |]
                    ))
                    (List<LocalFileVersion>([| file |]))
                    file.Size
                    DateTime.UtcNow

            let index = GraceIndex()

            index.TryAdd(rootMetadata.DirectoryVersionId, rootMetadata)
            |> ignore

            index.TryAdd(nestedMetadata.DirectoryVersionId, nestedMetadata)
            |> ignore

            index.TryAdd(emptyMetadata.DirectoryVersionId, emptyMetadata)
            |> ignore

            let status =
                { GraceStatus.Default with
                    Index = index
                    RootDirectoryId = rootDirectoryId
                    RootDirectorySha256Hash = rootSha256Hash
                    RootDirectoryBlake3Hash = rootBlake3Hash
                }

            LocalStateDb.validateCompleteStatusTree status
            |> required

            return target, status, rootMetadata
        }

    /// Creates the complete empty root baseline required by the read-only pre-mutation scanner.
    let private emptyPriorStatus (targetStatus: GraceStatus) (targetRoot: LocalDirectoryVersion) =
        let emptyEntries = [||]
        let emptySha256Hash = Grace.Shared.Services.computeSha256ForDirectoryEntries RootDirectoryPath emptyEntries
        let emptyBlake3Hash = Grace.Shared.Services.computeBlake3ForDirectory RootDirectoryPath emptyEntries

        let root =
            LocalDirectoryVersion.CreateWithHashes
                targetRoot.DirectoryVersionId
                targetRoot.OwnerId
                targetRoot.OrganizationId
                targetRoot.RepositoryId
                RootDirectoryPath
                emptySha256Hash
                emptyBlake3Hash
                (List<DirectoryVersionId>())
                (List<LocalFileVersion>())
                0L
                DateTime.UtcNow

        let index = GraceIndex()

        index.TryAdd(root.DirectoryVersionId, root)
        |> ignore

        { targetStatus with Index = index; RootDirectorySha256Hash = emptySha256Hash; RootDirectoryBlake3Hash = emptyBlake3Hash }

    /// Creates verified prepared bytes for one declared manifest path.
    let private preparedContentAt (relativePath: string) bytes =
        task {
            let! sha256Hash, blake3Hash = hashes bytes

            let manifest =
                WorkingDirectoryUpdateContracts.PreparedManifest.create [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.File(
                                                                              RelativePath relativePath,
                                                                              sha256Hash,
                                                                              blake3Hash
                                                                          ) ]
                |> required

            let files = Dictionary<string, byte array>()
            files[relativePath] <- bytes

            let! result =
                WorkingDirectoryUpdateContracts.PreparedContent.create
                    manifest
                    (new Reader(files) :> WorkingDirectoryUpdateContracts.IPreparedContentReader)
                    CancellationToken.None

            return result |> required
        }

    /// Creates verified prepared bytes that exactly bind the complete standard status graph.
    let private preparedContent bytes =
        task {
            let nestedBytes = [| 4uy; 5uy; 6uy |]
            let! fileSha256Hash, fileBlake3Hash = hashes bytes
            let! nestedFileSha256Hash, nestedFileBlake3Hash = hashesAt "nested/child.txt" nestedBytes

            let manifest =
                WorkingDirectoryUpdateContracts.PreparedManifest.create [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath "empty")
                                                                          WorkingDirectoryUpdateContracts.PreparedManifestEntry.Directory(RelativePath "nested")
                                                                          WorkingDirectoryUpdateContracts.PreparedManifestEntry.File(
                                                                              RelativePath "file.txt",
                                                                              fileSha256Hash,
                                                                              fileBlake3Hash
                                                                          )
                                                                          WorkingDirectoryUpdateContracts.PreparedManifestEntry.File(
                                                                              RelativePath "nested/child.txt",
                                                                              nestedFileSha256Hash,
                                                                              nestedFileBlake3Hash
                                                                          ) ]
                |> required

            let files = Dictionary<string, byte array>()
            files["file.txt"] <- bytes
            files["nested/child.txt"] <- nestedBytes

            let! result =
                WorkingDirectoryUpdateContracts.PreparedContent.create
                    manifest
                    (new Reader(files) :> WorkingDirectoryUpdateContracts.IPreparedContentReader)
                    CancellationToken.None

            return result |> required
        }

    /// Builds the same ignore-aware scan input the caller would freeze before the engine acquires its lease.
    let private defaultScanInput workingRoot =
        {
            RootDirectory = workingRoot
            GraceDirectory = Path.Combine(workingRoot, ".grace")
            GraceStatusFile = Path.Combine(workingRoot, ".grace", "status")
            DirectoryIgnoreEntries = [||]
            FileIgnoreEntries = [||]
        }

    /// Builds the approved private Request, selected-state reader, and typed recovery facts for one real SQLite scenario.
    let private requestWithPriorStatus
        readerFactory
        priorStatusFactory
        progress
        callerKind
        failurePoint
        contentFactory
        scanInputFactory
        beforeSelection
        workingRoot
        objectRoot
        databasePath
        =
        task {
            let! target, targetStatus, rootMetadata = targetAndStatus ()
            let priorStatus = priorStatusFactory targetStatus rootMetadata
            let! content = contentFactory ()
            let scanInput = scanInputFactory workingRoot

            let scope =
                WorkingDirectoryUpdateContracts.LocalRootScope.create workingRoot
                |> required

            do! LocalStateDb.replaceStatusSnapshot databasePath priorStatus
            do! beforeSelection target targetStatus rootMetadata databasePath
            let! acceptedRevision = LocalStateDb.readLocalStatusRevision databasePath

            let operation, finalizer, request =
                match callerKind with
                | "branch" ->
                    let previousBranchId = Guid.Parse("11111111-1111-1111-1111-111111111111")
                    let selectedReferenceId = Guid.Parse("22222222-2222-2222-2222-222222222222")

                    let operation =
                        WorkingDirectoryUpdateContracts.Operation.branchSwitch previousBranchId selectedReferenceId target
                        |> required

                    let configuration =
                        WorkingDirectoryUpdate.AcceptedConfiguration.create "engine-tests" scanInput
                        |> required

                    let selection =
                        WorkingDirectoryUpdate.AcceptedSelection.create target scope configuration acceptedRevision
                        |> required

                    let facts =
                        WorkingDirectoryUpdate.ApplicationFacts.create
                            target
                            workingRoot
                            objectRoot
                            databasePath
                            priorStatus
                            targetStatus
                            (targetStatus.Index.Values |> Seq.toArray)
                        |> required

                    let finalizer = Finalizer()

                    let request =
                        WorkingDirectoryUpdate.Request.branchSwitch
                            selection
                            facts
                            operation
                            content
                            "engine-tests"
                            (readerFactory selection)
                            previousBranchId
                            selectedReferenceId
                            (finalizer :> WorkingDirectoryUpdate.IIdempotentFinalizer)
                            progress
                        |> required

                    operation, Some finalizer, request
                | _ ->
                    let operation =
                        WorkingDirectoryUpdateContracts.Operation.connectBootstrap target "cursor-1" scope
                        |> required

                    let configuration =
                        WorkingDirectoryUpdate.AcceptedConfiguration.create "engine-tests" scanInput
                        |> required

                    let selection =
                        WorkingDirectoryUpdate.AcceptedSelection.create target scope configuration acceptedRevision
                        |> required

                    let facts =
                        WorkingDirectoryUpdate.ApplicationFacts.create
                            target
                            workingRoot
                            objectRoot
                            databasePath
                            priorStatus
                            targetStatus
                            (targetStatus.Index.Values |> Seq.toArray)
                        |> required

                    let request =
                        WorkingDirectoryUpdate.Request.connectBootstrap
                            selection
                            facts
                            operation
                            content
                            "engine-tests"
                            (readerFactory selection)
                            "cursor-1"
                            progress
                        |> required

                    operation, None, request

            WorkingDirectoryUpdate.setFailurePointForTests failurePoint
            return target, operation, finalizer, request
        }

    /// Builds a request with a caller-approved prior status shape for targeted planner tests.
    let private requestWithReader readerFactory callerKind failurePoint workingRoot objectRoot databasePath =
        requestWithPriorStatus
            readerFactory
            emptyPriorStatus
            None
            callerKind
            failurePoint
            (fun () -> preparedContent [| 1uy; 2uy; 3uy |])
            defaultScanInput
            (fun _ _ _ _ -> Task.FromResult(()))
            workingRoot
            objectRoot
            databasePath

    /// Builds a request with a best-effort observer used to prove ordering against real on-disk state.
    let private requestWithProgress progress callerKind failurePoint workingRoot objectRoot databasePath =
        requestWithPriorStatus
            (fun selection -> SelectedStateReader(selection) :> WorkingDirectoryUpdate.ISelectedStateReader)
            emptyPriorStatus
            (Some progress)
            callerKind
            failurePoint
            (fun () -> preparedContent [| 1uy; 2uy; 3uy |])
            defaultScanInput
            (fun _ _ _ _ -> Task.FromResult(()))
            workingRoot
            objectRoot
            databasePath

    /// Builds a request whose selected-state reader returns the same immutable snapshot.
    let private request callerKind failurePoint workingRoot objectRoot databasePath =
        requestWithReader
            (fun selection -> SelectedStateReader(selection) :> WorkingDirectoryUpdate.ISelectedStateReader)
            callerKind
            failurePoint
            workingRoot
            objectRoot
            databasePath

    /// Reacquires the real scope lease through a second handle to prove the terminal engine path released its handle.
    let private requireLeaseReleased target workingRoot =
        task {
            let scope =
                WorkingDirectoryUpdateCoordination.Scope.create (WorkingDirectoryUpdateContracts.Target.repositoryId target) workingRoot
                |> required

            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2.0))
            use! secondHandle = WorkingDirectoryUpdateCoordination.Lease.acquire scope timeout.Token
            return ()
        }

    [<Test>]
    let ``run writes verified object then working bytes and commits Updated`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                let! target, _, _, updateRequest = request "connect" None workingRoot objectRoot databasePath
                let! outcome = WorkingDirectoryUpdate.run updateRequest CancellationToken.None

                match outcome with
                | WorkingDirectoryUpdateContracts.Outcome.Updated receipt ->
                    WorkingDirectoryUpdateContracts.Receipt.bytesChanged receipt
                    |> should equal true
                | _ -> failwithf "Expected Updated but received %A." outcome

                File.ReadAllBytes(Path.Combine(workingRoot, "file.txt"))
                |> should equal [| 1uy; 2uy; 3uy |]

                File.ReadAllBytes(Path.Combine(workingRoot, "nested", "child.txt"))
                |> should equal [| 4uy; 5uy; 6uy |]

                Directory.Exists(Path.Combine(workingRoot, "empty"))
                |> should equal true

                Directory.EnumerateFiles(objectRoot, "*", SearchOption.AllDirectories)
                |> Seq.length
                |> should equal 2

                WorkingDirectoryUpdate.Request.preparedContentDisposalCountForTests updateRequest
                |> should equal 1

                do! requireLeaseReleased target workingRoot
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<Test>]
    let ``typed finalizer observes pending completion after matching status and object facts commit`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                let! target, targetStatus, rootMetadata = targetAndStatus ()
                let! content = preparedContent [| 1uy; 2uy; 3uy |]

                let scope =
                    WorkingDirectoryUpdateContracts.LocalRootScope.create workingRoot
                    |> required

                do! LocalStateDb.replaceStatusSnapshot databasePath (emptyPriorStatus targetStatus rootMetadata)
                let! acceptedRevision = LocalStateDb.readLocalStatusRevision databasePath
                let previousBranchId = Guid.NewGuid()
                let selectedReferenceId = Guid.NewGuid()

                let operation =
                    WorkingDirectoryUpdateContracts.Operation.branchSwitch previousBranchId selectedReferenceId target
                    |> required

                let configuration =
                    WorkingDirectoryUpdate.AcceptedConfiguration.create "engine-tests" (defaultScanInput workingRoot)
                    |> required

                let selection =
                    WorkingDirectoryUpdate.AcceptedSelection.create target scope configuration acceptedRevision
                    |> required

                let facts =
                    WorkingDirectoryUpdate.ApplicationFacts.create
                        target
                        workingRoot
                        objectRoot
                        databasePath
                        (emptyPriorStatus targetStatus rootMetadata)
                        targetStatus
                        (targetStatus.Index.Values |> Seq.toArray)
                    |> required

                let finalizer = PendingCompletionFinalizer(databasePath, target, operation, rootMetadata)

                WorkingDirectoryUpdate.setFailurePointForTests None

                let updateRequest =
                    WorkingDirectoryUpdate.Request.branchSwitch
                        selection
                        facts
                        operation
                        content
                        "engine-tests"
                        (SelectedStateReader(selection))
                        previousBranchId
                        selectedReferenceId
                        (finalizer :> WorkingDirectoryUpdate.IIdempotentFinalizer)
                        None
                    |> required

                let! outcome = WorkingDirectoryUpdate.run updateRequest CancellationToken.None

                match outcome with
                | WorkingDirectoryUpdateContracts.Outcome.Updated _ -> ()
                | _ -> failwithf "Expected Updated with ordered finalization but received %A." outcome

                finalizer.InvocationCount |> should equal 1
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<Test>]
    let ``corrupt existing object is atomically healed before the engine writes a working copy`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                let expectedBytes = [| 1uy; 2uy; 3uy |]
                let! expectedSha256Hash, expectedBlake3Hash = hashes expectedBytes
                let relativePath = RelativePath "file.txt"
                let objectFileName = Services.getLocalObjectCacheFileName relativePath expectedSha256Hash expectedBlake3Hash
                let objectDirectory = Path.Combine(objectRoot, "file.txt")

                Directory.CreateDirectory(objectDirectory)
                |> ignore

                let corruptObjectPath = Path.Combine(objectDirectory, objectFileName)
                File.WriteAllBytes(corruptObjectPath, [| 99uy |])
                let! target, _, _, updateRequest = request "connect" None workingRoot objectRoot databasePath
                let! outcome = WorkingDirectoryUpdate.run updateRequest CancellationToken.None

                match outcome with
                | WorkingDirectoryUpdateContracts.Outcome.Updated _ -> ()
                | _ -> failwithf "Expected Updated after healing but received %A." outcome

                File.ReadAllBytes(corruptObjectPath)
                |> should equal expectedBytes

                File.ReadAllBytes(Path.Combine(workingRoot, "file.txt"))
                |> should equal expectedBytes
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<Test>]
    let ``object path replaced by a directory rejects before any working-file mutation`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                let expectedBytes = [| 1uy; 2uy; 3uy |]
                let! expectedSha256Hash, expectedBlake3Hash = hashes expectedBytes
                let relativePath = RelativePath "file.txt"
                let objectFileName = Services.getLocalObjectCacheFileName relativePath expectedSha256Hash expectedBlake3Hash
                let objectPath = Path.Combine(objectRoot, "file.txt", objectFileName)
                Directory.CreateDirectory(objectPath) |> ignore
                let! target, _, _, updateRequest = request "connect" None workingRoot objectRoot databasePath
                let! outcome = WorkingDirectoryUpdate.run updateRequest CancellationToken.None

                match outcome with
                | WorkingDirectoryUpdateContracts.Outcome.Rejected _ -> ()
                | _ -> failwithf "Expected Rejected but received %A." outcome

                Directory.Exists(objectPath) |> should equal true

                File.Exists(Path.Combine(workingRoot, "file.txt"))
                |> should equal false
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<Test>]
    let ``object replacement after publication rejects before working-file mutation`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                let expectedBytes = [| 1uy; 2uy; 3uy |]
                let! expectedSha256Hash, expectedBlake3Hash = hashes expectedBytes

                let objectPath =
                    Path.Combine(objectRoot, "file.txt", Services.getLocalObjectCacheFileName (RelativePath "file.txt") expectedSha256Hash expectedBlake3Hash)

                let replacementDone = ref false

                let observer stage =
                    if stage = WorkingDirectoryUpdateContracts.Progress.Applying then
                        File.WriteAllBytes(objectPath, [| 42uy |])
                        replacementDone := true

                let! _, _, _, updateRequest = requestWithProgress observer "connect" None workingRoot objectRoot databasePath

                let! outcome = WorkingDirectoryUpdate.run updateRequest CancellationToken.None

                match outcome with
                | WorkingDirectoryUpdateContracts.Outcome.Rejected _ -> ()
                | _ -> failwithf "Expected Rejected but received %A." outcome

                !replacementDone |> should equal true

                File.ReadAllBytes(objectPath)
                |> should equal [| 42uy |]

                File.Exists(Path.Combine(workingRoot, "file.txt"))
                |> should equal false
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<Test>]
    let ``prepared snapshot materializes its verified bytes after the reader source changes`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                let sourceBytes = [| 1uy; 2uy; 3uy |]
                let prepared = ref None

                let contentFactory () =
                    task {
                        let! content = preparedContent sourceBytes
                        prepared := Some content
                        return content
                    }

                let! _, _, _, updateRequest =
                    requestWithPriorStatus
                        (fun selection -> SelectedStateReader(selection) :> WorkingDirectoryUpdate.ISelectedStateReader)
                        emptyPriorStatus
                        None
                        "connect"
                        None
                        contentFactory
                        defaultScanInput
                        (fun _ _ _ _ -> Task.FromResult(()))
                        workingRoot
                        objectRoot
                        databasePath

                sourceBytes[0] <- 99uy
                sourceBytes[1] <- 98uy
                sourceBytes[2] <- 97uy
                let! outcome = WorkingDirectoryUpdate.run updateRequest CancellationToken.None

                match outcome with
                | WorkingDirectoryUpdateContracts.Outcome.Updated _ -> ()
                | _ -> failwithf "Expected Updated from the verified prepared snapshot but received %A." outcome

                File.ReadAllBytes(Path.Combine(workingRoot, "file.txt"))
                |> should equal [| 1uy; 2uy; 3uy |]

                !prepared |> should not' (equal None)

                WorkingDirectoryUpdate.Request.preparedContentDisposalCountForTests updateRequest
                |> should equal 1
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<Test>]
    let ``progress observer exceptions do not change a successful update outcome or completion`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                let observer _ = invalidOp "progress must be best effort"
                let! target, operation, _, updateRequest = requestWithProgress observer "connect" None workingRoot objectRoot databasePath
                let! outcome = WorkingDirectoryUpdate.run updateRequest CancellationToken.None

                match outcome with
                | WorkingDirectoryUpdateContracts.Outcome.Updated _ -> ()
                | _ -> failwithf "Expected Updated but received %A." outcome

                let! completion = LocalStateDb.readWorkingDirectoryUpdateCompletion databasePath target operation

                completion
                |> should equal (Some LocalStateDb.WorkingDirectoryUpdateCompletion.Terminal)

                File.ReadAllBytes(Path.Combine(workingRoot, "file.txt"))
                |> should equal [| 1uy; 2uy; 3uy |]
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<Test>]
    let ``cancellation requested after the first mutation is deferred through verified completion`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                use cancellation = new CancellationTokenSource()

                let observer stage =
                    if stage = WorkingDirectoryUpdateContracts.Progress.Verifying then
                        cancellation.Cancel()

                let! target, operation, _, updateRequest = requestWithProgress observer "connect" None workingRoot objectRoot databasePath
                let! outcome = WorkingDirectoryUpdate.run updateRequest cancellation.Token

                match outcome with
                | WorkingDirectoryUpdateContracts.Outcome.Updated _ -> ()
                | _ -> failwithf "Expected Updated after deferred cancellation but received %A." outcome

                let! completion = LocalStateDb.readWorkingDirectoryUpdateCompletion databasePath target operation

                completion
                |> should equal (Some LocalStateDb.WorkingDirectoryUpdateCompletion.Terminal)

                File.Exists(Path.Combine(workingRoot, "file.txt"))
                |> should equal true
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<Test>]
    let ``final-root verification failure after copy leaves no completion or status revision advance`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                let observer stage =
                    if stage = WorkingDirectoryUpdateContracts.Progress.Verifying then
                        File.WriteAllBytes(Path.Combine(workingRoot, "file.txt"), [| 77uy |])

                let! target, operation, _, updateRequest = requestWithProgress observer "connect" None workingRoot objectRoot databasePath
                let! outcome = WorkingDirectoryUpdate.run updateRequest CancellationToken.None

                match outcome with
                | WorkingDirectoryUpdateContracts.Outcome.UpdateIncomplete _ -> ()
                | _ -> failwithf "Expected UpdateIncomplete but received %A." outcome

                let! completion = LocalStateDb.readWorkingDirectoryUpdateCompletion databasePath target operation
                completion |> should equal None
                let! revision = LocalStateDb.readLocalStatusRevision databasePath
                revision |> should equal 1L

                File.ReadAllBytes(Path.Combine(workingRoot, "file.txt"))
                |> should equal [| 77uy |]
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<Test>]
    let ``run rejects unexpected eligible content without deleting it`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                File.WriteAllText(Path.Combine(workingRoot, "foreign.txt"), "keep")
                let! target, _, _, updateRequest = request "connect" None workingRoot objectRoot databasePath
                let! outcome = WorkingDirectoryUpdate.run updateRequest CancellationToken.None

                match outcome with
                | WorkingDirectoryUpdateContracts.Outcome.Rejected _ -> ()
                | _ -> failwithf "Expected Rejected but received %A." outcome

                File.ReadAllText(Path.Combine(workingRoot, "foreign.txt"))
                |> should equal "keep"

                WorkingDirectoryUpdate.Request.preparedContentDisposalCountForTests updateRequest
                |> should equal 1

                do! requireLeaseReleased target workingRoot
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<Test>]
    let ``ignore-aware scan preserves ignored content outside the selected-root comparison`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                let ignoredPath = Path.Combine(workingRoot, "ignored.txt")
                File.WriteAllText(ignoredPath, "preserve this ignored content")

                let! _, _, _, updateRequest =
                    requestWithPriorStatus
                        (fun selection -> SelectedStateReader(selection) :> WorkingDirectoryUpdate.ISelectedStateReader)
                        emptyPriorStatus
                        None
                        "connect"
                        None
                        (fun () -> preparedContent [| 1uy; 2uy; 3uy |])
                        (fun root -> { defaultScanInput root with FileIgnoreEntries = [| "ignored.txt" |] })
                        (fun _ _ _ _ -> Task.FromResult(()))
                        workingRoot
                        objectRoot
                        databasePath

                let! outcome = WorkingDirectoryUpdate.run updateRequest CancellationToken.None

                match outcome with
                | WorkingDirectoryUpdateContracts.Outcome.Updated _ -> ()
                | _ -> failwithf "Expected Updated with ignored content but received %A." outcome

                File.ReadAllText(ignoredPath)
                |> should equal "preserve this ignored content"

                File.ReadAllBytes(Path.Combine(workingRoot, "file.txt"))
                |> should equal [| 1uy; 2uy; 3uy |]
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<Test>]
    let ``untracked eligible selected target path rejects with its path and remains unmodified`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                let selectedPath = Path.Combine(workingRoot, "file.txt")
                let originalBytes = [| 91uy; 92uy; 93uy |]
                File.WriteAllBytes(selectedPath, originalBytes)
                let! target, operation, _, updateRequest = request "connect" None workingRoot objectRoot databasePath
                let! outcome = WorkingDirectoryUpdate.run updateRequest CancellationToken.None

                match outcome with
                | WorkingDirectoryUpdateContracts.Outcome.Rejected failure ->
                    WorkingDirectoryUpdateContracts.Failure.reason failure
                    |> should contain "file.txt"

                    WorkingDirectoryUpdateContracts.Failure.reason failure
                    |> should contain "changed after selection"
                | _ -> failwithf "Expected selected-path Rejected but received %A." outcome

                File.ReadAllBytes(selectedPath)
                |> should equal originalBytes

                let! completion = LocalStateDb.readWorkingDirectoryUpdateCompletion databasePath target operation
                completion |> should equal None
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<Test>]
    let ``run returns UpdateIncomplete after a post-mutation finite seam`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                let! target, _, _, updateRequest =
                    request "connect" (Some WorkingDirectoryUpdate.FailurePoint.AfterWorkingMutation) workingRoot objectRoot databasePath

                let! outcome = WorkingDirectoryUpdate.run updateRequest CancellationToken.None

                match outcome with
                | WorkingDirectoryUpdateContracts.Outcome.UpdateIncomplete _ -> ()
                | _ -> failwithf "Expected UpdateIncomplete but received %A." outcome

                File.Exists(Path.Combine(workingRoot, "file.txt"))
                |> should equal true

                WorkingDirectoryUpdate.Request.preparedContentDisposalCountForTests updateRequest
                |> should equal 1

                do! requireLeaseReleased target workingRoot
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<Test>]
    let ``finalization incomplete remains typed and retry changes no working files`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                let! target, operation, finalizer, updateRequest =
                    request "branch" (Some WorkingDirectoryUpdate.FailurePoint.BeforeFinalization) workingRoot objectRoot databasePath

                let! firstOutcome = WorkingDirectoryUpdate.run updateRequest CancellationToken.None

                match firstOutcome with
                | WorkingDirectoryUpdateContracts.Outcome.FinalizationIncomplete _ -> ()
                | _ -> failwithf "Expected FinalizationIncomplete but received %A." firstOutcome

                WorkingDirectoryUpdate.Request.preparedContentDisposalCountForTests updateRequest
                |> should equal 1

                let beforeRetry = File.ReadAllBytes(Path.Combine(workingRoot, "file.txt"))

                let scope =
                    WorkingDirectoryUpdateContracts.LocalRootScope.create workingRoot
                    |> required

                let configuration =
                    WorkingDirectoryUpdate.AcceptedConfiguration.create
                        "engine-tests"
                        {
                            RootDirectory = workingRoot
                            GraceDirectory = Path.Combine(workingRoot, ".grace")
                            GraceStatusFile = Path.Combine(workingRoot, ".grace", "status")
                            DirectoryIgnoreEntries = [||]
                            FileIgnoreEntries = [||]
                        }
                    |> required

                let selection =
                    WorkingDirectoryUpdate.AcceptedSelection.create target scope configuration 2L
                    |> required

                match
                    WorkingDirectoryUpdate.FinalizationRequest.branchSwitch
                        selection
                        operation
                        databasePath
                        "engine-tests"
                        (SelectedStateReader(selection))
                        (Guid.Parse("33333333-3333-3333-3333-333333333333"))
                        (Guid.Parse("22222222-2222-2222-2222-222222222222"))
                        (finalizer.Value :> WorkingDirectoryUpdate.IIdempotentFinalizer)
                        None
                    with
                | Error _ -> ()
                | Ok _ -> failwith "Expected unrelated finalization facts to fail before retry leasing."

                let retryRequest =
                    WorkingDirectoryUpdate.FinalizationRequest.branchSwitch
                        selection
                        operation
                        databasePath
                        "engine-tests"
                        (SelectedStateReader(selection))
                        (Guid.Parse("11111111-1111-1111-1111-111111111111"))
                        (Guid.Parse("22222222-2222-2222-2222-222222222222"))
                        (finalizer.Value :> WorkingDirectoryUpdate.IIdempotentFinalizer)
                        None
                    |> required

                WorkingDirectoryUpdate.setFailurePointForTests (Some WorkingDirectoryUpdate.FailurePoint.BeforeTerminalCompletion)

                let! retryOutcome = WorkingDirectoryUpdate.retryFinalization retryRequest CancellationToken.None

                match retryOutcome with
                | WorkingDirectoryUpdateContracts.Outcome.FinalizationIncomplete _ -> ()
                | _ -> failwithf "Expected FinalizationIncomplete but received %A." retryOutcome

                File.ReadAllBytes(Path.Combine(workingRoot, "file.txt"))
                |> should equal beforeRetry
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<Test>]
    let ``matching terminal replay is Unchanged and does not rewrite working bytes`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                let! _, _, _, updateRequest = request "connect" None workingRoot objectRoot databasePath
                let! firstOutcome = WorkingDirectoryUpdate.run updateRequest CancellationToken.None

                match firstOutcome with
                | WorkingDirectoryUpdateContracts.Outcome.Updated _ -> ()
                | _ -> failwithf "Expected Updated but received %A." firstOutcome

                let workingFile = Path.Combine(workingRoot, "file.txt")
                let beforeReplay = File.ReadAllBytes(workingFile)
                File.SetLastWriteTimeUtc(workingFile, DateTime.UtcNow.AddMinutes(-1.0))
                let timestampBeforeReplay = File.GetLastWriteTimeUtc(workingFile)
                let! replayOutcome = WorkingDirectoryUpdate.run updateRequest CancellationToken.None

                match replayOutcome with
                | WorkingDirectoryUpdateContracts.Outcome.Unchanged receipt ->
                    WorkingDirectoryUpdateContracts.Receipt.bytesChanged receipt
                    |> should equal false
                | _ -> failwithf "Expected Unchanged but received %A." replayOutcome

                File.ReadAllBytes(workingFile)
                |> should equal beforeReplay

                File.GetLastWriteTimeUtc(workingFile)
                |> should equal timestampBeforeReplay

                WorkingDirectoryUpdate.Request.preparedContentDisposalCountForTests updateRequest
                |> should equal 1
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<Test>]
    let ``pre-mutation cancellation rejects and leaves neither working bytes nor an owned marker`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                let! target, _, _, updateRequest = request "connect" None workingRoot objectRoot databasePath
                use cancellation = new CancellationTokenSource()
                cancellation.Cancel()
                let! outcome = WorkingDirectoryUpdate.run updateRequest cancellation.Token

                match outcome with
                | WorkingDirectoryUpdateContracts.Outcome.Rejected _ -> ()
                | _ -> failwithf "Expected Rejected but received %A." outcome

                File.Exists(Path.Combine(workingRoot, "file.txt"))
                |> should equal false

                let scope =
                    WorkingDirectoryUpdateCoordination.Scope.create (WorkingDirectoryUpdateContracts.Target.repositoryId target) workingRoot
                    |> required

                File.Exists(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
                |> should equal false

                WorkingDirectoryUpdate.Request.preparedContentDisposalCountForTests updateRequest
                |> should equal 1

                do! requireLeaseReleased target workingRoot
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<Test>]
    let ``pre-mutation internal exception rejects and disposes the prepared snapshot exactly once`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                let! _, _, _, updateRequest =
                    request "connect" (Some WorkingDirectoryUpdate.FailurePoint.BeforeObjectPublication) workingRoot objectRoot databasePath

                let! outcome = WorkingDirectoryUpdate.run updateRequest CancellationToken.None

                match outcome with
                | WorkingDirectoryUpdateContracts.Outcome.Rejected _ -> ()
                | _ -> failwithf "Expected internal-exception Rejected but received %A." outcome

                File.Exists(Path.Combine(workingRoot, "file.txt"))
                |> should equal false

                WorkingDirectoryUpdate.Request.preparedContentDisposalCountForTests updateRequest
                |> should equal 1
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<Test>]
    let ``handled pre-completion failure removes only its owned marker and records no completion`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                let! target, operation, _, updateRequest =
                    request "connect" (Some WorkingDirectoryUpdate.FailurePoint.BeforeLocalCompletion) workingRoot objectRoot databasePath

                let! outcome = WorkingDirectoryUpdate.run updateRequest CancellationToken.None

                match outcome with
                | WorkingDirectoryUpdateContracts.Outcome.UpdateIncomplete _ -> ()
                | _ -> failwithf "Expected UpdateIncomplete but received %A." outcome

                let! completion = LocalStateDb.readWorkingDirectoryUpdateCompletion databasePath target operation
                completion |> should equal None

                let scope =
                    WorkingDirectoryUpdateCoordination.Scope.create (WorkingDirectoryUpdateContracts.Target.repositoryId target) workingRoot
                    |> required

                File.Exists(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
                |> should equal false
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<Test>]
    let ``same-operation marker adoption replaces the marker and replans before materializing`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                let observedReplacementToken = ref None
                let scopeForObservation = ref Unchecked.defaultof<WorkingDirectoryUpdateCoordination.Scope>

                let observer stage =
                    if stage = WorkingDirectoryUpdateContracts.Progress.Applying then
                        use document = JsonDocument.Parse(File.ReadAllText(WorkingDirectoryUpdateCoordination.Scope.markerPath !scopeForObservation))

                        let replacementToken =
                            document
                                .RootElement
                                .GetProperty("attemptToken")
                                .GetString()

                        observedReplacementToken := Some replacementToken

                let! target, operation, _, updateRequest = requestWithProgress observer "connect" None workingRoot objectRoot databasePath

                let scope =
                    WorkingDirectoryUpdateCoordination.Scope.create (WorkingDirectoryUpdateContracts.Target.repositoryId target) workingRoot
                    |> required

                scopeForObservation := scope

                let existingToken = WorkingDirectoryUpdateContracts.AttemptToken.create ()

                let marker =
                    WorkingDirectoryUpdateCoordination.Marker.create scope existingToken target operation
                    |> required

                do! WorkingDirectoryUpdateCoordination.Marker.write scope marker
                let! outcome = WorkingDirectoryUpdate.run updateRequest CancellationToken.None

                match outcome with
                | WorkingDirectoryUpdateContracts.Outcome.Updated _ -> ()
                | _ -> failwithf "Expected Updated after marker adoption but received %A." outcome

                File.Exists(Path.Combine(workingRoot, "file.txt"))
                |> should equal true

                !observedReplacementToken
                |> should not' (equal None)

                !observedReplacementToken
                |> should not' (equal (Some(WorkingDirectoryUpdateContracts.AttemptToken.value existingToken)))

                File.Exists(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
                |> should equal false
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<Test>]
    let ``different marker requires Doctor and preserves the foreign marker without writing files`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                let! target, _, _, updateRequest = request "connect" None workingRoot objectRoot databasePath

                let foreignOperation =
                    WorkingDirectoryUpdateContracts.Operation.branchSwitch (Guid.NewGuid()) (Guid.NewGuid()) target
                    |> required

                let scope =
                    WorkingDirectoryUpdateCoordination.Scope.create (WorkingDirectoryUpdateContracts.Target.repositoryId target) workingRoot
                    |> required

                let marker =
                    WorkingDirectoryUpdateCoordination.Marker.create scope (WorkingDirectoryUpdateContracts.AttemptToken.create ()) target foreignOperation
                    |> required

                do! WorkingDirectoryUpdateCoordination.Marker.write scope marker
                let markerContents = File.ReadAllText(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
                let! outcome = WorkingDirectoryUpdate.run updateRequest CancellationToken.None

                match outcome with
                | WorkingDirectoryUpdateContracts.Outcome.Rejected _ -> ()
                | _ -> failwithf "Expected Rejected but received %A." outcome

                File.Exists(Path.Combine(workingRoot, "file.txt"))
                |> should equal false

                File.ReadAllText(WorkingDirectoryUpdateCoordination.Scope.markerPath scope)
                |> should equal markerContents
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<Test>]
    let ``different pending operation blocks after its post-seed revision is accepted without mutation`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                let revisionAfterSeed = ref 0L
                let acceptedRevision = ref 0L

                let seedPendingOperation target targetStatus rootMetadata dbPath =
                    task {
                        let previousBranchId = Guid.NewGuid()
                        let selectedReferenceId = Guid.NewGuid()

                        let pendingOperation =
                            WorkingDirectoryUpdateContracts.Operation.branchSwitch previousBranchId selectedReferenceId target
                            |> required

                        let! _ =
                            LocalStateDb.commitWorkingDirectoryUpdateCompletion
                                dbPath
                                targetStatus
                                (targetStatus.Index.Values |> Seq.toArray)
                                (LocalStateDb.BranchFinalization(previousBranchId, selectedReferenceId))
                                target
                                pendingOperation

                        let! revision = LocalStateDb.readLocalStatusRevision dbPath
                        revisionAfterSeed := revision
                    }

                let! target, operation, _, updateRequest =
                    requestWithPriorStatus
                        (fun selection ->
                            acceptedRevision
                            := WorkingDirectoryUpdate.AcceptedSelection.localStatusRevision selection

                            SelectedStateReader(selection) :> WorkingDirectoryUpdate.ISelectedStateReader)
                        emptyPriorStatus
                        None
                        "branch"
                        None
                        (fun () -> preparedContent [| 1uy; 2uy; 3uy |])
                        defaultScanInput
                        seedPendingOperation
                        workingRoot
                        objectRoot
                        databasePath

                !acceptedRevision
                |> should equal !revisionAfterSeed

                let! outcome = WorkingDirectoryUpdate.run updateRequest CancellationToken.None

                match outcome with
                | WorkingDirectoryUpdateContracts.Outcome.Rejected failure ->
                    WorkingDirectoryUpdateContracts.Failure.reason failure
                    |> should equal "A different Working Directory Update finalization is pending."
                | _ -> failwithf "Expected different-pending Rejected but received %A." outcome

                File.Exists(Path.Combine(workingRoot, "file.txt"))
                |> should equal false

                let! revisionAfterRun = LocalStateDb.readLocalStatusRevision databasePath

                revisionAfterRun
                |> should equal !revisionAfterSeed

                let! operationCompletion = LocalStateDb.readWorkingDirectoryUpdateCompletion databasePath target operation
                operationCompletion |> should equal None

                let! pending = LocalStateDb.readPendingWorkingDirectoryUpdateFinalization databasePath
                pending |> should not' (equal None)
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<Test>]
    let ``stale SQLite revision rejects before planning or working-file mutation`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                let! _, _, _, updateRequest = request "connect" None workingRoot objectRoot databasePath
                let! replacementStatus = LocalStateDb.readStatusSnapshot databasePath
                do! LocalStateDb.replaceStatusSnapshot databasePath replacementStatus
                let! outcome = WorkingDirectoryUpdate.run updateRequest CancellationToken.None

                match outcome with
                | WorkingDirectoryUpdateContracts.Outcome.Rejected _ -> ()
                | _ -> failwithf "Expected Rejected but received %A." outcome

                File.Exists(Path.Combine(workingRoot, "file.txt"))
                |> should equal false
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<Test>]
    let ``changed selected state after lease acquisition rejects before planning or working-file mutation`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                let! _, _, _, updateRequest =
                    requestWithReader
                        (fun _ -> ChangedSelectedStateReader() :> WorkingDirectoryUpdate.ISelectedStateReader)
                        "connect"
                        None
                        workingRoot
                        objectRoot
                        databasePath

                let! outcome = WorkingDirectoryUpdate.run updateRequest CancellationToken.None

                match outcome with
                | WorkingDirectoryUpdateContracts.Outcome.Rejected _ -> ()
                | _ -> failwithf "Expected Rejected but received %A." outcome

                File.Exists(Path.Combine(workingRoot, "file.txt"))
                |> should equal false
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<Test>]
    let ``eligible content created after lease acquisition rejects before any target file is written`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                let! _, _, _, updateRequest =
                    requestWithReader
                        (fun selection ->
                            MutatingSelectedStateReader(selection, (fun () -> File.WriteAllText(Path.Combine(workingRoot, "late-eligible.txt"), "foreign")))
                            :> WorkingDirectoryUpdate.ISelectedStateReader)
                        "connect"
                        None
                        workingRoot
                        objectRoot
                        databasePath

                let! outcome = WorkingDirectoryUpdate.run updateRequest CancellationToken.None

                match outcome with
                | WorkingDirectoryUpdateContracts.Outcome.Rejected _ -> ()
                | _ -> failwithf "Expected Rejected but received %A." outcome

                File.ReadAllText(Path.Combine(workingRoot, "late-eligible.txt"))
                |> should equal "foreign"

                File.Exists(Path.Combine(workingRoot, "file.txt"))
                |> should equal false
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<Test>]
    let ``planner deletes only an accepted tracked path that is absent from the target`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                let oldPath = Path.Combine(workingRoot, "old.txt")
                let oldBytes = [| 9uy; 8uy; 7uy |]
                File.WriteAllBytes(oldPath, oldBytes)
                let oldLastWrite = File.GetLastWriteTimeUtc(oldPath)
                let! oldSha256Hash, oldBlake3Hash = hashes oldBytes

                let trackedOldFile =
                    LocalFileVersion.CreateWithHashes
                        (RelativePath "old.txt")
                        oldSha256Hash
                        oldBlake3Hash
                        false
                        (int64 oldBytes.Length)
                        (getCurrentInstant ())
                        true
                        oldLastWrite

                let priorStatusFactory (targetStatus: GraceStatus) (rootMetadata: LocalDirectoryVersion) =
                    let priorRoot =
                        LocalDirectoryVersion.CreateWithHashes
                            rootMetadata.DirectoryVersionId
                            rootMetadata.OwnerId
                            rootMetadata.OrganizationId
                            rootMetadata.RepositoryId
                            RootDirectoryPath
                            rootMetadata.Sha256Hash
                            rootMetadata.Blake3Hash
                            rootMetadata.Directories
                            (List<LocalFileVersion>([| trackedOldFile |]))
                            trackedOldFile.Size
                            rootMetadata.LastWriteTimeUtc

                    let priorIndex = GraceIndex()

                    priorIndex.TryAdd(priorRoot.DirectoryVersionId, priorRoot)
                    |> ignore

                    { targetStatus with Index = priorIndex }

                let! _, _, _, updateRequest =
                    requestWithPriorStatus
                        (fun selection -> SelectedStateReader(selection) :> WorkingDirectoryUpdate.ISelectedStateReader)
                        priorStatusFactory
                        None
                        "connect"
                        None
                        (fun () -> preparedContent [| 1uy; 2uy; 3uy |])
                        defaultScanInput
                        (fun _ _ _ _ -> Task.FromResult(()))
                        workingRoot
                        objectRoot
                        databasePath

                let! outcome = WorkingDirectoryUpdate.run updateRequest CancellationToken.None

                match outcome with
                | WorkingDirectoryUpdateContracts.Outcome.Updated _ -> ()
                | _ -> failwithf "Expected Updated but received %A." outcome

                File.Exists(oldPath) |> should equal false

                File.ReadAllBytes(Path.Combine(workingRoot, "file.txt"))
                |> should equal [| 1uy; 2uy; 3uy |]
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<Test>]
    let ``finalizer success before terminal SQLite failure remains pending and retries without touching files`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                let! target, operation, finalizer, updateRequest =
                    request "branch" (Some WorkingDirectoryUpdate.FailurePoint.BeforeTerminalCompletion) workingRoot objectRoot databasePath

                let! firstOutcome = WorkingDirectoryUpdate.run updateRequest CancellationToken.None

                match firstOutcome with
                | WorkingDirectoryUpdateContracts.Outcome.FinalizationIncomplete _ -> ()
                | _ -> failwithf "Expected FinalizationIncomplete but received %A." firstOutcome

                finalizer.Value.InvocationCount |> should equal 1
                let workingFile = Path.Combine(workingRoot, "file.txt")
                let beforeRetry = File.ReadAllBytes(workingFile)

                let scope =
                    WorkingDirectoryUpdateContracts.LocalRootScope.create workingRoot
                    |> required

                let configuration =
                    WorkingDirectoryUpdate.AcceptedConfiguration.create
                        "engine-tests"
                        {
                            RootDirectory = workingRoot
                            GraceDirectory = Path.Combine(workingRoot, ".grace")
                            GraceStatusFile = Path.Combine(workingRoot, ".grace", "status")
                            DirectoryIgnoreEntries = [||]
                            FileIgnoreEntries = [||]
                        }
                    |> required

                let selection =
                    WorkingDirectoryUpdate.AcceptedSelection.create target scope configuration 2L
                    |> required

                let retryRequest =
                    WorkingDirectoryUpdate.FinalizationRequest.branchSwitch
                        selection
                        operation
                        databasePath
                        "engine-tests"
                        (SelectedStateReader(selection))
                        (Guid.Parse("11111111-1111-1111-1111-111111111111"))
                        (Guid.Parse("22222222-2222-2222-2222-222222222222"))
                        (finalizer.Value :> WorkingDirectoryUpdate.IIdempotentFinalizer)
                        None
                    |> required

                WorkingDirectoryUpdate.setFailurePointForTests None
                let! retryOutcome = WorkingDirectoryUpdate.retryFinalization retryRequest CancellationToken.None

                match retryOutcome with
                | WorkingDirectoryUpdateContracts.Outcome.Unchanged _ -> ()
                | _ -> failwithf "Expected Unchanged retry but received %A." retryOutcome

                finalizer.Value.InvocationCount |> should equal 2

                File.ReadAllBytes(workingFile)
                |> should equal beforeRetry
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<Test>]
    let ``request construction rejects a manifest that does not exactly bind the frozen status graph before lease or SQLite completion`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                let! target, targetStatus, rootMetadata = targetAndStatus ()
                let! content = preparedContentAt "other.txt" [| 1uy; 2uy; 3uy |]

                let scope =
                    WorkingDirectoryUpdateContracts.LocalRootScope.create workingRoot
                    |> required

                let configuration =
                    WorkingDirectoryUpdate.AcceptedConfiguration.create "engine-tests" (defaultScanInput workingRoot)
                    |> required

                do! LocalStateDb.replaceStatusSnapshot databasePath (emptyPriorStatus targetStatus rootMetadata)
                let! revision = LocalStateDb.readLocalStatusRevision databasePath

                let selection =
                    WorkingDirectoryUpdate.AcceptedSelection.create target scope configuration revision
                    |> required

                let facts =
                    WorkingDirectoryUpdate.ApplicationFacts.create
                        target
                        workingRoot
                        objectRoot
                        databasePath
                        (emptyPriorStatus targetStatus rootMetadata)
                        targetStatus
                        (targetStatus.Index.Values |> Seq.toArray)
                    |> required

                let operation =
                    WorkingDirectoryUpdateContracts.Operation.connectBootstrap target "cursor-1" scope
                    |> required

                match
                    WorkingDirectoryUpdate.Request.connectBootstrap
                        selection
                        facts
                        operation
                        content
                        "diagnostic-a"
                        (SelectedStateReader(selection))
                        "cursor-1"
                        None
                    with
                | Error _ -> ()
                | Ok _ -> failwith "Expected graph/manifest mismatch to fail request construction."

                File.Exists(Path.Combine(workingRoot, "file.txt"))
                |> should equal false

                let! completion = LocalStateDb.readWorkingDirectoryUpdateCompletion databasePath target operation
                completion |> should equal None
                WorkingDirectoryUpdateContracts.PreparedContent.dispose content
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<Test>]
    let ``caller-bound diagnostics preserve one operation identity while a different Branch fact produces a distinct identity`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                let! target, targetStatus, rootMetadata = targetAndStatus ()

                let scope =
                    WorkingDirectoryUpdateContracts.LocalRootScope.create workingRoot
                    |> required

                let configuration =
                    WorkingDirectoryUpdate.AcceptedConfiguration.create "engine-tests" (defaultScanInput workingRoot)
                    |> required

                do! LocalStateDb.replaceStatusSnapshot databasePath (emptyPriorStatus targetStatus rootMetadata)
                let! revision = LocalStateDb.readLocalStatusRevision databasePath

                let selection =
                    WorkingDirectoryUpdate.AcceptedSelection.create target scope configuration revision
                    |> required

                let facts =
                    WorkingDirectoryUpdate.ApplicationFacts.create
                        target
                        workingRoot
                        objectRoot
                        databasePath
                        (emptyPriorStatus targetStatus rootMetadata)
                        targetStatus
                        (targetStatus.Index.Values |> Seq.toArray)
                    |> required

                let previousBranchId = Guid.Parse("11111111-1111-1111-1111-111111111111")
                let selectedReferenceId = Guid.Parse("22222222-2222-2222-2222-222222222222")

                let operation =
                    WorkingDirectoryUpdateContracts.Operation.branchSwitch previousBranchId selectedReferenceId target
                    |> required

                let finalizer = Finalizer() :> WorkingDirectoryUpdate.IIdempotentFinalizer
                let! firstContent = preparedContent [| 1uy; 2uy; 3uy |]
                let! secondContent = preparedContent [| 1uy; 2uy; 3uy |]
                let! differentContent = preparedContent [| 1uy; 2uy; 3uy |]

                let firstRequest =
                    WorkingDirectoryUpdate.Request.branchSwitch
                        selection
                        facts
                        operation
                        firstContent
                        "diagnostic-a"
                        (SelectedStateReader(selection))
                        previousBranchId
                        selectedReferenceId
                        finalizer
                        None
                    |> required

                let secondRequest =
                    WorkingDirectoryUpdate.Request.branchSwitch
                        selection
                        facts
                        operation
                        secondContent
                        "diagnostic-b"
                        (SelectedStateReader(selection))
                        previousBranchId
                        selectedReferenceId
                        finalizer
                        None
                    |> required

                let differentReferenceId = Guid.Parse("33333333-3333-3333-3333-333333333333")

                let differentOperation =
                    WorkingDirectoryUpdateContracts.Operation.branchSwitch previousBranchId differentReferenceId target
                    |> required

                let differentRequest =
                    WorkingDirectoryUpdate.Request.branchSwitch
                        selection
                        facts
                        differentOperation
                        differentContent
                        "diagnostic-a"
                        (SelectedStateReader(selection))
                        previousBranchId
                        differentReferenceId
                        finalizer
                        None
                    |> required

                WorkingDirectoryUpdate.Request.operationValueForTests firstRequest
                |> should equal (WorkingDirectoryUpdate.Request.operationValueForTests secondRequest)

                WorkingDirectoryUpdate.Request.operationValueForTests firstRequest
                |> should not' (equal (WorkingDirectoryUpdate.Request.operationValueForTests differentRequest))

                WorkingDirectoryUpdateContracts.PreparedContent.dispose firstContent
                WorkingDirectoryUpdateContracts.PreparedContent.dispose secondContent
                WorkingDirectoryUpdateContracts.PreparedContent.dispose differentContent
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously

    [<Test>]
    let ``request execution uses a deep-frozen target graph after the caller mutates its source status`` () =
        withScenario (fun workingRoot objectRoot databasePath ->
            task {
                let! target, targetStatus, rootMetadata = targetAndStatus ()
                let priorStatus = emptyPriorStatus targetStatus rootMetadata
                let! content = preparedContent [| 1uy; 2uy; 3uy |]

                let scope =
                    WorkingDirectoryUpdateContracts.LocalRootScope.create workingRoot
                    |> required

                let configuration =
                    WorkingDirectoryUpdate.AcceptedConfiguration.create "engine-tests" (defaultScanInput workingRoot)
                    |> required

                do! LocalStateDb.replaceStatusSnapshot databasePath priorStatus
                let! revision = LocalStateDb.readLocalStatusRevision databasePath

                let selection =
                    WorkingDirectoryUpdate.AcceptedSelection.create target scope configuration revision
                    |> required

                let facts =
                    WorkingDirectoryUpdate.ApplicationFacts.create
                        target
                        workingRoot
                        objectRoot
                        databasePath
                        priorStatus
                        targetStatus
                        (targetStatus.Index.Values |> Seq.toArray)
                    |> required

                rootMetadata.Files.Clear()
                targetStatus.Index.Clear()

                let operation =
                    WorkingDirectoryUpdateContracts.Operation.connectBootstrap target "cursor-1" scope
                    |> required

                let updateRequest =
                    WorkingDirectoryUpdate.Request.connectBootstrap
                        selection
                        facts
                        operation
                        content
                        "engine-tests"
                        (SelectedStateReader(selection))
                        "cursor-1"
                        None
                    |> required

                let! outcome = WorkingDirectoryUpdate.run updateRequest CancellationToken.None

                match outcome with
                | WorkingDirectoryUpdateContracts.Outcome.Updated _ -> ()
                | _ -> failwithf "Expected deep-frozen request to update successfully but received %A." outcome

                File.ReadAllBytes(Path.Combine(workingRoot, "nested", "child.txt"))
                |> should equal [| 4uy; 5uy; 6uy |]
            })
        |> Async.AwaitTask
        |> Async.RunSynchronously
