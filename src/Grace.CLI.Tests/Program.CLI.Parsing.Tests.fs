namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open NUnit.Framework

/// Groups command token parsing coverage for the CLI test project.
[<Parallelizable(ParallelScope.All)>]
module CommandTokenParsingTests =
    let private mixedCaseOutputFormats =
        [
            TestCaseData("nOrMaL", "Normal")
                .SetName("mixed-case Normal")
            TestCaseData("jSoN", "Json")
                .SetName("mixed-case Json")
            TestCaseData("mInImAl", "Minimal")
                .SetName("mixed-case Minimal")
            TestCaseData("sIlEnT", "Silent")
                .SetName("mixed-case Silent")
            TestCaseData("vErBoSe", "Verbose")
                .SetName("mixed-case Verbose")
        ]

    /// Verifies that every documented output value accepts mixed casing under the case-insensitive platform policy.
    [<TestCaseSource(nameof mixedCaseOutputFormats)>]
    let ``documented output values accept mixed casing`` suppliedValue canonicalValue =
        GraceCommand.normalizeOutputArguments
            [|
                "status"
                "--output"
                suppliedValue
            |]
            true
        |> should
            equal
            [|
                "status"
                "--output"
                canonicalValue
            |]

    let private outputOptionForms =
        [
            TestCaseData([| "--output"; "vErBoSe" |], [| "--output"; "Verbose" |])
                .SetName("split long output")
            TestCaseData([| "-o"; "vErBoSe" |], [| "-o"; "Verbose" |])
                .SetName("split short output")
            TestCaseData([| "--output=vErBoSe" |], [| "--output=Verbose" |])
                .SetName("equals long output")
            TestCaseData([| "-o=vErBoSe" |], [| "-o=Verbose" |])
                .SetName("equals short output")
        ]

    /// Verifies that all supported output option syntaxes select the canonical mode.
    [<TestCaseSource(nameof outputOptionForms)>]
    let ``output option forms select canonical verbose mode`` outputTokens expectedTokens =
        let args = Array.append [| "status" |] outputTokens

        GraceCommand.normalizeOutputArguments args true
        |> should equal (Array.append [| "status" |] expectedTokens)

    /// Verifies that unknown output values remain untouched for native parser diagnostics.
    [<Test>]
    let ``unknown output value remains a parser error`` () =
        let args = [| "status"; "--OuTpUt=Jsonish" |]
        let normalizedArgs = GraceCommand.normalizeOutputArguments args true
        normalizedArgs |> should equal args

    /// Verifies that case-sensitive platforms retain exact-case output validation.
    [<Test>]
    let ``case-sensitive policy preserves mixed-case output value`` () =
        let args = [| "status"; "--output"; "vErBoSe" |]
        let normalizedArgs = GraceCommand.normalizeOutputArguments args false
        normalizedArgs |> should equal args

    /// Verifies that output-looking tokens after the end-of-options marker are not rewritten.
    [<Test>]
    let ``output value after terminator is not rewritten`` () =
        let args =
            [|
                "status"
                "--output"
                "--"
                "--output=vErBoSe"
            |]

        GraceCommand.normalizeOutputArguments args true
        |> should equal args

    /// Verifies that canonicalization preserves the parser's later-option-wins behavior.
    [<Test>]
    let ``later output option still wins`` () =
        GraceCommand.normalizeOutputArguments
            [|
                "status"
                "--output"
                "jSoN"
                "-o=vErBoSe"
            |]
            true
        |> should
            equal
            [|
                "status"
                "--output"
                "Json"
                "-o=Verbose"
            |]

    let private malformedSplitOutputRecovery =
        [
            TestCaseData([| "--output"; "--output=vErBoSe" |], [| "--output"; "--output=Verbose" |])
                .SetName("malformed long output before long equals output")
            TestCaseData([| "--output"; "-o"; "vErBoSe" |], [| "--output"; "-o"; "Verbose" |])
                .SetName("malformed long output before short split output")
            TestCaseData([| "-o"; "-o"; "vErBoSe" |], [| "-o"; "-o"; "Verbose" |])
                .SetName("malformed short output before short split output")
            TestCaseData([| "-o"; "--output=vErBoSe" |], [| "-o"; "--output=Verbose" |])
                .SetName("malformed short output before long equals output")
        ]

    /// Verifies that a malformed split option does not hide a later recognized output option.
    [<TestCaseSource(nameof malformedSplitOutputRecovery)>]
    let ``malformed split output resumes normalization at the later option`` outputTokens expectedTokens =
        let args = Array.append outputTokens [| "repository"; "init" |]

        GraceCommand.normalizeOutputArguments args true
        |> should equal (Array.append expectedTokens [| "repository"; "init" |])

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
