namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.Shared
open Grace.Shared.Client.Configuration
open NUnit.Framework
open Spectre.Console
open System
open System.IO
open System.Text.Json

[<NonParallelizable>]
module MaintenanceCliTests =
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

    let private withTempRepo (action: string -> unit) =
        let tempDir = Path.Combine(Path.GetTempPath(), $"grace-maintenance-cli-tests-{Guid.NewGuid():N}")
        let graceDir = Path.Combine(tempDir, Constants.GraceConfigDirectory)
        Directory.CreateDirectory(graceDir) |> ignore
        File.WriteAllText(Path.Combine(graceDir, Constants.GraceConfigFileName), "{}")

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

    let private parseJsonOutput (output: string) =
        output
            .TrimStart()
            .StartsWith("{", StringComparison.Ordinal)
        |> should equal true

        JsonDocument.Parse(output)

    [<Test>]
    let ``maintenance check ignore entries json emits one clean envelope`` () =
        withTempRepo (fun _ ->
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "maintenance"
                                                  "check-ignore-entries" |]

            exitCode |> should equal 0
            standardError |> should equal String.Empty

            standardOut
            |> should not' (contain "Directory ignore entries:")

            standardOut |> should not' (contain "Elapsed:")

            use document = parseJsonOutput standardOut
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
