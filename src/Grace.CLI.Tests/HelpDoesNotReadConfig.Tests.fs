namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.Shared.Client.Configuration
open Grace.Shared.Utilities
open NUnit.Framework
open Spectre.Console
open System
open System.IO

[<NonParallelizable>]
module HelpDoesNotReadConfigTests =
    let private setAnsiConsoleOutput (writer: TextWriter) =
        let settings = AnsiConsoleSettings()
        settings.Out <- AnsiConsoleOutput(writer)
        AnsiConsole.Console <- AnsiConsole.Create(settings)

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

    let private captureOutput (action: unit -> unit) =
        use writer = new StringWriter()
        let originalOut = Console.Out

        try
            Console.SetOut(writer)
            setAnsiConsoleOutput writer
            action ()
            writer.ToString()
        finally
            Console.SetOut(originalOut)
            setAnsiConsoleOutput originalOut

    let private captureOutput (action: unit -> unit) =
        use writer = new StringWriter()
        let originalOut = Console.Out

        try
            Console.SetOut(writer)
            action ()
            writer.ToString()
        finally
            Console.SetOut(originalOut)


    let private withTempDir (action: string -> unit) =
        let tempDir = Path.Combine(Path.GetTempPath(), $"grace-cli-tests-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tempDir) |> ignore
        let originalDir = Environment.CurrentDirectory

        try
            Environment.CurrentDirectory <- tempDir
            resetConfiguration ()
            action tempDir
        finally
            Environment.CurrentDirectory <- originalDir

            if Directory.Exists(tempDir) then
                try
                    Directory.Delete(tempDir, true)
                with _ ->
                    ()

    let private writeInvalidConfig (root: string) =
        let graceDir = Path.Combine(root, ".grace")
        Directory.CreateDirectory(graceDir) |> ignore
        File.WriteAllText(Path.Combine(graceDir, "graceconfig.json"), "not json")

    let private writeValidConfig (root: string) (ownerId: Guid) (orgId: Guid) (repoId: Guid) (branchId: Guid) =
        let graceDir = Path.Combine(root, ".grace")
        Directory.CreateDirectory(graceDir) |> ignore
        let config = GraceConfiguration()
        config.OwnerId <- ownerId
        config.OrganizationId <- orgId
        config.RepositoryId <- repoId
        config.BranchId <- branchId
        let json = serialize config
        File.WriteAllText(Path.Combine(graceDir, "graceconfig.json"), json)

    [<Test>]
    let ``help works with invalid config`` () =
        withTempDir (fun root ->
            writeInvalidConfig root
            let exitCode, _ = runWithCapturedOutput [| "access"; "grant-role"; "-h" |]
            exitCode |> should equal 0)

    [<Test>]
    let ``help works without config`` () =
        withTempDir (fun _ ->
            let exitCode, _ = runWithCapturedOutput [| "access"; "grant-role"; "-h" |]
            exitCode |> should equal 0)

    [<Test>]
    let ``help shows symbolic defaults`` () =
        withTempDir (fun _ ->
            let exitCode, output = runWithCapturedOutput [| "access"; "grant-role"; "-h" |]
            exitCode |> should equal 0

            output |> should contain "[default: current OwnerId]"
            output |> should contain "[default: current OrganizationId]"
            output |> should contain "[default: current RepositoryId]"
            output |> should contain "[default: current BranchId]"
            output |> should contain "[default: new NanoId]")

    [<Test>]
    let ``create help rewrites empty guid defaults`` () =
        withTempDir (fun _ ->
            let exitCode, output = runWithCapturedOutput [| "repository"; "create"; "-h" |]
            exitCode |> should equal 0

            output |> should contain "[default: current OwnerId]"
            output |> should contain "[default: current OrganizationId]"
            output |> should contain "[default: new Guid]"
            output |> should not' (contain "00000000-0000-0000-0000-0000000000000"))

    [<Test>]
    let ``verbose parse result shows resolved ids`` () =
        withTempDir (fun root ->
            let ownerId = Guid.NewGuid()
            let orgId = Guid.NewGuid()
            let repoId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            writeValidConfig root ownerId orgId repoId branchId

            let parseResult = GraceCommand.rootCommand.Parse([| "access"; "grant-role" |])

            let output = captureOutput (fun () -> Common.printParseResult parseResult)

            output |> should contain "Resolved values:"
            output |> should contain $"{ownerId}"
            output |> should contain $"{orgId}"
            output |> should contain $"{repoId}"
            output |> should contain $"{branchId}")

    [<Test>]
    let ``getNormalizedIdsAndNames falls back to config ids`` () =
        withTempDir (fun root ->
            let ownerId = Guid.NewGuid()
            let orgId = Guid.NewGuid()
            let repoId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            writeValidConfig root ownerId orgId repoId branchId

            let parseResult = GraceCommand.rootCommand.Parse([| "access"; "grant-role" |])
            let graceIds = Services.getNormalizedIdsAndNames parseResult

            graceIds.OwnerId |> should equal ownerId
            graceIds.OrganizationId |> should equal orgId
            graceIds.RepositoryId |> should equal repoId
            graceIds.BranchId |> should equal branchId)
