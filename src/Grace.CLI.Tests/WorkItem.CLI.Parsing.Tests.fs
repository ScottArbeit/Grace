namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open NUnit.Framework
open System

/// Groups work item command parsing coverage for the CLI test project.
[<Parallelizable(ParallelScope.All)>]
module WorkItemCommandParsingTests =
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

    /// Asserts that parses without errors matches the expected contract.
    let private assertParsesWithoutErrors (args: string array) =
        let parseResult = GraceCommand.rootCommand.Parse(args)
        parseResult.Errors.Count |> should equal 0

    /// Asserts that the actual root parser rejects the supplied command line.
    let private assertRejected (args: string array) =
        let parseResult = GraceCommand.rootCommand.Parse(args)
        Assert.That(parseResult.Errors.Count, Is.GreaterThan(0))

    /// Builds attachments args for test scenarios.
    let private buildAttachmentsArgs (noun: string) (verb: string) (workItemIdentifier: string) (extraArgs: string array) =
        [|
            noun
            "attachments"
            verb
            workItemIdentifier
            yield! extraArgs
        |]

    /// Verifies that workitem create parses.
    [<Test>]
    let ``workitem create parses`` () =
        assertParsesWithoutErrors (
            withIds [| "workitem"
                       "create"
                       "--title"
                       "Test work" |]
        )

    /// Verifies that work alias still parses.
    [<Test>]
    let ``work alias still parses`` () =
        assertParsesWithoutErrors (
            withIds [| "work"
                       "create"
                       "--title"
                       "Alias still works" |]
        )

    /// Verifies that all work item command aliases parse create.
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

    /// Verifies that every supported status value routes through set-status for both work item identifier shapes.
    [<TestCase("42", "--status", "Active")>]
    [<TestCase("43", "--status", "Backlog")>]
    [<TestCase("44", "--status", "Blocked")>]
    [<TestCase("45", "--status", "Canceled")>]
    [<TestCase("46", "--status", "Done")>]
    [<TestCase("9e4c0f72-9b4f-4f28-8d8f-d7d73ec4f6fd", "-s", "InReview")>]
    let ``workitem set-status accepts every status exactly once plus guid and positive-number identifiers``
        (
            workItemIdentifier: string,
            statusOption: string,
            status: string
        )
        =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIds [| "workitem"
                           "set-status"
                           workItemIdentifier
                           statusOption
                           status |]
            )

        parseResult.Errors.Count |> should equal 0

        parseResult.CommandResult.Command.Name
        |> should equal "set-status"

    /// Verifies that missing and unsupported status values are rejected by the actual root parser.
    [<TestCase("workitem", "set-status", "42")>]
    [<TestCase("workitem", "set-status", "42", "--status")>]
    [<TestCase("workitem", "set-status", "42", "--status", "Unknown")>]
    let ``workitem set-status rejects missing or unsupported status values`` ([<ParamArray>] args: string array) = args |> withIds |> assertRejected

    /// Verifies that neither the old command nor the old option remains registered.
    [<TestCase("workitem", "status", "42", "--set", "Done")>]
    [<TestCase("workitem", "status", "42", "--status", "Done")>]
    [<TestCase("workitem", "set-status", "42", "--set", "Done")>]
    let ``workitem status and set option are unavailable`` ([<ParamArray>] args: string array) = args |> withIds |> assertRejected

    /// Verifies that workitem link ref parses for guid and numeric work item identifiers.
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

    /// Verifies that workitem link prset parses for guid and numeric work item identifiers.
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

    /// Verifies that the canonical attachment route accepts every type, input source, alias, and identifier shape.
    [<TestCase("workitem", "summary", "42")>]
    [<TestCase("work", "prompt", "43")>]
    [<TestCase("work-item", "notes", "9e4c0f72-9b4f-4f28-8d8f-d7d73ec4f6fd")>]
    [<TestCase("wi", "summary", "4f2e4a67-4b51-4c7a-b866-f82638852e9d")>]
    let ``workitem attachments add parses every type and input source`` (commandAlias: string, attachmentType: string, workItemIdentifier: string) =
        let inputSources =
            [|
                [|
                    "--file"
                    "C:\\temp\\attachment.txt"
                |]
                [| "--text"; "inline content" |]
                [| "--stdin" |]
            |]

        for inputSource in inputSources do
            assertParsesWithoutErrors (
                buildAttachmentsArgs
                    commandAlias
                    "add"
                    workItemIdentifier
                    [|
                        "--type"
                        attachmentType
                        yield! inputSource
                    |]
                |> withIds
            )

    /// Verifies that attachment creation requires the canonical lower-case type values.
    [<TestCase("summary")>]
    [<TestCase("prompt")>]
    [<TestCase("notes")>]
    let ``workitem attachments add accepts canonical lower-case type values`` (attachmentType: string) =
        let parseResult =
            buildAttachmentsArgs
                "workitem"
                "add"
                "42"
                [|
                    "--type"
                    attachmentType
                    "--text"
                    "content"
                |]
            |> withIds
            |> GraceCommand.rootCommand.Parse

        parseResult.Errors.Count |> should equal 0

        let expected =
            match attachmentType with
            | "summary" -> Grace.CLI.Command.WorkItemCommand.AttachmentType.Summary
            | "prompt" -> Grace.CLI.Command.WorkItemCommand.AttachmentType.Prompt
            | "notes" -> Grace.CLI.Command.WorkItemCommand.AttachmentType.Notes
            | value -> failwith $"Unexpected test attachment type: {value}"

        parseResult.GetValue<Grace.CLI.Command.WorkItemCommand.AttachmentType>("--type")
        |> should equal expected

    /// Verifies that normal and JSON modes bind the same one generic attachment action.
    [<TestCase(false)>]
    [<TestCase(true)>]
    let ``workitem attachments add binds one generic action in normal and json modes`` (jsonOutput: bool) =
        let args = ResizeArray<string>()

        args.AddRange(
            buildAttachmentsArgs
                "workitem"
                "add"
                "42"
                [|
                    "--type"
                    "summary"
                    "--text"
                    "content"
                |]
            |> withIds
        )

        if jsonOutput then
            args.Add("--output")
            args.Add("Json")

        let parseResult = GraceCommand.rootCommand.Parse(args.ToArray())
        parseResult.Errors.Count |> should equal 0

        parseResult.CommandResult.Command.Action.GetType()
        |> should equal typeof<Grace.CLI.Command.WorkItemCommand.AttachmentsAdd>

    /// Verifies that missing, unknown, and non-canonical attachment type spellings fail in the root parser.
    [<TestCase("--text", "content")>]
    [<TestCase("--type", "binary", "--text", "content")>]
    [<TestCase("--type", "Summary", "--text", "content")>]
    let ``workitem attachments add rejects missing invalid or non-canonical type values`` ([<ParamArray>] extraArgs: string array) =
        buildAttachmentsArgs "workitem" "add" "42" extraArgs
        |> withIds
        |> assertRejected

    /// Verifies that the removed singular attach tree is not retained as an alias.
    [<TestCase("summary")>]
    [<TestCase("prompt")>]
    [<TestCase("notes")>]
    let ``workitem old attach syntax is unavailable`` (attachmentType: string) =
        withIds [| "workitem"
                   "attach"
                   attachmentType
                   "42"
                   "--text"
                   "content" |]
        |> assertRejected

    /// Verifies that workitem links list parses for guid and numeric work item identifiers.
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

    /// Verifies that workitem attachments list parses for guid and numeric work item identifiers.
    [<TestCase("workitem", "52")>]
    [<TestCase("workitem", "9dfdb7a5-27f6-4fd8-95cf-f5e4f2b22803")>]
    [<TestCase("work", "53")>]
    [<TestCase("wi", "9761ae11-ec40-4c2a-a6e7-e13001642f8e")>]
    let ``workitem attachments list parses for guid and numeric work item identifiers`` (commandAlias: string, workItemIdentifier: string) =
        assertParsesWithoutErrors (
            buildAttachmentsArgs commandAlias "list" workItemIdentifier [||]
            |> withIds
        )

    /// Verifies that workitem attachments show parses with type and latest options.
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

    /// Verifies that workitem attachments download parses with artifact id and output file.
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

    /// Verifies that workitem links remove ref parses for guid and numeric work item identifiers.
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

    /// Verifies that workitem links remove prset parses for guid and numeric work item identifiers.
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

    /// Verifies that workitem links remove artifact type aliases are unavailable.
    [<TestCase("workitem", "summary", "50")>]
    [<TestCase("workitem", "prompt", "6a635cbe-19ce-4e5f-a0fd-f1c1d1d468ea")>]
    [<TestCase("wi", "notes", "51")>]
    [<TestCase("work", "summary", "fdb37dfa-699d-4f8f-80f0-6e2eb6222596")>]
    let ``workitem links remove artifact-type aliases are unavailable`` (commandAlias: string, linkType: string, workItemIdentifier: string) =
        assertRejected (
            withIds [| commandAlias
                       "links"
                       "remove"
                       linkType
                       workItemIdentifier |]
        )

    /// Verifies that work link artifact command is unavailable.
    [<Test>]
    let ``work link artifact command is unavailable`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "work"; "link"; "artifact" |])

        Assert.That(parseResult.Errors.Count, Is.GreaterThan(0))

        let hasArtifactError =
            parseResult.Errors
            |> Seq.exists (fun error -> error.Message.Contains("Unrecognized command or argument 'artifact'", StringComparison.OrdinalIgnoreCase))

        Assert.That(hasArtifactError, Is.True)

    /// Verifies that workitem attachments show rejects invalid type values during parse.
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

    /// Verifies that workitem attachments download requires artifact id and output file options.
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

    /// Verifies that attachment delete and undelete expose only the required specific-artifact inputs.
    [<Test>]
    let ``workitem attachment delete and undelete parse exact artifact identity`` () =
        let artifactId = Guid.NewGuid().ToString()

        assertParsesWithoutErrors (
            buildAttachmentsArgs
                "workitem"
                "delete"
                "123"
                [|
                    "--artifact-id"
                    artifactId
                    "--delete-reason"
                    "superseded"
                |]
            |> withIds
        )

        assertParsesWithoutErrors (
            buildAttachmentsArgs "workitem" "undelete" "123" [| "--artifact-id"; artifactId |]
            |> withIds
        )

    /// Verifies that deletion rejects missing reason or artifact identity before invoking SDK behavior.
    [<Test>]
    let ``workitem attachment delete requires artifact id and reason`` () =
        buildAttachmentsArgs
            "workitem"
            "delete"
            "123"
            [|
                "--artifact-id"
                Guid.NewGuid().ToString()
            |]
        |> withIds
        |> assertRejected

        buildAttachmentsArgs "workitem" "delete" "123" [| "--delete-reason"; "superseded" |]
        |> withIds
        |> assertRejected

    /// Verifies that removed bulk attachment unlink commands are absent from the public command tree.
    [<TestCase("summary")>]
    [<TestCase("prompt")>]
    [<TestCase("notes")>]
    let ``workitem links remove bulk attachment paths are unavailable`` (attachmentType: string) =
        withIds [| "workitem"
                   "links"
                   "remove"
                   attachmentType
                   "123" |]
        |> assertRejected
