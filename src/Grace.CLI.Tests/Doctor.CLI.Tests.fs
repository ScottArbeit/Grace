namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.Shared
open Grace.Shared.Client
open NUnit.Framework
open Spectre.Console
open System
open System.IO
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

    let private withTempDir (action: string -> unit) =
        let tempDir = Path.Combine(Path.GetTempPath(), $"grace-doctor-cli-tests-{Guid.NewGuid():N}")
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

    let private snapshotFiles root =
        if Directory.Exists(root) then
            Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            |> Array.map (fun path -> Path.GetRelativePath(root, path), File.GetLastWriteTimeUtc(path), FileInfo(path).Length)
            |> Array.sortBy (fun (relativePath, _, _) -> relativePath)
        else
            Array.empty

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
            |> should equal 11

            returnValue
                .GetProperty("Catalog")
                .GetArrayLength()
            |> should equal 11

            let catalogIds =
                returnValue
                    .GetProperty("Catalog")
                    .EnumerateArray()
                |> Seq.map (fun check -> check.GetProperty("Id").GetString())
                |> Set.ofSeq

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

            catalogIds.Count |> should equal 9

            catalogIds |> should contain "config.file.parse"

            catalogIds
            |> should contain "ignore.entries.parse"

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
    let ``doctor verbose select returns clean scalar for passing config check`` () =
        withTempDir (fun root ->
            withIsolatedHome root (fun _ ->
                writeGraceConfig root "http://127.0.0.1:5000"

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
