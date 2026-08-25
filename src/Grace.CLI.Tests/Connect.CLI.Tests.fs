namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.Branch
open Grace.Types.Reference
open Microsoft.Data.Sqlite
open NUnit.Framework
open Spectre.Console
open System
open System.IO
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Groups connect coverage for the CLI test project.
[<NonParallelizable>]
module ConnectTests =
    /// Creates the minimal repository-local configuration file required by the shared configuration cache.
    let private ensureGraceConfig root =
        let graceDirectory = Path.Combine(root, Constants.GraceConfigDirectory)

        Directory.CreateDirectory(graceDirectory)
        |> ignore

        File.WriteAllText(Path.Combine(graceDirectory, Constants.GraceConfigFileName), "{}")

    /// Configures the CLI local-state paths for a fresh Connect producer-path test repository.
    let private configureForRoot root =
        let configuration = GraceConfiguration()
        configuration.OwnerId <- Guid.NewGuid()
        configuration.OrganizationId <- Guid.NewGuid()
        configuration.RepositoryId <- Guid.NewGuid()
        configuration.BranchId <- Guid.NewGuid()
        configuration.RootDirectory <- root
        configuration.StandardizedRootDirectory <- normalizeFilePath root
        configuration.GraceDirectory <- Path.Combine(root, Constants.GraceConfigDirectory)
        configuration.ObjectDirectory <- Path.Combine(configuration.GraceDirectory, Constants.GraceObjectsDirectory)
        configuration.GraceStatusFile <- Path.Combine(configuration.GraceDirectory, Constants.GraceLocalStateDbFileName)
        configuration.GraceObjectCacheFile <- configuration.GraceStatusFile
        configuration.ConfigurationDirectory <- configuration.GraceDirectory
        configuration.IsPopulated <- true
        updateConfiguration configuration
        configuration

    /// Runs an asynchronous Connect producer-path test in an isolated repository and restores global CLI state.
    let private withConfiguredTempDir action =
        task {
            let tempDir = Path.Combine(Path.GetTempPath(), $"grace-connect-tests-{Guid.NewGuid():N}")
            Directory.CreateDirectory(tempDir) |> ignore
            let originalDir = Environment.CurrentDirectory
            let previousConfiguration = if configurationFileExists () then Some(Current()) else None

            try
                Environment.CurrentDirectory <- tempDir
                ensureGraceConfig tempDir
                let configuration = configureForRoot tempDir
                return! action configuration
            finally
                Environment.CurrentDirectory <- originalDir

                match previousConfiguration with
                | Some configuration -> updateConfiguration configuration
                | None -> resetConfiguration ()

                SqliteConnection.ClearAllPools()

                if Directory.Exists(tempDir) then
                    try
                        Directory.Delete(tempDir, true)
                    with
                    | _ -> ()
        }

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

    /// Computes both content hashes for deterministic Connect transaction fixtures.
    let private hashes (bytes: byte array) =
        let sha256 =
            SHA256.HashData(bytes)
            |> Convert.ToHexString
            |> fun value -> Sha256Hash(value.ToLowerInvariant())

        sha256, Blake3Hash(ContentAddress.computeBlake3Hex bytes)

    /// Creates a complete one-file target status and its exact root metadata.
    let private oneFileStatus (configuration: GraceConfiguration) (path: string) (bytes: byte array) =
        let sha256, blake3 = hashes bytes

        let file = LocalFileVersion.CreateWithHashes (RelativePath path) sha256 blake3 false (int64 bytes.Length) (getCurrentInstant ()) true DateTime.UtcNow

        let entries =
            [|
                Services.DirectoryVersionPreimageEntry.File file.RelativePath file.Size file.Blake3Hash file.Sha256Hash
            |]

        let root =
            LocalDirectoryVersion.CreateWithHashes
                (DirectoryVersionId.NewGuid())
                configuration.OwnerId
                configuration.OrganizationId
                configuration.RepositoryId
                (RelativePath Constants.RootDirectoryPath)
                (Services.computeSha256ForDirectoryEntries (RelativePath Constants.RootDirectoryPath) entries)
                (Services.computeBlake3ForDirectory (RelativePath Constants.RootDirectoryPath) entries)
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
        root,
        file

    /// Initializes and reads the production local-state sentinel from a repository that has no local database.
    let private readFreshConnectStatus (configuration: GraceConfiguration) =
        task {
            File.Exists(configuration.GraceStatusFile)
            |> should equal false

            let! status = Services.readGraceStatusFile ()

            File.Exists(configuration.GraceStatusFile)
            |> should equal true

            status.Index.Count |> should equal 0

            status.RootDirectoryId
            |> should equal DirectoryVersionId.Empty

            status.RootDirectorySha256Hash
            |> should equal (Sha256Hash String.Empty)

            status.RootDirectoryBlake3Hash
            |> should equal (Blake3Hash String.Empty)

            return status
        }

    /// Creates exact prepared bytes and the matching private WDU target for one Connect transaction.
    let private createConnectInput configuration path bytes =
        task {
            let targetStatus, _, targetFile = oneFileStatus configuration path bytes

            let manifest =
                WorkingDirectoryUpdateContracts.PreparedManifest.create [ WorkingDirectoryUpdateContracts.PreparedManifestEntry.File(
                                                                              targetFile.RelativePath,
                                                                              targetFile.Sha256Hash,
                                                                              targetFile.Blake3Hash
                                                                          ) ]
                |> required

            let! preparedResult = WorkingDirectoryUpdateContracts.PreparedContent.create manifest (new ByteReader(path, bytes)) CancellationToken.None

            let target =
                WorkingDirectoryUpdateContracts.Target.create
                    configuration.RepositoryId
                    configuration.BranchId
                    targetStatus.RootDirectoryId
                    targetStatus.RootDirectorySha256Hash
                    targetStatus.RootDirectoryBlake3Hash
                |> required

            return targetStatus, targetFile, target, (preparedResult |> required)
        }

    /// Connect routes exact prepared bytes and its initial cursor through one terminal WDU transaction.
    [<Test>]
    let ``connect WDU force replaces only the selected path and atomically records its cursor`` () =
        withConfiguredTempDir (fun configuration ->
            task {
                let selectedPath = "selected.txt"
                let selectedBytes = Encoding.UTF8.GetBytes("selected bytes")
                let unrelatedBytes = Encoding.UTF8.GetBytes("unrelated bytes")
                let! targetStatus, targetFile, target, prepared = createConnectInput configuration selectedPath selectedBytes
                let! currentStatus = readFreshConnectStatus configuration
                File.WriteAllText(Path.Combine(configuration.RootDirectory, selectedPath), "conflicting bytes")
                File.WriteAllBytes(Path.Combine(configuration.RootDirectory, "unrelated.txt"), unrelatedBytes)

                let initialCursor = "branch-event-v1:845"

                let! outcome =
                    WorkingDirectoryUpdate.Connect.run
                        target
                        currentStatus
                        targetStatus
                        prepared
                        initialCursor
                        true
                        "connect-845"
                        CancellationToken.None
                        WorkingDirectoryUpdate.Connect.none

                match outcome with
                | WorkingDirectoryUpdateContracts.Outcome.Updated _ -> ()
                | other -> Assert.Fail($"Expected Connect WDU Updated, got {other}.")

                File.ReadAllBytes(Path.Combine(configuration.RootDirectory, selectedPath))
                |> should equal selectedBytes

                File.ReadAllBytes(Path.Combine(configuration.RootDirectory, "unrelated.txt"))
                |> should equal unrelatedBytes

                let! persistedStatus = LocalStateDb.readStatusSnapshot configuration.GraceStatusFile

                WorkingDirectoryUpdate.LocalApplication.statusFingerprintMatches targetStatus persistedStatus
                |> should equal true

                persistedStatus.RootDirectoryId
                |> should equal targetStatus.RootDirectoryId

                persistedStatus.RootDirectorySha256Hash
                |> should equal targetStatus.RootDirectorySha256Hash

                persistedStatus.RootDirectoryBlake3Hash
                |> should equal targetStatus.RootDirectoryBlake3Hash

                let objectPath =
                    Path.Combine(
                        configuration.ObjectDirectory,
                        string targetFile.RelativePath,
                        Services.getLocalObjectCacheFileName targetFile.RelativePath targetFile.Sha256Hash targetFile.Blake3Hash
                    )

                File.ReadAllBytes(objectPath)
                |> should equal selectedBytes

                let! objectMetadataCommitted = LocalStateDb.isDirectoryVersionInObjectCache configuration.GraceStatusFile targetStatus.RootDirectoryId

                objectMetadataCommitted |> should equal true

                let! fileMetadataCommitted = LocalStateDb.isFileVersionInObjectCache configuration.GraceStatusFile targetFile

                fileMetadataCommitted |> should equal true

                let! boundary = LocalStateDb.readRemoteReferenceBoundary configuration.GraceStatusFile configuration.RepositoryId configuration.BranchId

                boundary
                |> should
                    equal
                    (Some
                        { ReferenceMaterializationBoundaryDto.Default with
                            RepositoryId = configuration.RepositoryId
                            BranchId = configuration.BranchId
                            DirectoryId = targetStatus.RootDirectoryId
                            Sha256Hash = targetStatus.RootDirectorySha256Hash
                            Blake3Hash = targetStatus.RootDirectoryBlake3Hash
                            EventCursor = initialCursor
                        })

                let localRootScope =
                    WorkingDirectoryUpdateContracts.LocalRootScope.create configuration.RootDirectory
                    |> required

                let operation =
                    WorkingDirectoryUpdateContracts.Operation.connectBootstrap target initialCursor localRootScope
                    |> required

                let! completion = LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target operation

                completion
                |> should equal (Some LocalStateDb.WorkingDirectoryUpdateCompletion.Terminal)
            })

    /// Connect without force rejects an exact untracked conflict without changing unrelated local content or terminal state.
    [<Test>]
    let ``connect WDU without force rejects the selected conflict and preserves local state`` () =
        withConfiguredTempDir (fun configuration ->
            task {
                let selectedPath = "selected.txt"
                let conflictingBytes = Encoding.UTF8.GetBytes("conflicting bytes")
                let unrelatedBytes = Encoding.UTF8.GetBytes("unrelated bytes")
                let! targetStatus, _, target, prepared = createConnectInput configuration selectedPath (Encoding.UTF8.GetBytes("selected bytes"))
                let! currentStatus = readFreshConnectStatus configuration
                let initialCursor = "branch-event-v1:846"

                File.WriteAllBytes(Path.Combine(configuration.RootDirectory, selectedPath), conflictingBytes)
                File.WriteAllBytes(Path.Combine(configuration.RootDirectory, "unrelated.txt"), unrelatedBytes)

                let! outcome =
                    WorkingDirectoryUpdate.Connect.run
                        target
                        currentStatus
                        targetStatus
                        prepared
                        initialCursor
                        false
                        "connect-846"
                        CancellationToken.None
                        WorkingDirectoryUpdate.Connect.none

                match outcome with
                | WorkingDirectoryUpdateContracts.Outcome.Rejected _ -> ()
                | other -> Assert.Fail($"Expected Connect WDU Rejected, got {other}.")

                File.ReadAllBytes(Path.Combine(configuration.RootDirectory, selectedPath))
                |> should equal conflictingBytes

                File.ReadAllBytes(Path.Combine(configuration.RootDirectory, "unrelated.txt"))
                |> should equal unrelatedBytes

                let! persistedStatus = LocalStateDb.readStatusSnapshot configuration.GraceStatusFile

                WorkingDirectoryUpdate.LocalApplication.statusFingerprintMatches currentStatus persistedStatus
                |> should equal true

                persistedStatus.RootDirectoryId
                |> should equal currentStatus.RootDirectoryId

                let! boundary = LocalStateDb.readRemoteReferenceBoundary configuration.GraceStatusFile configuration.RepositoryId configuration.BranchId

                boundary |> should equal None

                let! objectMetadataCommitted = LocalStateDb.isDirectoryVersionInObjectCache configuration.GraceStatusFile targetStatus.RootDirectoryId

                objectMetadataCommitted |> should equal false
            })

    /// Connect force never recursively deletes unrelated content nested under an exact target collision.
    [<Test>]
    let ``connect WDU force rejects a nonempty target directory and preserves its contents`` () =
        withConfiguredTempDir (fun configuration ->
            task {
                let selectedPath = "selected.txt"
                let nestedBytes = Encoding.UTF8.GetBytes("keep nested content")
                let! targetStatus, _, target, prepared = createConnectInput configuration selectedPath (Encoding.UTF8.GetBytes("selected bytes"))
                let! currentStatus = readFreshConnectStatus configuration
                let selectedDirectory = Path.Combine(configuration.RootDirectory, selectedPath)

                Directory.CreateDirectory(selectedDirectory)
                |> ignore

                File.WriteAllBytes(Path.Combine(selectedDirectory, "keep.txt"), nestedBytes)

                let! outcome =
                    WorkingDirectoryUpdate.Connect.run
                        target
                        currentStatus
                        targetStatus
                        prepared
                        "branch-event-v1:846-force"
                        true
                        "connect-846-force"
                        CancellationToken.None
                        WorkingDirectoryUpdate.Connect.none

                match outcome with
                | WorkingDirectoryUpdateContracts.Outcome.Rejected _ -> ()
                | other -> Assert.Fail($"Expected Connect WDU Rejected, got {other}.")

                Directory.Exists(selectedDirectory)
                |> should equal true

                File.ReadAllBytes(Path.Combine(selectedDirectory, "keep.txt"))
                |> should equal nestedBytes

                let! persistedStatus = LocalStateDb.readStatusSnapshot configuration.GraceStatusFile

                persistedStatus.RootDirectoryId
                |> should equal currentStatus.RootDirectoryId
            })

    /// A failure at the Connect commit boundary leaves status, object metadata, cursor, and completion uncommitted together.
    [<Test>]
    let ``connect WDU commits no terminal facts when the atomic commit fails`` () =
        withConfiguredTempDir (fun configuration ->
            task {
                let selectedPath = "selected.txt"
                let selectedBytes = Encoding.UTF8.GetBytes("selected bytes")
                let! targetStatus, targetFile, target, prepared = createConnectInput configuration selectedPath selectedBytes
                let! currentStatus = readFreshConnectStatus configuration
                let initialCursor = "branch-event-v1:847"

                let failureInjection =
                    { WorkingDirectoryUpdate.Connect.none with
                        ThrowAt =
                            fun point ->
                                if point = WorkingDirectoryUpdate.Connect.BeforeCommit then
                                    raise (InvalidOperationException("connect-before-commit"))
                    }

                let! outcome =
                    WorkingDirectoryUpdate.Connect.run
                        target
                        currentStatus
                        targetStatus
                        prepared
                        initialCursor
                        false
                        "connect-847"
                        CancellationToken.None
                        failureInjection

                match outcome with
                | WorkingDirectoryUpdateContracts.Outcome.UpdateIncomplete failure ->
                    WorkingDirectoryUpdateContracts.Failure.reason failure
                    |> should contain "connect-before-commit"
                | other -> Assert.Fail($"Expected Connect WDU UpdateIncomplete, got {other}.")

                File.ReadAllBytes(Path.Combine(configuration.RootDirectory, selectedPath))
                |> should equal selectedBytes

                let! persistedStatus = LocalStateDb.readStatusSnapshot configuration.GraceStatusFile

                WorkingDirectoryUpdate.LocalApplication.statusFingerprintMatches currentStatus persistedStatus
                |> should equal true

                persistedStatus.RootDirectoryId
                |> should equal currentStatus.RootDirectoryId

                let! boundary = LocalStateDb.readRemoteReferenceBoundary configuration.GraceStatusFile configuration.RepositoryId configuration.BranchId

                boundary |> should equal None

                let! objectMetadataCommitted = LocalStateDb.isDirectoryVersionInObjectCache configuration.GraceStatusFile targetStatus.RootDirectoryId

                objectMetadataCommitted |> should equal false

                let! fileMetadataCommitted = LocalStateDb.isFileVersionInObjectCache configuration.GraceStatusFile targetFile

                fileMetadataCommitted |> should equal false

                let localRootScope =
                    WorkingDirectoryUpdateContracts.LocalRootScope.create configuration.RootDirectory
                    |> required

                let operation =
                    WorkingDirectoryUpdateContracts.Operation.connectBootstrap target initialCursor localRootScope
                    |> required

                let! completion = LocalStateDb.readWorkingDirectoryUpdateCompletion configuration.GraceStatusFile target operation

                completion |> should equal None
            })

    /// A no-download Connect never enters the retrieval path that can persist a remote boundary.
    [<Test>]
    let ``connect no download does not invoke materialization`` () =
        task {
            let mutable invoked = false

            let! result =
                Connect.retrieveWhenRequested false (fun () ->
                    invoked <- true
                    Task.FromResult 0)

            Assert.That(result, Is.EqualTo(None))
            Assert.That(invoked, Is.False)
        }

    /// Sets ansi console output needed by the test scenario.
    let private setAnsiConsoleOutput (writer: TextWriter) =
        let settings = AnsiConsoleSettings()
        settings.Out <- AnsiConsoleOutput(writer)
        AnsiConsole.Console <- AnsiConsole.Create(settings)

    /// Runs with captured output for test scenarios.
    let private runWithCapturedOutput (args: string array) =
        use writer = new StringWriter()
        let originalOut = Console.Out

        try
            Console.SetOut(writer)
            setAnsiConsoleOutput writer
            let exitCode = GraceCommand.main args
            exitCode, writer.ToString()
        finally
            Console.SetOut(originalOut)
            setAnsiConsoleOutput originalOut

    /// Runs the supplied action with temp dir applied.
    let private withTempDir (action: string -> unit) =
        let tempDir = Path.Combine(Path.GetTempPath(), $"grace-cli-tests-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tempDir) |> ignore
        let originalDir = Environment.CurrentDirectory

        try
            Environment.CurrentDirectory <- tempDir
            action tempDir
        finally
            Environment.CurrentDirectory <- originalDir

            if Directory.Exists(tempDir) then
                try
                    Directory.Delete(tempDir, true)
                with
                | _ -> ()

    /// Gets grace config path needed by the test scenario.
    let private getGraceConfigPath root = Path.Combine(root, ".grace", "graceconfig.json")

    /// Creates the persisted Connect configuration result used by output projection tests.
    let private outputConfiguration repositoryId branchId : Grace.CLI.Common.LocalOutputDto.ConnectDto =
        {
            OwnerId = Guid.NewGuid()
            OwnerName = "owner"
            OrganizationId = Guid.NewGuid()
            OrganizationName = "organization"
            RepositoryId = repositoryId
            RepositoryName = "repository"
            BranchId = branchId
            BranchName = "main"
            DefaultBranchName = "main"
            RetrievedDefaultBranch = false
        }

    /// Reads one named Connect fact from the common JSON envelope properties.
    let private jsonPropertyValue (root: JsonElement) (name: string) =
        let properties = root.GetProperty("Properties")

        match properties.ValueKind with
        | JsonValueKind.Object -> properties.GetProperty(name).GetString()
        | JsonValueKind.Array ->
            properties.EnumerateArray()
            |> Seq.find (fun property -> property.GetProperty("Key").GetString() = name)
            |> fun property -> property.GetProperty("Value").GetString()
        | kind -> invalidOp $"Unexpected JSON Properties shape: {kind}."

    /// Verifies that connect creates config when missing.
    [<Test>]
    let ``connect creates config when missing`` () =
        withTempDir (fun root ->
            /// Verifies that the CLI connect scenario exits with the expected process status.
            let exitCode, _ = runWithCapturedOutput [| "connect" |]
            exitCode |> should equal -1

            File.Exists(getGraceConfigPath root)
            |> should equal true)

    /// A failed optional update reports persisted configuration and update failure separately in the JSON error envelope.
    [<Test>]
    let ``connect JSON separates configured repository from failed update outcome`` () =
        use writer = new StringWriter()
        let originalOut = Console.Out
        let repositoryId = Guid.NewGuid()
        let branchId = Guid.NewGuid()

        let configuration = outputConfiguration repositoryId branchId

        try
            Console.SetOut(writer)
            setAnsiConsoleOutput writer
            let parseResult = GraceCommand.rootCommand.Parse([| "--output"; "Json"; "connect" |])
            let error = GraceError.Create "local update failed" "connect-output-json"

            Connect.renderConfiguredUpdateFailure parseResult configuration "UpdateIncomplete" error
            |> should equal -1
        finally
            Console.SetOut(originalOut)
            setAnsiConsoleOutput originalOut

        use document = JsonDocument.Parse(writer.ToString())
        let root = document.RootElement

        root.GetProperty("Error").GetString()
        |> should equal "local update failed"

        jsonPropertyValue root Connect.configurationOutcomeProperty
        |> should equal "Configured"

        jsonPropertyValue root Connect.updateOutcomeProperty
        |> should equal "UpdateIncomplete"

        jsonPropertyValue root "Connect.RepositoryId"
        |> should equal (string repositoryId)

        jsonPropertyValue root "Connect.BranchId"
        |> should equal (string branchId)

        writer.ToString()
        |> should not' (contain "configuration remains saved")

    /// A successful optional update reports configuration and update outcomes separately in one JSON success envelope.
    [<Test>]
    let ``connect JSON separates configured repository from successful update outcome`` () =
        use writer = new StringWriter()
        let originalOut = Console.Out
        let repositoryId = Guid.NewGuid()
        let branchId = Guid.NewGuid()
        let configuration = outputConfiguration repositoryId branchId

        try
            Console.SetOut(writer)
            setAnsiConsoleOutput writer
            let parseResult = GraceCommand.rootCommand.Parse([| "--output"; "Json"; "connect" |])

            Connect.renderConfiguredUpdateSuccess parseResult configuration "Unchanged" "connect-output-json-success"
            |> should equal 0
        finally
            Console.SetOut(originalOut)
            setAnsiConsoleOutput originalOut

        use document = JsonDocument.Parse(writer.ToString())
        let root = document.RootElement
        let returnValue = root.GetProperty("ReturnValue")

        returnValue.GetProperty("RepositoryId").GetGuid()
        |> should equal repositoryId

        returnValue.GetProperty("BranchId").GetGuid()
        |> should equal branchId

        returnValue
            .GetProperty("RetrievedDefaultBranch")
            .GetBoolean()
        |> should equal true

        jsonPropertyValue root Connect.configurationOutcomeProperty
        |> should equal "Configured"

        jsonPropertyValue root Connect.updateOutcomeProperty
        |> should equal "Unchanged"

    /// A failed optional update repeats the durable configuration result before the human-readable update error.
    [<Test>]
    let ``connect human output keeps configuration success separate from update failure`` () =
        use writer = new StringWriter()
        let originalOut = Console.Out

        let configuration = outputConfiguration (Guid.NewGuid()) (Guid.NewGuid())

        try
            Console.SetOut(writer)
            setAnsiConsoleOutput writer
            let parseResult = GraceCommand.rootCommand.Parse([| "connect" |])
            let error = GraceError.Create "local update failed" "connect-output-human"

            Connect.renderConfiguredUpdateFailure parseResult configuration "Rejected" error
            |> should equal -1
        finally
            Console.SetOut(originalOut)
            setAnsiConsoleOutput originalOut

        writer.ToString()
        |> should contain "Grace repository configuration remains saved."

        writer.ToString()
        |> should contain "local update failed"

    /// Verifies that a typed default sentinel is reported as no Reference even if another field is adversarially populated.
    [<Test>]
    let ``typed reference lookup rejects canonical sentinel and wrong type`` () =
        let promotion =
            { ReferenceDto.Default with
                ReferenceId = ReferenceId.NewGuid()
                DirectoryId = DirectoryVersionId.NewGuid()
                ReferenceType = ReferenceType.Promotion
            }

        let branch = { BranchDto.Default with LatestPromotion = promotion; LatestCommit = ReferenceDto.Default }

        Connect.tryGetDirectoryIdFromBranch ReferenceType.Commit branch
        |> should equal None

        Connect.tryGetDirectoryIdFromBranch ReferenceType.Promotion branch
        |> should equal (Some promotion.DirectoryId)
