namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open NUnit.Framework
open System
open System.IO

[<NonParallelizable>]
module PromotionSetCommandTests =
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
    let ``promotion-set conflicts show rejects invalid promotion set id`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "promotion-set"
                                    "conflicts"
                                    "show"
                                    "--promotion-set"
                                    "not-a-guid" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    [<Test>]
    let ``promotion-set conflicts resolve rejects invalid step id`` () =
        let tempFile = Path.GetTempFileName()

        try
            File.WriteAllText(tempFile, "[]")

            let parseResult =
                GraceCommand.rootCommand.Parse(
                    withIdsAndSilent [| "promotion-set"
                                        "conflicts"
                                        "resolve"
                                        "--promotion-set"
                                        (Guid.NewGuid().ToString())
                                        "--step"
                                        "not-a-guid"
                                        "--decisions-file"
                                        tempFile |]
                )

            let exitCode = parseResult.Invoke()
            exitCode |> should equal -1
        finally
            if File.Exists tempFile then File.Delete tempFile

    [<Test>]
    let ``promotion-set conflicts resolve rejects missing decisions file`` () =
        let missingFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json")

        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "promotion-set"
                                    "conflicts"
                                    "resolve"
                                    "--promotion-set"
                                    (Guid.NewGuid().ToString())
                                    "--step"
                                    (Guid.NewGuid().ToString())
                                    "--decisions-file"
                                    missingFile |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    [<Test>]
    let ``promotion-set conflicts resolve rejects malformed decisions file`` () =
        let tempFile = Path.GetTempFileName()

        try
            File.WriteAllText(tempFile, "this is not json")

            let parseResult =
                GraceCommand.rootCommand.Parse(
                    withIdsAndSilent [| "promotion-set"
                                        "conflicts"
                                        "resolve"
                                        "--promotion-set"
                                        (Guid.NewGuid().ToString())
                                        "--step"
                                        (Guid.NewGuid().ToString())
                                        "--decisions-file"
                                        tempFile |]
                )

            let exitCode = parseResult.Invoke()
            exitCode |> should equal -1
        finally
            if File.Exists tempFile then File.Delete tempFile
