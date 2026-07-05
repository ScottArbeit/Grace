namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.Shared
open Grace.Shared.Client
open Grace.Types
open Microsoft.Data.Sqlite
open NUnit.Framework
open Spectre.Console
open System
open System.IO
open System.Net
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Grace.Types.Common

/// Groups doctor cli coverage for the CLI test project.
[<NonParallelizable>]
module DoctorCliTests =

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

    let private authEnvNames =
        [|
            Constants.EnvironmentVariables.GraceToken
            Constants.EnvironmentVariables.GraceTokenFile
            Constants.EnvironmentVariables.GraceAuthOidcAuthority
            Constants.EnvironmentVariables.GraceAuthOidcAudience
            Constants.EnvironmentVariables.GraceAuthOidcCliClientId
            Constants.EnvironmentVariables.GraceAuthOidcCliRedirectPort
            Constants.EnvironmentVariables.GraceAuthOidcCliScopes
            Constants.EnvironmentVariables.GraceAuthOidcM2mClientId
            Constants.EnvironmentVariables.GraceAuthOidcM2mClientSecret
            Constants.EnvironmentVariables.GraceAuthOidcM2mScopes
            Constants.EnvironmentVariables.GraceServerUri
        |]

    /// Runs the supplied action with cleared auth env applied.
    let private withClearedAuthEnv (action: unit -> unit) =
        let originalValues =
            authEnvNames
            |> Array.map (fun name -> name, Environment.GetEnvironmentVariable(name))

        try
            authEnvNames
            |> Array.iter (fun name -> Environment.SetEnvironmentVariable(name, null))

            action ()
        finally
            originalValues
            |> Array.iter (fun (name, value) -> Environment.SetEnvironmentVariable(name, value))

    /// Runs the supplied action with temp dir applied.
    let private withTempDir (action: string -> unit) =
        let tempDir = Path.Combine(Path.GetTempPath(), $"grace-doctor-cli-tests-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tempDir) |> ignore
        let originalDir = Environment.CurrentDirectory

        try
            Environment.CurrentDirectory <- tempDir
            withClearedAuthEnv (fun () -> action tempDir)
        finally
            Environment.CurrentDirectory <- originalDir

            if Directory.Exists(tempDir) then
                try
                    Directory.Delete(tempDir, true)
                with
                | _ -> ()

    /// Runs the supplied action with env applied.
    let private withEnv (name: string) (value: string option) (action: unit -> unit) =
        let original = Environment.GetEnvironmentVariable(name)

        match value with
        | Some v -> Environment.SetEnvironmentVariable(name, v)
        | None -> Environment.SetEnvironmentVariable(name, null)

        try
            action ()
        finally
            Environment.SetEnvironmentVariable(name, original)

    /// Runs the supplied action with local state db open trace applied.
    let private withLocalStateDbOpenTrace root (action: string -> unit) =
        let tracePath = Path.Combine(root, "local-state-open-trace.log")

        withEnv "GRACE_LOCALSTATE_DB_TRACE_PATH" (Some tracePath) (fun () -> withEnv "GRACE_LOCALSTATE_DB_TRACE_OPEN" (Some "1") (fun () -> action tracePath))

    /// Reads trace needed by the test scenario.
    let private readTrace tracePath = if File.Exists(tracePath) then File.ReadAllText(tracePath) else String.Empty

    /// Runs the supplied action with isolated home applied.
    let private withIsolatedHome (root: string) (action: string -> unit) =
        let home = Path.Combine(root, "home")
        Directory.CreateDirectory(home) |> ignore

        withEnv "USERPROFILE" (Some home) (fun () ->
            withEnv "HOME" (Some home) (fun () -> withEnv Constants.EnvironmentVariables.GraceServerUri None (fun () -> action home)))

    /// Writes grace config needed by the test scenario.
    let private writeGraceConfig root serverUri =
        let graceDir = Path.Combine(root, Constants.GraceConfigDirectory)
        Directory.CreateDirectory(graceDir) |> ignore
        let ownerId = string (Guid.NewGuid())
        let organizationId = string (Guid.NewGuid())
        let repositoryId = string (Guid.NewGuid())
        let branchId = string (Guid.NewGuid())

        File.WriteAllText(
            Path.Combine(graceDir, Constants.GraceConfigFileName),
            sprintf
                """
{
  "ownerId": "%s",
  "ownerName": "owner",
  "organizationId": "%s",
  "organizationName": "org",
  "repositoryId": "%s",
  "repositoryName": "repo",
  "branchId": "%s",
  "branchName": "main",
  "serverUri": "%s",
  "configurationVersion": "0.1",
  "programVersion": "0.1"
}
"""
                ownerId
                organizationId
                repositoryId
                branchId
                serverUri
        )

        Path.Combine(graceDir, Constants.GraceConfigFileName)

    /// Builds local state db path test data used to exercise CLI doctor behavior.
    let private localStateDbPath root = Path.Combine(root, Constants.GraceConfigDirectory, Constants.GraceLocalStateDbFileName)

    /// Gets corrupt backups needed by the test scenario.
    let private getCorruptBackups root =
        let graceDir = Path.Combine(root, Constants.GraceConfigDirectory)

        if Directory.Exists(graceDir) then
            Directory.GetFiles(graceDir, "grace-local.corrupt.*.db")
        else
            Array.empty

    /// Builds ensure valid local state db test data used to exercise CLI doctor behavior.
    let private ensureValidLocalStateDb root =
        writeGraceConfig root "http://127.0.0.1:5000"
        |> ignore

        LocalStateDb
            .ensureDbInitialized(localStateDbPath root)
            .GetAwaiter()
            .GetResult()

    /// Builds sha256 hex test data used to exercise CLI doctor behavior.
    let private sha256Hex (text: string) =
        use sha256 = SHA256.Create()

        Encoding.UTF8.GetBytes(text)
        |> sha256.ComputeHash
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    /// Builds a deterministic snapshot file for test scenarios fixture for the CLI doctor assertions.
    let private createSnapshotFile (relativePath: RelativePath) (content: string) (lastWriteTime: DateTime) =
        let bytes = Encoding.UTF8.GetBytes(content)
        use stream = new MemoryStream(bytes)

        let blake3Hash =
            Services
                .computeBlake3ForFile(stream)
                .GetAwaiter()
                .GetResult()

        LocalFileVersion.CreateWithHashes
            relativePath
            (sha256Hex content)
            (Blake3Hash $"{blake3Hash}")
            false
            (int64 bytes.Length)
            (Grace.Shared.Utilities.getCurrentInstant ())
            true
            lastWriteTime

    /// Builds seed working tree snapshot test data used to exercise CLI doctor behavior.
    let private seedWorkingTreeSnapshot root =
        writeGraceConfig root "http://127.0.0.1:5000"
        |> ignore

        let dbPath = localStateDbPath root

        LocalStateDb.ensureDbInitialized (dbPath)
        |> fun task -> task.GetAwaiter().GetResult()

        let ownerId = Guid.NewGuid()
        let organizationId = Guid.NewGuid()
        let repositoryId = Guid.NewGuid()
        let rootDirectoryId = Guid.NewGuid()
        let homeDirectoryId = Guid.NewGuid()
        let indexedWriteTime = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        let changedPath = Path.Combine(root, "changed.txt")
        let unchangedPath = Path.Combine(root, "mtime-only.txt")
        let graceIgnorePath = Path.Combine(root, Constants.GraceIgnoreFileName)

        File.WriteAllText(changedPath, "old content")
        File.SetLastWriteTimeUtc(changedPath, indexedWriteTime)
        File.WriteAllText(unchangedPath, "same content")
        File.SetLastWriteTimeUtc(unchangedPath, indexedWriteTime)

        let files =
            ResizeArray(
                [|
                    createSnapshotFile "changed.txt" "old content" indexedWriteTime
                    createSnapshotFile "deleted.txt" "deleted content" indexedWriteTime
                    createSnapshotFile "mtime-only.txt" "same content" indexedWriteTime
                |]
            )

        if File.Exists(graceIgnorePath) then
            files.Add(createSnapshotFile Constants.GraceIgnoreFileName (File.ReadAllText(graceIgnorePath)) (File.GetLastWriteTimeUtc(graceIgnorePath)))

        let homeDirectory =
            LocalDirectoryVersion.CreateWithHashes
                homeDirectoryId
                ownerId
                organizationId
                repositoryId
                "home"
                "home-snapshot-hash"
                (Blake3Hash "home-snapshot-blake3")
                (System.Collections.Generic.List<DirectoryVersionId>())
                (System.Collections.Generic.List<LocalFileVersion>())
                0L
                indexedWriteTime

        let rootChildDirectories =
            if Directory.Exists(Path.Combine(root, "home")) then
                System.Collections.Generic.List<DirectoryVersionId>([| homeDirectoryId |])
            else
                System.Collections.Generic.List<DirectoryVersionId>()

        let rootFiles = files |> Seq.toArray

        let rootDirectory =
            LocalDirectoryVersion.CreateWithHashes
                rootDirectoryId
                ownerId
                organizationId
                repositoryId
                Constants.RootDirectoryPath
                "root-snapshot-hash"
                (Blake3Hash "root-snapshot-blake3")
                rootChildDirectories
                (System.Collections.Generic.List<LocalFileVersion>(rootFiles))
                (rootFiles |> Array.sumBy (fun file -> file.Size))
                indexedWriteTime

        let indexEntries =
            [|
                System.Collections.Generic.KeyValuePair(rootDirectoryId, rootDirectory)

                if Directory.Exists(Path.Combine(root, "home")) then
                    System.Collections.Generic.KeyValuePair(homeDirectoryId, homeDirectory)
            |]

        let status =
            { GraceStatus.Default with Index = GraceIndex(indexEntries); RootDirectoryId = rootDirectoryId; RootDirectorySha256Hash = "root-snapshot-hash" }

        LocalStateDb.replaceStatusSnapshot dbPath status
        |> fun task -> task.GetAwaiter().GetResult()

        File.WriteAllText(changedPath, "new content")
        File.SetLastWriteTimeUtc(changedPath, indexedWriteTime.AddMinutes(5.0))
        File.WriteAllText(Path.Combine(root, "added.txt"), "added content")
        File.WriteAllText(unchangedPath, "same content")
        File.SetLastWriteTimeUtc(unchangedPath, indexedWriteTime.AddMinutes(10.0))

        dbPath

    /// Builds open raw connection test data used to exercise CLI doctor behavior.
    let private openRawConnection (dbPath: string) =
        let connection = new SqliteConnection($"Data Source={dbPath}")
        connection.Open()
        connection

    /// Builds execute non query test data used to exercise CLI doctor behavior.
    let private executeNonQuery (connection: SqliteConnection) (sql: string) =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- sql
        cmd.ExecuteNonQuery() |> ignore

    /// Builds seed schema version only test data used to exercise CLI doctor behavior.
    let private seedSchemaVersionOnly (dbPath: string) (schemaVersion: string) =
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath))
        |> ignore

        use connection = openRawConnection dbPath
        executeNonQuery connection "CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);"
        executeNonQuery connection $"INSERT OR REPLACE INTO meta (key, value) VALUES ('schema_version', '{schemaVersion}');"

    /// Builds snapshot file test data used to exercise CLI doctor behavior.
    let private snapshotFile (path: string) =
        if File.Exists(path) then
            use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
            use reader = new BinaryReader(stream)
            Some(Convert.ToBase64String(reader.ReadBytes(int stream.Length)), File.GetLastWriteTimeUtc(path))
        else
            None

    /// Determines whether volatile local state sidecar snapshot path for test assertions.
    let private isVolatileLocalStateSidecarSnapshotPath (relativePath: string) =
        let localStateDbRelativePath = Path.Combine(Constants.GraceConfigDirectory, Constants.GraceLocalStateDbFileName)

        [| "-shm"; "-wal"; "-journal" |]
        |> Array.exists (fun suffix -> relativePath.Equals(localStateDbRelativePath + suffix, StringComparison.OrdinalIgnoreCase))

    /// Builds snapshot last write time test data used to exercise CLI doctor behavior.
    let private snapshotLastWriteTime relativePath lastWriteTimeUtc =
        if isVolatileLocalStateSidecarSnapshotPath relativePath then
            DateTime.UnixEpoch
        else
            lastWriteTimeUtc

    /// Builds snapshot files test data used to exercise CLI doctor behavior.
    let private snapshotFiles root =
        if Directory.Exists(root) then
            Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            |> Array.map (fun path ->
                let relativePath = Path.GetRelativePath(root, path)
                let lastWriteTimeUtc = snapshotLastWriteTime relativePath (File.GetLastWriteTimeUtc(path))
                relativePath, lastWriteTimeUtc, FileInfo(path).Length)
            |> Array.sortBy (fun (relativePath, _, _) -> relativePath)
        else
            Array.empty

    /// Builds open exclusive read lock test data used to exercise CLI doctor behavior.
    let private openExclusiveReadLock path = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)

    /// Runs the supplied action with user config temporarily missing applied.
    let private withUserConfigTemporarilyMissing (action: string -> unit) =
        let userConfigPath = UserConfiguration.getUserConfigurationPath ()
        let backupPath = Path.Combine(Path.GetTempPath(), $"grace-userconfig-backup-{Guid.NewGuid():N}.json")
        let existed = File.Exists(userConfigPath)
        let userConfigDirectory = Path.GetDirectoryName(userConfigPath)

        try
            if existed then File.Move(userConfigPath, backupPath)

            action userConfigPath
        finally
            if File.Exists(userConfigPath) then File.Delete(userConfigPath)

            if existed then
                Directory.CreateDirectory(userConfigDirectory)
                |> ignore

                File.Move(backupPath, userConfigPath)

    /// Parses json output for test assertions.
    let private parseJsonOutput (output: string) =
        output
            .TrimStart()
            .StartsWith("{", StringComparison.Ordinal)
        |> should equal true

        JsonDocument.Parse(output)

    /// Asserts that clean json output matches the expected contract.
    let private assertCleanJsonOutput (standardOut: string) (standardError: string) =
        standardError |> should equal String.Empty
        standardOut |> should not' (contain "Elapsed:")

        standardOut
        |> should not' (contain "Grace Version Control System")

        standardOut
        |> should not' (contain "Doctor checks")

        parseJsonOutput standardOut

    /// Finds check by id used by the test scenario.
    let private findCheckById (checks: JsonElement) checkId =
        checks.EnumerateArray()
        |> Seq.find (fun check ->
            check
                .GetProperty("Id")
                .GetString()
                .Equals(checkId, StringComparison.Ordinal))

    /// Asserts that does not contain secrets matches the expected contract.
    let private assertDoesNotContainSecrets (secrets: string list) (standardOut: string) (standardError: string) =
        for secret in secrets do
            standardOut |> should not' (contain secret)
            standardError |> should not' (contain secret)

    /// Builds valid pat test data used to exercise CLI doctor behavior.
    let private validPat () = PersonalAccessToken.formatToken "doctor-user" (Guid.NewGuid()) (Array.create 32 7uy)

    let private localStateCheckIds =
        [|
            "state.db.file-present"
            "state.db.read-only-open"
            "state.db.schema-version"
            "state.db.required-tables"
            "state.db.required-indexes"
            "state.db.integrity-check"
            "state.db.foreign-key-check"
            "state.db.watch-journal"
            "object-cache.index-readable"
        |]

    let private serverCheckIds =
        [|
            "server.healthz.reachable"
            "server.lifecycle.headers"
            "server.auth-principal.available"
        |]

    /// Runs the supplied action with doctor server probe factory applied.
    let private withDoctorServerProbeFactory factory action =
        Doctor.setServerProbeFactoryForTests factory

        try
            action ()
        finally
            Doctor.resetServerProbeFactoryForTests ()

    /// Runs the supplied action with working tree scanner applied.
    let private withWorkingTreeScanner scanner action =
        Doctor.setWorkingTreeScannerForTests scanner

        try
            action ()
        finally
            Doctor.resetWorkingTreeScannerForTests ()

    /// Builds successful probe with lifecycle test data used to exercise CLI doctor behavior.
    let private successfulProbeWithLifecycle diagnostics =
        Doctor.ServerProbeSucceeded
            {
                StatusCode = HttpStatusCode.OK
                ReasonPhrase = "OK"
                LifecycleDiagnostics = diagnostics
                Body = """{"GraceUserId":"doctor-user","Claims":["repo.read"]}"""
            }

    let private noLifecycleProbe = successfulProbeWithLifecycle None

    /// Builds lifecycle diagnostics test data used to exercise CLI doctor behavior.
    let private lifecycleDiagnostics status unsupportedAfter minimumVersion recommendedVersion updateUrl updateUrlIsHttps unsupportedAfterIsMalformed =
        let diagnostics: Grace.SDK.ClientIdentity.LifecycleDiagnostics =
            {
                Status = status
                UnsupportedAfter = unsupportedAfter
                UnsupportedAfterIsMalformed = unsupportedAfterIsMalformed
                MinimumVersion = minimumVersion
                RecommendedVersion = recommendedVersion
                UpdateUrl = updateUrl
                UpdateUrlIsHttps = updateUrlIsHttps
            }

        Some diagnostics

    /// Verifies that doctor help works without config and shows v1 options.
    [<Test>]
    let ``doctor help works without config and shows v1 options`` () =
        withTempDir (fun root ->
            /// Verifies that the CLI doctor scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "doctor"
                                                  "--help" |]

            exitCode |> should equal 0
            standardError |> should equal String.Empty
            standardOut |> should contain "--full"
            standardOut |> should contain "--offline"
            standardOut |> should contain "--list-checks"
            standardOut |> should contain "--check"
            standardOut |> should contain "--strict"

            Directory.Exists(Path.Combine(root, ".grace"))
            |> should equal false)

    /// Verifies that doctor list checks emits clean json envelope without config.
    [<Test>]
    let ``doctor list checks emits clean json envelope without config`` () =
        withTempDir (fun root ->
            /// Verifies that the CLI doctor scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "doctor"
                                                  "--list-checks" |]

            exitCode |> should equal 0

            use document = assertCleanJsonOutput standardOut standardError
            let returnValue = document.RootElement.GetProperty("ReturnValue")

            returnValue
                .GetProperty("ReportVersion")
                .GetString()
            |> should equal "doctor-report-v1"

            returnValue.GetProperty("Status").GetString()
            |> should equal "Ok"

            returnValue.GetProperty("Checks").GetArrayLength()
            |> should equal 28

            returnValue
                .GetProperty("Catalog")
                .GetArrayLength()
            |> should equal 28

            let catalogIds =
                returnValue
                    .GetProperty("Catalog")
                    .EnumerateArray()
                |> Seq.map (fun check -> check.GetProperty("Id").GetString())
                |> Set.ofSeq

            catalogIds
            |> should contain "auth.source.detected"

            catalogIds
            |> should contain "auth.env-token.valid"

            catalogIds
            |> should contain "auth.token-file.unsupported"

            catalogIds
            |> should contain "auth.oidc.configuration"

            catalogIds
            |> should contain "state.db.file-present"

            catalogIds
            |> should contain "state.db.read-only-open"

            catalogIds
            |> should contain "state.db.schema-version"

            catalogIds
            |> should contain "state.db.required-tables"

            catalogIds
            |> should contain "state.db.required-indexes"

            catalogIds
            |> should contain "state.db.integrity-check"

            catalogIds
            |> should contain "state.db.foreign-key-check"

            catalogIds
            |> should contain "state.db.watch-journal"

            catalogIds
            |> should contain "object-cache.index-readable"

            catalogIds |> should contain "working-tree.scan"

            catalogIds
            |> should contain "identity.auth-session"

            catalogIds |> should contain "server.connectivity"
            catalogIds |> should contain "config.file.parse"

            for checkId in serverCheckIds do
                catalogIds |> should contain checkId

            catalogIds
            |> should contain "user-config.file.discover"

            catalogIds
            |> should contain "ignore.entries.parse"

            let firstCheck = returnValue.GetProperty("Checks")[0]

            firstCheck.GetProperty("Id").GetString()
            |> should not' (equal String.Empty)

            Directory.Exists(Path.Combine(root, ".grace"))
            |> should equal false)

    /// Verifies that doctor list checks offline keeps full catalog without probing.
    [<Test>]
    let ``doctor list checks offline keeps full catalog without probing`` () =
        withTempDir (fun _ ->
            let requestCount = ref 0

            withDoctorServerProbeFactory
                (fun _ ->
                    incr requestCount
                    Task.FromResult noLifecycleProbe)
                (fun () ->
                    /// Verifies that the CLI doctor scenario exits with the expected process status.
                    let exitCode, standardOut, standardError =
                        runWithCapturedStdoutAndStderr [| "--output"
                                                          "Json"
                                                          "doctor"
                                                          "--list-checks"
                                                          "--offline" |]

                    exitCode |> should equal 0

                    use document = assertCleanJsonOutput standardOut standardError

                    let catalogIds =
                        document
                            .RootElement
                            .GetProperty("ReturnValue")
                            .GetProperty("Catalog")
                            .EnumerateArray()
                        |> Seq.map (fun check -> check.GetProperty("Id").GetString())
                        |> Set.ofSeq

                    catalogIds.Count |> should equal 28

                    catalogIds |> should contain "config.file.parse"

                    catalogIds
                    |> should contain "ignore.entries.parse"

                    catalogIds
                    |> should contain "auth.source.detected"

                    catalogIds
                    |> should contain "auth.env-token.valid"

                    catalogIds
                    |> should contain "auth.token-file.unsupported"

                    catalogIds
                    |> should contain "auth.oidc.configuration"

                    catalogIds
                    |> should contain "state.db.file-present"

                    catalogIds
                    |> should contain "state.db.watch-journal"

                    catalogIds
                    |> should contain "object-cache.index-readable"

                    catalogIds
                    |> should contain "identity.auth-session"

                    for checkId in serverCheckIds do
                        catalogIds |> should contain checkId

                    !requestCount |> should equal 0))

    /// Verifies that doctor server healthz reachable warns when lifecycle status is deprecated.
    [<Test>]
    let ``doctor server healthz reachable warns when lifecycle status is deprecated`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                writeGraceConfig root "http://configured.example.test/base"
                |> ignore

                let requests = ResizeArray<Doctor.ServerProbeRequest>()

                let diagnostics =
                    lifecycleDiagnostics
                        (Some "deprecated")
                        (Some "2026-12-01")
                        (Some "0.1.0")
                        (Some "0.2.0")
                        (Some "https://github.com/ScottArbeit/Grace/releases")
                        (Some true)
                        false

                withDoctorServerProbeFactory
                    (fun request ->
                        requests.Add(request)
                        Task.FromResult(successfulProbeWithLifecycle diagnostics))
                    (fun () ->
                        /// Verifies that the CLI doctor scenario exits with the expected process status.
                        let exitCode, standardOut, standardError =
                            runWithCapturedStdoutAndStderr [| "--output"
                                                              "Json"
                                                              "doctor"
                                                              "--check"
                                                              "server.healthz.reachable"
                                                              "--check"
                                                              "server.lifecycle.headers" |]

                        exitCode |> should equal 0

                        use document = assertCleanJsonOutput standardOut standardError

                        let checks =
                            document
                                .RootElement
                                .GetProperty("ReturnValue")
                                .GetProperty("Checks")

                        (findCheckById checks "server.healthz.reachable")
                            .GetProperty("Status")
                            .GetString()
                        |> should equal "Ok"

                        let lifecycle = findCheckById checks "server.lifecycle.headers"

                        lifecycle.GetProperty("Status").GetString()
                        |> should equal "Warning"

                        lifecycle.GetProperty("Summary").GetString()
                        |> should contain "deprecated"

                        requests.Count |> should equal 1

                        requests[0].BaseUri.AbsoluteUri
                        |> should equal "http://configured.example.test/base"

                        requests[0].RelativePath |> should equal "healthz"
                        requests[0].BearerToken |> should equal None)))

    /// Verifies that doctor exact lifecycle headers fails when healthz returns non success without lifecycle headers.
    [<Test>]
    let ``doctor exact lifecycle headers fails when healthz returns non-success without lifecycle headers`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                writeGraceConfig root "http://example.test"
                |> ignore

                let requests = ResizeArray<Doctor.ServerProbeRequest>()

                let failedProbe =
                    Doctor.ServerProbeSucceeded
                        { StatusCode = HttpStatusCode.InternalServerError; ReasonPhrase = "Internal Server Error"; LifecycleDiagnostics = None; Body = "" }

                withDoctorServerProbeFactory
                    (fun request ->
                        requests.Add(request)
                        Task.FromResult failedProbe)
                    (fun () ->
                        /// Verifies that the CLI doctor scenario exits with the expected process status.
                        let exitCode, standardOut, standardError =
                            runWithCapturedStdoutAndStderr [| "--output"
                                                              "Json"
                                                              "doctor"
                                                              "--check"
                                                              "server.lifecycle.headers" |]

                        exitCode |> should equal 1

                        use document = assertCleanJsonOutput standardOut standardError

                        let returnValue = document.RootElement.GetProperty("ReturnValue")

                        returnValue.GetProperty("Status").GetString()
                        |> should equal "Failed"

                        let checks = returnValue.GetProperty("Checks")

                        checks.GetArrayLength() |> should equal 1

                        let lifecycle = checks[0]

                        lifecycle.GetProperty("Id").GetString()
                        |> should equal "server.lifecycle.headers"

                        lifecycle.GetProperty("Status").GetString()
                        |> should equal "Failed"

                        lifecycle.GetProperty("Summary").GetString()
                        |> should contain "HTTP 500 Internal Server Error"

                        requests.Count |> should equal 1

                        requests[0].RelativePath |> should equal "healthz")))

    /// Verifies that doctor server healthz uses grace server uri over config and reports mismatch separately.
    [<Test>]
    let ``doctor server healthz uses GRACE_SERVER_URI over config and reports mismatch separately`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                writeGraceConfig root "http://configured.example.test"
                |> ignore

                let requests = ResizeArray<Doctor.ServerProbeRequest>()

                withEnv Constants.EnvironmentVariables.GraceServerUri (Some "http://environment.example.test") (fun () ->
                    withDoctorServerProbeFactory
                        (fun request ->
                            requests.Add(request)
                            Task.FromResult noLifecycleProbe)
                        (fun () ->
                            /// Verifies that the CLI doctor scenario exits with the expected process status.
                            let exitCode, standardOut, standardError =
                                runWithCapturedStdoutAndStderr [| "--output"
                                                                  "Json"
                                                                  "doctor"
                                                                  "--check"
                                                                  "server-uri.consistency"
                                                                  "--check"
                                                                  "server.healthz.reachable" |]

                            exitCode |> should equal 0

                            use document = assertCleanJsonOutput standardOut standardError

                            let checks =
                                document
                                    .RootElement
                                    .GetProperty("ReturnValue")
                                    .GetProperty("Checks")

                            (findCheckById checks "server-uri.consistency")
                                .GetProperty("Status")
                                .GetString()
                            |> should equal "Warning"

                            (findCheckById checks "server.healthz.reachable")
                                .GetProperty("Status")
                                .GetString()
                            |> should equal "Ok"

                            requests.Count |> should equal 1

                            requests[0].BaseUri.AbsoluteUri
                            |> should equal "http://environment.example.test/"))))

    /// Verifies that doctor server healthz classifies non success timeout tls connection and malformed uri findings.
    [<Test>]
    let ``doctor server healthz classifies non-success timeout tls connection and malformed uri findings`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                for probeResult, expectedText in
                    [
                        Doctor.ServerProbeSucceeded
                            { StatusCode = HttpStatusCode.ServiceUnavailable; ReasonPhrase = "Service Unavailable"; LifecycleDiagnostics = None; Body = "" },
                        "503"
                        Doctor.ServerProbeTimedOut "Timed out after 1500 ms while probing http://example.test/healthz.", "Timed out"
                        Doctor.ServerProbeTlsFailed "TLS negotiation failed while probing https://example.test/healthz: certificate error", "TLS"
                        Doctor.ServerProbeConnectionFailed "Could not connect to http://example.test/healthz: connection refused", "connect"
                    ] do
                    writeGraceConfig root "http://example.test"
                    |> ignore

                    withDoctorServerProbeFactory
                        (fun _ -> Task.FromResult probeResult)
                        (fun () ->
                            /// Verifies that the CLI doctor scenario exits with the expected process status.
                            let exitCode, standardOut, standardError =
                                runWithCapturedStdoutAndStderr [| "--output"
                                                                  "Json"
                                                                  "doctor"
                                                                  "--check"
                                                                  "server.healthz.reachable" |]

                            exitCode |> should equal 1

                            use document = assertCleanJsonOutput standardOut standardError

                            let check =
                                document
                                    .RootElement
                                    .GetProperty("ReturnValue")
                                    .GetProperty("Checks")[0]

                            check.GetProperty("Status").GetString()
                            |> should equal "Failed"

                            check.GetProperty("Summary").GetString()
                            |> should contain expectedText)

                let lifecycleRejectionDiagnostics =
                    lifecycleDiagnostics
                        (Some "unsupported")
                        (Some "2026-12-01")
                        (Some "0.1.0")
                        (Some "0.2.0")
                        (Some "https://github.com/ScottArbeit/Grace/releases")
                        (Some true)
                        false

                let lifecycleRejection =
                    Doctor.ServerProbeSucceeded
                        {
                            StatusCode = HttpStatusCode.UpgradeRequired
                            ReasonPhrase = "Upgrade Required"
                            LifecycleDiagnostics = lifecycleRejectionDiagnostics
                            Body = ""
                        }

                withDoctorServerProbeFactory
                    (fun _ -> Task.FromResult lifecycleRejection)
                    (fun () ->
                        /// Verifies that the CLI doctor scenario exits with the expected process status.
                        let exitCode, standardOut, standardError =
                            runWithCapturedStdoutAndStderr [| "--output"
                                                              "Json"
                                                              "doctor"
                                                              "--check"
                                                              "server.healthz.reachable" |]

                        exitCode |> should equal 1

                        use document = assertCleanJsonOutput standardOut standardError

                        let check =
                            document
                                .RootElement
                                .GetProperty("ReturnValue")
                                .GetProperty("Checks")[0]

                        check.GetProperty("Status").GetString()
                        |> should equal "Failed"

                        let summary = check.GetProperty("Summary").GetString()

                        summary |> should contain "unsupported"

                        summary
                        |> should contain "Update the Grace CLI/SDK"

                        summary
                        |> should not' (contain "check server health and GRACE_SERVER_URI"))

                writeGraceConfig root "not-a-uri" |> ignore

                let requestCount = ref 0

                withDoctorServerProbeFactory
                    (fun _ ->
                        incr requestCount
                        Task.FromResult noLifecycleProbe)
                    (fun () ->
                        /// Verifies that the CLI doctor scenario exits with the expected process status.
                        let exitCode, standardOut, standardError =
                            runWithCapturedStdoutAndStderr [| "--output"
                                                              "Json"
                                                              "doctor"
                                                              "--check"
                                                              "server.healthz.reachable" |]

                        exitCode |> should equal 1

                        use document = assertCleanJsonOutput standardOut standardError

                        let check =
                            document
                                .RootElement
                                .GetProperty("ReturnValue")
                                .GetProperty("Checks")[0]

                        check.GetProperty("Status").GetString()
                        |> should equal "Failed"

                        check.GetProperty("Summary").GetString()
                        |> should contain "not an absolute URI"

                        !requestCount |> should equal 0)))

    /// Verifies that doctor server lifecycle malformed date and non https update url warn cleanly.
    [<Test>]
    let ``doctor server lifecycle malformed date and non https update url warn cleanly`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                writeGraceConfig root "http://example.test"
                |> ignore

                let diagnostics =
                    lifecycleDiagnostics (Some "unsupported") (Some "not-a-date") (Some "0.1.0") None (Some "http://example.test/update") (Some false) true

                withDoctorServerProbeFactory
                    (fun _ -> Task.FromResult(successfulProbeWithLifecycle diagnostics))
                    (fun () ->
                        /// Verifies that the CLI doctor scenario exits with the expected process status.
                        let exitCode, standardOut, standardError =
                            runWithCapturedStdoutAndStderr [| "--output"
                                                              "Json"
                                                              "doctor"
                                                              "--check"
                                                              "server.lifecycle.headers" |]

                        exitCode |> should equal 0

                        use document = assertCleanJsonOutput standardOut standardError

                        let check =
                            document
                                .RootElement
                                .GetProperty("ReturnValue")
                                .GetProperty("Checks")[0]

                        check.GetProperty("Status").GetString()
                        |> should equal "Warning"

                        check.GetProperty("Summary").GetString()
                        |> should contain "not-a-date"

                        let summary = check.GetProperty("Summary").GetString()

                        summary
                        |> should contain "updateUrl=<suppressed because server value was not HTTPS>"

                        summary
                        |> should not' (contain "http://example.test/update"))))

    /// Verifies that doctor server explicit checks offline skip without network calls.
    [<Test>]
    let ``doctor server explicit checks offline skip without network calls`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                writeGraceConfig root "http://example.test"
                |> ignore

                let requestCount = ref 0

                withDoctorServerProbeFactory
                    (fun _ ->
                        incr requestCount
                        Task.FromResult noLifecycleProbe)
                    (fun () ->
                        /// Verifies that the CLI doctor scenario exits with the expected process status.
                        let exitCode, standardOut, standardError =
                            runWithCapturedStdoutAndStderr [| "--output"
                                                              "Json"
                                                              "doctor"
                                                              "--offline"
                                                              "--check"
                                                              "server.healthz.reachable"
                                                              "--check"
                                                              "server.lifecycle.headers"
                                                              "--check"
                                                              "server.auth-principal.available" |]

                        exitCode |> should equal 0

                        use document = assertCleanJsonOutput standardOut standardError

                        let checks =
                            document
                                .RootElement
                                .GetProperty("ReturnValue")
                                .GetProperty("Checks")

                        checks.GetArrayLength() |> should equal 3

                        for check in checks.EnumerateArray() do
                            check.GetProperty("Status").GetString()
                            |> should equal "Skipped"

                            check.GetProperty("Summary").GetString()
                            |> should contain "--offline"

                        !requestCount |> should equal 0)))

    /// Verifies that doctor server principal skips without valid pat and runs with non refreshing pat only.
    [<Test>]
    let ``doctor server principal skips without valid PAT and runs with non refreshing PAT only`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                writeGraceConfig root "http://example.test"
                |> ignore

                let requestCount = ref 0

                withDoctorServerProbeFactory
                    (fun _ ->
                        incr requestCount
                        Task.FromResult noLifecycleProbe)
                    (fun () ->
                        /// Verifies that the CLI doctor scenario exits with the expected process status.
                        let exitCode, standardOut, standardError =
                            runWithCapturedStdoutAndStderr [| "--output"
                                                              "Json"
                                                              "doctor"
                                                              "--check"
                                                              "server.auth-principal.available" |]

                        exitCode |> should equal 0

                        use document = assertCleanJsonOutput standardOut standardError

                        let check =
                            document
                                .RootElement
                                .GetProperty("ReturnValue")
                                .GetProperty("Checks")[0]

                        check.GetProperty("Status").GetString()
                        |> should equal "Skipped"

                        check.GetProperty("Summary").GetString()
                        |> should contain "no valid non-refreshing"

                        !requestCount |> should equal 0)

                withEnv Constants.EnvironmentVariables.GraceToken (Some "not-a-pat") (fun () ->
                    withDoctorServerProbeFactory
                        (fun _ ->
                            incr requestCount
                            Task.FromResult noLifecycleProbe)
                        (fun () ->
                            /// Verifies that the CLI doctor scenario exits with the expected process status.
                            let exitCode, standardOut, standardError =
                                runWithCapturedStdoutAndStderr [| "--output"
                                                                  "Json"
                                                                  "doctor"
                                                                  "--check"
                                                                  "server.auth-principal.available" |]

                            exitCode |> should equal 0
                            use document = assertCleanJsonOutput standardOut standardError

                            let check =
                                document
                                    .RootElement
                                    .GetProperty("ReturnValue")
                                    .GetProperty("Checks")[0]

                            check.GetProperty("Status").GetString()
                            |> should equal "Skipped"

                            !requestCount |> should equal 0))

                let token = validPat ()
                let principalRequests = ResizeArray<Doctor.ServerProbeRequest>()

                withEnv Constants.EnvironmentVariables.GraceToken (Some token) (fun () ->
                    withDoctorServerProbeFactory
                        (fun request ->
                            principalRequests.Add(request)
                            Task.FromResult noLifecycleProbe)
                        (fun () ->
                            /// Verifies that the CLI doctor scenario exits with the expected process status.
                            let exitCode, standardOut, standardError =
                                runWithCapturedStdoutAndStderr [| "--output"
                                                                  "Json"
                                                                  "doctor"
                                                                  "--check"
                                                                  "server.auth-principal.available" |]

                            exitCode |> should equal 0
                            assertDoesNotContainSecrets [ token ] standardOut standardError

                            use document = assertCleanJsonOutput standardOut standardError

                            let check =
                                document
                                    .RootElement
                                    .GetProperty("ReturnValue")
                                    .GetProperty("Checks")[0]

                            check.GetProperty("Status").GetString()
                            |> should equal "Ok"

                            principalRequests.Count |> should equal 1

                            principalRequests[0].RelativePath
                            |> should equal "authenticate/me"

                            principalRequests[0].BearerToken
                            |> should equal (Some token)))))

    /// Verifies that doctor server output modes and select stay clean.
    [<Test>]
    let ``doctor server output modes and select stay clean`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                writeGraceConfig root "http://example.test"
                |> ignore

                withDoctorServerProbeFactory
                    (fun _ -> Task.FromResult noLifecycleProbe)
                    (fun () ->
                        for output in [ "Silent"; "Minimal" ] do
                            /// Verifies that the CLI doctor scenario exits with the expected process status.
                            let exitCode, standardOut, standardError =
                                runWithCapturedStdoutAndStderr [| "--output"
                                                                  output
                                                                  "doctor"
                                                                  "--check"
                                                                  "server.healthz.reachable" |]

                            exitCode |> should equal 0
                            standardOut |> should equal String.Empty
                            standardError |> should equal String.Empty

                        /// Verifies that the CLI doctor scenario exits with the expected process status.
                        let exitCode, standardOut, standardError =
                            runWithCapturedStdoutAndStderr [| "--output"
                                                              "Verbose"
                                                              "doctor"
                                                              "--check"
                                                              "server.healthz.reachable"
                                                              "--select"
                                                              "Status" |]

                        exitCode |> should equal 0
                        standardError |> should equal String.Empty
                        standardOut.Trim() |> should equal "\"Ok\""

                        standardOut
                        |> should not' (contain "Grace doctor")

                        standardOut |> should not' (contain "healthz"))))

    /// Verifies that doctor server selected output is clean for failure warning offline and principal cases.
    [<Test>]
    let ``doctor server selected output is clean for failure warning offline and principal cases`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                writeGraceConfig root "http://example.test"
                |> ignore

                /// Asserts that selected status matches the expected contract.
                let assertSelectedStatus expectedExitCode expectedStatus args probeResult token =
                    let requestCount = ref 0

                    withEnv Constants.EnvironmentVariables.GraceToken token (fun () ->
                        withDoctorServerProbeFactory
                            (fun _ ->
                                incr requestCount
                                Task.FromResult probeResult)
                            (fun () ->
                                /// Verifies that the CLI doctor scenario exits with the expected process status.
                                let exitCode, standardOut, standardError = runWithCapturedStdoutAndStderr args

                                exitCode |> should equal expectedExitCode
                                standardError |> should equal String.Empty
                                standardOut.Trim() |> should equal expectedStatus

                                standardOut
                                |> should not' (contain "Grace doctor")

                                standardOut |> should not' (contain "EventTime")

                                if Array.contains "--offline" args then !requestCount |> should equal 0))

                assertSelectedStatus
                    1
                    "\"Failed\""
                    [|
                        "--output"
                        "Verbose"
                        "doctor"
                        "--check"
                        "server.healthz.reachable"
                        "--select"
                        "Status"
                    |]
                    (Doctor.ServerProbeConnectionFailed "Could not connect to http://example.test/healthz: connection refused")
                    None

                let warningDiagnostics =
                    lifecycleDiagnostics (Some "unsupported") (Some "not-a-date") (Some "0.1.0") None (Some "http://example.test/update") (Some false) true

                assertSelectedStatus
                    0
                    "\"Warning\""
                    [|
                        "--output"
                        "Verbose"
                        "doctor"
                        "--check"
                        "server.lifecycle.headers"
                        "--select"
                        "Status"
                    |]
                    (successfulProbeWithLifecycle warningDiagnostics)
                    None

                assertSelectedStatus
                    0
                    "\"Ok\""
                    [|
                        "--output"
                        "Verbose"
                        "doctor"
                        "--offline"
                        "--check"
                        "server.healthz.reachable"
                        "--select"
                        "Status"
                    |]
                    noLifecycleProbe
                    None

                assertSelectedStatus
                    0
                    "\"Ok\""
                    [|
                        "--output"
                        "Verbose"
                        "doctor"
                        "--check"
                        "server.auth-principal.available"
                        "--select"
                        "Status"
                    |]
                    noLifecycleProbe
                    None

                let token = validPat ()

                assertSelectedStatus
                    0
                    "\"Ok\""
                    [|
                        "--output"
                        "Verbose"
                        "doctor"
                        "--check"
                        "server.auth-principal.available"
                        "--select"
                        "Status"
                    |]
                    noLifecycleProbe
                    (Some token)))

    /// Verifies that doctor server exact check selections are not filtered out.
    [<Test>]
    let ``doctor server exact check selections are not filtered out`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                writeGraceConfig root "http://example.test"
                |> ignore

                /// Asserts that exact check matches the expected contract.
                let assertExactCheck checkId =
                    /// Verifies that the CLI doctor scenario exits with the expected process status.
                    let exitCode, standardOut, standardError =
                        runWithCapturedStdoutAndStderr [| "--output"
                                                          "Json"
                                                          "doctor"
                                                          "--check"
                                                          checkId |]

                    exitCode |> should equal 0

                    use document = assertCleanJsonOutput standardOut standardError

                    let checks =
                        document
                            .RootElement
                            .GetProperty("ReturnValue")
                            .GetProperty("Checks")

                    checks.GetArrayLength() |> should equal 1

                    let actualCheckId = checks[ 0 ].GetProperty("Id").GetString()
                    actualCheckId |> should equal checkId

                withDoctorServerProbeFactory
                    (fun _ -> Task.FromResult noLifecycleProbe)
                    (fun () ->
                        assertExactCheck "server.healthz.reachable"
                        assertExactCheck "server.lifecycle.headers"
                        assertExactCheck "server.auth-principal.available")))

    /// Verifies that doctor check filter accepts mixed case ids and categories.
    [<Test>]
    let ``doctor check filter accepts mixed case ids and categories`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI doctor scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "doctor"
                                                  "--check"
                                                  "CLI.CATALOG"
                                                  "--check"
                                                  "configuration" |]

            exitCode |> should equal 0

            use document = assertCleanJsonOutput standardOut standardError

            let checks =
                document
                    .RootElement
                    .GetProperty("ReturnValue")
                    .GetProperty("Checks")

            checks.GetArrayLength()
            |> should be (greaterThanOrEqualTo 5))

    /// Verifies that doctor explicit check includes non default catalog entry without full.
    [<Test>]
    let ``doctor explicit check includes non-default catalog entry without full`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI doctor scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "doctor"
                                                  "--check"
                                                  "identity.auth-session" |]

            exitCode |> should equal 0

            use document = assertCleanJsonOutput standardOut standardError

            let checks =
                document
                    .RootElement
                    .GetProperty("ReturnValue")
                    .GetProperty("Checks")

            checks.GetArrayLength() |> should equal 1

            checks[ 0 ].GetProperty("Id").GetString()
            |> should equal "identity.auth-session")

    /// Verifies that doctor explicit config check is not filtered out without full.
    [<Test>]
    let ``doctor explicit config check is not filtered out without full`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                writeGraceConfig root "http://127.0.0.1:5000"
                |> ignore

                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "doctor"
                                                      "--check"
                                                      "config.file.parse" |]

                exitCode |> should equal 0

                use document = assertCleanJsonOutput standardOut standardError

                let checks =
                    document
                        .RootElement
                        .GetProperty("ReturnValue")
                        .GetProperty("Checks")

                checks.GetArrayLength() |> should equal 1

                checks[ 0 ].GetProperty("Id").GetString()
                |> should equal "config.file.parse"

                checks[ 0 ].GetProperty("Status").GetString()
                |> should equal "Ok"))

    /// Verifies that doctor auth detects valid grace token without printing raw value.
    [<Test>]
    let ``doctor auth detects valid GRACE_TOKEN without printing raw value`` () =
        withTempDir (fun _ ->
            let token = validPat ()

            withEnv Constants.EnvironmentVariables.GraceToken (Some token) (fun () ->
                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "doctor"
                                                      "--check"
                                                      "auth.source.detected"
                                                      "--check"
                                                      "auth.env-token.valid" |]

                exitCode |> should equal 0
                assertDoesNotContainSecrets [ token ] standardOut standardError

                use document = assertCleanJsonOutput standardOut standardError

                let checks =
                    document
                        .RootElement
                        .GetProperty("ReturnValue")
                        .GetProperty("Checks")

                checks.GetArrayLength() |> should equal 2

                (findCheckById checks "auth.source.detected")
                    .GetProperty("Summary")
                    .GetString()
                |> should contain "GRACE_TOKEN PAT"

                (findCheckById checks "auth.env-token.valid")
                    .GetProperty("Status")
                    .GetString()
                |> should equal "Ok"))

    /// Verifies that doctor auth accepts bearer grace token without printing raw value.
    [<Test>]
    let ``doctor auth accepts bearer GRACE_TOKEN without printing raw value`` () =
        withTempDir (fun _ ->
            let token = validPat ()
            let bearerToken = $"  Bearer {token}  "

            withEnv Constants.EnvironmentVariables.GraceToken (Some bearerToken) (fun () ->
                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "doctor"
                                                      "--check"
                                                      "auth.source.detected"
                                                      "--check"
                                                      "auth.env-token.valid" |]

                exitCode |> should equal 0
                assertDoesNotContainSecrets [ token; bearerToken ] standardOut standardError

                use document = assertCleanJsonOutput standardOut standardError

                let checks =
                    document
                        .RootElement
                        .GetProperty("ReturnValue")
                        .GetProperty("Checks")

                (findCheckById checks "auth.source.detected")
                    .GetProperty("Summary")
                    .GetString()
                |> should contain "GRACE_TOKEN PAT"

                (findCheckById checks "auth.env-token.valid")
                    .GetProperty("Status")
                    .GetString()
                |> should equal "Ok"))

    /// Verifies that doctor auth rejects invalid grace token safely.
    [<TestCase("not-a-pat")>]
    [<TestCase("Bearer not-a-pat")>]
    let ``doctor auth rejects invalid GRACE_TOKEN safely`` token =
        withTempDir (fun _ ->
            withEnv Constants.EnvironmentVariables.GraceToken (Some token) (fun () ->
                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "doctor"
                                                      "--check"
                                                      "auth.env-token.valid" |]

                exitCode |> should equal 1

                if not (String.IsNullOrEmpty token) then
                    assertDoesNotContainSecrets [ token ] standardOut standardError

                use document = assertCleanJsonOutput standardOut standardError

                let check =
                    document
                        .RootElement
                        .GetProperty("ReturnValue")
                        .GetProperty("Checks")[0]

                check.GetProperty("Status").GetString()
                |> should equal "Failed"

                check.GetProperty("Summary").GetString()
                |> should contain "GRACE_TOKEN"))

    /// Verifies that doctor auth treats blank grace token as unset and continues oidc inspection.
    [<TestCase("")>]
    [<TestCase("   ")>]
    let ``doctor auth treats blank GRACE_TOKEN as unset and continues OIDC inspection`` token =
        withTempDir (fun _ ->
            withEnv Constants.EnvironmentVariables.GraceToken (Some token) (fun () ->
                withEnv Constants.EnvironmentVariables.GraceAuthOidcAuthority (Some "https://tenant.example.invalid/") (fun () ->
                    withEnv Constants.EnvironmentVariables.GraceAuthOidcAudience (Some "https://api.example.invalid/") (fun () ->
                        withEnv Constants.EnvironmentVariables.GraceAuthOidcM2mClientId (Some "m2m-client-id") (fun () ->
                            withEnv Constants.EnvironmentVariables.GraceAuthOidcM2mClientSecret (Some "m2m-client-secret") (fun () ->
                                /// Verifies that the CLI doctor scenario exits with the expected process status.
                                let exitCode, standardOut, standardError =
                                    runWithCapturedStdoutAndStderr [| "--output"
                                                                      "Json"
                                                                      "doctor"
                                                                      "--check"
                                                                      "auth.source.detected"
                                                                      "--check"
                                                                      "auth.env-token.valid"
                                                                      "--check"
                                                                      "auth.oidc.configuration" |]

                                exitCode |> should equal 0
                                assertDoesNotContainSecrets [ "m2m-client-secret" ] standardOut standardError

                                use document = assertCleanJsonOutput standardOut standardError

                                let checks =
                                    document
                                        .RootElement
                                        .GetProperty("ReturnValue")
                                        .GetProperty("Checks")

                                (findCheckById checks "auth.source.detected")
                                    .GetProperty("Summary")
                                    .GetString()
                                |> should contain "OIDC M2M environment"

                                (findCheckById checks "auth.env-token.valid")
                                    .GetProperty("Status")
                                    .GetString()
                                |> should equal "Skipped"

                                (findCheckById checks "auth.oidc.configuration")
                                    .GetProperty("Status")
                                    .GetString()
                                |> should equal "Ok"))))))

    /// Verifies that doctor auth reports unsupported grace token file with grace token remediation.
    [<Test>]
    let ``doctor auth reports unsupported GRACE_TOKEN_FILE with GRACE_TOKEN remediation`` () =
        withTempDir (fun _ ->
            let tokenFilePath = "C:\\secret\\grace-token.txt"

            withEnv Constants.EnvironmentVariables.GraceTokenFile (Some tokenFilePath) (fun () ->
                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "doctor"
                                                      "--check"
                                                      "auth.token-file.unsupported" |]

                exitCode |> should equal 1
                assertDoesNotContainSecrets [ tokenFilePath ] standardOut standardError

                use document = assertCleanJsonOutput standardOut standardError

                let check =
                    document
                        .RootElement
                        .GetProperty("ReturnValue")
                        .GetProperty("Checks")[0]

                check.GetProperty("Status").GetString()
                |> should equal "Failed"

                check.GetProperty("Summary").GetString()
                |> should contain "GRACE_TOKEN"))

    /// Verifies that doctor auth treats blank grace token file as unset for explicit checks.
    [<TestCase("")>]
    [<TestCase("   ")>]
    let ``doctor auth treats blank GRACE_TOKEN_FILE as unset for explicit checks`` tokenFileValue =
        withTempDir (fun _ ->
            withEnv Constants.EnvironmentVariables.GraceTokenFile (Some tokenFileValue) (fun () ->
                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "doctor"
                                                      "--check"
                                                      "auth.token-file.unsupported" |]

                exitCode |> should equal 0

                use document = assertCleanJsonOutput standardOut standardError

                let check =
                    document
                        .RootElement
                        .GetProperty("ReturnValue")
                        .GetProperty("Checks")[0]

                check.GetProperty("Status").GetString()
                |> should equal "Ok"

                check.GetProperty("Summary").GetString()
                |> should contain "GRACE_TOKEN_FILE is not set."))

    /// Verifies that doctor auth treats blank grace token file as unset in default checks.
    [<TestCase("")>]
    [<TestCase("   ")>]
    let ``doctor auth treats blank GRACE_TOKEN_FILE as unset in default checks`` tokenFileValue =
        withTempDir (fun _ ->
            withEnv Constants.EnvironmentVariables.GraceTokenFile (Some tokenFileValue) (fun () ->
                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "doctor" |]

                exitCode |> should equal 0

                use document = assertCleanJsonOutput standardOut standardError

                let checks =
                    document
                        .RootElement
                        .GetProperty("ReturnValue")
                        .GetProperty("Checks")

                (findCheckById checks "auth.token-file.unsupported")
                    .GetProperty("Status")
                    .GetString()
                |> should equal "Ok"))

    /// Verifies that doctor auth missing environment warns and skips token validation.
    [<Test>]
    let ``doctor auth missing environment warns and skips token validation`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI doctor scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "doctor"
                                                  "--check"
                                                  "auth.source.detected"
                                                  "--check"
                                                  "auth.env-token.valid"
                                                  "--check"
                                                  "auth.oidc.configuration" |]

            exitCode |> should equal 0

            use document = assertCleanJsonOutput standardOut standardError

            let checks =
                document
                    .RootElement
                    .GetProperty("ReturnValue")
                    .GetProperty("Checks")

            (findCheckById checks "auth.source.detected")
                .GetProperty("Status")
                .GetString()
            |> should equal "Warning"

            (findCheckById checks "auth.env-token.valid")
                .GetProperty("Status")
                .GetString()
            |> should equal "Skipped"

            (findCheckById checks "auth.oidc.configuration")
                .GetProperty("Status")
                .GetString()
            |> should equal "Skipped")

    /// Verifies that doctor auth reports partial oidc m2 m and cli configuration without secret values.
    [<Test>]
    let ``doctor auth reports partial OIDC M2M and CLI configuration without secret values`` () =
        withTempDir (fun _ ->
            let authority = "https://tenant.example.invalid/very-secret-authority"
            let m2mSecret = "m2m-client-secret-value"
            let cliClientId = "cli-client-secret-looking-id"

            withEnv Constants.EnvironmentVariables.GraceAuthOidcAuthority (Some authority) (fun () ->
                withEnv Constants.EnvironmentVariables.GraceAuthOidcM2mClientSecret (Some m2mSecret) (fun () ->
                    withEnv Constants.EnvironmentVariables.GraceAuthOidcCliClientId (Some cliClientId) (fun () ->
                        /// Verifies that the CLI doctor scenario exits with the expected process status.
                        let exitCode, standardOut, standardError =
                            runWithCapturedStdoutAndStderr [| "--output"
                                                              "Json"
                                                              "doctor"
                                                              "--check"
                                                              "auth.oidc.configuration" |]

                        exitCode |> should equal 0
                        assertDoesNotContainSecrets [ authority; m2mSecret; cliClientId ] standardOut standardError

                        use document = assertCleanJsonOutput standardOut standardError

                        let check =
                            document
                                .RootElement
                                .GetProperty("ReturnValue")
                                .GetProperty("Checks")[0]

                        check.GetProperty("Status").GetString()
                        |> should equal "Warning"

                        check.GetProperty("Summary").GetString()
                        |> should contain "partial"))))

    /// Verifies that doctor auth treats blank oidc required values as missing.
    [<Test>]
    let ``doctor auth treats blank OIDC required values as missing`` () =
        withTempDir (fun _ ->
            withEnv Constants.EnvironmentVariables.GraceAuthOidcAuthority (Some "   ") (fun () ->
                withEnv Constants.EnvironmentVariables.GraceAuthOidcAudience (Some "https://api.example.invalid/") (fun () ->
                    withEnv Constants.EnvironmentVariables.GraceAuthOidcM2mClientId (Some "m2m-client-id") (fun () ->
                        withEnv Constants.EnvironmentVariables.GraceAuthOidcM2mClientSecret (Some "m2m-client-secret") (fun () ->
                            /// Verifies that the CLI doctor scenario exits with the expected process status.
                            let exitCode, standardOut, standardError =
                                runWithCapturedStdoutAndStderr [| "--output"
                                                                  "Json"
                                                                  "doctor"
                                                                  "--check"
                                                                  "auth.oidc.configuration" |]

                            exitCode |> should equal 0
                            assertDoesNotContainSecrets [ "m2m-client-secret" ] standardOut standardError

                            use document = assertCleanJsonOutput standardOut standardError

                            let check =
                                document
                                    .RootElement
                                    .GetProperty("ReturnValue")
                                    .GetProperty("Checks")[0]

                            check.GetProperty("Status").GetString()
                            |> should equal "Warning"

                            check.GetProperty("Summary").GetString()
                            |> should contain Constants.EnvironmentVariables.GraceAuthOidcAuthority)))))

    /// Verifies that doctor auth exact check selections are not filtered out.
    [<Test>]
    let ``doctor auth exact check selections are not filtered out`` () =
        withTempDir (fun _ ->
            for checkId in
                [
                    "auth.env-token.valid"
                    "auth.token-file.unsupported"
                    "auth.oidc.configuration"
                ] do
                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "doctor"
                                                      "--check"
                                                      checkId |]

                exitCode |> should equal 0

                use document = assertCleanJsonOutput standardOut standardError

                let checks =
                    document
                        .RootElement
                        .GetProperty("ReturnValue")
                        .GetProperty("Checks")

                checks.GetArrayLength() |> should equal 1

                checks[ 0 ].GetProperty("Id").GetString()
                |> should equal checkId)

    /// Verifies that doctor auth select output is clean for valid and invalid auth states.
    [<TestCase("Json")>]
    [<TestCase("Verbose")>]
    let ``doctor auth select output is clean for valid and invalid auth states`` output =
        withTempDir (fun _ ->
            for token, expectedStatus in
                [
                    validPat (), "\"Ok\""
                    "not-a-pat", "\"Failed\""
                ] do
                withEnv Constants.EnvironmentVariables.GraceToken (Some token) (fun () ->
                    /// Verifies that the CLI doctor scenario exits with the expected process status.
                    let exitCode, standardOut, standardError =
                        runWithCapturedStdoutAndStderr [| "--output"
                                                          output
                                                          "doctor"
                                                          "--check"
                                                          "auth.env-token.valid"
                                                          "--select"
                                                          "Status" |]

                    if expectedStatus = "\"Ok\"" then
                        exitCode |> should equal 0
                    else
                        exitCode |> should equal 1

                    standardError |> should equal String.Empty
                    standardOut.Trim() |> should equal expectedStatus
                    assertDoesNotContainSecrets [ token ] standardOut standardError

                    standardOut
                    |> should not' (contain "Grace doctor")

                    standardOut |> should not' (contain "EventTime")))

    /// Verifies that doctor auth successful diagnostics emit no output for quiet output modes.
    [<TestCase("Silent")>]
    [<TestCase("Minimal")>]
    let ``doctor auth successful diagnostics emit no output for quiet output modes`` output =
        withTempDir (fun _ ->
            let token = validPat ()

            withEnv Constants.EnvironmentVariables.GraceToken (Some token) (fun () ->
                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      output
                                                      "doctor"
                                                      "--check"
                                                      "auth.env-token.valid" |]

                exitCode |> should equal 0
                standardOut |> should equal String.Empty
                standardError |> should equal String.Empty))

    /// Verifies that doctor auth list checks includes s2 auth catalog ids.
    [<Test>]
    let ``doctor auth list checks includes S2 auth catalog ids`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI doctor scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "doctor"
                                                  "--list-checks"
                                                  "--select"
                                                  "Catalog" |]

            exitCode |> should equal 0
            standardError |> should equal String.Empty

            use document = JsonDocument.Parse(standardOut)

            let catalogIds =
                document.RootElement.EnumerateArray()
                |> Seq.map (fun check -> check.GetProperty("Id").GetString())
                |> Set.ofSeq

            catalogIds
            |> should contain "auth.source.detected"

            catalogIds
            |> should contain "auth.env-token.valid"

            catalogIds
            |> should contain "auth.token-file.unsupported"

            catalogIds
            |> should contain "auth.oidc.configuration")

    /// Verifies that doctor list checks includes local state and object cache catalog ids.
    [<Test>]
    let ``doctor list checks includes local-state and object-cache catalog ids`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI doctor scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "doctor"
                                                  "--list-checks"
                                                  "--select"
                                                  "Catalog" |]

            exitCode |> should equal 0
            standardError |> should equal String.Empty

            use document = JsonDocument.Parse(standardOut)

            let catalogIds =
                document.RootElement.EnumerateArray()
                |> Seq.map (fun check -> check.GetProperty("Id").GetString())
                |> Set.ofSeq

            for checkId in localStateCheckIds do
                catalogIds |> should contain checkId)

    /// Verifies that doctor list checks includes working tree scan catalog id.
    [<Test>]
    let ``doctor list checks includes working-tree scan catalog id`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI doctor scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "doctor"
                                                  "--list-checks"
                                                  "--select"
                                                  "Catalog" |]

            exitCode |> should equal 0
            standardError |> should equal String.Empty

            use document = JsonDocument.Parse(standardOut)

            let catalogIds =
                document.RootElement.EnumerateArray()
                |> Seq.map (fun check -> check.GetProperty("Id").GetString())
                |> Set.ofSeq

            catalogIds |> should contain "working-tree.scan")

    /// Verifies that doctor default working tree scan is skipped without traversing changed files.
    [<Test>]
    let ``doctor default working-tree scan is skipped without traversing changed files`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                let dbPath = seedWorkingTreeSnapshot root
                let beforeRoot = snapshotFiles root

                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "doctor"
                                                      "--select"
                                                      "Checks" |]

                exitCode |> should equal 0
                standardError |> should equal String.Empty
                use document = JsonDocument.Parse(standardOut)

                let checks = document.RootElement
                let workingTree = findCheckById checks "working-tree.scan"

                workingTree.GetProperty("Status").GetString()
                |> should equal "Skipped"

                workingTree.GetProperty("Summary").GetString()
                |> should contain "Skipped in the default profile"

                workingTree.GetProperty("Summary").GetString()
                |> should not' (contain "differs")

                snapshotFiles root |> should equal beforeRoot))

    /// Verifies that doctor full working tree scan reports drift warning without mutation.
    [<Test>]
    let ``doctor full working-tree scan reports drift warning without mutation`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                seedWorkingTreeSnapshot root |> ignore
                let beforeRoot = snapshotFiles root

                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "doctor"
                                                      "--full"
                                                      "--offline"
                                                      "--select"
                                                      "Checks" |]

                exitCode |> should equal 0
                standardError |> should equal String.Empty
                use document = JsonDocument.Parse(standardOut)

                let workingTree = findCheckById document.RootElement "working-tree.scan"

                workingTree.GetProperty("Status").GetString()
                |> should equal "Warning"

                let summary = workingTree.GetProperty("Summary").GetString()

                summary |> should contain "3 total"
                summary |> should contain "1 added"
                summary |> should contain "1 changed"
                summary |> should contain "1 deleted"
                summary |> should contain "grace maintenance scan"

                summary
                |> should contain "grace maintenance update-index"

                summary |> should not' (contain "mtime-only.txt")

                snapshotFiles root |> should equal beforeRoot))

    /// Verifies that doctor exact working tree scan runs without full and excludes ignored and grace owned paths.
    [<Test>]
    let ``doctor exact working-tree scan runs without full and excludes ignored and grace-owned paths`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                File.WriteAllText(Path.Combine(root, Constants.GraceIgnoreFileName), "ignored.txt")
                seedWorkingTreeSnapshot root |> ignore
                File.WriteAllText(Path.Combine(root, "ignored.txt"), "ignored content")
                File.WriteAllText(Path.Combine(root, Constants.GraceConfigDirectory, "grace-local.db-wal.extra"), "sidecar-ish content")
                let beforeRoot = snapshotFiles root

                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "doctor"
                                                      "--check"
                                                      "working-tree.scan"
                                                      "--select"
                                                      "Checks" |]

                exitCode |> should equal 0
                standardError |> should equal String.Empty
                use document = JsonDocument.Parse(standardOut)

                document.RootElement.GetArrayLength()
                |> should equal 1

                let workingTree = findCheckById document.RootElement "working-tree.scan"

                workingTree.GetProperty("Status").GetString()
                |> should equal "Warning"

                let summary = workingTree.GetProperty("Summary").GetString()

                summary |> should contain "3 total"
                summary |> should not' (contain "ignored.txt")
                summary |> should not' (contain ".grace")

                snapshotFiles root |> should equal beforeRoot))

    /// Verifies that doctor working tree scan prunes trailing slash ignored directories.
    [<Test>]
    let ``doctor working-tree scan prunes trailing-slash ignored directories`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                File.WriteAllText(Path.Combine(root, Constants.GraceIgnoreFileName), String.Join(Environment.NewLine, [| "bin/"; "obj/"; "home/cache/" |]))

                seedWorkingTreeSnapshot root |> ignore

                let ignoredPaths =
                    [|
                        Path.Combine(root, "bin", "added-from-bin.txt")
                        Path.Combine(root, "obj", "Debug", "generated.txt")
                        Path.Combine(root, "home", "cache", "generated.txt")
                    |]

                for ignoredPath in ignoredPaths do
                    Directory.CreateDirectory(Path.GetDirectoryName(ignoredPath))
                    |> ignore

                    File.WriteAllText(ignoredPath, "ignored generated content")

                let beforeRoot = snapshotFiles root

                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "doctor"
                                                      "--check"
                                                      "working-tree.scan"
                                                      "--select"
                                                      "Checks" |]

                exitCode |> should equal 0
                standardError |> should equal String.Empty
                use document = JsonDocument.Parse(standardOut)

                document.RootElement.GetArrayLength()
                |> should equal 1

                let workingTree = findCheckById document.RootElement "working-tree.scan"

                workingTree.GetProperty("Status").GetString()
                |> should equal "Warning"

                let summary = workingTree.GetProperty("Summary").GetString()

                summary |> should contain "3 total"
                summary |> should contain "1 added"
                summary |> should contain "1 changed"
                summary |> should contain "1 deleted"

                snapshotFiles root |> should equal beforeRoot))

    /// Verifies that doctor working tree scan does not prune directories from file only ignore entries.
    [<Test>]
    let ``doctor working-tree scan does not prune directories from file-only ignore entries`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                File.WriteAllText(Path.Combine(root, Constants.GraceIgnoreFileName), "build")

                seedWorkingTreeSnapshot root |> ignore

                let buildOutput = Path.Combine(root, "build", "generated.txt")

                Directory.CreateDirectory(Path.GetDirectoryName(buildOutput))
                |> ignore

                File.WriteAllText(buildOutput, "generated content that should be detected")

                let beforeRoot = snapshotFiles root

                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "doctor"
                                                      "--check"
                                                      "working-tree.scan"
                                                      "--select"
                                                      "Checks" |]

                exitCode |> should equal 0
                standardError |> should equal String.Empty
                use document = JsonDocument.Parse(standardOut)

                document.RootElement.GetArrayLength()
                |> should equal 1

                let workingTree = findCheckById document.RootElement "working-tree.scan"

                workingTree.GetProperty("Status").GetString()
                |> should equal "Warning"

                let summary = workingTree.GetProperty("Summary").GetString()

                summary |> should contain "5 total"
                summary |> should contain "3 added"
                summary |> should contain "1 changed"
                summary |> should contain "1 deleted"

                snapshotFiles root |> should equal beforeRoot))

    /// Verifies that doctor strict exact working tree scan fails when read only scan execution fails.
    [<Test>]
    let ``doctor strict exact working-tree scan fails when read-only scan execution fails`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                seedWorkingTreeSnapshot root |> ignore

                withWorkingTreeScanner
                    (fun _ _ -> Task.FromResult(Error "simulated read-only scan failure"))
                    (fun () ->
                        /// Verifies that the CLI doctor scenario exits with the expected process status.
                        let exitCode, standardOut, standardError =
                            runWithCapturedStdoutAndStderr [| "--output"
                                                              "Json"
                                                              "doctor"
                                                              "--check"
                                                              "working-tree.scan"
                                                              "--strict"
                                                              "--select"
                                                              "Checks" |]

                        exitCode |> should equal 1
                        standardError |> should equal String.Empty
                        use document = JsonDocument.Parse(standardOut)

                        document.RootElement.GetArrayLength()
                        |> should equal 1

                        let workingTree = findCheckById document.RootElement "working-tree.scan"

                        workingTree.GetProperty("Status").GetString()
                        |> should equal "Failed"

                        workingTree.GetProperty("Severity").GetString()
                        |> should equal "Error"

                        workingTree.GetProperty("Summary").GetString()
                        |> should contain "Working-tree scan failed without updating local state: simulated read-only scan failure")))

    /// Verifies that doctor working tree category selector runs scan.
    [<Test>]
    let ``doctor working-tree category selector runs scan`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                seedWorkingTreeSnapshot root |> ignore

                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "doctor"
                                                      "--check"
                                                      "working-tree"
                                                      "--select"
                                                      "Checks" |]

                exitCode |> should equal 0
                standardError |> should equal String.Empty
                use document = JsonDocument.Parse(standardOut)

                document.RootElement.GetArrayLength()
                |> should equal 1

                let workingTree = findCheckById document.RootElement "working-tree.scan"

                workingTree.GetProperty("Status").GetString()
                |> should equal "Warning"))

    /// Verifies that doctor working tree default quiet modes emit no successful diagnostic output.
    [<TestCase("Silent")>]
    [<TestCase("Minimal")>]
    let ``doctor working-tree default quiet modes emit no successful diagnostic output`` output =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                seedWorkingTreeSnapshot root |> ignore
                let indexedWriteTime = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                File.WriteAllText(Path.Combine(root, "changed.txt"), "old content")
                File.SetLastWriteTimeUtc(Path.Combine(root, "changed.txt"), indexedWriteTime)
                File.WriteAllText(Path.Combine(root, "deleted.txt"), "deleted content")
                File.SetLastWriteTimeUtc(Path.Combine(root, "deleted.txt"), indexedWriteTime)
                File.Delete(Path.Combine(root, "added.txt"))

                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      output
                                                      "doctor"
                                                      "--check"
                                                      "working-tree.scan" |]

                exitCode |> should equal 0
                standardOut |> should equal String.Empty
                standardError |> should equal String.Empty))

    /// Verifies that doctor working tree verbose select emits clean scalar without progress text.
    [<Test>]
    let ``doctor working-tree verbose select emits clean scalar without progress text`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                seedWorkingTreeSnapshot root |> ignore

                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Verbose"
                                                      "doctor"
                                                      "--full"
                                                      "--offline"
                                                      "--select"
                                                      "Status" |]

                exitCode |> should equal 0
                standardError |> should equal String.Empty
                standardOut.Trim() |> should equal "\"Warning\""

                standardOut
                |> should not' (contain "Grace doctor")

                standardOut
                |> should not' (contain "scanForDifferences")

                standardOut
                |> should not' (contain "Working tree differs")))

    /// Verifies that doctor working tree scan skips unreadable snapshot inside report without creating database.
    [<Test>]
    let ``doctor working-tree scan skips unreadable snapshot inside report without creating database`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                writeGraceConfig root "http://127.0.0.1:5000"
                |> ignore

                let dbPath = localStateDbPath root

                Directory.CreateDirectory(Path.GetDirectoryName(dbPath))
                |> ignore

                File.WriteAllText(dbPath, "not a sqlite database")
                let beforeRoot = snapshotFiles root

                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "doctor"
                                                      "--check"
                                                      "working-tree.scan"
                                                      "--select"
                                                      "Checks" |]

                exitCode |> should equal 0
                standardError |> should equal String.Empty
                use document = JsonDocument.Parse(standardOut)

                let workingTree = findCheckById document.RootElement "working-tree.scan"

                workingTree.GetProperty("Status").GetString()
                |> should equal "Skipped"

                workingTree.GetProperty("Summary").GetString()
                |> should contain "Skipped because"

                snapshotFiles root |> should equal beforeRoot))

    /// Verifies that doctor selected non local check does not open local state database.
    [<Test>]
    let ``doctor selected non-local check does not open local-state database`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                writeGraceConfig root "http://127.0.0.1:5000"
                |> ignore

                let dbPath = localStateDbPath root
                File.WriteAllText(dbPath, "not a sqlite database")
                let dbBefore = snapshotFile dbPath

                withLocalStateDbOpenTrace root (fun tracePath ->
                    /// Verifies that the CLI doctor scenario exits with the expected process status.
                    let exitCode, standardOut, standardError =
                        runWithCapturedStdoutAndStderr [| "--output"
                                                          "Json"
                                                          "doctor"
                                                          "--check"
                                                          "config.file.parse" |]

                    exitCode |> should equal 0

                    use document = assertCleanJsonOutput standardOut standardError

                    let checks =
                        document
                            .RootElement
                            .GetProperty("ReturnValue")
                            .GetProperty("Checks")

                    checks.GetArrayLength() |> should equal 1

                    checks[ 0 ].GetProperty("Id").GetString()
                    |> should equal "config.file.parse"

                    checks[ 0 ].GetProperty("Status").GetString()
                    |> should equal "Ok"

                    readTrace tracePath |> should equal String.Empty
                    snapshotFile dbPath |> should equal dbBefore)))

    /// Verifies that doctor selected local state check opens local state database read only.
    [<Test>]
    let ``doctor selected local-state check opens local-state database read-only`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                ensureValidLocalStateDb root

                withLocalStateDbOpenTrace root (fun tracePath ->
                    /// Verifies that the CLI doctor scenario exits with the expected process status.
                    let exitCode, standardOut, standardError =
                        runWithCapturedStdoutAndStderr [| "--output"
                                                          "Json"
                                                          "doctor"
                                                          "--check"
                                                          "state.db.schema-version" |]

                    exitCode |> should equal 0

                    use document = assertCleanJsonOutput standardOut standardError

                    let checks =
                        document
                            .RootElement
                            .GetProperty("ReturnValue")
                            .GetProperty("Checks")

                    checks.GetArrayLength() |> should equal 1

                    checks[ 0 ].GetProperty("Status").GetString()
                    |> should equal "Ok"

                    readTrace tracePath
                    |> should contain "openReadOnlyConnection starting")))

    /// Verifies that doctor local state checks inspect checkpointed wal database without creating missing sidecars.
    [<Test>]
    let ``doctor local-state checks inspect checkpointed wal database without creating missing sidecars`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                ensureValidLocalStateDb root
                SqliteConnection.ClearAllPools()

                let dbPath = localStateDbPath root
                let walPath = dbPath + "-wal"
                let shmPath = dbPath + "-shm"

                for sidecar in [| walPath; shmPath |] do
                    if File.Exists(sidecar) then File.Delete(sidecar)

                File.Exists(walPath) |> should equal false
                File.Exists(shmPath) |> should equal false

                let dbBefore = snapshotFile dbPath

                withLocalStateDbOpenTrace root (fun tracePath ->
                    /// Verifies that the CLI doctor scenario exits with the expected process status.
                    let exitCode, standardOut, standardError =
                        runWithCapturedStdoutAndStderr [| "--output"
                                                          "Json"
                                                          "doctor"
                                                          "--check"
                                                          "state.db.read-only-open"
                                                          "--check"
                                                          "state.db.schema-version"
                                                          "--check"
                                                          "state.db.integrity-check"
                                                          "--check"
                                                          "object-cache.index-readable" |]

                    exitCode |> should equal 0

                    use document = assertCleanJsonOutput standardOut standardError

                    let checks =
                        document
                            .RootElement
                            .GetProperty("ReturnValue")
                            .GetProperty("Checks")

                    for checkId in
                        [|
                            "state.db.read-only-open"
                            "state.db.schema-version"
                            "state.db.integrity-check"
                            "object-cache.index-readable"
                        |] do
                        (findCheckById checks checkId)
                            .GetProperty("Status")
                            .GetString()
                        |> should equal "Ok"

                    readTrace tracePath
                    |> should contain "openReadOnlyConnection starting"

                    snapshotFile dbPath |> should equal dbBefore
                    File.Exists(walPath) |> should equal false
                    File.Exists(shmPath) |> should equal false)))

    /// Verifies that doctor default run opens local state database and includes local state checks.
    [<Test>]
    let ``doctor default run opens local-state database and includes local-state checks`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                ensureValidLocalStateDb root

                withLocalStateDbOpenTrace root (fun tracePath ->
                    /// Verifies that the CLI doctor scenario exits with the expected process status.
                    let exitCode, standardOut, standardError =
                        runWithCapturedStdoutAndStderr [| "--output"
                                                          "Json"
                                                          "doctor" |]

                    exitCode |> should equal 0

                    use document = assertCleanJsonOutput standardOut standardError

                    let checks =
                        document
                            .RootElement
                            .GetProperty("ReturnValue")
                            .GetProperty("Checks")

                    for checkId in localStateCheckIds do
                        (findCheckById checks checkId)
                            .GetProperty("Id")
                            .GetString()
                        |> should equal checkId

                    readTrace tracePath
                    |> should contain "openReadOnlyConnection starting")))

    /// Verifies that doctor local state valid database reports read only metadata checks.
    [<Test>]
    let ``doctor local-state valid database reports read-only metadata checks`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                ensureValidLocalStateDb root

                let args =
                    [|
                        yield "--output"
                        yield "Json"
                        yield "doctor"

                        for checkId in localStateCheckIds do
                            yield "--check"
                            yield checkId
                    |]

                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError = runWithCapturedStdoutAndStderr args

                exitCode |> should equal 0

                use document = assertCleanJsonOutput standardOut standardError

                let checks =
                    document
                        .RootElement
                        .GetProperty("ReturnValue")
                        .GetProperty("Checks")

                checks.GetArrayLength()
                |> should equal localStateCheckIds.Length

                for checkId in localStateCheckIds do
                    let check = findCheckById checks checkId

                    check.GetProperty("Status").GetString()
                    |> should equal "Ok"

                (findCheckById checks "state.db.schema-version")
                    .GetProperty("Summary")
                    .GetString()
                |> should contain "schema_version is 6"

                (findCheckById checks "object-cache.index-readable")
                    .GetProperty("Summary")
                    .GetString()
                |> should contain "without mutation"))

    /// Verifies that doctor reports malformed Watch journal table shape without repairing local state.
    [<Test>]
    let ``doctor local-state reports malformed watch journal shape`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                ensureValidLocalStateDb root

                let dbPath = localStateDbPath root

                do
                    use connection = openRawConnection dbPath
                    executeNonQuery connection "DROP TABLE watch_journal;"
                    executeNonQuery connection "CREATE TABLE watch_journal (sequence INTEGER PRIMARY KEY, created_at_unix_ticks INTEGER NOT NULL);"

                let beforeRoot = snapshotFiles root

                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "doctor"
                                                      "--check"
                                                      "state.db.watch-journal" |]

                exitCode |> should equal 1

                use document = assertCleanJsonOutput standardOut standardError

                let check =
                    document
                        .RootElement
                        .GetProperty("ReturnValue")
                        .GetProperty("Checks")[0]

                check.GetProperty("Status").GetString()
                |> should equal "Failed"

                check.GetProperty("Summary").GetString()
                |> should contain "Watch journal table shape is invalid"

                snapshotFiles root |> should equal beforeRoot))

    /// Verifies that doctor reports missing Watch recovery metadata when journal rows exist.
    [<Test>]
    let ``doctor local-state reports missing watch journal metadata with rows`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                ensureValidLocalStateDb root

                let dbPath = localStateDbPath root

                do
                    use connection = openRawConnection dbPath

                    executeNonQuery
                        connection
                        "INSERT INTO watch_journal (sequence, created_at_unix_ticks, difference_type, entry_type, relative_path) VALUES (1, 1, 'Change', 'File', 'doctor-one.txt');"

                    executeNonQuery connection "DELETE FROM meta WHERE key = 'AppliedThroughSequence';"

                let beforeRoot = snapshotFiles root

                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "doctor"
                                                      "--check"
                                                      "state.db.watch-journal" |]

                exitCode |> should equal 1

                use document = assertCleanJsonOutput standardOut standardError

                let check =
                    document
                        .RootElement
                        .GetProperty("ReturnValue")
                        .GetProperty("Checks")[0]

                check.GetProperty("Status").GetString()
                |> should equal "Failed"

                check.GetProperty("Summary").GetString()
                |> should contain "applied-through metadata is missing"

                snapshotFiles root |> should equal beforeRoot))

    /// Verifies that doctor reports malformed Watch journal allocation without repairing local state.
    [<Test>]
    let ``doctor local-state reports malformed watch journal allocation without rows`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                ensureValidLocalStateDb root

                let dbPath = localStateDbPath root

                do
                    use connection = openRawConnection dbPath
                    executeNonQuery connection "DELETE FROM meta WHERE key = 'AppliedThroughSequence';"
                    executeNonQuery connection "INSERT INTO sqlite_sequence (name, seq) VALUES ('watch_journal', 'not-a-number');"

                let beforeRoot = snapshotFiles root

                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "doctor"
                                                      "--check"
                                                      "state.db.watch-journal" |]

                exitCode |> should equal 1

                use document = assertCleanJsonOutput standardOut standardError

                let check =
                    document
                        .RootElement
                        .GetProperty("ReturnValue")
                        .GetProperty("Checks")[0]

                check.GetProperty("Status").GetString()
                |> should equal "Failed"

                check.GetProperty("Summary").GetString()
                |> should contain "malformed"

                snapshotFiles root |> should equal beforeRoot))

    /// Verifies that doctor reports duplicate Watch recovery metadata without repairing local state.
    [<Test>]
    let ``doctor local-state reports duplicate watch journal metadata`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                ensureValidLocalStateDb root

                let dbPath = localStateDbPath root

                do
                    use connection = openRawConnection dbPath
                    executeNonQuery connection "DROP TABLE meta;"
                    executeNonQuery connection "CREATE TABLE meta (key TEXT NOT NULL, value TEXT NOT NULL);"
                    executeNonQuery connection "INSERT INTO meta (key, value) VALUES ('schema_version', '6');"

                    executeNonQuery connection "INSERT INTO meta (key, value) VALUES ('AppliedThroughSequence', '0');"

                    executeNonQuery connection "INSERT INTO meta (key, value) VALUES ('AppliedThroughSequence', '1');"

                let beforeRoot = snapshotFiles root

                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "doctor"
                                                      "--check"
                                                      "state.db.watch-journal" |]

                exitCode |> should equal 1

                use document = assertCleanJsonOutput standardOut standardError

                let check =
                    document
                        .RootElement
                        .GetProperty("ReturnValue")
                        .GetProperty("Checks")[0]

                check.GetProperty("Status").GetString()
                |> should equal "Failed"

                check.GetProperty("Summary").GetString()
                |> should contain "malformed"

                snapshotFiles root |> should equal beforeRoot))

    /// Verifies that doctor reports meta tables that cannot preserve one row per key without repairing local state.
    [<Test>]
    let ``doctor local-state reports malformed meta key uniqueness`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                ensureValidLocalStateDb root

                let dbPath = localStateDbPath root

                do
                    use connection = openRawConnection dbPath
                    executeNonQuery connection "DROP TABLE meta;"
                    executeNonQuery connection "CREATE TABLE meta (key TEXT NOT NULL, value TEXT NOT NULL);"
                    executeNonQuery connection "INSERT INTO meta (key, value) VALUES ('schema_version', '6');"

                    executeNonQuery connection "INSERT INTO meta (key, value) VALUES ('AppliedThroughSequence', '0');"

                let beforeRoot = snapshotFiles root

                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "doctor"
                                                      "--check"
                                                      "state.db.watch-journal" |]

                exitCode |> should equal 1

                use document = assertCleanJsonOutput standardOut standardError

                let check =
                    document
                        .RootElement
                        .GetProperty("ReturnValue")
                        .GetProperty("Checks")[0]

                check.GetProperty("Status").GetString()
                |> should equal "Failed"

                check.GetProperty("Summary").GetString()
                |> should contain "malformed"

                snapshotFiles root |> should equal beforeRoot))

    /// Verifies that doctor reports meta tables whose hidden constraints would ignore default Watch metadata writes.
    [<Test>]
    let ``doctor local-state reports constrained meta default insert shape`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                ensureValidLocalStateDb root

                let dbPath = localStateDbPath root

                do
                    use connection = openRawConnection dbPath
                    executeNonQuery connection "ALTER TABLE meta RENAME TO meta_valid;"

                    executeNonQuery
                        connection
                        "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL, required_marker TEXT NOT NULL CHECK (required_marker = 'seeded'));"

                    executeNonQuery
                        connection
                        "INSERT INTO meta (key, value, required_marker) SELECT key, value, 'seeded' FROM meta_valid WHERE key <> 'AppliedThroughSequence';"

                    executeNonQuery connection "DROP TABLE meta_valid;"

                let beforeRoot = snapshotFiles root

                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "doctor"
                                                      "--check"
                                                      "state.db.watch-journal" |]

                exitCode |> should equal 1

                use document = assertCleanJsonOutput standardOut standardError

                let check =
                    document
                        .RootElement
                        .GetProperty("ReturnValue")
                        .GetProperty("Checks")[0]

                check.GetProperty("Status").GetString()
                |> should equal "Failed"

                check.GetProperty("Summary").GetString()
                |> should contain "malformed"

                snapshotFiles root |> should equal beforeRoot))

    /// Verifies that doctor reports stale Watch journal allocation without repairing local state.
    [<Test>]
    let ``doctor local-state reports stale watch journal allocation with rows`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                ensureValidLocalStateDb root

                let dbPath = localStateDbPath root

                do
                    use connection = openRawConnection dbPath

                    executeNonQuery
                        connection
                        "INSERT INTO watch_journal (sequence, created_at_unix_ticks, difference_type, entry_type, relative_path) VALUES (1, 1, 'Change', 'File', 'doctor-one.txt');"

                    executeNonQuery
                        connection
                        "INSERT INTO watch_journal (sequence, created_at_unix_ticks, difference_type, entry_type, relative_path) VALUES (2, 2, 'Delete', 'File', 'doctor-two.txt');"

                    executeNonQuery connection "UPDATE sqlite_sequence SET seq = 1 WHERE name = 'watch_journal';"

                let beforeRoot = snapshotFiles root

                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "doctor"
                                                      "--check"
                                                      "state.db.watch-journal" |]

                exitCode |> should equal 1

                use document = assertCleanJsonOutput standardOut standardError

                let check =
                    document
                        .RootElement
                        .GetProperty("ReturnValue")
                        .GetProperty("Checks")[0]

                check.GetProperty("Status").GetString()
                |> should equal "Failed"

                check.GetProperty("Summary").GetString()
                |> should contain "applied-through metadata is missing"

                snapshotFiles root |> should equal beforeRoot))

    /// Verifies that doctor missing local state parent reports check result without creating files.
    [<Test>]
    let ``doctor missing local-state parent reports check result without creating files`` () =
        withTempDir (fun root ->
            let beforeRoot = snapshotFiles root

            /// Verifies that the CLI doctor scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "doctor"
                                                  "--check"
                                                  "state.db.file-present"
                                                  "--check"
                                                  "state.db.read-only-open" |]

            exitCode |> should equal 0

            use document = assertCleanJsonOutput standardOut standardError

            let checks =
                document
                    .RootElement
                    .GetProperty("ReturnValue")
                    .GetProperty("Checks")

            (findCheckById checks "state.db.file-present")
                .GetProperty("Status")
                .GetString()
            |> should equal "Warning"

            (findCheckById checks "state.db.read-only-open")
                .GetProperty("Status")
                .GetString()
            |> should equal "Skipped"

            Directory.Exists(Path.Combine(root, Constants.GraceConfigDirectory))
            |> should equal false

            snapshotFiles root |> should equal beforeRoot)

    /// Verifies that doctor missing local state database warns without creating database.
    [<Test>]
    let ``doctor missing local-state database warns without creating database`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                writeGraceConfig root "http://127.0.0.1:5000"
                |> ignore

                let dbPath = localStateDbPath root
                File.Exists(dbPath) |> should equal false
                let beforeRoot = snapshotFiles root

                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "doctor"
                                                      "--check"
                                                      "state.db.file-present"
                                                      "--check"
                                                      "state.db.schema-version" |]

                exitCode |> should equal 0

                use document = assertCleanJsonOutput standardOut standardError

                let checks =
                    document
                        .RootElement
                        .GetProperty("ReturnValue")
                        .GetProperty("Checks")

                (findCheckById checks "state.db.file-present")
                    .GetProperty("Status")
                    .GetString()
                |> should equal "Warning"

                (findCheckById checks "state.db.schema-version")
                    .GetProperty("Status")
                    .GetString()
                |> should equal "Skipped"

                File.Exists(dbPath) |> should equal false
                snapshotFiles root |> should equal beforeRoot))

    /// Verifies that doctor malformed config from nested directory inspects discovered local state database.
    [<Test>]
    let ``doctor malformed config from nested directory inspects discovered local-state database`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                ensureValidLocalStateDb root

                let graceDir = Path.Combine(root, Constants.GraceConfigDirectory)
                File.WriteAllText(Path.Combine(graceDir, Constants.GraceConfigFileName), "{ not json")

                let nested = Path.Combine(root, "src", "nested")
                Directory.CreateDirectory(nested) |> ignore
                let originalDir = Environment.CurrentDirectory

                try
                    Environment.CurrentDirectory <- nested
                    let repoDbPath = localStateDbPath root
                    let cwdDbPath = Path.Combine(nested, Constants.GraceConfigDirectory, Constants.GraceLocalStateDbFileName)

                    withLocalStateDbOpenTrace root (fun tracePath ->
                        /// Verifies that the CLI doctor scenario exits with the expected process status.
                        let exitCode, standardOut, standardError =
                            runWithCapturedStdoutAndStderr [| "--output"
                                                              "Json"
                                                              "doctor"
                                                              "--check"
                                                              "state.db.schema-version" |]

                        exitCode |> should equal 0

                        use document = assertCleanJsonOutput standardOut standardError

                        let checks =
                            document
                                .RootElement
                                .GetProperty("ReturnValue")
                                .GetProperty("Checks")

                        checks.GetArrayLength() |> should equal 1

                        checks[ 0 ].GetProperty("Status").GetString()
                        |> should equal "Ok"

                        checks[ 0 ].GetProperty("Summary").GetString()
                        |> should contain "schema_version is 6"

                        let trace = readTrace tracePath
                        trace |> should contain repoDbPath
                        trace |> should not' (contain cwdDbPath)

                        File.Exists(cwdDbPath) |> should equal false

                        Directory.Exists(Path.GetDirectoryName(cwdDbPath))
                        |> should equal false)
                finally
                    Environment.CurrentDirectory <- originalDir))

    /// Verifies that doctor local state database path occupied by directory reports failure without mutation.
    [<Test>]
    let ``doctor local-state database path occupied by directory reports failure without mutation`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                writeGraceConfig root "http://127.0.0.1:5000"
                |> ignore

                let dbPath = localStateDbPath root
                Directory.CreateDirectory(dbPath) |> ignore
                let beforeRoot = snapshotFiles root

                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "doctor"
                                                      "--check"
                                                      "state.db.file-present"
                                                      "--check"
                                                      "state.db.read-only-open" |]

                exitCode |> should equal 1

                use document = assertCleanJsonOutput standardOut standardError

                let checks =
                    document
                        .RootElement
                        .GetProperty("ReturnValue")
                        .GetProperty("Checks")

                (findCheckById checks "state.db.file-present")
                    .GetProperty("Status")
                    .GetString()
                |> should equal "Failed"

                (findCheckById checks "state.db.read-only-open")
                    .GetProperty("Status")
                    .GetString()
                |> should equal "Failed"

                Directory.Exists(dbPath) |> should equal true
                snapshotFiles root |> should equal beforeRoot))

    /// Verifies that doctor corrupt local state database reports failure without moving bytes or sidecars.
    [<Test>]
    let ``doctor corrupt local-state database reports failure without moving bytes or sidecars`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                writeGraceConfig root "http://127.0.0.1:5000"
                |> ignore

                let dbPath = localStateDbPath root
                let bytes = Encoding.UTF8.GetBytes("not a sqlite database")
                File.WriteAllBytes(dbPath, bytes)

                let oldTime = DateTime.UtcNow.AddDays(-3.0)

                let sidecars =
                    [| "-journal"; "-wal"; "-shm" |]
                    |> Array.map (fun suffix -> dbPath + suffix)

                for sidecar in sidecars do
                    File.WriteAllText(sidecar, "sentinel")
                    File.SetLastWriteTimeUtc(sidecar, oldTime)

                let dbBefore = snapshotFile dbPath
                let sidecarsBefore = sidecars |> Array.map snapshotFile
                let corruptBefore = getCorruptBackups root |> Array.length

                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Verbose"
                                                      "doctor"
                                                      "--check"
                                                      "state.db.read-only-open"
                                                      "--select"
                                                      "Status" |]

                exitCode |> should equal 1
                standardError |> should equal String.Empty
                standardOut.Trim() |> should equal "\"Failed\""

                standardOut
                |> should not' (contain "Grace doctor")

                standardOut
                |> should not' (contain "not a sqlite database")

                snapshotFile dbPath |> should equal dbBefore

                sidecars
                |> Array.map snapshotFile
                |> should equal sidecarsBefore

                let corruptAfter = getCorruptBackups root |> Array.length
                corruptAfter |> should equal corruptBefore))

    /// Verifies that doctor schema mismatch fails without corrupt backup.
    [<Test>]
    let ``doctor schema mismatch fails without corrupt backup`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                writeGraceConfig root "http://127.0.0.1:5000"
                |> ignore

                let dbPath = localStateDbPath root
                seedSchemaVersionOnly dbPath "0"
                let dbBefore = snapshotFile dbPath
                let corruptBefore = getCorruptBackups root |> Array.length

                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "doctor"
                                                      "--check"
                                                      "state.db.schema-version"
                                                      "--check"
                                                      "state.db.required-tables"
                                                      "--check"
                                                      "object-cache.index-readable" |]

                exitCode |> should equal 1

                use document = assertCleanJsonOutput standardOut standardError

                let checks =
                    document
                        .RootElement
                        .GetProperty("ReturnValue")
                        .GetProperty("Checks")

                (findCheckById checks "state.db.schema-version")
                    .GetProperty("Status")
                    .GetString()
                |> should equal "Failed"

                (findCheckById checks "state.db.required-tables")
                    .GetProperty("Summary")
                    .GetString()
                |> should contain "status_meta"

                (findCheckById checks "object-cache.index-readable")
                    .GetProperty("Status")
                    .GetString()
                |> should equal "Failed"

                snapshotFile dbPath |> should equal dbBefore

                let corruptAfter = getCorruptBackups root |> Array.length
                corruptAfter |> should equal corruptBefore))

    /// Verifies that doctor local state exact check selections are not filtered out.
    [<Test>]
    let ``doctor local-state exact check selections are not filtered out`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                ensureValidLocalStateDb root

                for checkId in localStateCheckIds do
                    /// Verifies that the CLI doctor scenario exits with the expected process status.
                    let exitCode, standardOut, standardError =
                        runWithCapturedStdoutAndStderr [| "--output"
                                                          "Json"
                                                          "doctor"
                                                          "--check"
                                                          checkId |]

                    exitCode |> should equal 0

                    use document = assertCleanJsonOutput standardOut standardError

                    let checks =
                        document
                            .RootElement
                            .GetProperty("ReturnValue")
                            .GetProperty("Checks")

                    checks.GetArrayLength() |> should equal 1

                    checks[ 0 ].GetProperty("Id").GetString()
                    |> should equal checkId))

    /// Verifies that doctor local state successful diagnostics emit no output for quiet output modes.
    [<TestCase("Silent")>]
    [<TestCase("Minimal")>]
    let ``doctor local-state successful diagnostics emit no output for quiet output modes`` output =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                ensureValidLocalStateDb root

                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      output
                                                      "doctor"
                                                      "--check"
                                                      "state.db.schema-version" |]

                exitCode |> should equal 0
                standardOut |> should equal String.Empty
                standardError |> should equal String.Empty))

    /// Verifies that doctor verbose select returns clean scalar for passing config check.
    [<Test>]
    let ``doctor verbose select returns clean scalar for passing config check`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                writeGraceConfig root "http://127.0.0.1:5000"
                |> ignore

                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Verbose"
                                                      "doctor"
                                                      "--check"
                                                      "config.file.parse"
                                                      "--select"
                                                      "Status" |]

                exitCode |> should equal 0
                standardError |> should equal String.Empty
                standardOut.Trim() |> should equal "\"Ok\""

                standardOut
                |> should not' (contain "Grace doctor")

                standardOut |> should not' (contain "EventTime")))

    /// Verifies that doctor reports config server uri mismatch and ignore counts without mutating files.
    [<Test>]
    let ``doctor reports config server uri mismatch and ignore counts without mutating files`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun home ->
                writeGraceConfig root "http://127.0.0.1:5000"
                |> ignore

                File.WriteAllText(
                    Path.Combine(root, Constants.GraceIgnoreFileName),
                    """
# comment
bin/
obj/
*.tmp

notes.txt # inline comment
"""
                )

                let beforeRoot = snapshotFiles root
                let beforeHome = snapshotFiles home

                withEnv Constants.EnvironmentVariables.GraceServerUri (Some "http://127.0.0.1:6000") (fun () ->
                    /// Verifies that the CLI doctor scenario exits with the expected process status.
                    let exitCode, standardOut, standardError =
                        runWithCapturedStdoutAndStderr [| "--output"
                                                          "Json"
                                                          "doctor"
                                                          "--check"
                                                          "config.file.parse"
                                                          "--check"
                                                          "server-uri.consistency"
                                                          "--check"
                                                          "ignore.entries.parse" |]

                    exitCode |> should equal 0

                    use document = assertCleanJsonOutput standardOut standardError

                    let checks =
                        document
                            .RootElement
                            .GetProperty("ReturnValue")
                            .GetProperty("Checks")

                    checks.GetArrayLength() |> should equal 3

                    checks[ 0 ].GetProperty("Status").GetString()
                    |> should equal "Ok"

                    checks[ 1 ].GetProperty("Status").GetString()
                    |> should equal "Warning"

                    checks[ 2 ].GetProperty("Summary").GetString()
                    |> should contain "4 active entries"

                    checks[ 2 ].GetProperty("Summary").GetString()
                    |> should contain "2 file patterns and 2 directory patterns"

                    snapshotFiles root |> should equal beforeRoot
                    snapshotFiles home |> should equal beforeHome

                    File.Exists(Path.Combine(root, Constants.GraceConfigDirectory, Constants.GraceLocalStateDbFileName))
                    |> should equal false)))

    /// Verifies that doctor malformed config fails parse and selected skipped dependent output stays clean.
    [<Test>]
    let ``doctor malformed config fails parse and selected skipped dependent output stays clean`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun home ->
                let graceDir = Path.Combine(root, Constants.GraceConfigDirectory)
                Directory.CreateDirectory(graceDir) |> ignore
                File.WriteAllText(Path.Combine(graceDir, Constants.GraceConfigFileName), "{ not json")

                let beforeRoot = snapshotFiles root
                let beforeHome = snapshotFiles home

                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Verbose"
                                                      "doctor"
                                                      "--check"
                                                      "config.file.parse"
                                                      "--check"
                                                      "ignore.entries.parse"
                                                      "--select"
                                                      "Checks" |]

                exitCode |> should equal 1
                standardError |> should equal String.Empty

                standardOut
                    .TrimStart()
                    .StartsWith("[", StringComparison.Ordinal)
                |> should equal true

                standardOut
                |> should not' (contain "Grace doctor")

                standardOut |> should not' (contain "EventTime")

                use document = JsonDocument.Parse(standardOut)
                let checks = document.RootElement

                checks.GetArrayLength() |> should equal 2

                checks[ 0 ].GetProperty("Status").GetString()
                |> should equal "Failed"

                checks[ 1 ].GetProperty("Status").GetString()
                |> should equal "Skipped"

                snapshotFiles root |> should equal beforeRoot
                snapshotFiles home |> should equal beforeHome))

    /// Verifies that doctor treats unreadable config file as parse failure instead of missing config.
    [<Test>]
    let ``doctor treats unreadable config file as parse failure instead of missing config`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                let configPath = writeGraceConfig root "http://127.0.0.1:5000"

                use _lock = openExclusiveReadLock configPath

                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "doctor"
                                                      "--check"
                                                      "config.file.discover"
                                                      "--check"
                                                      "config.file.parse" |]

                exitCode |> should equal 1

                use document = assertCleanJsonOutput standardOut standardError

                let checks =
                    document
                        .RootElement
                        .GetProperty("ReturnValue")
                        .GetProperty("Checks")

                checks.GetArrayLength() |> should equal 2

                checks[ 0 ].GetProperty("Id").GetString()
                |> should equal "config.file.discover"

                checks[ 0 ].GetProperty("Status").GetString()
                |> should equal "Ok"

                checks[ 0 ].GetProperty("Summary").GetString()
                |> should contain "parsing is reported"

                checks[ 1 ].GetProperty("Id").GetString()
                |> should equal "config.file.parse"

                checks[ 1 ].GetProperty("Status").GetString()
                |> should equal "Failed"

                checks[ 1 ].GetProperty("Summary").GetString()
                |> should contain "Could not parse"))

    /// Verifies that doctor carries unreadable graceignore into ignore parse result without affecting config parse.
    [<Test>]
    let ``doctor carries unreadable graceignore into ignore parse result without affecting config parse`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                writeGraceConfig root "http://127.0.0.1:5000"
                |> ignore

                let graceIgnorePath = Path.Combine(root, Constants.GraceIgnoreFileName)
                File.WriteAllText(graceIgnorePath, "bin/")

                use _lock = openExclusiveReadLock graceIgnorePath

                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "doctor"
                                                      "--check"
                                                      "config.file.parse"
                                                      "--check"
                                                      "ignore.entries.parse" |]

                exitCode |> should equal 1

                use document = assertCleanJsonOutput standardOut standardError

                let checks =
                    document
                        .RootElement
                        .GetProperty("ReturnValue")
                        .GetProperty("Checks")

                checks.GetArrayLength() |> should equal 2

                checks[ 0 ].GetProperty("Id").GetString()
                |> should equal "config.file.parse"

                checks[ 0 ].GetProperty("Status").GetString()
                |> should equal "Ok"

                checks[ 1 ].GetProperty("Id").GetString()
                |> should equal "ignore.entries.parse"

                checks[ 1 ].GetProperty("Status").GetString()
                |> should equal "Failed"

                checks[ 1 ].GetProperty("Summary").GetString()
                |> should contain "Could not parse"))

    /// Verifies that doctor missing user config reports warning without creating user grace directory.
    [<Test>]
    let ``doctor missing user config reports warning without creating user grace directory`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun home ->
                withUserConfigTemporarilyMissing (fun userConfigPath ->
                    let beforeHome = snapshotFiles home
                    let beforeUserConfigExists = File.Exists(userConfigPath)

                    /// Verifies that the CLI doctor scenario exits with the expected process status.
                    let exitCode, standardOut, standardError =
                        runWithCapturedStdoutAndStderr [| "--output"
                                                          "Json"
                                                          "doctor"
                                                          "--check"
                                                          "user-config.file.discover" |]

                    exitCode |> should equal 0

                    use document = assertCleanJsonOutput standardOut standardError

                    let check =
                        document
                            .RootElement
                            .GetProperty("ReturnValue")
                            .GetProperty("Checks")[0]

                    check.GetProperty("Status").GetString()
                    |> should equal "Warning"

                    check.GetProperty("Summary").GetString()
                    |> should contain "no file was created"

                    snapshotFiles home |> should equal beforeHome

                    File.Exists(userConfigPath)
                    |> should equal beforeUserConfigExists

                    Directory.Exists(Path.Combine(home, Constants.GraceConfigDirectory))
                    |> should equal false)))

    /// Verifies that doctor invalid check emits grace error in json mode.
    [<Test>]
    let ``doctor invalid check emits GraceError in json mode`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI doctor scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "doctor"
                                                  "--check"
                                                  "unknown, configuration, also-unknown" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            use document = parseJsonOutput standardOut
            let rootElement = document.RootElement

            rootElement.GetProperty("Error").GetString()
            |> should contain "Unknown doctor check token"

            /// Tracks return Value changes so this scenario can assert the resulting side effect explicitly.
            let mutable returnValue = Unchecked.defaultof<JsonElement>

            rootElement.TryGetProperty("ReturnValue", &returnValue)
            |> should equal false)

    /// Verifies that doctor check followed by option shaped token emits grace error.
    [<Test>]
    let ``doctor check followed by option-shaped token emits GraceError`` () =
        withTempDir (fun _ ->
            /// Verifies that the CLI doctor scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "doctor"
                                                  "--check"
                                                  "--strict" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            use document = parseJsonOutput standardOut
            let rootElement = document.RootElement

            rootElement.GetProperty("Error").GetString()
            |> should contain "Unknown doctor check token: --strict"

            /// Tracks return Value changes so this scenario can assert the resulting side effect explicitly.
            let mutable returnValue = Unchecked.defaultof<JsonElement>

            rootElement.TryGetProperty("ReturnValue", &returnValue)
            |> should equal false)

    /// Verifies that doctor schema and examples work without config.
    [<Test>]
    let ``doctor schema and examples work without config`` () =
        withTempDir (fun root ->
            for args, expectedKind in
                [
                    [| "doctor"; "--schema" |], "schema"
                    [| "doctor"; "--examples" |], "examples"
                ] do
                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError = runWithCapturedStdoutAndStderr args

                exitCode |> should equal 0
                standardError |> should equal String.Empty

                use document = parseJsonOutput standardOut
                let rootElement = document.RootElement

                rootElement.GetProperty("Kind").GetString()
                |> should equal expectedKind

                rootElement
                    .GetProperty("Command")
                    .GetProperty("Id")
                    .GetString()
                |> should equal "doctor"

            Directory.Exists(Path.Combine(root, ".grace"))
            |> should equal false)

    /// Verifies that doctor suppresses successful human output for quiet output modes.
    [<TestCase("Silent")>]
    [<TestCase("Minimal")>]
    let ``doctor suppresses successful human output for quiet output modes`` output =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                writeGraceConfig root "http://127.0.0.1:5000"
                |> ignore

                /// Verifies that the CLI doctor scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      output
                                                      "doctor"
                                                      "--check"
                                                      "config.file.parse" |]

                exitCode |> should equal 0
                standardOut |> should equal String.Empty
                standardError |> should equal String.Empty

                File.Exists(Path.Combine(root, Constants.GraceConfigDirectory, Constants.GraceLocalStateDbFileName))
                |> should equal false))

    /// Verifies that doctor select projects return value property without config.
    [<TestCase("Json")>]
    [<TestCase("Verbose")>]
    let ``doctor select projects return value property without config`` output =
        withTempDir (fun root ->
            /// Verifies that the CLI doctor scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  output
                                                  "doctor"
                                                  "--select"
                                                  "Status" |]

            exitCode |> should equal 0
            standardError |> should equal String.Empty
            standardOut.Trim() |> should equal "\"Warning\""

            Directory.Exists(Path.Combine(root, ".grace"))
            |> should equal false)

    /// Verifies that doctor select missing property returns projection failure exit code.
    [<Test>]
    let ``doctor select missing property returns projection failure exit code`` () =
        withTempDir (fun root ->
            /// Verifies that the CLI doctor scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "doctor"
                                                  "--select"
                                                  "NoSuchProperty" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            use document = parseJsonOutput standardOut

            document
                .RootElement
                .GetProperty("Error")
                .GetString()
            |> should contain "NoSuchProperty"

            Directory.Exists(Path.Combine(root, ".grace"))
            |> should equal false)

    /// Verifies that doctor select rejects envelope qualified path before config.
    [<Test>]
    let ``doctor select rejects envelope-qualified path before config`` () =
        withTempDir (fun root ->
            /// Verifies that the CLI doctor scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "doctor"
                                                  "--select"
                                                  "ReturnValue.Status" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            use document = parseJsonOutput standardOut

            document
                .RootElement
                .GetProperty("Error")
                .GetString()
            |> should contain "cannot project 'ReturnValue'"

            Directory.Exists(Path.Combine(root, ".grace"))
            |> should equal false)

    /// Verifies that doctor diagnostic exit codes map warning and failure reports.
    [<Test>]
    let ``doctor diagnostic exit codes map warning and failure reports`` () =
        let warningReport =
            Doctor.createReportForChecks false true false Doctor.catalog
            |> Doctor.withStatus "Warning"

        Doctor.diagnosticExitCode false warningReport
        |> should equal 0

        Doctor.diagnosticExitCode true warningReport
        |> should equal 1

        let failedReport = warningReport |> Doctor.withStatus "Failed"

        Doctor.diagnosticExitCode false failedReport
        |> should equal 1
