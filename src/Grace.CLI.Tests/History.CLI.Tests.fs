namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.Shared.Utilities
open NodaTime
open NUnit.Framework
open System

/// Groups history command coverage for the CLI test project.
[<TestFixture>]
module HistoryCommandTests =
    /// Builds a deterministic entry for test scenarios fixture for the CLI history assertions.
    let private createEntry (offsetMinutes: float) (source: string option) (commandLine: string) : HistoryStorage.HistoryEntry =
        let timestamp = getCurrentInstant ()
        let offset = Duration.FromMinutes(offsetMinutes)

        {
            id = Guid.NewGuid()
            timestampUtc = timestamp.Plus(offset)
            argvOriginal = [| "history"; "show" |]
            argvNormalized = [| "history"; "show" |]
            commandLine = commandLine
            cwd = Environment.CurrentDirectory
            repoRoot = None
            repoName = None
            repoBranch = None
            graceVersion = "0.1"
            exitCode = 0
            durationMs = 5L
            parseSucceeded = true
            redactions = List.empty
            source = source
        }

    /// Verifies that filter entries applies case insensitive source filter.
    [<Test>]
    let ``filterEntries applies case insensitive source filter`` () =
        let entries =
            [
                createEntry 0.0 (Some "codex") "workitem show"
                createEntry 1.0 (Some "manual") "workitem show"
                createEntry 2.0 None "workitem show"
            ]

        let filtered = History.filterEntries entries 50 false false false None None (Some "CODEX")

        filtered.Length |> should equal 1
        filtered[0].source |> should equal (Some "codex")

    /// Verifies that filter entries combines source and text filters.
    [<Test>]
    let ``filterEntries combines source and text filters`` () =
        let entries =
            [
                createEntry 0.0 (Some "codex") "branch status"
                createEntry 1.0 (Some "codex") "workitem show --id 10"
                createEntry 2.0 (Some "manual") "workitem show --id 11"
            ]

        let filtered = History.filterEntries entries 50 false false false None (Some "workitem") (Some "codex")

        filtered.Length |> should equal 1
        filtered[0].source |> should equal (Some "codex")

        filtered[0].commandLine
        |> should contain "workitem"
