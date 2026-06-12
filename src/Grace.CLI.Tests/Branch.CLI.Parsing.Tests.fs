namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open NUnit.Framework
open System

[<Parallelizable(ParallelScope.All)>]
module BranchCommandParsingTests =
    let private ownerId = Guid.NewGuid()
    let private organizationId = Guid.NewGuid()
    let private repositoryId = Guid.NewGuid()
    let private branchId = Guid.NewGuid()
    let private referenceId = Guid.NewGuid()
    let private sha256Hash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
    let private blake3Hash = "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adcd1e8c76d9a8885f16a39f"

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

    let private assertParses args =
        let parseResult = GraceCommand.rootCommand.Parse(withIds args)
        parseResult.Errors.Count |> should equal 0
        parseResult

    let private assertDoesNotParse args =
        let parseResult = GraceCommand.rootCommand.Parse(withIds args)
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

    [<Test>]
    let ``branch switch hash locator evidence supports single-hash forms`` () =
        Branch.switchHashLocatorEvidence sha256Hash String.Empty
        |> should equal (sha256Hash, String.Empty)

        Branch.switchHashLocatorEvidence String.Empty blake3Hash
        |> should equal (String.Empty, blake3Hash)

    [<Test>]
    let ``reference assign parses BLAKE3 hash option`` () =
        let parseResult =
            assertParses [| "reference"
                            "assign"
                            "--blake3-hash"
                            blake3Hash |]

        parseResult.GetValue<string>("--blake3-hash")
        |> should equal blake3Hash

    [<Test>]
    let ``reference assign preserves SHA-256 and BLAKE3 locator evidence`` () =
        let parseResult =
            assertParses [| "reference"
                            "assign"
                            "--sha256-hash"
                            sha256Hash
                            "--blake3-hash"
                            blake3Hash |]

        parseResult.GetValue<string>("--sha256-hash")
        |> should equal sha256Hash

        parseResult.GetValue<string>("--blake3-hash")
        |> should equal blake3Hash

    [<Test>]
    let ``reference assign preserves message in assign parameters`` () =
        let message = "Promote CLI candidate"

        let parseResult =
            assertParses [| "reference"
                            "assign"
                            "--sha256-hash"
                            sha256Hash
                            "--message"
                            message |]

        let parameters = Reference.buildAssignParameters parseResult

        parameters.Sha256Hash |> should equal sha256Hash
        parameters.Message |> should equal message

    [<Test>]
    let ``reference delete is not exposed from root command`` () =
        let parseResult =
            assertDoesNotParse [| "reference"
                                  "delete" |]

        parseResult.Errors
        |> Seq.exists (fun error -> error.Message.Contains("Unrecognized command or argument 'delete'", StringComparison.OrdinalIgnoreCase))
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
