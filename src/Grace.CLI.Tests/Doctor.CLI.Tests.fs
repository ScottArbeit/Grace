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
open System.Text
open System.Text.Json

[<NonParallelizable>]
module DoctorCliTests =

    let private setAnsiConsoleOutput (writer: TextWriter) =
        let settings = AnsiConsoleSettings()
        settings.Out <- AnsiConsoleOutput(writer)
        AnsiConsole.Console <- AnsiConsole.Create(settings)

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

    let private withEnv (name: string) (value: string option) (action: unit -> unit) =
        let original = Environment.GetEnvironmentVariable(name)

        match value with
        | Some v -> Environment.SetEnvironmentVariable(name, v)
        | None -> Environment.SetEnvironmentVariable(name, null)

        try
            action ()
        finally
            Environment.SetEnvironmentVariable(name, original)

    let private withIsolatedHome (root: string) (action: string -> unit) =
        let home = Path.Combine(root, "home")
        Directory.CreateDirectory(home) |> ignore

        withEnv "USERPROFILE" (Some home) (fun () ->
            withEnv "HOME" (Some home) (fun () -> withEnv Constants.EnvironmentVariables.GraceServerUri None (fun () -> action home)))

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

    let private localStateDbPath root = Path.Combine(root, Constants.GraceConfigDirectory, Constants.GraceLocalStateDbFileName)

    let private getCorruptBackups root =
        let graceDir = Path.Combine(root, Constants.GraceConfigDirectory)

        if Directory.Exists(graceDir) then
            Directory.GetFiles(graceDir, "grace-local.corrupt.*.db")
        else
            Array.empty

    let private ensureValidLocalStateDb root =
        writeGraceConfig root "http://127.0.0.1:5000"
        |> ignore

        LocalStateDb
            .ensureDbInitialized(localStateDbPath root)
            .GetAwaiter()
            .GetResult()

    let private openRawConnection (dbPath: string) =
        let connection = new SqliteConnection($"Data Source={dbPath}")
        connection.Open()
        connection

    let private executeNonQuery (connection: SqliteConnection) (sql: string) =
        use cmd = connection.CreateCommand()
        cmd.CommandText <- sql
        cmd.ExecuteNonQuery() |> ignore

    let private seedSchemaVersionOnly (dbPath: string) (schemaVersion: string) =
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath))
        |> ignore

        use connection = openRawConnection dbPath
        executeNonQuery connection "CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);"
        executeNonQuery connection $"INSERT OR REPLACE INTO meta (key, value) VALUES ('schema_version', '{schemaVersion}');"

    let private snapshotFile (path: string) =
        if File.Exists(path) then
            use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
            use reader = new BinaryReader(stream)
            Some(Convert.ToBase64String(reader.ReadBytes(int stream.Length)), File.GetLastWriteTimeUtc(path))
        else
            None

    let private snapshotFiles root =
        if Directory.Exists(root) then
            Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            |> Array.map (fun path -> Path.GetRelativePath(root, path), File.GetLastWriteTimeUtc(path), FileInfo(path).Length)
            |> Array.sortBy (fun (relativePath, _, _) -> relativePath)
        else
            Array.empty

    let private openExclusiveReadLock path = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)

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

    let private parseJsonOutput (output: string) =
        output
            .TrimStart()
            .StartsWith("{", StringComparison.Ordinal)
        |> should equal true

        JsonDocument.Parse(output)

    let private assertCleanJsonOutput (standardOut: string) (standardError: string) =
        standardError |> should equal String.Empty
        standardOut |> should not' (contain "Elapsed:")

        standardOut
        |> should not' (contain "Grace Version Control System")

        standardOut
        |> should not' (contain "Doctor checks")

        parseJsonOutput standardOut

    let private findCheckById (checks: JsonElement) checkId =
        checks.EnumerateArray()
        |> Seq.find (fun check ->
            check
                .GetProperty("Id")
                .GetString()
                .Equals(checkId, StringComparison.Ordinal))

    let private assertDoesNotContainSecrets (secrets: string list) (standardOut: string) (standardError: string) =
        for secret in secrets do
            standardOut |> should not' (contain secret)
            standardError |> should not' (contain secret)

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
            "object-cache.index-readable"
        |]

    [<Test>]
    let ``doctor help works without config and shows v1 options`` () =
        withTempDir (fun root ->
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

    [<Test>]
    let ``doctor list checks emits clean json envelope without config`` () =
        withTempDir (fun root ->
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
            |> should equal 23

            returnValue
                .GetProperty("Catalog")
                .GetArrayLength()
            |> should equal 23

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
            |> should contain "object-cache.index-readable"

            catalogIds
            |> should contain "identity.auth-session"

            catalogIds |> should contain "server.connectivity"
            catalogIds |> should contain "config.file.parse"

            catalogIds
            |> should contain "user-config.file.discover"

            catalogIds
            |> should contain "ignore.entries.parse"

            let firstCheck = returnValue.GetProperty("Checks")[0]

            firstCheck.GetProperty("Id").GetString()
            |> should not' (equal String.Empty)

            Directory.Exists(Path.Combine(root, ".grace"))
            |> should equal false)

    [<Test>]
    let ``doctor list checks offline keeps only offline catalog entries`` () =
        withTempDir (fun _ ->
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

            catalogIds.Count |> should equal 21

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
            |> should contain "object-cache.index-readable"

            catalogIds
            |> should not' (contain "identity.auth-session")

            catalogIds
            |> should not' (contain "server.connectivity"))

    [<Test>]
    let ``doctor check filter accepts mixed case ids and categories`` () =
        withTempDir (fun _ ->
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

    [<Test>]
    let ``doctor explicit check includes non-default catalog entry without full`` () =
        withTempDir (fun _ ->
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

    [<Test>]
    let ``doctor explicit config check is not filtered out without full`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                writeGraceConfig root "http://127.0.0.1:5000"
                |> ignore

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

    [<Test>]
    let ``doctor auth detects valid GRACE_TOKEN without printing raw value`` () =
        withTempDir (fun _ ->
            let token = validPat ()

            withEnv Constants.EnvironmentVariables.GraceToken (Some token) (fun () ->
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

    [<Test>]
    let ``doctor auth accepts bearer GRACE_TOKEN without printing raw value`` () =
        withTempDir (fun _ ->
            let token = validPat ()
            let bearerToken = $"  Bearer {token}  "

            withEnv Constants.EnvironmentVariables.GraceToken (Some bearerToken) (fun () ->
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

    [<TestCase("not-a-pat")>]
    [<TestCase("Bearer not-a-pat")>]
    let ``doctor auth rejects invalid GRACE_TOKEN safely`` token =
        withTempDir (fun _ ->
            withEnv Constants.EnvironmentVariables.GraceToken (Some token) (fun () ->
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

    [<TestCase("")>]
    [<TestCase("   ")>]
    let ``doctor auth treats blank GRACE_TOKEN as unset and continues OIDC inspection`` token =
        withTempDir (fun _ ->
            withEnv Constants.EnvironmentVariables.GraceToken (Some token) (fun () ->
                withEnv Constants.EnvironmentVariables.GraceAuthOidcAuthority (Some "https://tenant.example.invalid/") (fun () ->
                    withEnv Constants.EnvironmentVariables.GraceAuthOidcAudience (Some "https://api.example.invalid/") (fun () ->
                        withEnv Constants.EnvironmentVariables.GraceAuthOidcM2mClientId (Some "m2m-client-id") (fun () ->
                            withEnv Constants.EnvironmentVariables.GraceAuthOidcM2mClientSecret (Some "m2m-client-secret") (fun () ->
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

    [<Test>]
    let ``doctor auth reports unsupported GRACE_TOKEN_FILE with GRACE_TOKEN remediation`` () =
        withTempDir (fun _ ->
            let tokenFilePath = "C:\\secret\\grace-token.txt"

            withEnv Constants.EnvironmentVariables.GraceTokenFile (Some tokenFilePath) (fun () ->
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

    [<TestCase("")>]
    [<TestCase("   ")>]
    let ``doctor auth treats blank GRACE_TOKEN_FILE as unset for explicit checks`` tokenFileValue =
        withTempDir (fun _ ->
            withEnv Constants.EnvironmentVariables.GraceTokenFile (Some tokenFileValue) (fun () ->
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

    [<TestCase("")>]
    [<TestCase("   ")>]
    let ``doctor auth treats blank GRACE_TOKEN_FILE as unset in default checks`` tokenFileValue =
        withTempDir (fun _ ->
            withEnv Constants.EnvironmentVariables.GraceTokenFile (Some tokenFileValue) (fun () ->
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

    [<Test>]
    let ``doctor auth missing environment warns and skips token validation`` () =
        withTempDir (fun _ ->
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

    [<Test>]
    let ``doctor auth reports partial OIDC M2M and CLI configuration without secret values`` () =
        withTempDir (fun _ ->
            let authority = "https://tenant.example.invalid/very-secret-authority"
            let m2mSecret = "m2m-client-secret-value"
            let cliClientId = "cli-client-secret-looking-id"

            withEnv Constants.EnvironmentVariables.GraceAuthOidcAuthority (Some authority) (fun () ->
                withEnv Constants.EnvironmentVariables.GraceAuthOidcM2mClientSecret (Some m2mSecret) (fun () ->
                    withEnv Constants.EnvironmentVariables.GraceAuthOidcCliClientId (Some cliClientId) (fun () ->
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

    [<Test>]
    let ``doctor auth treats blank OIDC required values as missing`` () =
        withTempDir (fun _ ->
            withEnv Constants.EnvironmentVariables.GraceAuthOidcAuthority (Some "   ") (fun () ->
                withEnv Constants.EnvironmentVariables.GraceAuthOidcAudience (Some "https://api.example.invalid/") (fun () ->
                    withEnv Constants.EnvironmentVariables.GraceAuthOidcM2mClientId (Some "m2m-client-id") (fun () ->
                        withEnv Constants.EnvironmentVariables.GraceAuthOidcM2mClientSecret (Some "m2m-client-secret") (fun () ->
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

    [<Test>]
    let ``doctor auth exact check selections are not filtered out`` () =
        withTempDir (fun _ ->
            for checkId in
                [
                    "auth.env-token.valid"
                    "auth.token-file.unsupported"
                    "auth.oidc.configuration"
                ] do
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

    [<TestCase("Silent")>]
    [<TestCase("Minimal")>]
    let ``doctor auth successful diagnostics emit no output for quiet output modes`` output =
        withTempDir (fun _ ->
            let token = validPat ()

            withEnv Constants.EnvironmentVariables.GraceToken (Some token) (fun () ->
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      output
                                                      "doctor"
                                                      "--check"
                                                      "auth.env-token.valid" |]

                exitCode |> should equal 0
                standardOut |> should equal String.Empty
                standardError |> should equal String.Empty))

    [<Test>]
    let ``doctor auth list checks includes S2 auth catalog ids`` () =
        withTempDir (fun _ ->
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

    [<Test>]
    let ``doctor list checks includes local-state and object-cache catalog ids`` () =
        withTempDir (fun _ ->
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
                |> should contain "schema_version is 2"

                (findCheckById checks "object-cache.index-readable")
                    .GetProperty("Summary")
                    .GetString()
                |> should contain "without mutation"))

    [<Test>]
    let ``doctor missing local-state parent reports check result without creating files`` () =
        withTempDir (fun root ->
            let beforeRoot = snapshotFiles root

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

    [<Test>]
    let ``doctor missing local-state database warns without creating database`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                writeGraceConfig root "http://127.0.0.1:5000"
                |> ignore

                let dbPath = localStateDbPath root
                File.Exists(dbPath) |> should equal false
                let beforeRoot = snapshotFiles root

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

    [<Test>]
    let ``doctor local-state database path occupied by directory reports failure without mutation`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                writeGraceConfig root "http://127.0.0.1:5000"
                |> ignore

                let dbPath = localStateDbPath root
                Directory.CreateDirectory(dbPath) |> ignore
                let beforeRoot = snapshotFiles root

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

    [<Test>]
    let ``doctor local-state exact check selections are not filtered out`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                ensureValidLocalStateDb root

                for checkId in localStateCheckIds do
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

    [<TestCase("Silent")>]
    [<TestCase("Minimal")>]
    let ``doctor local-state successful diagnostics emit no output for quiet output modes`` output =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                ensureValidLocalStateDb root

                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      output
                                                      "doctor"
                                                      "--check"
                                                      "state.db.schema-version" |]

                exitCode |> should equal 0
                standardOut |> should equal String.Empty
                standardError |> should equal String.Empty))

    [<Test>]
    let ``doctor verbose select returns clean scalar for passing config check`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                writeGraceConfig root "http://127.0.0.1:5000"
                |> ignore

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

                    snapshotFiles root |> should equal beforeRoot
                    snapshotFiles home |> should equal beforeHome

                    File.Exists(Path.Combine(root, Constants.GraceConfigDirectory, Constants.GraceLocalStateDbFileName))
                    |> should equal false)))

    [<Test>]
    let ``doctor malformed config fails parse and selected skipped dependent output stays clean`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun home ->
                let graceDir = Path.Combine(root, Constants.GraceConfigDirectory)
                Directory.CreateDirectory(graceDir) |> ignore
                File.WriteAllText(Path.Combine(graceDir, Constants.GraceConfigFileName), "{ not json")

                let beforeRoot = snapshotFiles root
                let beforeHome = snapshotFiles home

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

    [<Test>]
    let ``doctor treats unreadable config file as parse failure instead of missing config`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                let configPath = writeGraceConfig root "http://127.0.0.1:5000"

                use _lock = openExclusiveReadLock configPath

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

    [<Test>]
    let ``doctor carries unreadable graceignore into ignore parse result without affecting config parse`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                writeGraceConfig root "http://127.0.0.1:5000"
                |> ignore

                let graceIgnorePath = Path.Combine(root, Constants.GraceIgnoreFileName)
                File.WriteAllText(graceIgnorePath, "bin/")

                use _lock = openExclusiveReadLock graceIgnorePath

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

    [<Test>]
    let ``doctor missing user config reports warning without creating user grace directory`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun home ->
                withUserConfigTemporarilyMissing (fun userConfigPath ->
                    let beforeHome = snapshotFiles home
                    let beforeUserConfigExists = File.Exists(userConfigPath)

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

    [<Test>]
    let ``doctor invalid check emits GraceError in json mode`` () =
        withTempDir (fun _ ->
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

            let mutable returnValue = Unchecked.defaultof<JsonElement>

            rootElement.TryGetProperty("ReturnValue", &returnValue)
            |> should equal false)

    [<Test>]
    let ``doctor check followed by option-shaped token emits GraceError`` () =
        withTempDir (fun _ ->
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

            let mutable returnValue = Unchecked.defaultof<JsonElement>

            rootElement.TryGetProperty("ReturnValue", &returnValue)
            |> should equal false)

    [<Test>]
    let ``doctor schema and examples work without config`` () =
        withTempDir (fun root ->
            for args, expectedKind in
                [
                    [| "doctor"; "--schema" |], "schema"
                    [| "doctor"; "--examples" |], "examples"
                ] do
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

    [<TestCase("Silent")>]
    [<TestCase("Minimal")>]
    let ``doctor suppresses successful human output for quiet output modes`` output =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                writeGraceConfig root "http://127.0.0.1:5000"
                |> ignore

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

    [<TestCase("Json")>]
    [<TestCase("Verbose")>]
    let ``doctor select projects return value property without config`` output =
        withTempDir (fun root ->
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

    [<Test>]
    let ``doctor select missing property returns projection failure exit code`` () =
        withTempDir (fun root ->
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

    [<Test>]
    let ``doctor select rejects envelope-qualified path before config`` () =
        withTempDir (fun root ->
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

    [<Test>]
    let ``doctor diagnostic exit codes map warning and failure reports`` () =
        let warningReport =
            Doctor.createReportForChecks false false false Doctor.catalog
            |> Doctor.withStatus "Warning"

        Doctor.diagnosticExitCode false warningReport
        |> should equal 0

        Doctor.diagnosticExitCode true warningReport
        |> should equal 1

        let failedReport = warningReport |> Doctor.withStatus "Failed"

        Doctor.diagnosticExitCode false failedReport
        |> should equal 1
