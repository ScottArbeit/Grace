namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open NUnit.Framework

[<TestFixture>]
module CommandParsingTests =
    [<Test>]
    let ``top level command returns none for empty args`` () =
        GraceCommand.tryGetTopLevelCommandFromArgs Array.empty true |> should equal None

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
    let ``top level command honors end of options marker`` () =
        GraceCommand.tryGetTopLevelCommandFromArgs [| "--output"; "Verbose"; "--"; "connect" |] true
        |> should equal (Some "connect")
