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

    /// A fresh default Connect persists the exact server-selected root identity in both status and boundary state.
    [<Test>]
    let ``fresh connect materialization persists the selected server root identity`` () =
        withConfiguredTempDir (fun configuration ->
            task {
                let parseResult = GraceCommand.rootCommand.Parse([| "connect" |])
                let selectedRootId = DirectoryVersionId.Parse "11111111-8020-4000-8000-111111111111"
                let! scannedStatus = Services.createNewGraceStatusFile GraceStatus.Default parseResult

                let boundary =
                    { ReferenceMaterializationBoundaryDto.Default with
                        RepositoryId = configuration.RepositoryId
                        BranchId = configuration.BranchId
                        DirectoryId = selectedRootId
                        Sha256Hash = scannedStatus.RootDirectorySha256Hash
                        Blake3Hash = scannedStatus.RootDirectoryBlake3Hash
                        EventCursor = "branch-event-v1:1"
                    }

                Assert.That(File.Exists(configuration.GraceStatusFile), Is.False)

                let! materializedStatus = Connect.createAndWriteMaterializedStatus GraceStatus.Default parseResult boundary CancellationToken.None
                let! persistedStatus = LocalStateDb.readStatusMeta configuration.GraceStatusFile

                let! persistedBoundary =
                    LocalStateDb.readRemoteReferenceBoundary configuration.GraceStatusFile configuration.RepositoryId configuration.BranchId

                Assert.That(materializedStatus.RootDirectoryId, Is.EqualTo(selectedRootId))
                Assert.That(persistedStatus.RootDirectoryId, Is.EqualTo(selectedRootId))
                Assert.That(persistedStatus.RootDirectorySha256Hash, Is.EqualTo(boundary.Sha256Hash))
                Assert.That(persistedStatus.RootDirectoryBlake3Hash, Is.EqualTo(boundary.Blake3Hash))
                Assert.That(persistedBoundary, Is.EqualTo(Some boundary))
            })

    /// Cancellation after the status scan but before SQLite acceptance leaves neither status nor boundary committed.
    [<Test>]
    let ``connect cancellation before durable acceptance leaves no status or boundary`` () =
        withConfiguredTempDir (fun configuration ->
            task {
                let parseResult = GraceCommand.rootCommand.Parse([| "connect" |])
                let selectedRootId = DirectoryVersionId.Parse "22222222-8020-4000-8000-222222222222"
                let! scannedStatus = Services.createNewGraceStatusFile GraceStatus.Default parseResult

                let boundary =
                    { ReferenceMaterializationBoundaryDto.Default with
                        RepositoryId = configuration.RepositoryId
                        BranchId = configuration.BranchId
                        DirectoryId = selectedRootId
                        Sha256Hash = scannedStatus.RootDirectorySha256Hash
                        Blake3Hash = scannedStatus.RootDirectoryBlake3Hash
                        EventCursor = "branch-event-v1:2"
                    }

                use cancellation = new CancellationTokenSource()
                let mutable cancelled = false

                try
                    let! _ =
                        Connect.createAndWriteMaterializedStatusWithBeforeDurableWrite
                            GraceStatus.Default
                            parseResult
                            boundary
                            cancellation.Token
                            cancellation.Cancel

                    ()
                with
                | :? OperationCanceledException -> cancelled <- true

                let! persistedStatus = LocalStateDb.readStatusMeta configuration.GraceStatusFile

                let! persistedBoundary =
                    LocalStateDb.readRemoteReferenceBoundary configuration.GraceStatusFile configuration.RepositoryId configuration.BranchId

                Assert.That(cancelled, Is.True)
                Assert.That(persistedStatus.RootDirectoryId, Is.EqualTo(DirectoryVersionId.Empty))
                Assert.That(persistedBoundary, Is.EqualTo(None))
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

    /// Verifies that connect creates config when missing.
    [<Test>]
    let ``connect creates config when missing`` () =
        withTempDir (fun root ->
            /// Verifies that the CLI connect scenario exits with the expected process status.
            let exitCode, _ = runWithCapturedOutput [| "connect" |]
            exitCode |> should equal -1

            File.Exists(getGraceConfigPath root)
            |> should equal true)

    /// Verifies that connect skip decision requires matching blake3 when remote has one.
    [<Test>]
    let ``connect skip decision requires matching blake3 when remote has one`` () =
        let remoteFile =
            FileVersion.CreateWithHashes
                (RelativePath "same-sha-different-blake3.txt")
                (Sha256Hash "shared-sha")
                (Blake3Hash "remote-blake3")
                String.Empty
                false
                10L

        Connect.existingFileMatchesRemoteVersion (Sha256Hash "shared-sha") (Blake3Hash "local-blake3") remoteFile
        |> should equal false

        Connect.existingFileMatchesRemoteVersion (Sha256Hash "shared-sha") (Blake3Hash "remote-blake3") remoteFile
        |> should equal true

    /// Verifies that connect does not accept a remote file without BLAKE3 as a match.
    [<Test>]
    let ``connect skip decision rejects empty remote blake3`` () =
        let remoteFile = FileVersion.Default
        remoteFile.RelativePath <- RelativePath "missing-blake3.txt"
        remoteFile.Sha256Hash <- Sha256Hash "sha"

        Connect.existingFileMatchesRemoteVersion (Sha256Hash "sha") (Blake3Hash String.Empty) remoteFile
        |> should equal false

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
