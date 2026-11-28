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

/// Covers building only the requested top-level command tree at startup, so `grace <command>` skips other commands' setup.
[<Parallelizable(ParallelScope.All)>]
module LazyCommandTreeTests =
    open System
    open System.CommandLine
    open System.CommandLine.Parsing

    /// Lists every command path in the full tree, depth first.
    let private allCommandPaths () =
        let rec walk (prefix: string list) (command: Command) =
            [
                for subcommand in command.Subcommands do
                    let path = prefix @ [ subcommand.Name ]
                    yield path
                    yield! walk path subcommand
            ]

        walk [] GraceCommand.fullRootCommand.Value

    /// Summarizes what a parse selected and reported, independent of which root produced it.
    let private parseSummary (parseResult: ParseResult) =
        let rec commandPath (result: CommandResult) =
            match result.Parent with
            | :? CommandResult as parent -> commandPath parent @ [ result.Command.Name ]
            | _ -> [ result.Command.Name ]

        let actionName =
            if isNull parseResult.Action then
                "<none>"
            else
                parseResult.Action.GetType().FullName

        let errors =
            parseResult.Errors
            |> Seq.map (fun error -> error.Message)
            |> Seq.toList

        let unmatched = parseResult.UnmatchedTokens |> Seq.toList
        commandPath parseResult.CommandResult, actionName, errors, unmatched

    /// Verifies each startup lookup entry lists exactly the name and aliases its command declares, so adding or renaming
    /// a command alias without updating the table fails here instead of silently building the full tree.
    [<Test>]
    let ``top-level command table matches each command's declared name and aliases`` () =
        let fromTree =
            GraceCommand.fullRootCommand.Value.Subcommands
            |> Seq.map (fun command -> Array.append [| command.Name |] (command.Aliases |> Seq.toArray))
            |> Seq.toArray

        GraceCommand.topLevelCommands
        |> Array.map fst
        |> should equal fromTree

    /// Verifies a recognized command or alias builds a root containing only that command.
    [<TestCase("branch", "branch")>]
    [<TestCase("br", "branch")>]
    [<TestCase("wi", "workitem")>]
    [<TestCase("alias", "alias")>]
    let ``recognized top-level token builds only that command`` (token: string) (expectedName: string) =
        let root = GraceCommand.buildRootCommand (Some token)

        root.Subcommands
        |> Seq.map (fun command -> command.Name)
        |> Seq.toList
        |> should equal [ expectedName ]

    /// Verifies input that doesn't exactly name a top-level command gets the full tree, so help and suggestions see every command.
    [<TestCase("Branch")>]
    [<TestCase("brnch")>]
    [<TestCase("status")>]
    [<TestCase("")>]
    let ``unrecognized top-level token builds the full tree`` (token: string) =
        let root = GraceCommand.buildRootCommand (Some token)

        obj.ReferenceEquals(root, GraceCommand.fullRootCommand.Value)
        |> should equal true

    /// Verifies a missing command token gets the full tree.
    [<Test>]
    let ``missing top-level token builds the full tree`` () =
        obj.ReferenceEquals(GraceCommand.buildRootCommand None, GraceCommand.fullRootCommand.Value)
        |> should equal true

    /// Verifies that, for every command path and common option placements, parsing through the startup root selects the
    /// same command and reports the same errors as the full tree.
    [<Test>]
    let ``every command parses the same through the startup root`` () =
        let cases =
            [
                for path in allCommandPaths () do
                    let path = List.toArray path
                    yield path
                    yield Array.append path [| "--bogus-option" |]
                    yield Array.append path [| "--output"; "Json" |]
                    yield Array.append [| "--output"; "Json" |] path
                    yield Array.append [| "-c"; "abc" |] path
                    yield Array.append path [| "extra-positional" |]
                    yield Array.append path [| "--help" |]

                yield [| "--"; "branch"; "status" |]
                yield [| "--unknown"; "branch"; "status" |]
                yield [| "--output"; "branch"; "status" |]
                yield [| "branch"; "--help"; "status" |]
            ]

        let mismatches =
            cases
            |> List.choose (fun args ->
                let startupRoot = GraceCommand.buildRootCommand (GraceCommand.tryGetTopLevelCommandFromArgs args false)
                let expected = parseSummary (GraceCommand.fullRootCommand.Value.Parse(args))
                let actual = parseSummary (startupRoot.Parse(args))

                if expected = actual then
                    None
                else
                    Some $"""grace {String.Join(" ", args)}: expected {expected}, got {actual}""")

        mismatches |> should be Empty

    /// Verifies name-based option lookups treat an option that only other commands declare as "not supplied" when the
    /// startup root holds just one command, instead of throwing.
    [<Test>]
    let ``name lookups for options other commands declare report not supplied`` () =
        let args = [| "maintenance"; "update-index" |]

        let parseResult =
            (GraceCommand.buildRootCommand (Some "maintenance"))
                .Parse(args)

        Services.getResultByName parseResult Grace.CLI.Text.OptionName.BranchId
        |> isNull
        |> should equal true

        Services.getValueByName<Guid> parseResult Grace.CLI.Text.OptionName.BranchId
        |> should equal Guid.Empty

        Services.isOptionPresent parseResult Grace.CLI.Text.OptionName.BranchId
        |> should equal false

    /// Verifies the shared presence helpers accept every option name in the full tree for every top-level command's
    /// startup root, so shared code can ask about any option without knowing which commands were built.
    [<Test>]
    let ``shared presence helpers accept every option name on every startup root`` () =
        let rec optionNames (command: Command) =
            seq {
                for option in command.Options do
                    yield option.Name

                for subcommand in command.Subcommands do
                    yield! optionNames subcommand
            }

        let allNames =
            optionNames GraceCommand.fullRootCommand.Value
            |> Seq.distinct
            |> Seq.toArray

        let failures =
            [
                for (names, _) in GraceCommand.topLevelCommands do
                    let parseResult =
                        (GraceCommand.buildRootCommand (Some names[0]))
                            .Parse([| names[0] |])

                    for name in allNames do
                        try
                            Services.isOptionPresent parseResult name
                            |> ignore

                            Services.isOptionResultImplicit parseResult name
                            |> ignore

                            Services.getValueByName<obj> parseResult name
                            |> ignore
                        with
                        | ex -> yield $"{names[0]} / {name}: {ex.Message}"
            ]

        failures |> should be Empty

/// Covers reflection-free output format names and parsing used on every CLI invocation.
[<Parallelizable(ParallelScope.All)>]
module OutputFormatTests =
    open Grace.CLI.Common
    open Microsoft.FSharp.Reflection

    /// Verifies output format names list every case, in declaration order.
    [<Test>]
    let ``output format Names match reflected case names`` () =
        OutputFormat.Names
        |> should
            equal
            (FSharpType.GetUnionCases typeof<OutputFormat>
             |> Array.map (fun case -> case.Name))

    /// Verifies every output format parses from its name in any casing.
    [<Test>]
    let ``output format TryParse accepts every name in any casing`` () =
        for format in OutputFormat.All do
            OutputFormat.TryParse(format.Name.ToUpperInvariant())
            |> should equal (Some format)

            OutputFormat.TryParse(format.Name.ToLowerInvariant())
            |> should equal (Some format)

    /// Verifies unknown or missing output format names don't parse.
    [<TestCase("Fancy")>]
    [<TestCase("")>]
    [<TestCase(null)>]
    let ``output format TryParse rejects unknown names`` (value: string) = OutputFormat.TryParse(value) |> should equal None

/// Covers predicting the start bar from arguments, so Grace draws it before building the command tree.
[<Parallelizable(ParallelScope.All)>]
module StartBannerTests =
    open Grace.CLI.GraceCommand

    let private configured () = true
    let private notConfigured () = false

    let private cases =
        [
            TestCaseData([| "branch"; "status" |], PlainStartBanner)
                .SetName("normal output draws the plain bar")
            TestCaseData([| "br"; "status" |], PlainStartBanner)
                .SetName("top-level alias draws the plain bar")
            TestCaseData(
                [|
                    "branch"
                    "status"
                    "--output"
                    "Normal"
                |],
                PlainStartBanner
            )
                .SetName("explicit Normal draws the plain bar")
            TestCaseData(
                [|
                    "branch"
                    "status"
                    "--output"
                    "Verbose"
                |],
                VerboseStartBanner
            )
                .SetName("Verbose draws the timed bar")
            TestCaseData([| "-o=Verbose"; "branch"; "status" |], VerboseStartBanner)
                .SetName("short equals Verbose draws the timed bar")
            TestCaseData(
                [|
                    "branch"
                    "status"
                    "--output"
                    "Json"
                    "--output"
                    "Normal"
                |],
                NoStartBanner
            )
                .SetName("repeated output draws no early bar")
            TestCaseData(
                [|
                    "branch"
                    "status"
                    "--output"
                    "Json"
                |],
                NoStartBanner
            )
                .SetName("Json draws no bar")
            TestCaseData(
                [|
                    "branch"
                    "status"
                    "--output=Minimal"
                |],
                NoStartBanner
            )
                .SetName("Minimal draws no bar")
            TestCaseData([| "branch"; "status"; "-o"; "Silent" |], NoStartBanner)
                .SetName("Silent draws no bar")
            TestCaseData([| "branch"; "status"; "--output" |], NoStartBanner)
                .SetName("missing output value draws no bar")
            TestCaseData(
                [|
                    "branch"
                    "status"
                    "--select"
                    "Name"
                |],
                NoStartBanner
            )
                .SetName("select draws no bar")
            TestCaseData(
                [|
                    "branch"
                    "status"
                    "--select=Name"
                |],
                NoStartBanner
            )
                .SetName("select equals draws no bar")
            TestCaseData([| "branch"; "status"; "--help" |], NoStartBanner)
                .SetName("help draws no bar")
            TestCaseData([| "branch"; "-h" |], NoStartBanner)
                .SetName("short help draws no bar")
            TestCaseData([| "branch"; "status"; "--schema" |], NoStartBanner)
                .SetName("schema draws no bar")
            TestCaseData([| "branch"; "status"; "--examples" |], NoStartBanner)
                .SetName("examples draws no bar")
            TestCaseData([| "doctor" |], NoStartBanner)
                .SetName("doctor draws no bar")
            TestCaseData([| "brnch"; "status" |], NoStartBanner)
                .SetName("unknown command draws no early bar")
            TestCaseData(Array.empty<string>, NoStartBanner)
                .SetName("no arguments draws no bar")
            TestCaseData([| "branch"; "status"; "--"; "--help" |], PlainStartBanner)
                .SetName("tokens after the separator are ignored")
            TestCaseData(
                [|
                    "branch"
                    "status"
                    "--output:Json"
                |],
                NoStartBanner
            )
                .SetName("colon Json draws no bar")
            TestCaseData([| "branch"; "status"; "-o:Json" |], NoStartBanner)
                .SetName("short colon Json draws no bar")
            TestCaseData([| "branch"; "status"; "-oJson" |], NoStartBanner)
                .SetName("attached short Json draws no bar")
            TestCaseData([| "branch"; "status"; "-oVerbose" |], VerboseStartBanner)
                .SetName("attached short Verbose draws the timed bar")
            TestCaseData(
                [|
                    "branch"
                    "status"
                    "--select:Name"
                |],
                NoStartBanner
            )
                .SetName("select colon draws no bar")
            TestCaseData(
                [|
                    "branch"
                    "status"
                    "--Output"
                    "Json"
                |],
                NoStartBanner
            )
                .SetName("other-case output Json draws no bar")
            TestCaseData([| "branch"; "status"; "-O"; "Json" |], NoStartBanner)
                .SetName("other-case short output Json draws no bar")
            TestCaseData([| "branch"; "status"; "--Schema" |], NoStartBanner)
                .SetName("other-case schema draws no bar")
            TestCaseData([| "branch"; "status"; "--HELP" |], NoStartBanner)
                .SetName("other-case help draws no bar")
            TestCaseData([| "branch"; "get"; "-do"; "Json" |], NoStartBanner)
                .SetName("bundled short options with output draw no bar")
            TestCaseData([| "branch"; "get"; "-doJson" |], NoStartBanner)
                .SetName("bundled attached output draws no bar")
            TestCaseData([| "branch"; "get"; "@args.rsp" |], NoStartBanner)
                .SetName("response file draws no bar")
            TestCaseData([| "branch"; "get"; "-d" |], PlainStartBanner)
                .SetName("short flag without o draws the plain bar")
            TestCaseData(
                [|
                    "branch"
                    "create"
                    "--branch-name"
                    "FOO"
                |],
                PlainStartBanner
            )
                .SetName("option values containing o draw the plain bar")
        ]

    /// Verifies the start bar predicted for each argument shape when a graceconfig.json exists.
    [<TestCaseSource(nameof cases)>]
    let ``start bar prediction`` (args: string array) (expected: StartBanner) =
        startBannerFromArgs args configured
        |> should equal expected

    /// Verifies no early bar is drawn outside a configured repository, where Grace reports the missing configuration instead.
    [<Test>]
    let ``no start bar without a graceconfig`` () =
        startBannerFromArgs [| "branch"; "status" |] notConfigured
        |> should equal NoStartBanner
