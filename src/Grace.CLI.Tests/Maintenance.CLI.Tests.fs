namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Types.Common
open Microsoft.Data.Sqlite
open NodaTime
open NUnit.Framework
open Spectre.Console
open System
open System.Collections.Generic
open System.IO
open System.Text.Json

/// Groups maintenance cli coverage for the CLI test project.
[<NonParallelizable>]
module MaintenanceCliTests =
    /// Sets ansi console output needed by the test scenario.
    let private setAnsiConsoleOutput (writer: TextWriter) =
        let settings = AnsiConsoleSettings()
        settings.Out <- AnsiConsoleOutput(writer)
        AnsiConsole.Console <- AnsiConsole.Create(settings)

    /// Runs with captured stdout and stderr for test scenarios.
    let private runWithCapturedStdoutAndStderr (args: string array) =
        use standardOutWriter = new StringWriter()
        use standardErrorWriter = new StringWriter()
        let originalOut = Console.Out
        let originalError = Console.Error

        try
            Console.SetOut(standardOutWriter)
            Console.SetError(standardErrorWriter)
            setAnsiConsoleOutput standardOutWriter
            let exitCode = GraceCommand.main args
            exitCode, standardOutWriter.ToString(), standardErrorWriter.ToString()
        finally
            Console.SetOut(originalOut)
            Console.SetError(originalError)
            setAnsiConsoleOutput originalOut

    /// Runs the supplied action with temp repo applied.
    let private withTempRepo (action: string -> unit) =
        let tempDir = Path.Combine(Path.GetTempPath(), $"grace-maintenance-cli-tests-{Guid.NewGuid():N}")
        let graceDir = Path.Combine(tempDir, Constants.GraceConfigDirectory)
        Directory.CreateDirectory(graceDir) |> ignore
        File.WriteAllText(Path.Combine(graceDir, Constants.GraceConfigFileName), "{}")
        File.WriteAllText(Path.Combine(tempDir, "tracked.txt"), "tracked content")

        let originalDir = Environment.CurrentDirectory

        try
            Environment.CurrentDirectory <- tempDir
            resetConfiguration ()
            action tempDir
        finally
            resetConfiguration ()
            Environment.CurrentDirectory <- originalDir

            if Directory.Exists(tempDir) then
                try
                    Directory.Delete(tempDir, true)
                with
                | _ -> ()

    /// Parses json output for test assertions.
    let private parseJsonOutput (output: string) =
        output.StartsWith("{", StringComparison.Ordinal)
        |> should equal true

        JsonDocument.Parse(output)

    /// Executes a scalar integer query against the local state database.
    let private executeScalarInt (connection: SqliteConnection) (sql: string) =
        use command = connection.CreateCommand()
        command.CommandText <- sql
        command.ExecuteScalar() |> Convert.ToInt32

    /// Writes a SQL statement against the local state database for maintenance command setup.
    let private executeNonQuery (connection: SqliteConnection) (sql: string) =
        use command = connection.CreateCommand()
        command.CommandText <- sql
        command.ExecuteNonQuery() |> ignore

    /// Seeds durable Watch journal rows without persisting raw watcher event payloads.
    let private seedWatchJournalRows root throughSequence appliedThrough =
        let dbPath = Path.Combine(root, Constants.GraceConfigDirectory, Constants.GraceLocalStateDbFileName)

        LocalStateDb.ensureDbInitialized dbPath
        |> Async.AwaitTask
        |> Async.RunSynchronously

        use connection = new SqliteConnection($"Data Source={dbPath}")
        connection.Open()

        for sequence in [| 1L .. throughSequence |] do
            executeNonQuery connection $"INSERT INTO watch_journal (sequence, created_at_unix_ticks) VALUES ({sequence}, {sequence});"

        executeNonQuery connection $"UPDATE meta SET value = '{appliedThrough}' WHERE key = 'AppliedThroughSequence';"

    /// Counts local state rows so maintenance reset tests can prove destructive scope.
    let private countRows root tableName =
        let dbPath = Path.Combine(root, Constants.GraceConfigDirectory, Constants.GraceLocalStateDbFileName)
        use connection = new SqliteConnection($"Data Source={dbPath}")
        connection.Open()
        executeScalarInt connection $"SELECT COUNT(*) FROM {tableName};"

    /// Writes a fresh Watch IPC snapshot so clear-journal can prove the v1 single-writer refusal.
    let private writeLiveWatchIpc () =
        let current = Current()

        let status: Services.GraceWatchStatus =
            {
                UpdatedAt = Grace.Shared.Utilities.getCurrentInstant ()
                IsStartupClaim = false
                RepositoryId = current.RepositoryId
                RepositoryName = RepositoryName current.RepositoryName
                BranchId = current.BranchId
                BranchName = BranchName current.BranchName
                RootDirectory = current.RootDirectory
                HasPendingWatchWork = false
                IsWorkingTreeClean = true
                RootDirectoryId = Guid.NewGuid()
                RootDirectorySha256Hash = Sha256Hash "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                RootDirectoryBlake3Hash = Blake3Hash "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                LastFileUploadInstant = Instant.MinValue
                LastDirectoryVersionInstant = Instant.MinValue
                DirectoryIds = HashSet<DirectoryVersionId>()
            }

        let ipcFileName = Services.IpcFileName()

        Directory.CreateDirectory(Path.GetDirectoryName(ipcFileName))
        |> ignore

        File.WriteAllText(ipcFileName, Grace.Shared.Utilities.serialize status)

    /// Writes an unreadable Watch IPC payload for conservative clear-journal refusal coverage.
    let private writeUnreadableWatchIpc () =
        let ipcFileName = Services.IpcFileName()

        Directory.CreateDirectory(Path.GetDirectoryName(ipcFileName))
        |> ignore

        File.WriteAllText(ipcFileName, "{")

    /// Asserts that clean json stdout matches the expected contract.
    let private assertCleanJsonStdout (standardOut: string) =
        standardOut |> should not' (contain "Elapsed:")

        standardOut
        |> should not' (contain "Reading Grace index file")

        standardOut
        |> should not' (contain "Scanning working directory")

        standardOut
        |> should not' (contain "All values taken from the local Grace status file")

        standardOut
        |> should not' (contain "Number of differences")

        standardOut
        |> should not' (contain "Number of directories")

        parseJsonOutput standardOut

    /// Runs json maintenance for test scenarios.
    let private runJsonMaintenance args = runWithCapturedStdoutAndStderr (Array.append [| "--output"; "Json"; "maintenance" |] args)

    /// Builds a deterministic index for test scenarios fixture for the CLI maintenance assertions.
    let private createIndex () =
        /// Verifies that the CLI maintenance scenario exits with the expected process status.
        let exitCode, standardOut, standardError = runJsonMaintenance [| "update-index" |]
        exitCode |> should equal 0
        standardError |> should equal String.Empty
        use document = assertCleanJsonStdout standardOut
        let returnValue = document.RootElement.GetProperty("ReturnValue")

        returnValue
            .GetProperty(
                "DirectoryCount"
            )
            .ValueKind
        |> should equal JsonValueKind.Number

        returnValue
            .GetProperty(
                "RootSha256Hash"
            )
            .GetString()
            .Length
        |> should equal 64

        returnValue
            .GetProperty(
                "RootBlake3Hash"
            )
            .GetString()
            .Length
        |> should equal 64

    /// Writes status meta only snapshot needed by the test scenario.
    let private writeStatusMetaOnlySnapshot root =
        let status =
            { GraceStatus.Default with
                RootDirectoryId = DirectoryVersionId("11111111-1111-1111-1111-111111111111")
                RootDirectorySha256Hash = Sha256Hash "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                RootDirectoryBlake3Hash = Blake3Hash "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            }

        LocalStateDb.replaceStatusSnapshot (Path.Combine(root, Constants.GraceConfigDirectory, Constants.GraceLocalStateDbFileName)) status
        |> Async.AwaitTask
        |> Async.RunSynchronously

    /// Verifies that maintenance check ignore entries json emits one clean envelope.
    [<Test>]
    let ``maintenance check ignore entries json emits one clean envelope`` () =
        withTempRepo (fun _ ->
            /// Verifies that the CLI maintenance scenario exits with the expected process status.
            let exitCode, standardOut, standardError = runJsonMaintenance [| "check-ignore-entries" |]

            exitCode |> should equal 0
            standardError |> should equal String.Empty

            standardOut
            |> should not' (contain "Directory ignore entries:")

            use document = assertCleanJsonStdout standardOut
            let root = document.RootElement

            root.GetProperty("ReturnValue").GetProperty(
                "DirectoryEntries"
            )
                .ValueKind
            |> should equal JsonValueKind.Array

            root.GetProperty("ReturnValue").GetProperty(
                "FileEntries"
            )
                .ValueKind
            |> should equal JsonValueKind.Array

            root.GetProperty("Properties").ValueKind
            |> should equal JsonValueKind.Array)

    /// Verifies that maintenance update index json emits stats envelope with clean stdout.
    [<Test>]
    let ``maintenance update-index json emits stats envelope with clean stdout`` () =
        withTempRepo (fun _ ->
            /// Verifies that the CLI maintenance scenario exits with the expected process status.
            let exitCode, standardOut, standardError = runJsonMaintenance [| "update-index" |]

            exitCode |> should equal 0
            standardError |> should equal String.Empty

            use document = assertCleanJsonStdout standardOut
            let returnValue = document.RootElement.GetProperty("ReturnValue")

            returnValue
                .GetProperty(
                    "DirectoryCount"
                )
                .ValueKind
            |> should equal JsonValueKind.Number

            returnValue.GetProperty("FileCount").ValueKind
            |> should equal JsonValueKind.Number

            returnValue.GetProperty("TotalFileSize").ValueKind
            |> should equal JsonValueKind.Number

            returnValue
                .GetProperty(
                    "RootSha256Hash"
                )
                .GetString()
                .Length
            |> should equal 64

            returnValue
                .GetProperty(
                    "RootBlake3Hash"
                )
                .GetString()
                .Length
            |> should equal 64)

    /// Verifies that maintenance update index json exception emits one clean error envelope.
    [<Test>]
    let ``maintenance update-index json exception emits one clean error envelope`` () =
        withTempRepo (fun root ->
            let localStateDbPath = Path.Combine(root, Constants.GraceConfigDirectory, Constants.GraceLocalStateDbFileName)

            Directory.CreateDirectory(localStateDbPath)
            |> ignore

            /// Verifies that the CLI maintenance scenario exits with the expected process status.
            let exitCode, standardOut, standardError = runJsonMaintenance [| "update-index" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            standardOut |> should not' (contain "Elapsed:")

            standardOut
            |> should not' (contain "Reading existing Grace index file")

            standardOut
            |> should not' (contain "Computing new Grace index file")

            standardOut
            |> should not' (contain "Writing new Grace index file")

            standardOut
            |> should not' (contain "Number of directories scanned")

            use document = assertCleanJsonStdout standardOut
            let rootElement = document.RootElement

            rootElement.GetProperty("Error").GetString()
            |> should contain "Exception in UpdateIndex:"

            /// Tracks return Value changes so this scenario can assert the resulting side effect explicitly.
            let mutable returnValue = Unchecked.defaultof<JsonElement>

            rootElement.TryGetProperty("ReturnValue", &returnValue)
            |> should equal false)

    /// Verifies that maintenance scan json emits scan envelope with clean stdout.
    [<Test>]
    let ``maintenance scan json emits scan envelope with clean stdout`` () =
        withTempRepo (fun root ->
            createIndex ()
            File.WriteAllText(Path.Combine(root, "changed.txt"), "changed content")

            /// Verifies that the CLI maintenance scenario exits with the expected process status.
            let exitCode, standardOut, standardError = runJsonMaintenance [| "scan" |]

            exitCode |> should equal 0
            standardError |> should equal String.Empty

            use document = assertCleanJsonStdout standardOut
            let returnValue = document.RootElement.GetProperty("ReturnValue")

            returnValue
                .GetProperty(
                    "DifferenceCount"
                )
                .ValueKind
            |> should equal JsonValueKind.Number

            returnValue.GetProperty("Differences").ValueKind
            |> should equal JsonValueKind.Array

            returnValue
                .GetProperty(
                    "NewDirectoryVersionCount"
                )
                .ValueKind
            |> should equal JsonValueKind.Number

            returnValue
                .GetProperty(
                    "NewDirectoryVersions"
                )
                .ValueKind
            |> should equal JsonValueKind.Array

            let newDirectoryVersions = returnValue.GetProperty("NewDirectoryVersions")

            newDirectoryVersions.GetArrayLength()
            |> should greaterThan 0

            let newDirectoryVersion = newDirectoryVersions[0]

            newDirectoryVersion
                .GetProperty(
                    "Sha256Hash"
                )
                .GetString()
                .Length
            |> should equal 64

            newDirectoryVersion
                .GetProperty(
                    "Blake3Hash"
                )
                .GetString()
                .Length
            |> should equal 64)

    /// Verifies that maintenance stats json emits stats envelope with clean stdout.
    [<Test>]
    let ``maintenance stats json emits stats envelope with clean stdout`` () =
        withTempRepo (fun _ ->
            createIndex ()

            /// Verifies that the CLI maintenance scenario exits with the expected process status.
            let exitCode, standardOut, standardError = runJsonMaintenance [| "stats" |]

            exitCode |> should equal 0
            standardError |> should equal String.Empty

            use document = assertCleanJsonStdout standardOut
            let returnValue = document.RootElement.GetProperty("ReturnValue")

            returnValue
                .GetProperty(
                    "DirectoryCount"
                )
                .ValueKind
            |> should equal JsonValueKind.Number

            returnValue.GetProperty("FileCount").ValueKind
            |> should equal JsonValueKind.Number

            returnValue
                .GetProperty(
                    "RootSha256Hash"
                )
                .ValueKind
            |> should not' (equal JsonValueKind.Undefined)

            returnValue
                .GetProperty(
                    "RootSha256Hash"
                )
                .GetString()
                .Length
            |> should equal 64

            returnValue
                .GetProperty(
                    "RootBlake3Hash"
                )
                .GetString()
                .Length
            |> should equal 64)

    /// Verifies that maintenance stats json includes root hashes from status metadata when root row is absent.
    [<Test>]
    let ``maintenance stats json includes root hashes from status metadata when root row is absent`` () =
        withTempRepo (fun root ->
            writeStatusMetaOnlySnapshot root

            /// Verifies that the CLI maintenance scenario exits with the expected process status.
            let exitCode, standardOut, standardError = runJsonMaintenance [| "stats" |]

            exitCode |> should equal 0
            standardError |> should equal String.Empty

            use document = assertCleanJsonStdout standardOut
            let returnValue = document.RootElement.GetProperty("ReturnValue")

            returnValue
                .GetProperty("RootSha256Hash")
                .GetString()
            |> should equal "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"

            returnValue
                .GetProperty("RootBlake3Hash")
                .GetString()
            |> should equal "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")

    /// Verifies that maintenance list contents json emits contents envelope with clean stdout.
    [<Test>]
    let ``maintenance list contents json emits contents envelope with clean stdout`` () =
        withTempRepo (fun _ ->
            createIndex ()

            /// Verifies that the CLI maintenance scenario exits with the expected process status.
            let exitCode, standardOut, standardError = runJsonMaintenance [| "list-contents" |]

            exitCode |> should equal 0
            standardError |> should equal String.Empty

            use document = assertCleanJsonStdout standardOut
            let returnValue = document.RootElement.GetProperty("ReturnValue")

            returnValue.GetProperty("Summary").GetProperty(
                "DirectoryCount"
            )
                .ValueKind
            |> should equal JsonValueKind.Number

            returnValue.GetProperty("Directories").ValueKind
            |> should equal JsonValueKind.Array

            let directories = returnValue.GetProperty("Directories")

            directories.GetArrayLength()
            |> should greaterThan 0

            let directory = directories[0]

            directory.GetProperty("Sha256Hash").GetString()
                .Length
            |> should equal 64

            directory.GetProperty("Blake3Hash").GetString()
                .Length
            |> should equal 64

            let files = directory.GetProperty("Files")
            files.GetArrayLength() |> should greaterThan 0

            let file = files[0]

            file.GetProperty("Sha256Hash").GetString().Length
            |> should equal 64

            file.GetProperty("Blake3Hash").GetString().Length
            |> should equal 64)

    /// Verifies that maintenance list contents json with directories disabled emits dual hash summary.
    [<Test>]
    let ``maintenance list contents json with directories disabled emits dual hash summary`` () =
        withTempRepo (fun _ ->
            createIndex ()

            /// Verifies that the CLI maintenance scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runJsonMaintenance [| "list-contents"
                                      "--list-directories"
                                      "false" |]

            exitCode |> should equal 0
            standardError |> should equal String.Empty

            use document = assertCleanJsonStdout standardOut
            let returnValue = document.RootElement.GetProperty("ReturnValue")
            let summary = returnValue.GetProperty("Summary")

            summary.GetProperty("RootSha256Hash").GetString()
                .Length
            |> should equal 64

            summary.GetProperty("RootBlake3Hash").GetString()
                .Length
            |> should equal 64

            returnValue
                .GetProperty("Directories")
                .GetArrayLength()
            |> should equal 0)

    /// Verifies that maintenance show journal emits filtered Watch journal rows in the JSON contract.
    [<Test>]
    let ``maintenance show journal json filters by pending state and limit`` () =
        withTempRepo (fun root ->
            seedWatchJournalRows root 4L 2L

            /// Verifies that the CLI maintenance scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runJsonMaintenance [| "show-journal"
                                      "--state"
                                      "pending"
                                      "--limit"
                                      "1" |]

            exitCode |> should equal 0
            standardError |> should equal String.Empty

            use document = assertCleanJsonStdout standardOut
            let returnValue = document.RootElement.GetProperty("ReturnValue")

            returnValue
                .GetProperty("AppliedThroughSequence")
                .GetInt64()
            |> should equal 2L

            returnValue
                .GetProperty("AllocatedSequence")
                .GetInt64()
            |> should equal 4L

            returnValue.GetProperty("TotalRows").GetInt64()
            |> should equal 4L

            returnValue.GetProperty("StateFilter").GetString()
            |> should equal "pending"

            returnValue.GetProperty("Limit").GetInt32()
            |> should equal 1

            let rows = returnValue.GetProperty("Rows")
            rows.GetArrayLength() |> should equal 1

            let row = rows[0]

            row.GetProperty("Sequence").GetInt64()
            |> should equal 4L

            row.GetProperty("State").GetString()
            |> should equal "pending"

            /// Verifies that state filtering happens before limit selection.
            let appliedExitCode, appliedStandardOut, appliedStandardError =
                runJsonMaintenance [| "show-journal"
                                      "--state"
                                      "applied"
                                      "--limit"
                                      "1" |]

            appliedExitCode |> should equal 0
            appliedStandardError |> should equal String.Empty

            use appliedDocument = assertCleanJsonStdout appliedStandardOut

            let appliedRow =
                appliedDocument
                    .RootElement
                    .GetProperty("ReturnValue")
                    .GetProperty("Rows")[0]

            appliedRow.GetProperty("Sequence").GetInt64()
            |> should equal 2L)

    /// Verifies that maintenance clear journal resets journal state without removing status data.
    [<Test>]
    let ``maintenance clear journal json clears only journal rows and metadata`` () =
        withTempRepo (fun root ->
            createIndex ()
            seedWatchJournalRows root 3L 2L

            let statusRowsBefore = countRows root "status_meta"

            /// Verifies that the CLI maintenance scenario exits with the expected process status.
            let exitCode, standardOut, standardError = runJsonMaintenance [| "clear-journal" |]

            exitCode |> should equal 0
            standardError |> should equal String.Empty

            use document = assertCleanJsonStdout standardOut
            let returnValue = document.RootElement.GetProperty("ReturnValue")

            returnValue.GetProperty("RowsDeleted").GetInt64()
            |> should equal 3L

            returnValue
                .GetProperty("AppliedThroughSequenceBefore")
                .GetInt64()
            |> should equal 2L

            returnValue
                .GetProperty("AppliedThroughSequenceAfter")
                .GetInt64()
            |> should equal 0L

            countRows root "watch_journal" |> should equal 0

            countRows root "status_meta"
            |> should equal statusRowsBefore)

    /// Verifies that maintenance clear journal refuses while a fresh Watch process owns IPC.
    [<Test>]
    let ``maintenance clear journal refuses while watch is running`` () =
        withTempRepo (fun root ->
            seedWatchJournalRows root 2L 1L

            try
                writeLiveWatchIpc ()

                /// Verifies that the CLI maintenance scenario exits with the expected process status.
                let exitCode, standardOut, standardError = runJsonMaintenance [| "clear-journal" |]

                exitCode |> should equal -1
                standardError |> should equal String.Empty

                use document = assertCleanJsonStdout standardOut

                document
                    .RootElement
                    .GetProperty("Error")
                    .GetString()
                |> should contain "Grace Watch is running"

                countRows root "watch_journal" |> should equal 2
            finally
                let ipcFileName = Services.IpcFileName()

                if File.Exists(ipcFileName) then File.Delete(ipcFileName))

    /// Verifies that maintenance clear journal treats unreadable Watch IPC as live to avoid racing a heartbeat write.
    [<Test>]
    let ``maintenance clear journal refuses when watch status exists but cannot be read`` () =
        withTempRepo (fun root ->
            seedWatchJournalRows root 2L 1L

            try
                writeUnreadableWatchIpc ()

                /// Verifies that the CLI maintenance scenario exits with the expected process status.
                let exitCode, standardOut, standardError = runJsonMaintenance [| "clear-journal" |]

                exitCode |> should equal -1
                standardError |> should equal String.Empty

                use document = assertCleanJsonStdout standardOut

                document
                    .RootElement
                    .GetProperty("Error")
                    .GetString()
                |> should contain "could not be read"

                countRows root "watch_journal" |> should equal 2
            finally
                let ipcFileName = Services.IpcFileName()

                if File.Exists(ipcFileName) then File.Delete(ipcFileName))
