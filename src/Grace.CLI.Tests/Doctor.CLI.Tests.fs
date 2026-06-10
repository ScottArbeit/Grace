namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.Shared
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
            |> should equal 4

            returnValue
                .GetProperty("Catalog")
                .GetArrayLength()
            |> should equal 4

            let catalogIds =
                returnValue
                    .GetProperty("Catalog")
                    .EnumerateArray()
                |> Seq.map (fun check -> check.GetProperty("Id").GetString())
                |> Set.ofSeq

            catalogIds
            |> should contain "identity.auth-session"

            catalogIds |> should contain "server.connectivity"

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

            catalogIds.Count |> should equal 2

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

            checks.GetArrayLength() |> should equal 2)

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
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  output
                                                  "doctor" |]

            exitCode |> should equal 0
            standardOut |> should equal String.Empty
            standardError |> should equal String.Empty

            Directory.Exists(Path.Combine(root, ".grace"))
            |> should equal false)

    [<Test>]
    let ``doctor select projects return value property without config`` () =
        withTempDir (fun root ->
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "doctor"
                                                  "--select"
                                                  "Status" |]

            exitCode |> should equal 0
            standardError |> should equal String.Empty
            standardOut.Trim() |> should equal "\"Ok\""

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
