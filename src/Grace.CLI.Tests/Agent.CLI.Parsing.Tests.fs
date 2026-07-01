namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open NUnit.Framework
open System

/// Groups agent command parsing coverage for the CLI test project.
[<Parallelizable(ParallelScope.All)>]
module AgentCommandParsingTests =
    let private ownerId = Guid.NewGuid()
    let private organizationId = Guid.NewGuid()
    let private repositoryId = Guid.NewGuid()

    /// Runs the supplied action with ids applied.
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
            |]

    /// Verifies that agent add summary accepts numeric work item identifier.
    [<Test>]
    let ``agent add-summary accepts numeric work item identifier`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIds [| "agent"
                           "add-summary"
                           "--work-item-id"
                           "123"
                           "--summary-file"
                           "C:\\temp\\missing-summary.md" |]
            )

        parseResult.Errors.Count |> should equal 0

    /// Verifies that agent add summary accepts guid work item identifier.
    [<Test>]
    let ``agent add-summary accepts guid work item identifier`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIds [| "agent"
                           "add-summary"
                           "--work-item-id"
                           (Guid.NewGuid().ToString())
                           "--summary-file"
                           "C:\\temp\\missing-summary.md" |]
            )

        parseResult.Errors.Count |> should equal 0

    /// Verifies that agent add summary accepts prompt and promotion set options with numeric identifier.
    [<Test>]
    let ``agent add-summary accepts prompt and promotion-set options with numeric identifier`` () =
        let promotionSetId = Guid.NewGuid().ToString()

        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIds [| "agent"
                           "add-summary"
                           "--work-item-id"
                           "42"
                           "--summary-file"
                           "C:\\temp\\missing-summary.md"
                           "--prompt-file"
                           "C:\\temp\\missing-prompt.md"
                           "--prompt-origin"
                           "agent://codex"
                           "--promotion-set-id"
                           promotionSetId |]
            )

        parseResult.Errors.Count |> should equal 0
