namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open NUnit.Framework

/// Groups command token parsing coverage for the CLI test project.
[<Parallelizable(ParallelScope.All)>]
module CommandTokenParsingTests =
    /// Verifies that top level command returns none for empty args.
    [<Test>]
    let ``top level command returns none for empty args`` () =
        GraceCommand.tryGetTopLevelCommandFromArgs Array.empty true
        |> should equal None

    /// Verifies that top level command detects command token.
    [<Test>]
    let ``top level command detects command token`` () =
        GraceCommand.tryGetTopLevelCommandFromArgs [| "connect"; "owner/org/repo" |] true
        |> should equal (Some "connect")

    /// Verifies that top level command skips output option.
    [<Test>]
    let ``top level command skips output option`` () =
        GraceCommand.tryGetTopLevelCommandFromArgs [| "--output"; "Verbose"; "connect" |] true
        |> should equal (Some "connect")

    /// Verifies that top level command skips correlation id option.
    [<Test>]
    let ``top level command skips correlation id option`` () =
        GraceCommand.tryGetTopLevelCommandFromArgs [| "-c"; "abc123"; "connect" |] true
        |> should equal (Some "connect")

    /// Verifies that top level command skips source option.
    [<Test>]
    let ``top level command skips source option`` () =
        GraceCommand.tryGetTopLevelCommandFromArgs [| "--source"; "codex"; "connect" |] true
        |> should equal (Some "connect")

    /// Verifies that top level command skips schema option.
    [<Test>]
    let ``top level command skips schema option`` () =
        GraceCommand.tryGetTopLevelCommandFromArgs [| "--schema"; "repository"; "init" |] true
        |> should equal (Some "repository")

    /// Verifies that top level command skips examples option.
    [<Test>]
    let ``top level command skips examples option`` () =
        GraceCommand.tryGetTopLevelCommandFromArgs [| "--examples"; "workitem"; "show" |] true
        |> should equal (Some "workitem")

    /// Verifies that top level command honors end of options marker.
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
