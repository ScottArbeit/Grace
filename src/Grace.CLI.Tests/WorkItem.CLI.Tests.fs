namespace Grace.CLI.Tests

open FsCheck.NUnit
open FsUnit
open Grace.CLI
open NUnit.Framework
open System
open System.Collections.Generic

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

    let private assertParsesWithoutErrors (args: string array) =
        let parseResult = GraceCommand.rootCommand.Parse(args)
        parseResult.Errors.Count |> should equal 0

    let private buildAttachArgs (noun: string) (attachmentType: string) (workItemIdentifier: string) (extraArgs: string array) =
        [|
            noun
            "attach"
            attachmentType
            workItemIdentifier
            yield! extraArgs
        |]

    let private buildAttachmentsArgs (noun: string) (verb: string) (workItemIdentifier: string) (extraArgs: string array) =
        [|
            noun
            "attachments"
            verb
            workItemIdentifier
            yield! extraArgs
        |]

    [<Test>]
    let ``workitem create parses`` () =
        assertParsesWithoutErrors (
            withIds [| "workitem"
                       "create"
                       "--title"
                       "Test work" |]
        )

    [<Test>]
    let ``work alias still parses`` () =
        assertParsesWithoutErrors (
            withIds [| "work"
                       "create"
                       "--title"
                       "Alias still works" |]
        )

    [<TestCase("workitem")>]
    [<TestCase("work")>]
    [<TestCase("work-item")>]
    [<TestCase("wi")>]
    let ``all work item command aliases parse create`` (commandAlias: string) =
        assertParsesWithoutErrors (
            withIds [| commandAlias
                       "create"
                       "--title"
                       "Alias command" |]
        )

    [<TestCase("workitem", "40")>]
    [<TestCase("workitem", "9e4c0f72-9b4f-4f28-8d8f-d7d73ec4f6fd")>]
    [<TestCase("wi", "41")>]
    [<TestCase("work-item", "4f2e4a67-4b51-4c7a-b866-f82638852e9d")>]
    let ``workitem link ref parses for guid and numeric work item identifiers`` (commandAlias: string, workItemIdentifier: string) =
        assertParsesWithoutErrors (
            withIds [| commandAlias
                       "link"
                       "ref"
                       workItemIdentifier
                       Guid.NewGuid().ToString() |]
        )

    [<TestCase("workitem", "42")>]
    [<TestCase("workitem", "f4b59cad-8d03-4a39-b1ff-8bcaf3e609d6")>]
    [<TestCase("wi", "43")>]
    [<TestCase("work", "4caedab7-2472-4df2-a948-94e8e89f2f77")>]
    let ``workitem link prset parses for guid and numeric work item identifiers`` (commandAlias: string, workItemIdentifier: string) =
        assertParsesWithoutErrors (
            withIds [| commandAlias
                       "link"
                       "prset"
                       workItemIdentifier
                       Guid.NewGuid().ToString() |]
        )

    [<TestCase("workitem", "summary")>]
    [<TestCase("workitem", "prompt")>]
    [<TestCase("workitem", "notes")>]
    [<TestCase("wi", "summary")>]
    [<TestCase("work-item", "prompt")>]
    let ``workitem attach parses with file text and stdin modes`` (commandAlias: string, attachmentType: string) =
        let workItemIdentifier = Guid.NewGuid().ToString()

        let fileArgs =
            buildAttachArgs
                commandAlias
                attachmentType
                workItemIdentifier
                [|
                    "--file"
                    "C:\\temp\\attachment.txt"
                |]
            |> withIds

        let textArgs =
            buildAttachArgs commandAlias attachmentType workItemIdentifier [| "--text"; "inline content" |]
            |> withIds

        let stdinArgs =
            buildAttachArgs commandAlias attachmentType workItemIdentifier [| "--stdin" |]
            |> withIds

        assertParsesWithoutErrors fileArgs
        assertParsesWithoutErrors textArgs
        assertParsesWithoutErrors stdinArgs

    [<TestCase("workitem", "44")>]
    [<TestCase("workitem", "02f8563a-8508-4fdb-a55f-3a326d2be3e0")>]
    [<TestCase("work", "45")>]
    [<TestCase("wi", "d0ac8efe-5f60-4a4f-9563-30dfd8fd2f3e")>]
    let ``workitem links list parses for guid and numeric work item identifiers`` (commandAlias: string, workItemIdentifier: string) =
        assertParsesWithoutErrors (
            withIds [| commandAlias
                       "links"
                       "list"
                       workItemIdentifier |]
        )

    [<TestCase("workitem", "52")>]
    [<TestCase("workitem", "9dfdb7a5-27f6-4fd8-95cf-f5e4f2b22803")>]
    [<TestCase("work", "53")>]
    [<TestCase("wi", "9761ae11-ec40-4c2a-a6e7-e13001642f8e")>]
    let ``workitem attachments list parses for guid and numeric work item identifiers`` (commandAlias: string, workItemIdentifier: string) =
        assertParsesWithoutErrors (
            buildAttachmentsArgs commandAlias "list" workItemIdentifier [||]
            |> withIds
        )

    [<TestCase("workitem", "summary", "54", true)>]
    [<TestCase("workitem", "prompt", "36f74308-b75c-4a2a-bf2f-fe3e2036b232", false)>]
    [<TestCase("work", "notes", "55", true)>]
    [<TestCase("wi", "summary", "16dd0b9b-00eb-480f-bf9c-8cfdad68f249", false)>]
    let ``workitem attachments show parses with type and latest options``
        (
            commandAlias: string,
            attachmentType: string,
            workItemIdentifier: string,
            includeLatest: bool
        )
        =
        let extraArgs = ResizeArray<string>()
        extraArgs.Add("--type")
        extraArgs.Add(attachmentType)

        if includeLatest then extraArgs.Add("--latest")

        assertParsesWithoutErrors (
            buildAttachmentsArgs commandAlias "show" workItemIdentifier (extraArgs.ToArray())
            |> withIds
        )

    [<TestCase("workitem", "56")>]
    [<TestCase("workitem", "f4cf5f70-f4ff-461f-8f2d-5be9734b5b7f")>]
    [<TestCase("work-item", "57")>]
    [<TestCase("wi", "b87d5076-6467-4ef6-93f5-8ee7f014295c")>]
    let ``workitem attachments download parses with artifact id and output file`` (commandAlias: string, workItemIdentifier: string) =
        assertParsesWithoutErrors (
            buildAttachmentsArgs
                commandAlias
                "download"
                workItemIdentifier
                [|
                    "--artifact-id"
                    Guid.NewGuid().ToString()
                    "--output-file"
                    "C:\\temp\\attachment.bin"
                |]
            |> withIds
        )

    [<TestCase("workitem", "46")>]
    [<TestCase("workitem", "f4bc1e7f-5d7a-4f54-a80f-e2d36dc19374")>]
    [<TestCase("wi", "47")>]
    let ``workitem links remove ref parses for guid and numeric work item identifiers`` (commandAlias: string, workItemIdentifier: string) =
        assertParsesWithoutErrors (
            withIds [| commandAlias
                       "links"
                       "remove"
                       "ref"
                       workItemIdentifier
                       Guid.NewGuid().ToString() |]
        )

    [<TestCase("workitem", "48")>]
    [<TestCase("workitem", "8b684baf-3fe4-4829-b2e8-a67d8c63d1b6")>]
    [<TestCase("work-item", "49")>]
    let ``workitem links remove prset parses for guid and numeric work item identifiers`` (commandAlias: string, workItemIdentifier: string) =
        assertParsesWithoutErrors (
            withIds [| commandAlias
                       "links"
                       "remove"
                       "prset"
                       workItemIdentifier
                       Guid.NewGuid().ToString() |]
        )

    [<TestCase("workitem", "summary", "50")>]
    [<TestCase("workitem", "prompt", "6a635cbe-19ce-4e5f-a0fd-f1c1d1d468ea")>]
    [<TestCase("wi", "notes", "51")>]
    [<TestCase("work", "summary", "fdb37dfa-699d-4f8f-80f0-6e2eb6222596")>]
    let ``workitem links remove artifact-type aliases parse for guid and numeric work item identifiers``
        (
            commandAlias: string,
            linkType: string,
            workItemIdentifier: string
        )
        =
        assertParsesWithoutErrors (
            withIds [| commandAlias
                       "links"
                       "remove"
                       linkType
                       workItemIdentifier |]
        )

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
    let ``work link artifact command is unavailable`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "work"; "link"; "artifact" |])

        Assert.That(parseResult.Errors.Count, Is.GreaterThan(0))

        let hasArtifactError =
            parseResult.Errors
            |> Seq.exists (fun error -> error.Message.Contains("Unrecognized command or argument 'artifact'", StringComparison.OrdinalIgnoreCase))

        Assert.That(hasArtifactError, Is.True)

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
    let ``workitem attachments show rejects invalid type values during parse`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIds [| "workitem"
                           "attachments"
                           "show"
                           Guid.NewGuid().ToString()
                           "--type"
                           "binary" |]
            )

        Assert.That(parseResult.Errors.Count, Is.GreaterThan(0))

    [<Test>]
    let ``workitem attachments download requires artifact id and output file options`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIds [| "workitem"
                           "attachments"
                           "download"
                           Guid.NewGuid().ToString() |]
            )

        Assert.That(parseResult.Errors.Count, Is.GreaterThan(0))

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

    [<Test>]
    let ``workitem links remove summary parses numeric work item`` () =
        assertParsesWithoutErrors (
            withIds [| "workitem"
                       "links"
                       "remove"
                       "summary"
                       "123" |]
        )

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
