namespace Grace.CLI.Tests

open FsCheck.NUnit
open FsUnit
open Grace.CLI
open System
open System.Collections.Generic
open NUnit.Framework

[<NonParallelizable>]
module WorkItemCommandTests =
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
    let ``workitem show rejects invalid work item identifier`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "workitem"
                                    "show"
                                    "not-a-guid" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    [<Test>]
    let ``workitem link ref rejects invalid reference id`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "workitem"
                                    "link"
                                    "ref"
                                    Guid.NewGuid().ToString()
                                    "not-a-guid" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    [<Test>]
    let ``work link prset rejects invalid promotion set id`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "work"
                                    "link"
                                    "prset"
                                    Guid.NewGuid().ToString()
                                    "not-a-guid" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    [<Test>]
    let ``workitem attach summary requires exactly one input source`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "workitem"
                                    "attach"
                                    "summary"
                                    Guid.NewGuid().ToString() |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    [<Test>]
    let ``workitem attach summary rejects multiple input sources`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "workitem"
                                    "attach"
                                    "summary"
                                    Guid.NewGuid().ToString()
                                    "--text"
                                    "hello"
                                    "--stdin" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    [<Test>]
    let ``workitem attachments download rejects invalid artifact id`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "workitem"
                                    "attachments"
                                    "download"
                                    "58"
                                    "--artifact-id"
                                    "not-a-guid"
                                    "--output-file"
                                    "C:\\temp\\attachment.bin" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    [<Test>]
    let ``workitem attachments download rejects invalid output file path`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "workitem"
                                    "attachments"
                                    "download"
                                    "59"
                                    "--artifact-id"
                                    Guid.NewGuid().ToString()
                                    "--output-file"
                                    "C:\\temp\\invalid|name.bin" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    [<FsCheck.NUnit.Property(MaxTest = 64)>]
    let ``workitem attach input source combinations are valid iff exactly one is selected`` (useFile: bool) (useText: bool) (useStdin: bool) =
        let args = List<string>()
        args.Add("workitem")
        args.Add("attach")
        args.Add("summary")
        args.Add(Guid.NewGuid().ToString())

        if useFile then
            args.Add("--file")
            args.Add("C:\\temp\\summary.md")

        if useText then
            args.Add("--text")
            args.Add("inline summary")

        if useStdin then args.Add("--stdin")

        let selectedCount =
            (if useFile then 1 else 0)
            + (if useText then 1 else 0)
            + (if useStdin then 1 else 0)

        let parseResult =
            args.ToArray()
            |> withIdsAndSilent
            |> GraceCommand.rootCommand.Parse

        if selectedCount = 1 then
            parseResult.Errors.Count = 0
        else
            parseResult.Invoke() = -1
