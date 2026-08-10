namespace Grace.CLI.Tests

open FsCheck.NUnit
open FsUnit
open Grace.CLI
open Grace.Shared.Client.Configuration
open Grace.Shared.Utilities
open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading.Tasks
open NUnit.Framework

/// Groups work item command coverage for the CLI test project.
[<NonParallelizable>]
module WorkItemCommandTests =
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

    /// Runs a CLI action against an isolated local configuration and removes it after the assertion finishes.
    let private withTemporaryGraceConfiguration (serverUri: string) (action: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"grace-description-tests-{Guid.NewGuid():N}")
        let graceDirectory = Path.Combine(root, ".grace")
        let originalDirectory = Environment.CurrentDirectory

        Directory.CreateDirectory(graceDirectory)
        |> ignore

        let configuration = GraceConfiguration()
        configuration.OwnerId <- ownerId
        configuration.OrganizationId <- organizationId
        configuration.RepositoryId <- repositoryId
        configuration.ServerUri <- serverUri
        File.WriteAllText(Path.Combine(graceDirectory, "graceconfig.json"), serialize configuration)

        try
            Environment.CurrentDirectory <- root
            resetConfiguration ()
            action root
        finally
            Environment.CurrentDirectory <- originalDirectory
            resetConfiguration ()

            if Directory.Exists(root) then Directory.Delete(root, true)

    /// Captures one SetDescription SDK request with a deterministic loopback response.
    let private captureDescriptionSetRequest (action: string -> unit) =
        use reservation = new TcpListener(IPAddress.Loopback, 0)
        reservation.Start()
        let port = (reservation.LocalEndpoint :?> IPEndPoint).Port
        reservation.Stop()

        use listener = new HttpListener()
        listener.Prefixes.Add($"http://127.0.0.1:{port}/")
        listener.Start()

        let requestTask =
            Task.Run (fun () ->
                let context = listener.GetContext()
                use reader = new StreamReader(context.Request.InputStream)
                let requestBody = reader.ReadToEnd()
                let method = context.Request.HttpMethod
                let path = context.Request.RawUrl

                let responseBody =
                    "{\"ReturnValue\":\"ok\",\"EventTime\":\"2026-08-10T00:00:00Z\",\"CorrelationId\":\"description-source-test\",\"Properties\":{}}"

                let responseBytes = Encoding.UTF8.GetBytes(responseBody)
                context.Response.StatusCode <- 200
                context.Response.ContentType <- "application/json"
                context.Response.ContentLength64 <- int64 responseBytes.Length
                context.Response.OutputStream.Write(responseBytes, 0, responseBytes.Length)
                context.Response.Close()
                method, path, requestBody)

        action $"http://127.0.0.1:{port}"
        requestTask.GetAwaiter().GetResult()

    /// Verifies that one successful source reaches the existing SetDescription SDK route with its exact text payload.
    let private assertDescriptionSetDispatch (sourceArgs: string array) (expectedText: string) =
        let method, path, requestBody =
            captureDescriptionSetRequest (fun serverUri ->
                withTemporaryGraceConfiguration serverUri (fun _ ->
                    let parseResult =
                        GraceCommand.rootCommand.Parse(
                            withIdsAndSilent [| "workitem"
                                                "description"
                                                "set"
                                                "42"
                                                yield! sourceArgs |]
                        )

                    parseResult.Errors.Count |> should equal 0
                    parseResult.Invoke() |> should equal 0))

        method |> should equal "POST"
        path |> should equal "/work/description/set"

        use request = JsonDocument.Parse(requestBody)

        request
            .RootElement
            .GetProperty("Text")
            .GetString()
        |> should equal expectedText

    /// Verifies that workitem show rejects invalid work item identifier.
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

    /// Verifies that set-status dispatches the existing work item validation handler.
    [<Test>]
    let ``workitem set-status rejects invalid work item identifier through its bound action`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "workitem"
                                    "set-status"
                                    "not-a-work-item"
                                    "--status"
                                    "Done" |]
            )

        parseResult.Errors.Count |> should equal 0

        parseResult.CommandResult.Command.Name
        |> should equal "set-status"

        parseResult.CommandResult.Command.Action.GetType()
        |> should equal typeof<Grace.CLI.Command.WorkItemCommand.SetStatus>

        parseResult.Invoke() |> should equal -1

    /// Verifies that description set rejects an invalid work-item identifier through its bound action before a server call.
    [<Test>]
    let ``workitem description set rejects invalid work item identifier through its bound action`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "workitem"
                                    "description"
                                    "set"
                                    "not-a-work-item"
                                    "--text"
                                    "Description" |]
            )

        parseResult.Errors.Count |> should equal 0

        parseResult.CommandResult.Command.Name
        |> should equal "set"

        parseResult.CommandResult.Command.Action.GetType()
        |> should equal typeof<Grace.CLI.Command.WorkItemCommand.SetDescription>

        parseResult.Invoke() |> should equal -1

    /// Verifies that description clear rejects an invalid work-item identifier through its bound action before a server call.
    [<Test>]
    let ``workitem description clear rejects invalid work item identifier through its bound action`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "workitem"
                                    "description"
                                    "clear"
                                    "not-a-work-item" |]
            )

        parseResult.Errors.Count |> should equal 0

        parseResult.CommandResult.Command.Action.GetType()
        |> should equal typeof<Grace.CLI.Command.WorkItemCommand.ClearDescription>

        parseResult.Invoke() |> should equal -1

    /// Verifies that description set rejects missing text through action validation without blocking introspection.
    [<Test>]
    let ``workitem description set rejects missing text through its bound action`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "workitem"
                                    "description"
                                    "set"
                                    "42" |]
            )

        parseResult.Errors.Count |> should equal 0
        parseResult.Invoke() |> should equal -1

    /// Verifies that description set rejects every source combination except exactly one source before SDK dispatch.
    [<FsCheck.NUnit.Property(MaxTest = 64)>]
    let ``workitem description set input source combinations are valid iff exactly one is selected`` (useFile: bool) (useText: bool) (useStdin: bool) =
        let args = List<string>()
        args.Add("workitem")
        args.Add("description")
        args.Add("set")
        args.Add("42")

        if useFile then
            args.Add("--file")
            args.Add(Path.Combine(Path.GetTempPath(), "description.md"))

        if useText then
            args.Add("--text")
            args.Add("inline description")

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

    /// Verifies that a missing description file fails through the bound action before SDK dispatch.
    [<Test>]
    let ``workitem description set rejects a missing file through its bound action`` () =
        let filePath = Path.Combine(Path.GetTempPath(), $"missing-description-{Guid.NewGuid():N}.md")

        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "workitem"
                                    "description"
                                    "set"
                                    "42"
                                    "--file"
                                    filePath |]
            )

        parseResult.Errors.Count |> should equal 0
        parseResult.Invoke() |> should equal -1

    /// Verifies that an empty description file fails through the bound action before SDK dispatch.
    [<Test>]
    let ``workitem description set rejects an empty file through its bound action`` () =
        let filePath = Path.Combine(Path.GetTempPath(), $"empty-description-{Guid.NewGuid():N}.md")
        File.WriteAllText(filePath, String.Empty)

        try
            let parseResult =
                GraceCommand.rootCommand.Parse(
                    withIdsAndSilent [| "workitem"
                                        "description"
                                        "set"
                                        "42"
                                        "--file"
                                        filePath |]
                )

            parseResult.Errors.Count |> should equal 0
            parseResult.Invoke() |> should equal -1
        finally
            if File.Exists(filePath) then File.Delete(filePath)

    /// Verifies that a locally unreadable description file fails through the bound action before SDK dispatch.
    [<Test>]
    let ``workitem description set rejects an unreadable file through its bound action`` () =
        let filePath = Path.Combine(Path.GetTempPath(), $"locked-description-{Guid.NewGuid():N}.md")
        File.WriteAllText(filePath, "content")
        let lockedFile = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)

        try
            let parseResult =
                GraceCommand.rootCommand.Parse(
                    withIdsAndSilent [| "workitem"
                                        "description"
                                        "set"
                                        "42"
                                        "--file"
                                        filePath |]
                )

            parseResult.Errors.Count |> should equal 0
            parseResult.Invoke() |> should equal -1
        finally
            lockedFile.Dispose()

            if File.Exists(filePath) then File.Delete(filePath)

    /// Verifies that an empty inline description fails locally instead of becoming an implicit clear operation.
    [<Test>]
    let ``workitem description set rejects empty inline text through its bound action`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "workitem"
                                    "description"
                                    "set"
                                    "42"
                                    "--text"
                                    String.Empty |]
            )

        parseResult.Errors.Count |> should equal 0
        parseResult.Invoke() |> should equal -1

    /// Verifies that empty standard input fails through the bound action without leaking process-global console state.
    [<Test>]
    let ``workitem description set rejects empty standard input through its bound action`` () =
        let originalIn = Console.In
        use input = new StringReader(String.Empty)

        try
            Console.SetIn(input)

            let parseResult =
                GraceCommand.rootCommand.Parse(
                    withIdsAndSilent [| "workitem"
                                        "description"
                                        "set"
                                        "42"
                                        "--stdin" |]
                )

            parseResult.Errors.Count |> should equal 0
            parseResult.Invoke() |> should equal -1
        finally
            Console.SetIn(originalIn)

    /// Verifies that help leaves standard input untouched and does not invoke the bound description action.
    [<Test>]
    let ``workitem description set help leaves standard input unread`` () =
        let originalIn = Console.In
        let expectedInput = "# remains unread\r\n🙂"
        use input = new StringReader(expectedInput)

        try
            Console.SetIn(input)

            let parseResult =
                GraceCommand.rootCommand.Parse(
                    withIds [| "workitem"
                               "description"
                               "set"
                               "42"
                               "--stdin"
                               "--help" |]
                )

            parseResult.Errors.Count |> should equal 0
            parseResult.Invoke() |> should equal 0
            input.ReadToEnd() |> should equal expectedInput
        finally
            Console.SetIn(originalIn)

    /// Verifies that inline, file, and standard-input sources each dispatch their exact multiline Unicode text once.
    [<Test>]
    let workitemDescriptionSetDispatchesEachSourceWithUnchangedText () =
        let expectedText = "# Heading\r\n\r\nRésumé 🙂\n"
        assertDescriptionSetDispatch [| "--text"; expectedText |] expectedText

        let filePath = Path.Combine(Path.GetTempPath(), $"description-payload-{Guid.NewGuid():N}.md")
        File.WriteAllText(filePath, expectedText)

        try
            assertDescriptionSetDispatch [| "--file"; filePath |] expectedText
        finally
            if File.Exists(filePath) then File.Delete(filePath)

        let originalIn = Console.In
        use input = new StringReader(expectedText)

        try
            Console.SetIn(input)
            assertDescriptionSetDispatch [| "--stdin" |] expectedText
        finally
            Console.SetIn(originalIn)

    /// Verifies that workitem link ref rejects invalid reference id.
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

    /// Verifies that work link prset rejects invalid promotion set id.
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

    /// Verifies that workitem attachments add requires exactly one input source.
    [<Test>]
    let ``workitem attachments add requires exactly one input source`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "workitem"
                                    "attachments"
                                    "add"
                                    Guid.NewGuid().ToString()
                                    "--type"
                                    "summary" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    /// Verifies that workitem attachments add rejects multiple input sources.
    [<Test>]
    let ``workitem attachments add rejects multiple input sources`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                withIdsAndSilent [| "workitem"
                                    "attachments"
                                    "add"
                                    Guid.NewGuid().ToString()
                                    "--type"
                                    "summary"
                                    "--text"
                                    "hello"
                                    "--stdin" |]
            )

        let exitCode = parseResult.Invoke()
        exitCode |> should equal -1

    /// Verifies that workitem attachments download rejects invalid artifact id.
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

    /// Verifies that workitem attachments download rejects invalid output file path.
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

    /// Verifies that workitem attachments add input source combinations are valid iff exactly one is selected.
    [<FsCheck.NUnit.Property(MaxTest = 64)>]
    let ``workitem attachments add input source combinations are valid iff exactly one is selected`` (useFile: bool) (useText: bool) (useStdin: bool) =
        let args = List<string>()
        args.Add("workitem")
        args.Add("attachments")
        args.Add("add")
        args.Add(Guid.NewGuid().ToString())
        args.Add("--type")
        args.Add("summary")

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
