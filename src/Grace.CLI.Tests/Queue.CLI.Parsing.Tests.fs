namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open NUnit.Framework
open System

[<Parallelizable(ParallelScope.All)>]
module QueueCommandParsingTests =
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

    [<Test>]
    let ``queue enqueue accepts numeric work item identifier`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIds [| "queue"
                           "enqueue"
                           "--branch-id"
                           branchId.ToString()
                           "--work"
                           "42" |]
            )

        parseResult.Errors.Count |> should equal 0

    [<Test>]
    let ``queue retry command is unavailable`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "queue"; "retry" |])

        Assert.That(parseResult.Errors.Count, Is.GreaterThan(0))

        let hasRetryError =
            parseResult.Errors
            |> Seq.exists (fun error -> error.Message.Contains("Unrecognized command or argument 'retry'", StringComparison.OrdinalIgnoreCase))

        Assert.That(hasRetryError, Is.True)
