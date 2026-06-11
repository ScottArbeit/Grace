namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open NUnit.Framework
open System

[<Parallelizable(ParallelScope.All)>]
module BranchCommandParsingTests =
    let private ownerId = Guid.NewGuid()
    let private organizationId = Guid.NewGuid()
    let private repositoryId = Guid.NewGuid()
    let private branchId = Guid.NewGuid()
    let private referenceId = Guid.NewGuid()
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
