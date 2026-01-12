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
            {
                Args = [| "pg"; "-h" |]
                Headings =
                    [
                        "Create and inspect:"
                        "Manage promotions:"
                        "Workflow:"
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
            dayToDay |> should not' (contain "work"))

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
