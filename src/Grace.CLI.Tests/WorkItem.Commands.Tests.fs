namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open NUnit.Framework
open System

[<NonParallelizable>]
module WorkItemCommandTests =
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
    let ``work create parses`` () =
        let parseResult = GraceCommand.rootCommand.Parse(withIds [| "work"; "create"; "--title"; "Test work" |])

        parseResult.Errors.Count |> should equal 0

    [<Test>]
    let ``work show rejects invalid work item id`` () =
        let parseResult = GraceCommand.rootCommand.Parse(withIdsAndSilent [| "work"; "show"; "not-a-guid" |])
        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    [<Test>]
    let ``work link ref rejects invalid reference id`` () =
        let parseResult = GraceCommand.rootCommand.Parse(withIdsAndSilent [| "work"; "link"; "ref"; Guid.NewGuid().ToString(); "not-a-guid" |])
        let exitCode = parseResult.Invoke()

        exitCode |> should equal -1
