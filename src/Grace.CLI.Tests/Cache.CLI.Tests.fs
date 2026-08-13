namespace Grace.CLI.Tests

open Grace.Cache
open Grace.CLI
open Grace.CLI.Command
open Grace.SDK
open Grace.Shared
open Grace.Types.CacheRegistration
open Grace.Types.Common
open NodaTime
open NUnit.Framework
open Spectre.Console
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
open System.Text.Json
open System.Text
open System.Threading
open System.Threading.Tasks

/// Covers serialized root-command behavior for pure local Cache status.
[<TestFixture>]
[<NonParallelizable>]
module CacheCliTests =

    /// Sets AnsiConsole output for root-command tests that capture stdout.
    let private setAnsiConsoleOutput writer =
        let settings = AnsiConsoleSettings()
        settings.Out <- AnsiConsoleOutput(writer)
        AnsiConsole.Console <- AnsiConsole.Create(settings)

    /// Runs the root command while capturing complete stdout.
    let private run (args: string array) =
        use writer = new StringWriter()
        let originalOut = Console.Out

        try
            Console.SetOut(writer)
            setAnsiConsoleOutput writer
            GraceCommand.main args, writer.ToString()
        finally
            Console.SetOut(originalOut)
            setAnsiConsoleOutput originalOut

    /// Runs an enrollment command through the single production root graph with controlled Cache collaborators.
    let private runEnrollment (dependencies: CacheCommand.Dependencies) (args: string array) (cancellationToken: CancellationToken) =
        use writer = new StringWriter()
        let originalOut = Console.Out

        try
            Console.SetOut(writer)
            setAnsiConsoleOutput writer

            let rootDependencies: GraceCommand.RootDependencies =
                {
                    CreateCacheCommand = (fun () -> CacheCommand.create dependencies)
                    InitializeExecution = (fun () -> ())
                    AfterCommandExit = (fun _ -> Task.FromResult(()))
                }

            let exitCode =
                GraceCommand.run rootDependencies args cancellationToken
                |> fun invocation -> invocation.GetAwaiter().GetResult()

            exitCode, writer.ToString()
        finally
            Console.SetOut(originalOut)
            setAnsiConsoleOutput originalOut

    /// Supplies the closed Product V1 enrollment grammar with a local HTTP endpoint used by root-dispatch tests.
    let private enrollmentArguments =
        [|
            "--output"
            "Json"
            "cache"
            "enroll"
            "--owner-id"
            "22222222-2222-2222-2222-222222222222"
            "--organization-id"
            "33333333-3333-3333-3333-333333333333"
            "--repository"
            "33333333-3333-3333-3333-333333333333/44444444-4444-4444-4444-444444444444"
            "--endpoint"
            "http://cache.example.test"
            "--allow-http"
        |]

    /// Produces the strictly accepted response corresponding to the exact staged request.
    let private acceptedResponse (request: CacheEnrollmentRequest) =
        let now = SystemClock.Instance.GetCurrentInstant()

        let registration =
            {
                Class = nameof CacheRegistration
                CacheId = Guid.Parse("11111111-1111-1111-1111-111111111111")
                DisplayName = request.DisplayName
                BoundaryKind = request.BoundaryKind
                OwnerId = request.OwnerId
                OrganizationId = request.OrganizationId
                RepositoryScopes = request.RepositoryScopes |> Seq.toArray
                PublicKey = request.PublicKey
                Endpoint = request.Endpoint
                AllowHttpEndpoint = request.AllowHttpEndpoint
                Health = CacheHealthStatus.Unhealthy
                SoftwareVersion = request.SoftwareVersion
                ProtocolVersion = request.ProtocolVersion
                PrefetchSupported = request.PrefetchSupported
                EnrolledBy = "cache-test"
                EnrolledAt = now
                LastRefreshedAt = now
                RefreshAfter = now.Plus(Duration.FromHours(1))
                ExpiresAt = now.Plus(Duration.FromHours(2))
                RevokedAt = None
            }

        CacheRegistrationResult.Create(CacheRegistrationRefreshStatus.Enrolled, Some registration, "enrolled")
        |> fun result -> GraceReturnValue.Create result "cache-test-correlation"

    /// Supplies deterministic enrollment collaborators while retaining the production Cache command and root graph.
    let private enrollmentDependencies root resolveBearer sendEnrollment onPhase commitReady : CacheCommand.Dependencies =
        { StateRoot = root; ResolveBearer = resolveBearer; SendEnrollment = sendEnrollment; CommitReady = commitReady; OnPhase = onPhase }

    /// Captures a loopback request made by the production credential and selected-server transport path.
    type private EnrollmentRequest = { Method: string; Path: string; Authorization: string option; Body: string }

    /// Runs the production Cache collaborators through the sole root construction and run entry points.
    let private runProductionEnrollment (args: string array) =
        use writer = new StringWriter()
        let originalOut = Console.Out

        try
            Console.SetOut(writer)
            setAnsiConsoleOutput writer

            let exitCode =
                GraceCommand.run (GraceCommand.productionDependencies ()) args CancellationToken.None
                |> fun invocation -> invocation.GetAwaiter().GetResult()

            exitCode, writer.ToString()
        finally
            Console.SetOut(originalOut)
            setAnsiConsoleOutput originalOut

    /// Reads a complete loopback HTTP request including fixed-length and chunked request bodies.
    let private readEnrollmentRequest (client: TcpClient) =
        task {
            let stream = client.GetStream()
            use reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true)
            let! requestLine = reader.ReadLineAsync()

            if String.IsNullOrWhiteSpace(requestLine) then
                return { Method = String.Empty; Path = String.Empty; Authorization = None; Body = String.Empty }
            else
                let parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                let headers = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                let mutable header = reader.ReadLine()

                while not (String.IsNullOrEmpty(header)) do
                    let separator = header.IndexOf(':')

                    if separator > 0 then
                        headers[header.Substring(0, separator).Trim()] <- header.Substring(separator + 1).Trim()

                    header <- reader.ReadLine()

                let readCharacters count =
                    task {
                        let characters = Array.zeroCreate<char> count
                        let mutable offset = 0

                        while offset < count do
                            let! read = reader.ReadAsync(characters, offset, count - offset)

                            if read = 0 then
                                failwith "The loopback responder received an incomplete request body."

                            offset <- offset + read

                        return String(characters)
                    }

                let contentLength =
                    match headers.TryGetValue("Content-Length") with
                    | true, value ->
                        match Int32.TryParse(value) with
                        | true, length when length > 0 -> length
                        | _ -> 0
                    | false, _ -> 0

                let readChunkedBody () =
                    task {
                        let body = StringBuilder()
                        let mutable complete = false

                        while not complete do
                            let! sizeLine = reader.ReadLineAsync()
                            let sizeText = sizeLine.Split(';', 2)[0]

                            match Int32.TryParse(sizeText, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture) with
                            | true, size when size > 0 ->
                                let! chunk = readCharacters size
                                body.Append(chunk) |> ignore
                                let! terminator = reader.ReadLineAsync()

                                if not (String.IsNullOrEmpty(terminator)) then
                                    failwith "The loopback responder received an invalid chunk terminator."
                            | true, 0 ->
                                let! terminator = reader.ReadLineAsync()

                                if not (String.IsNullOrEmpty(terminator)) then
                                    failwith "The loopback responder received unsupported chunk trailers."

                                complete <- true
                            | _ -> failwith "The loopback responder received an invalid chunk size."

                        return body.ToString()
                    }

                let isChunked =
                    match headers.TryGetValue("Transfer-Encoding") with
                    | true, value -> value.Contains("chunked", StringComparison.OrdinalIgnoreCase)
                    | false, _ -> false

                let! body =
                    if contentLength > 0 then readCharacters contentLength
                    elif isChunked then readChunkedBody ()
                    else Task.FromResult(String.Empty)

                return
                    {
                        Method = if parts.Length > 0 then parts[0] else String.Empty
                        Path = if parts.Length > 1 then parts[1] else String.Empty
                        Authorization =
                            match headers.TryGetValue("Authorization") with
                            | true, value -> Some value
                            | false, _ -> None
                        Body = body
                    }
        }

    /// Writes one HTTP response without redirect following or additional server requests.
    let private writeEnrollmentResponse (client: TcpClient) (statusCode: int) (body: string) =
        task {
            use stream = client.GetStream()
            let bytes = Encoding.UTF8.GetBytes(body)

            let status =
                if statusCode = 200 then "OK"
                elif statusCode = 302 then "Found"
                else "Bad Request"

            let headers =
                $"HTTP/1.1 {statusCode} {status}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"
                |> Encoding.ASCII.GetBytes

            do! stream.WriteAsync(headers, 0, headers.Length)
            do! stream.WriteAsync(bytes, 0, bytes.Length)
        }

    /// Hosts one selected server and its OAuth endpoints for real production-credential root-dispatch tests.
    let private withProductionEnrollmentServer (action: Uri -> ConcurrentQueue<EnrollmentRequest> -> unit) =
        use listener = new TcpListener(IPAddress.Loopback, 0)
        use cancellation = new CancellationTokenSource()
        let requests = ConcurrentQueue<EnrollmentRequest>()
        listener.Start()
        let serverUri = Uri($"http://127.0.0.1:{(listener.LocalEndpoint :?> IPEndPoint).Port}")

        let rec serve () =
            task {
                if not cancellation.IsCancellationRequested then
                    try
                        let! client =
                            listener
                                .AcceptTcpClientAsync(cancellation.Token)
                                .AsTask()

                        use client = client
                        let! request = readEnrollmentRequest client
                        requests.Enqueue(request)

                        let response =
                            match request.Path with
                            | "/oauth/device/code" ->
                                200,
                                "{\"device_code\":\"cache-device-code\",\"user_code\":\"CACHE\",\"verification_uri\":\""
                                + serverUri.AbsoluteUri
                                + "\",\"expires_in\":120,\"interval\":1}"
                            | "/oauth/token" when request.Body.Contains("device_code", StringComparison.Ordinal) ->
                                200,
                                "{\"access_token\":\"interactive-access-token\",\"refresh_token\":\"interactive-refresh-token\",\"expires_in\":3600,\"scope\":\"openid offline_access\",\"token_type\":\"Bearer\"}"
                            | "/oauth/token" -> 200, "{\"access_token\":\"m2m-access-token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}"
                            | "/cache/enroll" ->
                                let enrollment = JsonSerializer.Deserialize<CacheEnrollmentRequest>(request.Body, Constants.JsonSerializerOptions)

                                let body =
                                    acceptedResponse enrollment
                                    |> fun result -> JsonSerializer.Serialize(result, Constants.JsonSerializerOptions)

                                200, body
                            | _ -> 400, "{\"error\":\"unexpected_request\"}"

                        do! writeEnrollmentResponse client (fst response) (snd response)
                        return! serve ()
                    with
                    | :? OperationCanceledException
                    | :? ObjectDisposedException -> return ()
            }

        let serverTask = Task.Run(Func<Task>(fun () -> serve ()))

        try
            action serverUri requests
        finally
            cancellation.Cancel()
            listener.Stop()

            serverTask.Wait(TimeSpan.FromSeconds(5.0))
            |> ignore

    /// Applies isolated producer settings so a root-dispatch test uses exactly the requested credential mechanism.
    let private withCredentialEnvironment (values: (string * string option) list) (action: unit -> 'T) =
        let originalValues =
            values
            |> List.map (fun (name, _) -> name, Environment.GetEnvironmentVariable(name))

        try
            values
            |> List.iter (fun (name, value) -> Environment.SetEnvironmentVariable(name, value |> Option.toObj))

            action ()
        finally
            originalValues
            |> List.iter (fun (name, value) -> Environment.SetEnvironmentVariable(name, value))

    /// Runs an action with one temporary environment variable value.
    let private withEnv (name: string) (value: string option) (action: unit -> 'T) =
        let original = Environment.GetEnvironmentVariable(name)

        match value with
        | Some value -> Environment.SetEnvironmentVariable(name, value)
        | None -> Environment.SetEnvironmentVariable(name, null)

        try
            action ()
        finally
            Environment.SetEnvironmentVariable(name, original)

    /// Captures a file's existence and immutable-on-read metadata without reading its contents.
    let private snapshotFile (path: string) =
        if File.Exists(path) then
            let info = FileInfo(path)
            Some(info.Length, info.LastWriteTimeUtc, File.GetUnixFileMode(path))
        else
            None

    /// Requires a protected identity setup step to succeed.
    let private requireOk =
        function
        | Ok value -> value
        | Error error ->
            Assert.Fail($"Unexpected identity setup error: {error}")
            Unchecked.defaultof<_>

    /// Constructs a ready registration tied to one staged key.
    let private registration endpoint publicKey : CacheAcceptedRegistration =
        {
            CacheId = Guid.Parse("11111111-1111-1111-1111-111111111111")
            DisplayName = "Test cache"
            BoundaryKind = "Organization"
            OwnerId = Guid.Parse("22222222-2222-2222-2222-222222222222")
            OrganizationId = Some(Guid.Parse("33333333-3333-3333-3333-333333333333"))
            RepositoryScopes =
                [|
                    { OrganizationId = Guid.Parse("33333333-3333-3333-3333-333333333333"); RepositoryId = Guid.Parse("44444444-4444-4444-4444-444444444444") }
                |]
            Endpoint = endpoint
            ProtocolVersion = "v1"
            PublicKey = publicKey
        }

    /// Captures all accessible protected-state paths and metadata without reading protected file contents.
    let private snapshot (root: string) =
        let paths =
            try
                Array.append [| root |] (Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories))
            with
            | :? UnauthorizedAccessException -> [| root |]

        paths
        |> Array.sort
        |> Array.map (fun path ->
            let mode =
                try
                    Some(File.GetUnixFileMode(path))
                with
                | _ -> None

            let lastWriteTimeUtc =
                try
                    Some(File.GetLastWriteTimeUtc(path))
                with
                | _ -> None

            let length =
                if File.Exists(path) then
                    try
                        Some(FileInfo(path).Length)
                    with
                    | _ -> None
                else
                    None

            path, mode, lastWriteTimeUtc, length)

    /// Collects protected values that status output must never reveal.
    let private protectedValues (root: string) =
        let privateFileContents =
            try
                Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                |> Array.choose (fun path ->
                    if Path
                        .GetFileName(path)
                           .Equals("identity.pk8", StringComparison.Ordinal) then
                        try
                            Some(File.ReadAllText(path))
                        with
                        | _ -> None
                    else
                        None)
            with
            | _ -> [||]

        Array.append
            [|
                root
                "identity.pk8"
                "registration.json"
                "invalid-private-key-material"
                "test-token-should-not-appear"
                "UnauthorizedAccessException"
                "DirectoryNotFoundException"
            |]
            privateFileContents

    /// Fails without echoing protected values when command output exposes a sensitive path, secret, or raw filesystem detail.
    let private assertRedacted (output: string) (protectedValues: string array) =
        for value in protectedValues do
            if not (String.IsNullOrWhiteSpace(value)) then
                Assert.That(output.Contains(value, StringComparison.Ordinal), Is.False, "Cache status output exposed protected state.")

    /// Invokes the root command and proves the Cache status early path has no protected-state, repository, local-state, SDK, history, or loopback side effects.
    let private invoke (root: string) (repositoryRoot: string) (args: string array) =
        let rootBefore = snapshot root
        let historyPath = HistoryStorage.getHistoryFilePath ()
        let historyLockPath = HistoryStorage.getHistoryLockPath ()
        let historyBefore = snapshotFile historyPath, snapshotFile historyLockPath
        let tracePath = Path.Combine(repositoryRoot, "local-state-open-trace.log")
        let graceDirectory = Path.Combine(repositoryRoot, ".grace")

        Assert.That(Directory.Exists(graceDirectory), Is.False)
        Assert.That(File.Exists(tracePath), Is.False)

        let sdkIdentityBefore = Grace.SDK.ClientIdentity.tryGetConfiguredClientType ()

        use listener = new TcpListener(IPAddress.Loopback, 0)
        listener.Start()
        let endpoint = listener.LocalEndpoint :?> IPEndPoint

        let exitCode, output =
            withEnv "GRACE_SERVER_URI" (Some $"http://127.0.0.1:{endpoint.Port}") (fun () ->
                withEnv "GRACE_TOKEN" (Some "test-token-should-not-appear") (fun () ->
                    withEnv "GRACE_LOCALSTATE_DB_TRACE_PATH" (Some tracePath) (fun () ->
                        withEnv "GRACE_LOCALSTATE_DB_TRACE_OPEN" (Some "1") (fun () -> run args))))

        Assert.That(listener.Pending(), Is.False, "Cache status made a loopback request.")
        Assert.That(snapshot root = rootBefore, Is.True, "Cache status changed protected state.")
        Assert.That(Directory.Exists(graceDirectory), Is.False, "Cache status created repository local state.")
        Assert.That(File.Exists(tracePath), Is.False, "Cache status opened the LocalState DB.")
        Assert.That(snapshotFile historyPath = fst historyBefore, Is.True, "Cache status changed invocation history.")
        Assert.That(snapshotFile historyLockPath = snd historyBefore, Is.True, "Cache status changed the history lock.")
        Assert.That((Grace.SDK.ClientIdentity.tryGetConfiguredClientType () = sdkIdentityBefore), Is.True, "Cache status configured SDK identity.")
        assertRedacted output (protectedValues root)
        exitCode, output

    /// Creates an isolated protected state root, repository, home, and Cache root override for one serialized test.
    let private withRoot (action: string -> string -> unit) =
        let testRoot = Path.Combine(Path.GetTempPath(), $"grace-cache-status-{Guid.NewGuid():N}")
        let root = Path.Combine(testRoot, "protected-cache-root")
        let repositoryRoot = Path.Combine(testRoot, "repository")
        let home = Path.Combine(testRoot, "home")
        Directory.CreateDirectory(root) |> ignore

        Directory.CreateDirectory(repositoryRoot)
        |> ignore

        Directory.CreateDirectory(home) |> ignore

        File.SetUnixFileMode(
            root,
            UnixFileMode.UserRead
            ||| UnixFileMode.UserWrite
            ||| UnixFileMode.UserExecute
        )

        let originalDirectory = Environment.CurrentDirectory

        try
            CacheCommand.setStateRootForTests root
            Environment.CurrentDirectory <- repositoryRoot

            withEnv "USERPROFILE" (Some home) (fun () ->
                withEnv "HOME" (Some home) (fun () ->
                    withEnv Constants.EnvironmentVariables.GraceServerUri (Some "http://127.0.0.1:9") (fun () -> action root repositoryRoot)))
        finally
            Environment.CurrentDirectory <- originalDirectory
            CacheCommand.resetStateRootForTests ()

            if Directory.Exists(root) then
                File.SetUnixFileMode(
                    root,
                    UnixFileMode.UserRead
                    ||| UnixFileMode.UserWrite
                    ||| UnixFileMode.UserExecute
                )

            if Directory.Exists(testRoot) then Directory.Delete(testRoot, true)

    /// Creates the valid staged-attempt inspection state.
    let private createAttempt (root: string) = CacheIdentity.createAttempt root |> requireOk

    /// Creates the valid ready inspection state.
    let private createReady (root: string) =
        let publicKey = createAttempt root

        CacheIdentity.commitReady root (registration "https://cache.example.test" publicKey)
        |> requireOk

    /// Creates a syntactically invalid staged key while preserving the required protected file modes.
    let private createInvalid (root: string) =
        let attempt = Path.Combine(root, "attempt")
        let identity = Path.Combine(attempt, "identity.pk8")
        Directory.CreateDirectory(attempt) |> ignore

        File.SetUnixFileMode(
            attempt,
            UnixFileMode.UserRead
            ||| UnixFileMode.UserWrite
            ||| UnixFileMode.UserExecute
        )

        File.WriteAllText(identity, "invalid-private-key-material")
        File.SetUnixFileMode(identity, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

    /// Removes read and search access from the protected root to create the inaccessible inspection state.
    let private createInaccessible (root: string) = File.SetUnixFileMode(root, UnixFileMode.UserWrite)

    /// Verifies one JSON status contains only redacted fields for its state.
    let private assertStatus (enrollment: string) (key: string) (expectedExit: int) (output: string) =
        use document = JsonDocument.Parse(output)
        let status = document.RootElement.GetProperty("ReturnValue")
        Assert.That(expectedExit, Is.EqualTo(if enrollment = "enrolled" then 0 else 1))
        Assert.That(status.GetProperty("Class").GetString(), Is.EqualTo("Grace.Cache.Status"))
        Assert.That(status.GetProperty("Enrollment").GetString(), Is.EqualTo(enrollment))
        Assert.That(status.GetProperty("Key").GetString(), Is.EqualTo(key))

        if enrollment = "enrolled" then
            [
                "CacheId"
                "Endpoint"
                "BoundaryKind"
                "RepositoryCount"
            ]
            |> List.iter (fun name ->
                let mutable ignored = Unchecked.defaultof<JsonElement>
                Assert.That(status.TryGetProperty(name, &ignored), Is.True))
        else
            [
                "CacheId"
                "Endpoint"
                "BoundaryKind"
                "RepositoryCount"
            ]
            |> List.iter (fun name ->
                let mutable ignored = Unchecked.defaultof<JsonElement>
                Assert.That(status.TryGetProperty(name, &ignored), Is.False))

    /// Compares approved human status facts with the same facts in a JSON status envelope.
    let private assertHumanJsonParity (humanOutput: string) (jsonOutput: string) =
        use document = JsonDocument.Parse(jsonOutput)
        let status = document.RootElement.GetProperty("ReturnValue")

        for property in status.EnumerateObject() do
            let value =
                if property.Value.ValueKind = JsonValueKind.String then
                    property.Value.GetString()
                else
                    property.Value.ToString()

            Assert.That(humanOutput, Does.Contain($"{property.Name}: {value}"))

    /// Parses the complete shared JSON error envelope and verifies redacted exception details.
    let private assertRedactedError (exitCode: int) (output: string) =
        Assert.That(exitCode, Is.Not.EqualTo(0))
        use document = JsonDocument.Parse(output)
        let root = document.RootElement

        let names =
            root.EnumerateObject()
            |> Seq.map (fun property -> property.Name)
            |> Set.ofSeq

        Assert.That(
            (names = Set.ofList [ "Exception"
                                  "Error"
                                  "EventTime"
                                  "CorrelationId"
                                  "Properties" ]),
            Is.True
        )

        Assert.That(root.GetProperty("Error").GetString(), Is.Not.Empty)
        Assert.That(root.GetProperty("CorrelationId").GetString(), Is.Not.Empty)

        let exceptionDetails = root.GetProperty("Exception")

        Assert.That(
            exceptionDetails
                .GetProperty("Message")
                .GetString(),
            Is.Empty
        )

        Assert.That(
            exceptionDetails
                .GetProperty("StackTrace")
                .GetString(),
            Is.Empty
        )

        Assert.That(
            exceptionDetails
                .GetProperty(
                    "InnerException"
                )
                .ValueKind,
            Is.EqualTo(JsonValueKind.Null)
        )

        let mutable returnValue = Unchecked.defaultof<JsonElement>
        Assert.That(root.TryGetProperty("ReturnValue", &returnValue), Is.False)

    /// Proves root dispatch has no Linux identity implementation until running in the real service-user environment.
    [<Test>]
    let ``cache status maps unsupported hosts to inaccessible without repository state`` () =
        if OperatingSystem.IsLinux() then
            Assert.Ignore("This direct branch proof is for unsupported hosts.")

        let status = CacheIdentity.status CacheIdentity.StateRoot
        Assert.That(status.Enrollment, Is.EqualTo("invalid"))
        Assert.That(status.Key, Is.EqualTo("inaccessible"))

    /// Proves actual Linux root dispatch covers every finite protected identity inspection state.
    [<Test>]
    let ``Linux cache status projects every protected identity state through root dispatch`` () =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Cache status Product V1 supports Linux only.")

        let cases =
            [
                "Missing", (fun _ -> ()), "notEnrolled", "missing", 1
                "AttemptPresent", createAttempt >> ignore, "notEnrolled", "available", 1
                "Ready", createReady >> ignore, "enrolled", "available", 0
                "Invalid", createInvalid, "invalid", "invalid", 1
                "Inaccessible", createInaccessible, "invalid", "inaccessible", 1
            ]

        for name, setup, enrollment, key, expectedExit in cases do
            withRoot (fun root repositoryRoot ->
                setup root

                let exitCode, output =
                    invoke
                        root
                        repositoryRoot
                        [|
                            "--output"
                            "Json"
                            "cache"
                            "status"
                        |]

                Assert.That(exitCode, Is.EqualTo(expectedExit), name)
                assertStatus enrollment key expectedExit output)

    /// Proves every no-selector output mode preserves its shared renderer behavior for ready and representative non-ready status.
    [<Test>]
    let ``Linux cache status honors every shared no-selector output mode and human JSON parity`` () =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Cache status Product V1 supports Linux only.")

        let assertModes (root: string) (repositoryRoot: string) (expectedExit: int) =
            let normalExit, normal = invoke root repositoryRoot [| "cache"; "status" |]
            Assert.That(normalExit, Is.EqualTo(expectedExit))

            let minimalExit, minimal =
                invoke
                    root
                    repositoryRoot
                    [|
                        "--output"
                        "Minimal"
                        "cache"
                        "status"
                    |]

            Assert.That(minimalExit, Is.EqualTo(expectedExit))
            Assert.That(minimal, Is.Empty)

            let silentExit, silent =
                invoke
                    root
                    repositoryRoot
                    [|
                        "--output"
                        "Silent"
                        "cache"
                        "status"
                    |]

            Assert.That(silentExit, Is.EqualTo(expectedExit))
            Assert.That(silent, Is.Empty)

            let verboseExit, verbose =
                invoke
                    root
                    repositoryRoot
                    [|
                        "--output"
                        "Verbose"
                        "cache"
                        "status"
                    |]

            Assert.That(verboseExit, Is.EqualTo(expectedExit))
            Assert.That(verbose, Does.Contain("EventTime:"))

            let jsonExit, json =
                invoke
                    root
                    repositoryRoot
                    [|
                        "--output"
                        "Json"
                        "cache"
                        "status"
                    |]

            Assert.That(jsonExit, Is.EqualTo(expectedExit))
            assertHumanJsonParity normal json
            normal, json

        withRoot (fun root repositoryRoot ->
            let missingHuman, missingJson = assertModes root repositoryRoot 1
            assertStatus "notEnrolled" "missing" 1 missingJson
            Assert.That(missingHuman, Does.Contain("Enrollment: notEnrolled"))

            createReady root |> ignore
            let readyHuman, readyJson = assertModes root repositoryRoot 0
            assertStatus "enrolled" "available" 0 readyJson
            Assert.That(readyHuman, Does.Contain("CacheId: 11111111-1111-1111-1111-111111111111")))

    /// Proves selectors take precedence over human output modes and renderer failure wins over a ready domain exit.
    [<Test>]
    let ``Linux cache status selectors take precedence and use the shared redacted error envelope`` () =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Cache status Product V1 supports Linux only.")

        let outputModes =
            [
                "Normal"
                "Minimal"
                "Silent"
                "Verbose"
            ]

        let assertSelector (root: string) (repositoryRoot: string) (state: string) (expectedExit: int) (selector: string) (expected: string) =
            for outputMode in outputModes do
                let exitCode, output =
                    invoke
                        root
                        repositoryRoot
                        [|
                            "--output"
                            outputMode
                            "--select"
                            selector
                            "cache"
                            "status"
                        |]

                Assert.That(exitCode, Is.EqualTo(expectedExit), $"{state} {outputMode} {selector}")
                Assert.That(output.Trim(), Is.EqualTo($"\"{expected}\""), $"{state} {outputMode} {selector}")

        withRoot (fun root repositoryRoot ->
            assertSelector root repositoryRoot "missing" 1 "Enrollment" "notEnrolled"
            assertSelector root repositoryRoot "missing" 1 "Key" "missing"

            let missingCacheIdExit, missingCacheId =
                invoke
                    root
                    repositoryRoot
                    [|
                        "--select"
                        "CacheId"
                        "cache"
                        "status"
                    |]

            assertRedactedError missingCacheIdExit missingCacheId
            Assert.That(missingCacheId, Does.Contain("was not found in ReturnValue"))

            for selector, expectedError in
                [
                    "CacheId[0]", "supports only dot-separated"
                    "Error", "cannot project 'Error'"
                ] do
                let exitCode, output =
                    invoke
                        root
                        repositoryRoot
                        [|
                            "--select"
                            selector
                            "cache"
                            "status"
                        |]

                assertRedactedError exitCode output

                use errorDocument = JsonDocument.Parse(output)

                Assert.That(
                    errorDocument
                        .RootElement
                        .GetProperty("Error")
                        .GetString(),
                    Does.Contain(expectedError)
                )

            createReady root |> ignore
            assertSelector root repositoryRoot "ready" 0 "Enrollment" "enrolled"
            assertSelector root repositoryRoot "ready" 0 "Key" "available"
            assertSelector root repositoryRoot "ready" 0 "CacheId" "11111111-1111-1111-1111-111111111111"

            let missingReadyFieldExit, missingReadyField =
                invoke
                    root
                    repositoryRoot
                    [|
                        "--select"
                        "DoesNotExist"
                        "cache"
                        "status"
                    |]

            assertRedactedError missingReadyFieldExit missingReadyField
            Assert.That(missingReadyField, Does.Contain("was not found in ReturnValue")))

    /// Proves built Cache status schema and examples remain inert and retain their existing-behavior registry metadata.
    [<Test>]
    let ``Linux cache status built schema and examples are existing behavior without side effects`` () =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Cache status Product V1 supports Linux only.")

        withRoot (fun root repositoryRoot ->
            let schemaExit, schemaOutput = invoke root repositoryRoot [| "cache"; "status"; "--schema" |]
            Assert.That(schemaExit, Is.EqualTo(0))

            use schemaDocument = JsonDocument.Parse(schemaOutput)
            let schemaRegistry = schemaDocument.RootElement.GetProperty("Registry")
            Assert.That(schemaRegistry.GetProperty("Schema").GetString(), Is.EqualTo("ExistingBehavior"))
            Assert.That(schemaRegistry.GetProperty("Examples").GetString(), Is.EqualTo("ExistingBehavior"))

            let examplesExit, examplesOutput = invoke root repositoryRoot [| "cache"; "status"; "--examples" |]
            Assert.That(examplesExit, Is.EqualTo(0))

            use examplesDocument = JsonDocument.Parse(examplesOutput)
            let examplesRegistry = examplesDocument.RootElement.GetProperty("Registry")

            Assert.That(
                examplesRegistry
                    .GetProperty("JsonMode")
                    .GetString(),
                Is.EqualTo("ExistingBehavior")
            )

            Assert.That(
                examplesDocument
                    .RootElement
                    .GetProperty("Examples")
                    .GetArrayLength(),
                Is.GreaterThanOrEqualTo(3)
            ))

    /// Proves the enrolled result is committed once and rendered through every supported output mode and selector.
    [<Test>]
    let ``Linux cache enroll commits one accepted root-dispatch attempt and renders every output mode`` () =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Cache enrollment Product V1 supports Linux only.")

        for mode in
            [
                "Normal"
                "Minimal"
                "Silent"
                "Verbose"
                "Json"
            ] do
            withRoot (fun root _ ->
                let mutable bearerCount = 0
                let mutable postCount = 0

                let dependencies =
                    enrollmentDependencies
                        root
                        (fun _ ->
                            bearerCount <- bearerCount + 1
                            Task.FromResult(Ok "pat-test-token"))
                        (fun request _ bearer _ _ ->
                            postCount <- postCount + 1
                            Assert.That(bearer, Is.EqualTo("pat-test-token"))
                            Task.FromResult(Accepted(acceptedResponse request)))
                        (fun _ -> Task.FromResult(()))
                        CacheIdentity.commitClaimedReady

                let arguments = Array.copy enrollmentArguments
                arguments[1] <- mode
                let exitCode, output = runEnrollment dependencies arguments CancellationToken.None
                Assert.That(exitCode, Is.EqualTo(0), $"{mode}: {output}")
                Assert.That(bearerCount, Is.EqualTo(1))
                Assert.That(postCount, Is.EqualTo(1))
                Assert.That((CacheIdentity.status root).Enrollment, Is.EqualTo("enrolled"))

                match mode with
                | "Normal" -> Assert.That(output, Does.Contain("Enrollment: enrolled"))
                | "Verbose" -> Assert.That(output, Does.Contain("Enrollment: enrolled"))
                | "Json" ->
                    use document = JsonDocument.Parse(output)

                    Assert.That(
                        document
                            .RootElement
                            .GetProperty("ReturnValue")
                            .GetProperty("Enrollment")
                            .GetString(),
                        Is.EqualTo("enrolled")
                    )
                | "Minimal"
                | "Silent" -> Assert.That(output, Is.Empty)
                | _ -> Assert.Fail($"Unsupported output mode {mode}")

                let rootWithSelector = Path.Combine(Path.GetTempPath(), $"grace-cache-select-{Guid.NewGuid():N}")

                Directory.CreateDirectory(rootWithSelector)
                |> ignore

                try
                    File.SetUnixFileMode(
                        rootWithSelector,
                        UnixFileMode.UserRead
                        ||| UnixFileMode.UserWrite
                        ||| UnixFileMode.UserExecute
                    )

                    let selectorDependencies = { dependencies with StateRoot = rootWithSelector }
                    let selectorArguments = Array.append arguments [| "--select"; "Enrollment" |]
                    let selectedExit, selectedOutput = runEnrollment selectorDependencies selectorArguments CancellationToken.None
                    Assert.That(selectedExit, Is.EqualTo(0), $"{mode} selector: {selectedOutput}")
                    Assert.That(selectedOutput.Trim(), Is.EqualTo("\"enrolled\""))
                finally
                    if Directory.Exists(rootWithSelector) then
                        Directory.Delete(rootWithSelector, true))

    /// Proves credential failures happen before a protected claim, staged key, or POST and leave a prior ready state unchanged.
    [<Test>]
    let ``Linux cache enroll rejects missing and failed credentials before effects`` () =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Cache enrollment Product V1 supports Linux only.")

        for credentialResult in
            [
                Error "Authentication required."
                Ok ""
            ] do
            withRoot (fun root _ ->
                let before = snapshot root
                let mutable postCount = 0

                let dependencies =
                    enrollmentDependencies
                        root
                        (fun _ -> Task.FromResult(credentialResult))
                        (fun _ _ _ _ _ ->
                            postCount <- postCount + 1
                            Task.FromResult(Rejected(GraceError.Create "Unexpected POST." "cache-test")))
                        (fun _ -> Task.FromResult(()))
                        CacheIdentity.commitClaimedReady

                let exitCode, output = runEnrollment dependencies enrollmentArguments CancellationToken.None
                Assert.That(exitCode, Is.Not.EqualTo(0), output)
                Assert.That(postCount, Is.EqualTo(0))
                Assert.That(snapshot root = before, Is.True)
                Assert.That(output, Does.Not.Contain("identity.pk8")))

        withRoot (fun root _ ->
            createReady root |> ignore
            let before = snapshot root
            let mutable bearerCount = 0
            let mutable postCount = 0

            let dependencies =
                enrollmentDependencies
                    root
                    (fun _ ->
                        bearerCount <- bearerCount + 1
                        Task.FromResult(Error "Authentication required."))
                    (fun _ _ _ _ _ ->
                        postCount <- postCount + 1
                        Task.FromResult(Rejected(GraceError.Create "Unexpected POST." "cache-test")))
                    (fun _ -> Task.FromResult(()))
                    CacheIdentity.commitClaimedReady

            let exitCode, output = runEnrollment dependencies enrollmentArguments CancellationToken.None
            Assert.That(exitCode, Is.Not.EqualTo(0), output)
            Assert.That(bearerCount, Is.EqualTo(0))
            Assert.That(postCount, Is.EqualTo(0))
            Assert.That(snapshot root = before, Is.True))

    /// Proves cancellation before transport is truthful and removes the staged attempt without issuing a request.
    [<Test>]
    let ``Linux cache enroll cancellation phases clean local state before transport`` () =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Cache enrollment Product V1 supports Linux only.")

        let runCancelled phase =
            withRoot (fun root _ ->
                use cancellation = new CancellationTokenSource()
                let before = snapshot root
                let mutable postCount = 0

                let resolveBearer (cancellationToken: CancellationToken) =
                    if phase = "credential" then
                        task {
                            do! Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                            return Ok "not-reached"
                        }
                    else
                        Task.FromResult(Ok "phase-token")

                let onPhase current =
                    if (phase = "before-claim"
                        && current = CacheCommand.EnrollmentPhase.CredentialResolved)
                       || (phase = "after-staging"
                           && current = CacheCommand.EnrollmentPhase.AttemptStaged) then
                        cancellation.Cancel()

                    Task.FromResult(())

                let dependencies =
                    enrollmentDependencies
                        root
                        resolveBearer
                        (fun _ _ _ _ _ ->
                            postCount <- postCount + 1
                            Task.FromResult(Rejected(GraceError.Create "Unexpected POST." "cache-test")))
                        onPhase
                        CacheIdentity.commitClaimedReady

                if phase = "credential" then cancellation.Cancel()

                let exitCode, output = runEnrollment dependencies enrollmentArguments cancellation.Token
                Assert.That(exitCode, Is.Not.EqualTo(0), output)
                Assert.That(output, Does.Contain("cancelled"))
                Assert.That(postCount, Is.EqualTo(0))
                Assert.That((CacheIdentity.status root).Enrollment, Is.EqualTo("notEnrolled"))
                Assert.That(Directory.Exists(Path.Combine(root, "attempt")), Is.False))

        runCancelled "credential"
        runCancelled "before-claim"
        runCancelled "after-staging"

    /// Proves terminal definite and indeterminate transport outcomes do not retry or publish ready state.
    [<Test>]
    let ``Linux cache enroll preserves truthful state for rejected indeterminate and malformed responses`` () =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Cache enrollment Product V1 supports Linux only.")

        let cases =
            [
                "rejected", (fun (_: CacheEnrollmentRequest) -> Rejected(GraceError.Create "Selected server rejected enrollment." "cache-test"))
                "indeterminate", (fun _ -> Indeterminate(GraceError.Create "Cache enrollment outcome is unknown after transport started." "cache-test"))
                "malformed",
                fun request ->
                    let accepted = acceptedResponse request
                    Accepted { accepted with ReturnValue = { accepted.ReturnValue with Class = "UnexpectedResult" } }
            ]

        for name, makeOutcome in cases do
            withRoot (fun root _ ->
                let mutable postCount = 0

                let dependencies =
                    enrollmentDependencies
                        root
                        (fun _ -> Task.FromResult(Ok "transport-token"))
                        (fun request _ _ _ _ ->
                            postCount <- postCount + 1
                            Task.FromResult(makeOutcome request))
                        (fun _ -> Task.FromResult(()))
                        CacheIdentity.commitClaimedReady

                let exitCode, output = runEnrollment dependencies enrollmentArguments CancellationToken.None
                Assert.That(exitCode, Is.Not.EqualTo(0), $"{name}: {output}")
                Assert.That(postCount, Is.EqualTo(1), $"{name} retried transport.")
                Assert.That((CacheIdentity.status root).Enrollment, Is.Not.EqualTo("enrolled"))
                Assert.That(Directory.Exists(Path.Combine(root, "attempt")), Is.False)
                Assert.That(File.Exists(Path.Combine(root, "enrollment.claim")), Is.True))

    /// Proves an accepted response cannot report ready state when the final local publication fails.
    [<Test>]
    let ``Linux cache enroll removes the attempt when ready publication fails`` () =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Cache enrollment Product V1 supports Linux only.")

        withRoot (fun root _ ->
            let mutable postCount = 0

            let dependencies =
                enrollmentDependencies
                    root
                    (fun _ -> Task.FromResult(Ok "commit-token"))
                    (fun request _ _ _ _ ->
                        postCount <- postCount + 1
                        Task.FromResult(Accepted(acceptedResponse request)))
                    (fun _ -> Task.FromResult(()))
                    (fun _ _ _ -> Error CacheIdentityError.StateUnavailable)

            let exitCode, output = runEnrollment dependencies enrollmentArguments CancellationToken.None
            Assert.That(exitCode, Is.Not.EqualTo(0), output)
            Assert.That(postCount, Is.EqualTo(1))
            Assert.That((CacheIdentity.status root).Enrollment, Is.Not.EqualTo("enrolled"))
            Assert.That(Directory.Exists(Path.Combine(root, "attempt")), Is.False)
            Assert.That(File.Exists(Path.Combine(root, "enrollment.claim")), Is.True))

    /// Proves the selected-server SDK transport rejects one redirect response without following it or publishing ready state.
    [<Test>]
    let ``Linux cache enroll rejects a selected-server redirect without a second POST`` () =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Cache enrollment Product V1 supports Linux only.")

        withRoot (fun root _ ->
            use listener = new TcpListener(IPAddress.Loopback, 0)
            listener.Start()
            let serverUri = Uri($"http://127.0.0.1:{(listener.LocalEndpoint :?> IPEndPoint).Port}")
            let received = TaskCompletionSource<EnrollmentRequest>(TaskCreationOptions.RunContinuationsAsynchronously)

            let serverTask =
                Task.Run(
                    Func<Task> (fun () ->
                        task {
                            let! client = listener.AcceptTcpClientAsync()
                            use client = client
                            let! request = readEnrollmentRequest client
                            received.TrySetResult(request) |> ignore
                            do! writeEnrollmentResponse client 302 "{\"redirect\":true}"
                        })
                )

            try
                let dependencies =
                    enrollmentDependencies
                        root
                        (fun _ -> Task.FromResult(Ok "redirect-token"))
                        (fun request _ bearer correlationId cancellationToken ->
                            CacheRegistration.Enroll(request, serverUri, bearer, correlationId, cancellationToken))
                        (fun _ -> Task.FromResult(()))
                        CacheIdentity.commitClaimedReady

                let exitCode, output = runEnrollment dependencies enrollmentArguments CancellationToken.None
                Assert.That(exitCode, Is.Not.EqualTo(0), output)
                let request = received.Task.GetAwaiter().GetResult()
                Assert.That(request.Method, Is.EqualTo("POST"))
                Assert.That(request.Path, Is.EqualTo("/cache/enroll"))
                Assert.That((CacheIdentity.status root).Enrollment, Is.Not.EqualTo("enrolled"))
                Assert.That(Directory.Exists(Path.Combine(root, "attempt")), Is.False)
                Assert.That(serverTask.Wait(TimeSpan.FromSeconds(5.0)), Is.True)
            finally
                listener.Stop())

    /// Proves PAT and M2M credential producers each supply one bearer to the selected server through the production root graph.
    [<Test>]
    let ``Linux cache enroll uses production PAT and machine credentials exactly once`` () =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Cache enrollment Product V1 supports Linux only.")

        let clearInteractive =
            [
                Constants.EnvironmentVariables.GraceTokenFile, None
                Constants.EnvironmentVariables.GraceAuthOidcCliClientId, None
                Constants.EnvironmentVariables.GraceAuthOidcCliRedirectPort, None
                Constants.EnvironmentVariables.GraceAuthOidcCliScopes, None
            ]

        let assertSingleEnrollment expectedBearer (requests: ConcurrentQueue<EnrollmentRequest>) =
            let enrollmentRequests =
                requests.ToArray()
                |> Array.filter (fun request -> request.Path = "/cache/enroll")

            Assert.That(enrollmentRequests.Length, Is.EqualTo(1))
            Assert.That(enrollmentRequests[0].Method, Is.EqualTo("POST"))
            Assert.That(enrollmentRequests[0].Authorization, Is.EqualTo(Some $"Bearer {expectedBearer}"))

        let pat = Grace.Types.PersonalAccessToken.formatToken "cache-test-user" (Guid.NewGuid()) (Array.zeroCreate 32)

        withRoot (fun root _ ->
            withProductionEnrollmentServer (fun serverUri requests ->
                withCredentialEnvironment
                    ([
                        Constants.EnvironmentVariables.GraceServerUri, Some serverUri.AbsoluteUri
                        Constants.EnvironmentVariables.GraceToken, Some pat
                        Constants.EnvironmentVariables.GraceAuthOidcAuthority, None
                        Constants.EnvironmentVariables.GraceAuthOidcAudience, None
                        Constants.EnvironmentVariables.GraceAuthOidcM2mClientId, None
                        Constants.EnvironmentVariables.GraceAuthOidcM2mClientSecret, None
                        Constants.EnvironmentVariables.GraceAuthOidcM2mScopes, None
                     ]
                     @ clearInteractive)
                    (fun () ->
                        let exitCode, output = runProductionEnrollment enrollmentArguments
                        Assert.That(exitCode, Is.EqualTo(0), output)
                        assertSingleEnrollment pat requests
                        Assert.That((CacheIdentity.status root).Enrollment, Is.EqualTo("enrolled")))))

        withRoot (fun root _ ->
            withProductionEnrollmentServer (fun serverUri requests ->
                withCredentialEnvironment
                    ([
                        Constants.EnvironmentVariables.GraceServerUri, Some serverUri.AbsoluteUri
                        Constants.EnvironmentVariables.GraceToken, None
                        Constants.EnvironmentVariables.GraceAuthOidcAuthority, Some serverUri.AbsoluteUri
                        Constants.EnvironmentVariables.GraceAuthOidcAudience, Some "https://grace.test/api"
                        Constants.EnvironmentVariables.GraceAuthOidcM2mClientId, Some "cache-m2m-client"
                        Constants.EnvironmentVariables.GraceAuthOidcM2mClientSecret, Some "cache-m2m-secret"
                        Constants.EnvironmentVariables.GraceAuthOidcM2mScopes, Some "cache.enroll"
                     ]
                     @ clearInteractive)
                    (fun () ->
                        let exitCode, output = runProductionEnrollment enrollmentArguments
                        Assert.That(exitCode, Is.EqualTo(0), output)
                        assertSingleEnrollment "m2m-access-token" requests

                        let paths =
                            requests.ToArray()
                            |> Array.map (fun request -> request.Path)

                        Assert.That((paths = [| "/oauth/token"; "/cache/enroll" |]), Is.True)
                        Assert.That((CacheIdentity.status root).Enrollment, Is.EqualTo("enrolled")))))

    /// Proves the normal-build holder owns the protected enrollment claim until its exact process is killed and joined.
    [<Test>]
    let ``Linux claim holder blocks enrollment claim then releases a retry`` () =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Claim-holder process proof requires Linux protected-root semantics.")

        withRoot (fun root _ ->
            let holder =
                Path.GetFullPath(
                    Path.Combine(
                        AppContext.BaseDirectory,
                        "..",
                        "..",
                        "..",
                        "..",
                        "Grace.CLI.CacheEnrollment.ClaimHolder",
                        "bin",
                        "Release",
                        "net10.0",
                        "Grace.CLI.CacheEnrollment.ClaimHolder.dll"
                    )
                )

            Assert.That(File.Exists(holder), Is.True, "The normal build did not produce the Cache enrollment claim holder.")
            let signalPath = Path.Combine(Path.GetTempPath(), $"grace-cache-holder-{Guid.NewGuid():N}")
            use holderProcess = new Process()
            holderProcess.StartInfo.FileName <- "dotnet"
            holderProcess.StartInfo.UseShellExecute <- false
            holderProcess.StartInfo.CreateNoWindow <- true
            holderProcess.StartInfo.ArgumentList.Add(holder)
            holderProcess.StartInfo.ArgumentList.Add(root)
            holderProcess.StartInfo.ArgumentList.Add(signalPath)

            try
                Assert.That(holderProcess.Start(), Is.True)
                let deadline = DateTime.UtcNow.AddSeconds(10.0)

                while not (File.Exists(signalPath))
                      && DateTime.UtcNow < deadline do
                    Thread.Sleep(50)

                Assert.That(File.Exists(signalPath), Is.True, "The holder did not acquire the production claim.")

                match CacheIdentity.tryAcquireEnrollmentClaim root with
                | Error CacheIdentityError.StateUnavailable -> ()
                | Error error -> Assert.Fail($"Expected a conflicting claim failure, received {error}.")
                | Ok claim ->
                    CacheIdentity.releaseEnrollmentClaim claim
                    Assert.Fail("A second process acquired the claim while the holder was live.")

                holderProcess.Kill()
                Assert.That(holderProcess.WaitForExit(10000), Is.True, "The exact claim-holder process did not exit.")

                let retry =
                    CacheIdentity.tryAcquireEnrollmentClaim root
                    |> requireOk

                CacheIdentity.releaseEnrollmentClaim retry
            finally
                if not holderProcess.HasExited then
                    holderProcess.Kill()
                    holderProcess.WaitForExit(10000) |> ignore

                if File.Exists(signalPath) then File.Delete(signalPath))

    /// Proves the Linux libsecret-backed interactive credential is read from the real GNOME keyring before one enrollment POST.
    [<Test>]
    let ``Linux cache enroll uses the real GNOME keyring interactive credential once`` () =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Cache enrollment Product V1 supports Linux only.")

        if
            Environment.GetEnvironmentVariable("GRACE_TEST_LINUX_KEYRING")
            <> "1"
        then
            Assert.Ignore("This proof requires the D-Bus and GNOME keyring session configured by validate.yml.")

        withRoot (fun root _ ->
            withProductionEnrollmentServer (fun serverUri requests ->
                withCredentialEnvironment
                    [
                        Constants.EnvironmentVariables.GraceServerUri, Some serverUri.AbsoluteUri
                        Constants.EnvironmentVariables.GraceToken, None
                        Constants.EnvironmentVariables.GraceTokenFile, None
                        Constants.EnvironmentVariables.GraceAuthOidcAuthority, Some serverUri.AbsoluteUri
                        Constants.EnvironmentVariables.GraceAuthOidcAudience, Some "https://grace.test/api"
                        Constants.EnvironmentVariables.GraceAuthOidcM2mClientId, None
                        Constants.EnvironmentVariables.GraceAuthOidcM2mClientSecret, None
                        Constants.EnvironmentVariables.GraceAuthOidcM2mScopes, None
                        Constants.EnvironmentVariables.GraceAuthOidcCliClientId, Some "cache-interactive-client"
                        Constants.EnvironmentVariables.GraceAuthOidcCliRedirectPort, None
                        Constants.EnvironmentVariables.GraceAuthOidcCliScopes, Some "openid offline_access"
                    ]
                    (fun () ->
                        let loginExit, loginOutput =
                            runProductionEnrollment [| "authenticate"
                                                       "login"
                                                       "--auth"
                                                       "device" |]

                        Assert.That(loginExit, Is.EqualTo(0), loginOutput)
                        let exitCode, output = runProductionEnrollment enrollmentArguments
                        Assert.That(exitCode, Is.EqualTo(0), output)
                        let requests = requests.ToArray()

                        let enrollments =
                            requests
                            |> Array.filter (fun request -> request.Path = "/cache/enroll")

                        Assert.That(enrollments.Length, Is.EqualTo(1))
                        Assert.That(enrollments[0].Authorization, Is.EqualTo(Some "Bearer interactive-access-token"))

                        let paths =
                            requests
                            |> Array.map (fun request -> request.Path)

                        Assert.That(
                            (paths = [|
                                "/oauth/device/code"
                                "/oauth/token"
                                "/cache/enroll"
                            |]),
                            Is.True
                        )

                        Assert.That((CacheIdentity.status root).Enrollment, Is.EqualTo("enrolled")))))
