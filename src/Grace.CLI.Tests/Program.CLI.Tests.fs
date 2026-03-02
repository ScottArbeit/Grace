namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open NUnit.Framework
open System

[<TestFixture>]
module CommandParsingTests =
    let private withEnvironmentVariable (name: string) (value: string option) (action: unit -> unit) =
        let original = Environment.GetEnvironmentVariable(name)

        try
            Environment.SetEnvironmentVariable(name, value |> Option.toObj)
            action ()
        finally
            Environment.SetEnvironmentVariable(name, original)

    [<Test>]
    let ``top level command returns none for empty args`` () =
        GraceCommand.tryGetTopLevelCommandFromArgs Array.empty true
        |> should equal None

    [<Test>]
    let ``top level command detects command token`` () =
        GraceCommand.tryGetTopLevelCommandFromArgs [| "connect"; "owner/org/repo" |] true
        |> should equal (Some "connect")

    [<Test>]
    let ``top level command skips output option`` () =
        GraceCommand.tryGetTopLevelCommandFromArgs [| "--output"; "Verbose"; "connect" |] true
        |> should equal (Some "connect")

    [<Test>]
    let ``top level command skips correlation id option`` () =
        GraceCommand.tryGetTopLevelCommandFromArgs [| "-c"; "abc123"; "connect" |] true
        |> should equal (Some "connect")

    [<Test>]
    let ``top level command skips source option`` () =
        GraceCommand.tryGetTopLevelCommandFromArgs [| "--source"; "codex"; "connect" |] true
        |> should equal (Some "connect")

    [<Test>]
    let ``top level command honors end of options marker`` () =
        GraceCommand.tryGetTopLevelCommandFromArgs
            [|
                "--output"
                "Verbose"
                "--"
                "connect"
            |]
            true
        |> should equal (Some "connect")

    [<Test>]
    let ``resolveInvocationSource prefers explicit source over environment`` () =
        withEnvironmentVariable Common.SourceEnvironmentVariableName (Some "env-source") (fun () ->
            let parseResult =
                GraceCommand.rootCommand.Parse(
                    [|
                        "--source"
                        "explicit-source"
                        "history"
                        "show"
                    |]
                )

            parseResult.Errors.Count |> should equal 0

            Common.resolveInvocationSource parseResult
            |> should equal (Some "explicit-source"))

    [<Test>]
    let ``resolveInvocationSource falls back to environment`` () =
        withEnvironmentVariable Common.SourceEnvironmentVariableName (Some "env-source") (fun () ->
            let parseResult = GraceCommand.rootCommand.Parse([| "history"; "show" |])
            parseResult.Errors.Count |> should equal 0

            Common.resolveInvocationSource parseResult
            |> should equal (Some "env-source"))

    [<Test>]
    let ``history show accepts source filter option`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                [|
                    "history"
                    "show"
                    "--source"
                    "codex"
                |]
            )

        parseResult.Errors.Count |> should equal 0

    [<Test>]
    let ``history search accepts source filter option`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse [| "history"
                                              "search"
                                              "workitem"
                                              "--source"
                                              "codex" |]

        parseResult.Errors.Count |> should equal 0


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
                with
                | _ -> ()

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

            let exitCode, _ =
                runWithCapturedOutput [| "access"
                                         "grant-role"
                                         "-h" |]

            exitCode |> should equal 0)

    [<Test>]
    let ``help works without config`` () =
        withTempDir (fun _ ->
            let exitCode, _ =
                runWithCapturedOutput [| "access"
                                         "grant-role"
                                         "-h" |]

            exitCode |> should equal 0)

    [<Test>]
    let ``help shows symbolic defaults`` () =
        withTempDir (fun _ ->
            let exitCode, output =
                runWithCapturedOutput [| "access"
                                         "grant-role"
                                         "-h" |]

            exitCode |> should equal 0

            output
            |> should contain "[default: current OwnerId]"

            output
            |> should contain "[default: current OrganizationId]"

            output
            |> should contain "[default: current RepositoryId]"

            output
            |> should contain "[default: current BranchId]"

            output |> should contain "[default: new NanoId]")

    [<Test>]
    let ``create help rewrites empty guid defaults`` () =
        withTempDir (fun _ ->
            let exitCode, output =
                runWithCapturedOutput [| "repository"
                                         "create"
                                         "-h" |]

            exitCode |> should equal 0

            output
            |> should contain "[default: current OwnerId]"

            output
            |> should contain "[default: current OrganizationId]"

            output |> should contain "[default: new Guid]"

            output
            |> should not' (contain "00000000-0000-0000-0000-0000000000000"))

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


namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.Shared.Client
open NUnit.Framework
open Spectre.Console
open System
open System.IO

[<NonParallelizable>]
module RootHelpGroupingTests =
    type private GroupedHelpExpectation = { Args: string array; Headings: string list }

    let private groupedHelpExpectations =
        [
            {
                Args = [| "repo"; "-h" |]
                Headings =
                    [
                        "Create and initialize:"
                        "Inspect:"
                        "Configuration:"
                        "Lifecycle:"
                    ]
            }
            {
                Args = [| "branch"; "-h" |]
                Headings =
                    [
                        "Create and contribute:"
                        "Promotion workflow:"
                        "Inspect:"
                        "Settings:"
                        "Lifecycle:"
                    ]
            }
            {
                Args = [| "owner"; "-h" |]
                Headings =
                    [
                        "Create and inspect:"
                        "Settings:"
                        "Lifecycle:"
                    ]
            }
            {
                Args = [| "org"; "-h" |]
                Headings =
                    [
                        "Create and inspect:"
                        "Settings:"
                        "Lifecycle:"
                    ]
            }
        ]

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

    let private withFileBackup (path: string) (action: unit -> unit) =
        let backupPath = path + ".testbackup"
        let hadExisting = File.Exists(path)

        if hadExisting then File.Copy(path, backupPath, true)

        try
            action ()
        finally
            if hadExisting then
                File.Copy(backupPath, path, true)
                File.Delete(backupPath)
            elif File.Exists(path) then
                File.Delete(path)

    let private withGraceUserFileBackups (action: unit -> unit) =
        let configPath = UserConfiguration.getUserConfigurationPath ()
        let historyPath = HistoryStorage.getHistoryFilePath ()
        let lockPath = HistoryStorage.getHistoryLockPath ()

        withFileBackup configPath (fun () -> withFileBackup historyPath (fun () -> withFileBackup lockPath action))

    let private sliceBetween (text: string) (startText: string) (endText: string) =
        let startIndex = text.IndexOf(startText, StringComparison.Ordinal)
        let endIndex = text.IndexOf(endText, StringComparison.Ordinal)

        if startIndex >= 0 && endIndex > startIndex then
            text.Substring(startIndex, endIndex - startIndex)
        else
            text

    [<Test>]
    let ``root help groups commands`` () =
        withGraceUserFileBackups (fun () ->
            let exitCode, output = runWithCapturedOutput [||]
            exitCode |> should equal 0

            output |> should contain "Getting started:"
            output |> should contain "Day-to-day development:"
            output |> should contain "Review and promotion:"

            output
            |> should contain "Administration and access:"

            output |> should contain "Local utilities:"

            let gettingStarted = sliceBetween output "Getting started:" "Day-to-day development:"

            gettingStarted |> should contain "auth"
            gettingStarted |> should contain "connect"
            gettingStarted |> should contain "config"
            gettingStarted |> should not' (contain "branch")

            let dayToDay = sliceBetween output "Day-to-day development:" "Review and promotion:"

            dayToDay |> should contain "branch"
            dayToDay |> should contain "diff"
            dayToDay |> should contain "directory-version"
            dayToDay |> should contain "watch"
            dayToDay |> should not' (contain "work")

            let reviewAndPromotion = sliceBetween output "Review and promotion:" "Administration and access:"

            reviewAndPromotion |> should contain "workitem"
            reviewAndPromotion |> should contain "review"
            reviewAndPromotion |> should contain "candidate"
            reviewAndPromotion |> should contain "queue"
            reviewAndPromotion |> should contain "agent"

            reviewAndPromotion
            |> should contain "promotion-set")

    [<Test>]
    let ``subcommand help is not grouped`` () =
        withGraceUserFileBackups (fun () ->
            let exitCode, output =
                runWithCapturedOutput [| "branch"
                                         "-h" |]

            exitCode |> should equal 0
            output |> should not' (contain "Getting started:")

            output
            |> should not' (contain "Day-to-day development:")

            output
            |> should not' (contain "Review and promotion:")

            output
            |> should not' (contain "Administration and access:")

            output |> should not' (contain "Local utilities:"))

    [<Test>]
    let ``selected command helps are grouped`` () =
        withGraceUserFileBackups (fun () ->
            for expectation in groupedHelpExpectations do
                let exitCode, output = runWithCapturedOutput expectation.Args
                exitCode |> should equal 0

                for heading in expectation.Headings do
                    output |> should contain heading)
