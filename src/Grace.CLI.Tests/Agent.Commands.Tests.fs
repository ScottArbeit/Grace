namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open NUnit.Framework
open System
open System.IO

[<NonParallelizable>]
module AgentCommandTests =
    let private ownerId = Guid.NewGuid()
    let private organizationId = Guid.NewGuid()
    let private repositoryId = Guid.NewGuid()

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

    let private withIdsAndSilent (args: string array) =
        args
        |> Array.append [| "--output"; "Silent" |]
        |> withIds

    [<Test>]
    let ``agent add-summary rejects invalid work item id`` () =
        let missingSummary = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.md")

        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "agent"
                                    "add-summary"
                                    "--work-item-id"
                                    "not-a-guid"
                                    "--summary-file"
                                    missingSummary |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    [<Test>]
    let ``agent add-summary rejects missing summary file`` () =
        let missingSummary = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.md")

        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "agent"
                                    "add-summary"
                                    "--work-item-id"
                                    Guid.NewGuid().ToString()
                                    "--summary-file"
                                    missingSummary |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1
