namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open NUnit.Framework
open System
open System.CommandLine

[<Parallelizable(ParallelScope.All)>]
module BranchCommandParsingTests =
    let private ownerId = Guid.NewGuid()
    let private organizationId = Guid.NewGuid()
    let private repositoryId = Guid.NewGuid()
    let private branchId = Guid.NewGuid()
    let private referenceId = Guid.NewGuid()
    let private sha256Hash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
    let private blake3Hash = "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adcd1e8c76d9a8885f16a39f"
    let private shortestHashPrefix = "ab"
    let private shortHashPrefix = "abc1234"
    let private uppercaseSha256Hash = "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855"

    let private withIds (args: string array) =
        Array.append
            args
            [|
                "--owner-id"
                ownerId.ToString()
                "--organization-id"
                organizationId.ToString()
                "--repository-id"
                repositoryId.ToString()
                "--branch-id"
                branchId.ToString()
            |]

    let private withRepositoryIds (args: string array) =
        Array.append
            args
            [|
                "--owner-id"
                ownerId.ToString()
                "--organization-id"
                organizationId.ToString()
                "--repository-id"
                repositoryId.ToString()
            |]

    let private referenceSourceRoot =
        let rootCommand = RootCommand("Reference source-only parser")
        rootCommand.Options.Add(Common.Options.correlationId)
        rootCommand.Options.Add(Common.Options.source)
        rootCommand.Options.Add(Common.Options.output)
        rootCommand.Options.Add(Common.Options.schema)
        rootCommand.Options.Add(Common.Options.examples)
        rootCommand.Options.Add(Common.Options.select)
        rootCommand.Subcommands.Add(Reference.Build)
        rootCommand

    let private assertParses args =
        let parseResult = GraceCommand.rootCommand.Parse(withIds args)
        parseResult.Errors.Count |> should equal 0
        parseResult

    let private assertDoesNotParse args =
        let parseResult = GraceCommand.rootCommand.Parse(withIds args)
        Assert.That(parseResult.Errors.Count, Is.GreaterThan(0))
        parseResult

    let private assertRepositoryCommandParses args =
        let parseResult = GraceCommand.rootCommand.Parse(withRepositoryIds args)
        parseResult.Errors.Count |> should equal 0
        parseResult

    let private assertRepositoryCommandDoesNotParse args =
        let parseResult = GraceCommand.rootCommand.Parse(withRepositoryIds args)
        Assert.That(parseResult.Errors.Count, Is.GreaterThan(0))
        parseResult

    let private assertReferenceSourceParses args =
        let parseResult = referenceSourceRoot.Parse(withIds args)
        parseResult.Errors.Count |> should equal 0
        parseResult

    let private assertReferenceSourceDoesNotParse args =
        let parseResult = referenceSourceRoot.Parse(withIds args)
        Assert.That(parseResult.Errors.Count, Is.GreaterThan(0))
        parseResult

    [<Test>]
    let ``branch annotate parses V1 options`` () =
        assertParses [| "branch"
                        "annotate"
                        "--path"
                        "src/App.fs"
                        "--reference-id"
                        referenceId.ToString()
                        "-L"
                        "10,12"
                        "--reference-types"
                        "Commit, Promotion"
                        "--show"
                        "both"
                        "--max-references"
                        "250" |]
        |> ignore

    [<Test>]
    let ``branch annotate requires path`` () =
        assertDoesNotParse [| "branch"
                              "annotate"
                              "--reference-id"
                              referenceId.ToString() |]
        |> ignore

    [<TestCase("last-changed")>]
    [<TestCase("introduced")>]
    [<TestCase("both")>]
    let ``branch annotate show accepts V1 human modes`` showMode =
        assertParses [| "branch"
                        "annotate"
                        "--path"
                        "src/App.fs"
                        "--show"
                        showMode |]
        |> ignore

    [<Test>]
    let ``branch annotate rejects unsupported show mode`` () =
        assertDoesNotParse [| "branch"
                              "annotate"
                              "--path"
                              "src/App.fs"
                              "--show"
                              "source" |]
        |> ignore

    [<TestCase("switch")>]
    [<TestCase("list-contents")>]
    [<TestCase("get-recursive-size")>]
    let ``branch version locator commands parse BLAKE3 hash option`` commandName =
        assertParses [| "branch"
                        commandName
                        "--blake3-hash"
                        blake3Hash |]
        |> ignore

    [<TestCase("switch")>]
    [<TestCase("list-contents")>]
    [<TestCase("get-recursive-size")>]
    let ``branch version locator commands parse display hash modes independently from lookup`` commandName =
        let parseResult =
            assertParses [| "branch"
                            commandName
                            "--blake3-hash"
                            blake3Hash
                            "--full-hashes"
                            "--show-sha256"
                            "--output"
                            "Json" |]

        Common.HashOptions.bindVersionHashLookupMode parseResult
        |> should equal (Common.HashOptions.Blake3VersionHashLookup blake3Hash)

        let displayMode = Common.HashOptions.bindVersionHashDisplayMode parseResult
        displayMode.FullHashes |> should equal true
        displayMode.ShowSha256 |> should equal true

        displayMode.UsedDeprecatedFullSha
        |> should equal false

    [<Test>]
    let ``branch version locator commands bind deprecated full sha as compatibility display mode`` () =
        let parseResult =
            assertParses [| "branch"
                            "list-contents"
                            "--sha256-hash"
                            sha256Hash
                            "--full-sha" |]

        Common.HashOptions.bindVersionHashLookupMode parseResult
        |> should equal (Common.HashOptions.Sha256CompatibilityVersionHashLookup sha256Hash)

        let displayMode = Common.HashOptions.bindVersionHashDisplayMode parseResult
        displayMode.FullHashes |> should equal true
        displayMode.ShowSha256 |> should equal true

        displayMode.UsedDeprecatedFullSha
        |> should equal true

    [<Test>]
    let ``branch version hash lookup accepts shortest prefix and exact full hash with normalization`` () =
        let prefixResult =
            assertParses [| "branch"
                            "switch"
                            "--blake3-hash"
                            shortestHashPrefix |]

        Common.HashOptions.bindVersionHashLookupMode prefixResult
        |> should equal (Common.HashOptions.Blake3VersionHashLookup shortestHashPrefix)

        let fullResult =
            assertParses [| "branch"
                            "switch"
                            "--sha256-hash"
                            uppercaseSha256Hash |]

        Common.HashOptions.bindVersionHashLookupMode fullResult
        |> should equal (Common.HashOptions.Sha256CompatibilityVersionHashLookup sha256Hash)

    [<TestCase("--sha256-hash", "ab", TestName = "branch version hash accepts 2-character SHA-256 prefix")>]
    [<TestCase("--sha256-hash", "abc", TestName = "branch version hash accepts 3-character SHA-256 prefix")>]
    [<TestCase("--sha256-hash", "abc1234", TestName = "branch version hash accepts 7-character SHA-256 prefix")>]
    [<TestCase("--blake3-hash", "ab", TestName = "branch version hash accepts 2-character BLAKE3 prefix")>]
    [<TestCase("--blake3-hash", "abc", TestName = "branch version hash accepts 3-character BLAKE3 prefix")>]
    [<TestCase("--blake3-hash", "abc1234", TestName = "branch version hash accepts 7-character BLAKE3 prefix")>]
    let ``branch version hash lookup accepts server-valid short prefixes`` optionName value =
        assertParses [| "branch"
                        "switch"
                        optionName
                        value |]
        |> ignore

    [<TestCase("--sha256-hash", "a", TestName = "branch version hash rejects too short SHA-256 prefix")>]
    [<TestCase("--sha256-hash", "not-a-hex-hash", TestName = "branch version hash rejects malformed SHA-256 prefix")>]
    [<TestCase("--blake3-hash", "a", TestName = "branch version hash rejects too short BLAKE3 prefix")>]
    [<TestCase("--blake3-hash", "xyz12345", TestName = "branch version hash rejects malformed BLAKE3 prefix")>]
    let ``branch version hash lookup rejects malformed values`` optionName value =
        assertDoesNotParse [| "branch"
                              "switch"
                              optionName
                              value |]
        |> ignore

    [<Test>]
    let ``branch version hash lookup rejects overlong values`` () =
        assertDoesNotParse [| "branch"
                              "switch"
                              "--blake3-hash"
                              String.replicate 65 "a" |]
        |> ignore

    [<Test>]
    let ``branch version hash lookup rejects generic hash option`` () =
        assertDoesNotParse [| "branch"
                              "switch"
                              "--hash"
                              sha256Hash |]
        |> ignore

    [<Test>]
    let ``branch assign parses BLAKE3 hash option`` () =
        assertParses [| "branch"
                        "assign"
                        "--blake3-hash"
                        blake3Hash |]
        |> ignore

    [<Test>]
    let ``branch switch keeps branch name precedence options available beside BLAKE3 locator`` () =
        assertParses [| "branch"
                        "switch"
                        "--to-branch-name"
                        "af1349b9"
                        "--blake3-hash"
                        blake3Hash |]
        |> ignore

    [<Test>]
    let ``branch switch preserves SHA-256 and BLAKE3 locator evidence`` () =
        let parseResult =
            assertParses [| "branch"
                            "switch"
                            "--sha256-hash"
                            sha256Hash
                            "--blake3-hash"
                            blake3Hash |]

        parseResult.GetValue<string>("--sha256-hash")
        |> should equal sha256Hash

        parseResult.GetValue<string>("--blake3-hash")
        |> should equal blake3Hash

        Branch.switchHashLocatorEvidence sha256Hash blake3Hash
        |> should equal (sha256Hash, blake3Hash)

        Common.HashOptions.bindVersionHashLookupMode parseResult
        |> should equal (Common.HashOptions.PairedVersionHashLookup(sha256Hash, blake3Hash))

    [<Test>]
    let ``branch switch hash locator evidence supports single-hash forms`` () =
        Branch.switchHashLocatorEvidence sha256Hash String.Empty
        |> should equal (sha256Hash, String.Empty)

        Branch.switchHashLocatorEvidence String.Empty blake3Hash
        |> should equal (String.Empty, blake3Hash)

    [<Test>]
    let ``reference assign parses BLAKE3 hash option`` () =
        let parseResult =
            assertReferenceSourceParses [| "reference"
                                           "assign"
                                           "--blake3-hash"
                                           blake3Hash |]

        parseResult.GetValue<string>("--blake3-hash")
        |> should equal blake3Hash

        Common.HashOptions.bindVersionHashLookupMode parseResult
        |> should equal (Common.HashOptions.Blake3VersionHashLookup blake3Hash)

    [<Test>]
    let ``reference assign preserves SHA-256 and BLAKE3 locator evidence`` () =
        let parseResult =
            assertReferenceSourceParses [| "reference"
                                           "assign"
                                           "--sha256-hash"
                                           sha256Hash
                                           "--blake3-hash"
                                           blake3Hash |]

        parseResult.GetValue<string>("--sha256-hash")
        |> should equal sha256Hash

        parseResult.GetValue<string>("--blake3-hash")
        |> should equal blake3Hash

        Common.HashOptions.bindVersionHashLookupMode parseResult
        |> should equal (Common.HashOptions.PairedVersionHashLookup(sha256Hash, blake3Hash))

    [<Test>]
    let ``reference assign preserves message in assign parameters`` () =
        let message = "Promote CLI candidate"

        let parseResult =
            assertReferenceSourceParses [| "reference"
                                           "assign"
                                           "--sha256-hash"
                                           sha256Hash
                                           "--message"
                                           message |]

        let parameters = Reference.buildAssignParameters parseResult

        parameters.Sha256Hash |> should equal sha256Hash
        parameters.Message |> should equal message

    [<Test>]
    let ``reference assign normalizes hash option values in parameters`` () =
        let parseResult =
            assertReferenceSourceParses [| "reference"
                                           "assign"
                                           "--sha256-hash"
                                           uppercaseSha256Hash
                                           "--message"
                                           "Promote CLI candidate" |]

        let parameters = Reference.buildAssignParameters parseResult

        parameters.Sha256Hash |> should equal sha256Hash

    [<Test>]
    let ``reference assign rejects malformed BLAKE3 option values`` () =
        assertReferenceSourceDoesNotParse [| "reference"
                                             "assign"
                                             "--blake3-hash"
                                             "not-blake3"
                                             "--message"
                                             "Promote CLI candidate" |]
        |> ignore

    [<Test>]
    let ``diff hash commands parse validated SHA-256 and BLAKE3 prefixes`` () =
        assertRepositoryCommandParses [| "diff"
                                         "sha"
                                         "--sha256-hash-1"
                                         shortHashPrefix
                                         "--sha256-hash-2"
                                         sha256Hash |]
        |> ignore

        assertRepositoryCommandParses [| "diff"
                                         "blake3"
                                         "--blake3-hash-1"
                                         shortHashPrefix
                                         "--blake3-hash-2"
                                         blake3Hash |]
        |> ignore

    [<Test>]
    let ``diff hash commands reject malformed BLAKE3 prefixes during parse`` () =
        assertRepositoryCommandDoesNotParse [| "diff"
                                               "blake3"
                                               "--blake3-hash-1"
                                               "not-blake3"
                                               "--blake3-hash-2"
                                               blake3Hash |]
        |> ignore

    [<Test>]
    let ``directory version zip command rejects malformed SHA-256 prefix during parse`` () =
        assertRepositoryCommandDoesNotParse [| "directory-version"
                                               "get-zip-file"
                                               "--directory-version-id"
                                               Guid.NewGuid().ToString()
                                               "--sha256-hash"
                                               "not-sha" |]
        |> ignore

    [<Test>]
    let ``source-only reference commands are not exposed from root command`` () =
        for commandName in
            [|
                "assign"
                "checkpoint"
                "commit"
                "create-external"
                "delete"
                "get"
                "promote"
                "save"
                "tag"
            |] do
            let parseResult =
                assertDoesNotParse [| "reference"
                                      commandName |]

            parseResult.Errors
            |> Seq.exists (fun error -> error.Message.Contains("Unrecognized command or argument 'reference'", StringComparison.OrdinalIgnoreCase))
            |> should equal true

    [<Test>]
    let ``forbidden branch annotate V1 options are unavailable`` () =
        for forbiddenOption in
            [|
                "--current-branch-only"
                "--ignore-whitespace"
                "--no-save"
                "--force-save"
            |] do
            let parseResult =
                GraceCommand.rootCommand.Parse(
                    withIds [| "branch"
                               "annotate"
                               "--path"
                               "src/App.fs"
                               forbiddenOption |]
                )

            Assert.That(parseResult.Errors.Count, Is.GreaterThan(0), forbiddenOption)
