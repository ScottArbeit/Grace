namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.CommandOutputContract
open NUnit.Framework
open Spectre.Console
open System
open System.IO
open System.Text.Json

/// Groups output-contract and process-static command invocation coverage for static Cache commands.
[<NonParallelizable>]
module CacheCliTests =

    /// Configures Spectre output so command invocation assertions can inspect only the current process output.
    let private setAnsiConsoleOutput writer =
        let settings = AnsiConsoleSettings()
        settings.Out <- AnsiConsoleOutput(writer)
        AnsiConsole.Console <- AnsiConsole.Create(settings)

    /// Executes a CLI command in a fresh service directory that contains no repository graceconfig.
    let private invokeWithoutRepositoryConfig args =
        let temporaryDirectory = Path.Combine(Path.GetTempPath(), $"grace-cache-cli-tests-{Guid.NewGuid():N}")

        Directory.CreateDirectory(temporaryDirectory)
        |> ignore

        let originalDirectory = Environment.CurrentDirectory
        use output = new StringWriter()
        let originalOutput = Console.Out

        try
            Environment.CurrentDirectory <- temporaryDirectory
            Console.SetOut(output)
            setAnsiConsoleOutput output
            let exitCode = GraceCommand.main args
            exitCode, output.ToString()
        finally
            Environment.CurrentDirectory <- originalDirectory
            Console.SetOut(originalOutput)
            setAnsiConsoleOutput originalOutput
            Directory.Delete(temporaryDirectory, true)

    /// Verifies that cache enrollment and redacted local status are registered with standard CLI output handling.
    [<Test>]
    let ``cache commands are registered in the output contract`` () =
        let commandIds =
            entries
            |> List.map (fun entry -> entry.Identity.CommandId)

        commandIds |> should contain "cache.enroll"
        commandIds |> should contain "cache.status"

    /// Verifies local status is a repository-independent redacted observation in both human and JSON output modes.
    [<Test>]
    let ``cache status runs without repository config and emits redacted status`` () =
        let jsonExitCode, jsonOutput =
            invokeWithoutRepositoryConfig [| "--output"
                                             "Json"
                                             "cache"
                                             "status" |]

        let humanExitCode, humanOutput =
            invokeWithoutRepositoryConfig [| "cache"
                                             "status" |]

        jsonExitCode |> should equal 1
        humanExitCode |> should equal 1

        jsonOutput
        |> should not' (contain "graceconfig.json")

        humanOutput
        |> should not' (contain "graceconfig.json")

        jsonOutput
        |> should not' (contain "identity.pkcs8")

        jsonOutput |> should not' (contain "staging-")

        use document = JsonDocument.Parse(jsonOutput)
        let status = document.RootElement.GetProperty("ReturnValue")

        status.GetProperty("Enrollment").GetString()
        |> should equal "notEnrolled"

        status.GetProperty("Key").GetString()
        |> should equal "missing"

    /// Verifies invalid enrollment reaches the cache handler before any repository configuration lookup or local key staging.
    [<Test>]
    let ``cache enroll validates before repository config or key staging`` () =
        let exitCode, output =
            invokeWithoutRepositoryConfig [| "--output"
                                             "Json"
                                             "cache"
                                             "enroll"
                                             "--display-name"
                                             "invalid"
                                             "--endpoint"
                                             "not-a-uri"
                                             "--boundary"
                                             "owner"
                                             "--owner-id"
                                             "11111111-1111-1111-1111-111111111111"
                                             "--repository-organization-id"
                                             "22222222-2222-2222-2222-222222222222"
                                             "--repository-id"
                                             "33333333-3333-3333-3333-333333333333" |]

        exitCode |> should equal -1
        output |> should not' (contain "graceconfig.json")
        output |> should not' (contain "staging-")

        use document = JsonDocument.Parse(output)

        document
            .RootElement
            .GetProperty("Error")
            .GetString()
        |> should contain "Endpoint"
