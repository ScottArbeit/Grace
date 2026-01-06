namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open NUnit.Framework
open System

[<NonParallelizable>]
module ReviewCommandTests =
    let private ownerId = Guid.NewGuid()
    let private organizationId = Guid.NewGuid()
    let private repositoryId = Guid.NewGuid()

    let private withIds (args: string array) =
        Array.append
            args
            [| "--owner-id"
               ownerId.ToString()
               "--organization-id"
               organizationId.ToString()
               "--repository-id"
               repositoryId.ToString() |]

    let private withIdsAndSilent (args: string array) = args |> Array.append [| "--output"; "Silent" |] |> withIds

    [<Test>]
    let ``review open rejects invalid candidate id`` () =
        let parseResult = GraceCommand.rootCommand.Parse(withIdsAndSilent [| "review"; "open"; "--candidate"; "not-a-guid" |])

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    [<Test>]
    let ``review checkpoint rejects invalid reference id`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent
                    [| "review"
                       "checkpoint"
                       "--candidate"
                       Guid.NewGuid().ToString()
                       "--reference-id"
                       "not-a-guid"
                       "--policy-snapshot-id"
                       "snapshot" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    [<Test>]
    let ``review resolve requires resolution state`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent
                    [| "review"
                       "resolve"
                       "--candidate"
                       Guid.NewGuid().ToString()
                       "--finding-id"
                       Guid.NewGuid().ToString() |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1
