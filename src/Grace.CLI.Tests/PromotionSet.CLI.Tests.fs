namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open NUnit.Framework
open System
open System.IO

/// Groups promotion set command coverage for the CLI test project.
[<NonParallelizable>]
module PromotionSetCommandTests =
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

    /// Runs the supplied action with ids and silent applied.
    let private withIdsAndSilent (args: string array) =
        args
        |> Array.append [| "--output"; "Silent" |]
        |> withIds

    /// Verifies that promotion set create rejects invalid promotion set id.
    [<Test>]
    let ``promotion-set create rejects invalid promotion set id`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "promotion-set"
                                    "create"
                                    "--promotion-set"
                                    "not-a-guid"
                                    "--target-branch-id"
                                    (Guid.NewGuid().ToString()) |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    /// Verifies that promotion set create rejects invalid target branch id.
    [<Test>]
    let ``promotion-set create rejects invalid target branch id`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "promotion-set"
                                    "create"
                                    "--target-branch-id"
                                    "not-a-guid" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    /// Verifies that promotion set get rejects invalid promotion set id.
    [<Test>]
    let ``promotion-set get rejects invalid promotion set id`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "promotion-set"
                                    "get"
                                    "--promotion-set"
                                    "not-a-guid" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    /// Verifies that promotion set get events rejects invalid promotion set id.
    [<Test>]
    let ``promotion-set get-events rejects invalid promotion set id`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "promotion-set"
                                    "get-events"
                                    "--promotion-set"
                                    "not-a-guid" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    /// Verifies that promotion set update input promotions rejects invalid promotion set id.
    [<Test>]
    let ``promotion-set update-input-promotions rejects invalid promotion set id`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "promotion-set"
                                    "update-input-promotions"
                                    "--promotion-set"
                                    "not-a-guid"
                                    "--promotion-pointers-file"
                                    "pointers.json" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    /// Verifies that promotion set update input promotions rejects missing pointers file.
    [<Test>]
    let ``promotion-set update-input-promotions rejects missing pointers file`` () =
        let missingFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json")

        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "promotion-set"
                                    "update-input-promotions"
                                    "--promotion-set"
                                    (Guid.NewGuid().ToString())
                                    "--promotion-pointers-file"
                                    missingFile |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    /// Verifies that promotion set update input promotions rejects malformed pointers file.
    [<Test>]
    let ``promotion-set update-input-promotions rejects malformed pointers file`` () =
        let tempFile = Path.GetTempFileName()

        try
            File.WriteAllText(tempFile, "this is not json")

            let parseResult =
                GraceCommand.rootCommand.Parse(
                    withIdsAndSilent [| "promotion-set"
                                        "update-input-promotions"
                                        "--promotion-set"
                                        (Guid.NewGuid().ToString())
                                        "--promotion-pointers-file"
                                        tempFile |]
                )

            let exitCode = parseResult.Invoke()
            exitCode |> should equal -1
        finally
            if File.Exists tempFile then File.Delete tempFile

    /// Verifies that promotion set recompute rejects invalid promotion set id.
    [<Test>]
    let ``promotion-set recompute rejects invalid promotion set id`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "promotion-set"
                                    "recompute"
                                    "--promotion-set"
                                    "not-a-guid" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    /// Verifies that promotion set apply rejects invalid promotion set id.
    [<Test>]
    let ``promotion-set apply rejects invalid promotion set id`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "promotion-set"
                                    "apply"
                                    "--promotion-set"
                                    "not-a-guid" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    /// Verifies that promotion set delete rejects invalid promotion set id.
    [<Test>]
    let ``promotion-set delete rejects invalid promotion set id`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "promotion-set"
                                    "delete"
                                    "--promotion-set"
                                    "not-a-guid" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    /// Verifies that promotion set conflicts show rejects invalid promotion set id.
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

    /// Verifies that promotion set conflicts resolve rejects invalid step id.
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

    /// Verifies that promotion set conflicts resolve rejects missing decisions file.
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

    /// Verifies that promotion set conflicts resolve rejects malformed decisions file.
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
