namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open NUnit.Framework
open System

[<NonParallelizable>]
module QueueCommandTests =
    let private ownerId = Guid.NewGuid()
    let private organizationId = Guid.NewGuid()
    let private repositoryId = Guid.NewGuid()
    let private branchId = Guid.NewGuid()

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
    let ``queue enqueue rejects invalid candidate id`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "queue"
                                    "enqueue"
                                    "--branch-id"
                                    branchId.ToString()
                                    "--candidate"
                                    "not-a-guid" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    [<Test>]
    let ``queue enqueue rejects invalid work item id`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "queue"
                                    "enqueue"
                                    "--branch-id"
                                    branchId.ToString()
                                    "--work"
                                    "not-a-guid" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    [<Test>]
    let ``queue dequeue rejects invalid candidate id`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "queue"
                                    "dequeue"
                                    "--branch-id"
                                    branchId.ToString()
                                    "--candidate"
                                    "not-a-guid" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    [<Test>]
    let ``queue retry rejects invalid candidate id`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "queue"
                                    "retry"
                                    "--branch-id"
                                    branchId.ToString()
                                    "--candidate"
                                    "not-a-guid" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1
