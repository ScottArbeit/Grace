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
open System.Threading
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

    /// Captures standard output while leaving the caller responsible for any process-wide input setup.
    let private captureStandardOutput (action: unit -> unit) =
        use writer = new StringWriter()
        let originalOut = Console.Out

        try
            Console.SetOut(writer)
            action ()
            writer.ToString()
        finally
            Console.SetOut(originalOut)

    /// Finds the next CRLF boundary in a raw HTTP request buffer.
    let private findCrLf (bytes: byte array) (startIndex: int) =
        let mutable index = startIndex

        while index + 1 < bytes.Length
              && (bytes[index] <> 13uy || bytes[index + 1] <> 10uy) do
            index <- index + 1

        if index + 1 < bytes.Length then index else -1

    /// Decodes the byte chunks sent by HttpClient when a request body uses HTTP chunked transfer encoding.
    let private readChunkedRequestBody (bytes: byte array) (startIndex: int) =
        use body = new MemoryStream()
        let mutable index = startIndex
        let mutable chunkLength = -1

        while chunkLength <> 0 do
            let chunkHeaderEnd = findCrLf bytes index

            if chunkHeaderEnd < 0 then
                invalidOp "Loopback description-set request ended before a chunk header completed."

            let chunkHeader = Encoding.ASCII.GetString(bytes, index, chunkHeaderEnd - index)
            chunkLength <- Convert.ToInt32(chunkHeader, 16)
            index <- chunkHeaderEnd + 2

            if chunkLength > 0 then
                if index + chunkLength + 2 > bytes.Length then
                    invalidOp "Loopback description-set request ended before a declared chunk completed."

                body.Write(bytes, index, chunkLength)
                index <- index + chunkLength + 2

        Encoding.UTF8.GetString(body.ToArray())

    /// Runs one action against a response listener that owns its assigned loopback port for the listener lifetime.
    let private observeDescriptionSetDispatch (action: string -> unit) =
        use listener = new TcpListener(IPAddress.Loopback, 0)
        use cancellation = new CancellationTokenSource()
        listener.Start()

        let mutable requestCount = 0
        let port = (listener.LocalEndpoint :?> IPEndPoint).Port

        let requestTask =
            Task.Run (fun () ->
                try
                    use client =
                        listener
                            .AcceptTcpClientAsync(cancellation.Token)
                            .AsTask()
                            .GetAwaiter()
                            .GetResult()

                    Interlocked.Increment(&requestCount) |> ignore
                    use stream = client.GetStream()
                    use requestBuffer = new MemoryStream()
                    let readBuffer = Array.zeroCreate<byte> 8192
                    let mutable headerLength = -1
                    let mutable contentLength = None
                    let mutable isChunked = false
                    let mutable requestComplete = false

                    while not requestComplete do
                        let read = stream.Read(readBuffer, 0, readBuffer.Length)

                        if read = 0 then
                            invalidOp "Loopback description-set request ended before its body completed."

                        requestBuffer.Write(readBuffer, 0, read)
                        let rawRequest = requestBuffer.ToArray()

                        if headerLength < 0 then
                            let headerText = Encoding.ASCII.GetString(rawRequest)
                            let headerEnd = headerText.IndexOf("\r\n\r\n", StringComparison.Ordinal)

                            if headerEnd >= 0 then
                                headerLength <- headerEnd + 4
                                isChunked <- headerText.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase)

                                contentLength <-
                                    headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                                    |> Array.tryPick (fun header ->
                                        if header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) then
                                            header.Substring("Content-Length:".Length).Trim()
                                            |> Int32.Parse
                                            |> Some
                                        else
                                            None)

                        if headerLength >= 0 then
                            requestComplete <-
                                match contentLength, isChunked with
                                | Some length, _ -> rawRequest.Length >= headerLength + length
                                | None, true ->
                                    Encoding
                                        .ASCII
                                        .GetString(rawRequest)
                                        .IndexOf("\r\n0\r\n\r\n", headerLength, StringComparison.Ordinal)
                                    >= 0
                                | None, false -> true

                    let rawRequest = requestBuffer.ToArray()
                    let requestHeader = Encoding.ASCII.GetString(rawRequest, 0, headerLength)
                    let requestLine = requestHeader.Substring(0, requestHeader.IndexOf("\r\n", StringComparison.Ordinal))

                    let requestBody =
                        match contentLength, isChunked with
                        | Some length, _ -> Encoding.UTF8.GetString(rawRequest, headerLength, length)
                        | None, true -> readChunkedRequestBody rawRequest headerLength
                        | None, false -> String.Empty

                    let responseBody =
                        "{\"ReturnValue\":\"ok\",\"EventTime\":\"2026-08-10T00:00:00Z\",\"CorrelationId\":\"description-source-test\",\"Properties\":{}}"

                    let responseBytes = Encoding.UTF8.GetBytes(responseBody)

                    let responseHeaders =
                        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {responseBytes.Length}\r\nConnection: close\r\n\r\n"

                    let headerBytes = Encoding.ASCII.GetBytes(responseHeaders)
                    stream.Write(headerBytes, 0, headerBytes.Length)
                    stream.Write(responseBytes, 0, responseBytes.Length)
                    stream.Flush()

                    let requestSegments = requestLine.Split(' ')
                    Some(requestSegments[0], requestSegments[1], requestBody)
                with
                | :? SocketException
                | :? ObjectDisposedException
                | :? OperationCanceledException
                | :? InvalidOperationException -> None)

        try
            action $"http://127.0.0.1:{port}"
        finally
            cancellation.Cancel()
            listener.Stop()

        Interlocked.CompareExchange(&requestCount, 0, 0), requestTask.GetAwaiter().GetResult()

    /// Captures one SetDescription SDK request with a deterministic loopback response.
    let private captureDescriptionSetRequest (action: string -> unit) =
        let requestCount, capturedRequest = observeDescriptionSetDispatch action
        requestCount |> should equal 1

        match capturedRequest with
        | Some request -> request
        | None -> invalidOp "Expected one description-set SDK request, but the loopback listener received none."

    /// Verifies that an action fails locally without sending any description-set SDK request.
    let private assertDescriptionSetDoesNotDispatch (action: string -> unit) =
        let requestCount, capturedRequest = observeDescriptionSetDispatch action
        requestCount |> should equal 0
        capturedRequest |> should equal None

    /// Invokes description set with one invalid source selection and proves the local failure occurs before SDK dispatch.
    let private assertDescriptionSetFailsWithoutDispatch (sourceArgs: string array) =
        assertDescriptionSetDoesNotDispatch (fun serverUri ->
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
                parseResult.Invoke() |> should equal -1))

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

    /// Verifies that concurrent fixture instances retain their own loopback ports instead of releasing and rebinding one.
    [<Test>]
    [<Category("DescriptionInputReviewFix")>]
    let ``workitem description set dispatch observer retains loopback ports during concurrent fixture use`` () =
        let observers =
            Array.init 32 (fun _ ->
                Task.Run (fun () ->
                    let requestCount, capturedRequest = observeDescriptionSetDispatch (fun _ -> ())
                    requestCount |> should equal 0
                    capturedRequest |> should equal None))

        Task.WhenAll(observers).GetAwaiter().GetResult()

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
    [<Category("DescriptionInputReviewFix")>]
    let ``workitem description set rejects missing text through its bound action`` () = assertDescriptionSetFailsWithoutDispatch Array.empty

    /// Verifies that description set rejects every source combination except exactly one source before SDK dispatch.
    [<TestCase(false, false, false)>]
    [<TestCase(true, true, false)>]
    [<TestCase(true, false, true)>]
    [<TestCase(false, true, true)>]
    [<TestCase(true, true, true)>]
    [<Category("DescriptionInputReviewFix")>]
    let ``workitem description set rejects every invalid source combination before SDK dispatch`` (useFile: bool, useText: bool, useStdin: bool) =
        let args = List<string>()

        if useFile then
            args.Add("--file")
            args.Add(Path.Combine(Path.GetTempPath(), "description.md"))

        if useText then
            args.Add("--text")
            args.Add("inline description")

        if useStdin then args.Add("--stdin")

        assertDescriptionSetFailsWithoutDispatch (args.ToArray())

    /// Verifies that a missing description file fails through the bound action before SDK dispatch.
    [<Test>]
    [<Category("DescriptionInputReviewFix")>]
    let ``workitem description set rejects a missing file through its bound action`` () =
        let filePath = Path.Combine(Path.GetTempPath(), $"missing-description-{Guid.NewGuid():N}.md")

        assertDescriptionSetFailsWithoutDispatch [| "--file"
                                                    filePath |]

    /// Verifies that an empty description file fails through the bound action before SDK dispatch.
    [<Test>]
    [<Category("DescriptionInputReviewFix")>]
    let ``workitem description set rejects an empty file through its bound action`` () =
        let filePath = Path.Combine(Path.GetTempPath(), $"empty-description-{Guid.NewGuid():N}.md")
        File.WriteAllText(filePath, String.Empty)

        try
            assertDescriptionSetFailsWithoutDispatch [| "--file"
                                                        filePath |]
        finally
            if File.Exists(filePath) then File.Delete(filePath)

    /// Verifies that a locally unreadable description file fails through the bound action before SDK dispatch.
    [<Test>]
    [<Category("DescriptionInputReviewFix")>]
    let ``workitem description set rejects an unreadable file through its bound action`` () =
        let filePath = Path.Combine(Path.GetTempPath(), $"locked-description-{Guid.NewGuid():N}.md")
        File.WriteAllText(filePath, "content")
        let lockedFile = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)

        try
            assertDescriptionSetFailsWithoutDispatch [| "--file"
                                                        filePath |]
        finally
            lockedFile.Dispose()

            if File.Exists(filePath) then File.Delete(filePath)

    /// Verifies that an empty inline description fails locally instead of becoming an implicit clear operation.
    [<Test>]
    [<Category("DescriptionInputReviewFix")>]
    let ``workitem description set rejects empty inline text through its bound action`` () =
        assertDescriptionSetFailsWithoutDispatch [| "--text"
                                                    String.Empty |]

    /// Verifies that empty standard input fails through the bound action without leaking process-global console state.
    [<Test>]
    [<Category("DescriptionInputReviewFix")>]
    let ``workitem description set rejects empty standard input through its bound action`` () =
        let originalIn = Console.In
        use input = new StringReader(String.Empty)

        try
            Console.SetIn(input)
            assertDescriptionSetFailsWithoutDispatch [| "--stdin" |]
        finally
            Console.SetIn(originalIn)

    /// Verifies that schema and examples declare the three exclusive input sources without reading local input or dispatching.
    [<TestCase("--schema")>]
    [<TestCase("--examples")>]
    [<Category("DescriptionInputReviewFix")>]
    let ``workitem description set introspection exposes inert exclusive input sources`` (introspectionOption: string) =
        let inaccessibleFile = Path.Combine(Path.GetTempPath(), $"inaccessible-description-{Guid.NewGuid():N}.md")
        let expectedInput = "# input must remain unread\r\n🙂"
        let originalIn = Console.In
        use input = new StringReader(expectedInput)

        try
            Console.SetIn(input)

            let mutable exitCode = -99

            let output =
                captureStandardOutput (fun () ->
                    assertDescriptionSetDoesNotDispatch (fun serverUri ->
                        withTemporaryGraceConfiguration serverUri (fun _ ->
                            exitCode <-
                                GraceCommand.main (
                                    withIds [| "workitem"
                                               "description"
                                               "set"
                                               "42"
                                               "--file"
                                               inaccessibleFile
                                               introspectionOption |]
                                ))))

            exitCode |> should equal 0
            input.ReadToEnd() |> should equal expectedInput

            use document = JsonDocument.Parse(output)
            let inputContract = document.RootElement.GetProperty("Input")

            inputContract.GetProperty("Selection").GetString()
            |> should equal "ExactlyOne"

            inputContract.GetProperty("Options")
            |> fun options ->
                options.EnumerateArray()
                |> Seq.map (fun option -> option.GetProperty("Name").GetString())
                |> Set.ofSeq
            |> should
                equal
                (Set.ofList [ "--text"
                              "--file"
                              "--stdin" ])
        finally
            Console.SetIn(originalIn)

    /// Verifies that help leaves standard input untouched and does not invoke the bound description action.
    [<Test>]
    [<Category("DescriptionInputReviewFix")>]
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
    [<Category("DescriptionInputPayload")>]
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
