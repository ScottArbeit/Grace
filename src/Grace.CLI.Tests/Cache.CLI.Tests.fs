namespace Grace.CLI.Tests

open Grace.Cache
open Grace.CLI
open Grace.CLI.Command
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
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Covers serialized root-command behavior for pure local Cache status.
[<TestFixture>]
[<NonParallelizable>]
module CacheCliTests =

    /// Captures one loopback request made by a production credential or enrollment path.
    type private ReceivedRequest = { Method: string; Path: string; Authorization: string option; Body: string }

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

    /// Applies a complete isolated credential environment for one root-command test.
    let private withEnvironment (values: (string * string option) list) (action: unit -> 'T) =
        let rec apply remaining =
            match remaining with
            | [] -> action ()
            | (name, value) :: tail -> withEnv name value (fun () -> apply tail)

        apply values

    /// Reads one complete HTTP/1.1 request from the loopback credential responder.
    let private readRequest (client: TcpClient) =
        task {
            let stream = client.GetStream()
            use reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true)
            let! requestLine = reader.ReadLineAsync()

            if String.IsNullOrWhiteSpace(requestLine) then
                return { Method = String.Empty; Path = String.Empty; Authorization = None; Body = String.Empty }
            else
                let requestParts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                let headers = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                let mutable headerLine = reader.ReadLine()

                while not (String.IsNullOrEmpty(headerLine)) do
                    let separator = headerLine.IndexOf(':')

                    if separator > 0 then
                        headers[headerLine.Substring(0, separator).Trim()] <- headerLine.Substring(separator + 1).Trim()

                    headerLine <- reader.ReadLine()

                let contentLength =
                    match headers.TryGetValue("Content-Length") with
                    | true, value ->
                        match Int32.TryParse(value) with
                        | true, parsed when parsed > 0 -> parsed
                        | _ -> 0
                    | false, _ -> 0

                let readCharacters count =
                    task {
                        let characters = Array.zeroCreate<char> count
                        let mutable offset = 0

                        while offset < count do
                            let! read = reader.ReadAsync(characters, offset, count - offset)

                            if read = 0 then
                                failwith "The loopback responder received an incomplete HTTP request body."
                            else
                                offset <- offset + read

                        return String(characters)
                    }

                let readChunkedBody () =
                    task {
                        let body = StringBuilder()
                        let mutable finished = false

                        while not finished do
                            let! sizeLine = reader.ReadLineAsync()

                            if String.IsNullOrWhiteSpace(sizeLine) then
                                failwith "The loopback responder received an incomplete chunked HTTP request body."

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
                                    failwith "The loopback responder received unsupported HTTP chunk trailers."

                                finished <- true
                            | _ -> failwith $"The loopback responder received an invalid HTTP chunk size '{sizeText}'."

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

                let authorization =
                    match headers.TryGetValue("Authorization") with
                    | true, value -> Some value
                    | false, _ -> None

                return
                    {
                        Method = if requestParts.Length > 0 then requestParts[0] else String.Empty
                        Path = if requestParts.Length > 1 then requestParts[1] else String.Empty
                        Authorization = authorization
                        Body = body
                    }
        }

    /// Writes one compact JSON response from the loopback credential responder.
    let private writeResponse (client: TcpClient) (statusCode: int) (body: string) =
        task {
            use stream = client.GetStream()
            let bodyBytes = Encoding.UTF8.GetBytes(body)
            let reasonPhrase = if statusCode = 200 then "OK" else "Not Found"

            let headers =
                $"HTTP/1.1 {statusCode} {reasonPhrase}\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n"
                |> Encoding.ASCII.GetBytes

            do! stream.WriteAsync(headers, 0, headers.Length)
            do! stream.WriteAsync(bodyBytes, 0, bodyBytes.Length)
        }

    /// Constructs a strictly accepted enrollment response after applying the production server's display-name normalization.
    let private acceptedEnrollmentResponseWithDisplayName (normalizeDisplayName: string -> string) (request: ReceivedRequest) =
        let enrollment = JsonSerializer.Deserialize<CacheEnrollmentRequest>(request.Body, Constants.JsonSerializerOptions)
        Assert.That(enrollment, Is.Not.Null, "The enrollment responder received an invalid request body.")
        let now = SystemClock.Instance.GetCurrentInstant()

        let registration =
            {
                Class = nameof CacheRegistration
                CacheId = Guid.Parse("11111111-1111-1111-1111-111111111111")
                DisplayName = normalizeDisplayName enrollment.DisplayName
                BoundaryKind = enrollment.BoundaryKind
                OwnerId = enrollment.OwnerId
                OrganizationId = enrollment.OrganizationId
                RepositoryScopes = enrollment.RepositoryScopes |> Seq.toArray
                PublicKey = enrollment.PublicKey
                Endpoint = enrollment.Endpoint
                AllowHttpEndpoint = enrollment.AllowHttpEndpoint
                Health = CacheHealthStatus.Unhealthy
                SoftwareVersion = enrollment.SoftwareVersion
                ProtocolVersion = enrollment.ProtocolVersion
                PrefetchSupported = enrollment.PrefetchSupported
                EnrolledBy = "test-admin"
                EnrolledAt = now
                LastRefreshedAt = now
                RefreshAfter = now.Plus(Duration.FromHours(1))
                ExpiresAt = now.Plus(Duration.FromHours(2))
                RevokedAt = None
            }

        CacheRegistrationResult.Create(CacheRegistrationRefreshStatus.Enrolled, Some registration, "enrolled")
        |> fun result -> GraceReturnValue.Create result "test-correlation"
        |> fun result -> JsonSerializer.Serialize(result, Constants.JsonSerializerOptions)

    /// Constructs a strictly accepted enrollment response from the exact production request sent by the CLI.
    let private acceptedEnrollmentResponse (request: ReceivedRequest) = acceptedEnrollmentResponseWithDisplayName id request

    /// Hosts local OAuth and enrollment endpoints used to prove real credential producers through root command dispatch.
    let private withEnrollmentResponderWith enrollmentResponse (action: Uri -> ConcurrentQueue<ReceivedRequest> -> unit) =
        use listener = new TcpListener(IPAddress.Loopback, 0)
        use cancellation = new CancellationTokenSource()
        let requests = ConcurrentQueue<ReceivedRequest>()
        listener.Start()
        let port = (listener.LocalEndpoint :?> IPEndPoint).Port
        let authority = Uri($"http://127.0.0.1:{port}")

        let deviceCodeResponse =
            "{\"device_code\":\"device-code\",\"user_code\":\"ABCD\",\"verification_uri\":\""
            + authority.AbsoluteUri
            + "\",\"expires_in\":120,\"interval\":1}"

        let rec serve () =
            task {
                if not cancellation.IsCancellationRequested then
                    try
                        let! client =
                            listener
                                .AcceptTcpClientAsync(cancellation.Token)
                                .AsTask()

                        use client = client
                        let! request = readRequest client
                        requests.Enqueue(request)

                        let response =
                            match request.Path with
                            | "/oauth/device/code" -> 200, deviceCodeResponse
                            | "/oauth/token" when request.Body.Contains("urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code") ->
                                200,
                                "{\"access_token\":\"interactive-access-token\",\"refresh_token\":\"interactive-refresh-token\",\"expires_in\":3600,\"scope\":\"openid offline_access\",\"token_type\":\"Bearer\"}"
                            | "/oauth/token" -> 200, "{\"access_token\":\"m2m-access-token\",\"expires_in\":3600,\"token_type\":\"Bearer\"}"
                            | "/cache/enroll" -> 200, enrollmentResponse request
                            | _ -> 404, "{\"error\":\"not_found\"}"

                        do! writeResponse client (fst response) (snd response)
                        return! serve ()
                    with
                    | :? OperationCanceledException -> return ()
                    | :? ObjectDisposedException -> return ()
            }

        let serverTask = Task.Run(Func<Task>(fun () -> serve ()))

        try
            action authority requests
        finally
            cancellation.Cancel()
            listener.Stop()

            if not (serverTask.Wait(TimeSpan.FromSeconds(5.0))) then
                Assert.Fail("Timed out waiting for the local OAuth/enrollment responder to stop.")

    /// Hosts the standard strict enrollment responder used by credential and output tests.
    let private withEnrollmentResponder (action: Uri -> ConcurrentQueue<ReceivedRequest> -> unit) =
        withEnrollmentResponderWith acceptedEnrollmentResponse action

    /// Applies an isolated PAT-only credential environment to an enrollment command test.
    let private withPatEnrollmentEnvironment (serverUri: Uri) (pat: string) action =
        withEnvironment
            [
                Constants.EnvironmentVariables.GraceServerUri, Some serverUri.AbsoluteUri
                Constants.EnvironmentVariables.GraceToken, Some pat
                Constants.EnvironmentVariables.GraceTokenFile, None
                Constants.EnvironmentVariables.GraceAuthOidcAuthority, None
                Constants.EnvironmentVariables.GraceAuthOidcAudience, None
                Constants.EnvironmentVariables.GraceAuthOidcM2mClientId, None
                Constants.EnvironmentVariables.GraceAuthOidcM2mClientSecret, None
                Constants.EnvironmentVariables.GraceAuthOidcM2mScopes, None
                Constants.EnvironmentVariables.GraceAuthOidcCliClientId, None
                Constants.EnvironmentVariables.GraceAuthOidcCliRedirectPort, None
                Constants.EnvironmentVariables.GraceAuthOidcCliScopes, None
            ]
            action

    /// Supplies the closed Product V1 enrollment grammar with one Linux HTTP test endpoint.
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

    /// Checks the shared redacted ready-status result and the one exact authenticated enrollment effect.
    let private assertSuccessfulEnrollment (expectedBearer: string) (root: string) (requests: ConcurrentQueue<ReceivedRequest>) =
        let enrollmentRequests =
            requests.ToArray()
            |> Array.filter (fun request -> request.Path = "/cache/enroll")

        Assert.That(enrollmentRequests.Length, Is.EqualTo(1), "The supported producer must make exactly one enrollment POST.")
        let request = enrollmentRequests[0]
        Assert.That(request.Method, Is.EqualTo("POST"))
        Assert.That(request.Authorization, Is.EqualTo(Some(String.Concat("Bearer ", expectedBearer))))

        match CacheIdentity.status root with
        | status when
            status.Enrollment = "enrolled"
            && status.Key = "available"
            ->
            ()
        | status -> Assert.Fail($"Enrollment did not publish ready protected state: {status.Enrollment}/{status.Key}.")

    /// Verifies a credential producer made the expected ordered local OAuth and enrollment requests.
    let private assertRequestPaths (expected: string array) (actual: string array) =
        Assert.That(actual.Length, Is.EqualTo(expected.Length))
        Assert.That(Array.forall2 (=) expected actual, Is.True)

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

            withEnv "USERPROFILE" (Some home) (fun () -> withEnv "HOME" (Some home) (fun () -> action root repositoryRoot))
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

    /// Verifies the normal-build holder directly owns the production root claim until its exact process is terminated and joined.
    [<Test>]
    let ``Linux claim holder blocks then releases the production enrollment claim`` () =
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

            Assert.That(File.Exists(holder), Is.True, "The normal solution build did not produce the enrollment claim holder.")
            let signalPath = Path.Combine(Path.GetTempPath(), $"grace-cache-claim-holder-{Guid.NewGuid():N}")
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

                Assert.That(File.Exists(signalPath), Is.True, "The holder did not report its acquired production claim.")

                match CacheIdentity.tryAcquireEnrollmentClaim root with
                | Error CacheIdentityError.StateUnavailable -> ()
                | Error error -> Assert.Fail($"Expected a conflicting claim to fail safely, received {error}.")
                | Ok claim ->
                    CacheIdentity.releaseEnrollmentClaim claim
                    Assert.Fail("A second holder acquired the production claim while the direct holder was alive.")

                holderProcess.Kill()
                Assert.That(holderProcess.WaitForExit(10000), Is.True, "The exact direct claim holder did not exit after termination.")

                let retry =
                    CacheIdentity.tryAcquireEnrollmentClaim root
                    |> requireOk

                CacheIdentity.releaseEnrollmentClaim retry
            finally
                if not holderProcess.HasExited then
                    holderProcess.Kill()
                    holderProcess.WaitForExit(10000) |> ignore

                if File.Exists(signalPath) then File.Delete(signalPath))

    /// Verifies GRACE_TOKEN resolves before root effects and completes one authenticated enrollment POST.
    [<Test>]
    let ``Linux cache enroll dispatches a PAT through the selected server once`` () =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Cache enrollment Product V1 supports Linux only.")

        let pat = Grace.Types.PersonalAccessToken.formatToken "cache-test-user" (Guid.NewGuid()) (Array.zeroCreate 32)

        withRoot (fun root _ ->
            withEnrollmentResponder (fun serverUri requests ->
                withEnvironment
                    [
                        Constants.EnvironmentVariables.GraceServerUri, Some serverUri.AbsoluteUri
                        Constants.EnvironmentVariables.GraceToken, Some pat
                        Constants.EnvironmentVariables.GraceTokenFile, None
                        Constants.EnvironmentVariables.GraceAuthOidcAuthority, None
                        Constants.EnvironmentVariables.GraceAuthOidcAudience, None
                        Constants.EnvironmentVariables.GraceAuthOidcM2mClientId, None
                        Constants.EnvironmentVariables.GraceAuthOidcM2mClientSecret, None
                        Constants.EnvironmentVariables.GraceAuthOidcM2mScopes, None
                        Constants.EnvironmentVariables.GraceAuthOidcCliClientId, None
                        Constants.EnvironmentVariables.GraceAuthOidcCliRedirectPort, None
                        Constants.EnvironmentVariables.GraceAuthOidcCliScopes, None
                    ]
                    (fun () ->
                        let exitCode, output = run enrollmentArguments
                        Assert.That(exitCode, Is.EqualTo(0), output)
                        assertSuccessfulEnrollment pat root requests)))

    /// Verifies the existing M2M client-credential producer resolves once before root effects and completes enrollment.
    [<Test>]
    let ``Linux cache enroll dispatches M2M through the selected server once`` () =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Cache enrollment Product V1 supports Linux only.")

        withRoot (fun root _ ->
            withEnrollmentResponder (fun serverUri requests ->
                withEnvironment
                    [
                        Constants.EnvironmentVariables.GraceServerUri, Some serverUri.AbsoluteUri
                        Constants.EnvironmentVariables.GraceToken, None
                        Constants.EnvironmentVariables.GraceTokenFile, None
                        Constants.EnvironmentVariables.GraceAuthOidcAuthority, Some serverUri.AbsoluteUri
                        Constants.EnvironmentVariables.GraceAuthOidcAudience, Some "https://grace.test/api"
                        Constants.EnvironmentVariables.GraceAuthOidcM2mClientId, Some "cache-m2m-client"
                        Constants.EnvironmentVariables.GraceAuthOidcM2mClientSecret, Some "cache-m2m-secret"
                        Constants.EnvironmentVariables.GraceAuthOidcM2mScopes, Some "cache.enroll"
                        Constants.EnvironmentVariables.GraceAuthOidcCliClientId, None
                        Constants.EnvironmentVariables.GraceAuthOidcCliRedirectPort, None
                        Constants.EnvironmentVariables.GraceAuthOidcCliScopes, None
                    ]
                    (fun () ->
                        let exitCode, output = run enrollmentArguments
                        Assert.That(exitCode, Is.EqualTo(0), output)
                        assertSuccessfulEnrollment "m2m-access-token" root requests

                        let requestPaths =
                            requests.ToArray()
                            |> Array.map (fun request -> request.Path)

                        assertRequestPaths [| "/oauth/token"; "/cache/enroll" |] requestPaths)))

    /// Verifies a real Linux libsecret-backed interactive token is written, loaded, and dispatched through cache enrollment.
    [<Test>]
    let ``Linux cache enroll dispatches the real GNOME keyring interactive credential once`` () =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Cache enrollment Product V1 supports Linux only.")

        if
            Environment.GetEnvironmentVariable("GRACE_TEST_LINUX_KEYRING")
            <> "1"
        then
            Assert.Ignore("This proof requires the D-Bus and GNOME keyring session configured by validate.yml.")

        withRoot (fun root _ ->
            withEnrollmentResponder (fun serverUri requests ->
                withEnvironment
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
                            run [| "authenticate"
                                   "login"
                                   "--auth"
                                   "device" |]

                        Assert.That(loginExit, Is.EqualTo(0), loginOutput)

                        let exitCode, output = run enrollmentArguments
                        Assert.That(exitCode, Is.EqualTo(0), output)
                        assertSuccessfulEnrollment "interactive-access-token" root requests

                        let requestPaths =
                            requests.ToArray()
                            |> Array.map (fun request -> request.Path)

                        assertRequestPaths
                            [|
                                "/oauth/device/code"
                                "/oauth/token"
                                "/cache/enroll"
                            |]
                            requestPaths)))

    /// Verifies an explicitly empty organization selector fails before credential lookup, transport, or protected-state effects.
    [<Test>]
    let ``Linux cache enroll rejects an explicit empty organization id before effects`` () =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Cache enrollment Product V1 supports Linux only.")

        let pat = Grace.Types.PersonalAccessToken.formatToken "cache-test-user" (Guid.NewGuid()) (Array.zeroCreate 32)

        withRoot (fun root _ ->
            let before = snapshot root
            let arguments = Array.copy enrollmentArguments
            arguments[7] <- Guid.Empty.ToString("D")

            withEnrollmentResponder (fun serverUri requests ->
                withPatEnrollmentEnvironment serverUri pat (fun () ->
                    let exitCode, output = run arguments
                    assertRedactedError exitCode output
                    Assert.That(output, Does.Contain("--organization-id must be a non-empty GUID when supplied."))
                    Assert.That(requests.IsEmpty, Is.True, "An invalid organization selector made an enrollment request.")
                    Assert.That(snapshot root = before, Is.True, "An invalid organization selector changed protected state."))))

    /// Verifies request canonicalization matches server display-name normalization before strict response comparison.
    [<Test>]
    let ``Linux cache enroll canonicalizes padded display names before the request`` () =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Cache enrollment Product V1 supports Linux only.")

        let pat = Grace.Types.PersonalAccessToken.formatToken "cache-test-user" (Guid.NewGuid()) (Array.zeroCreate 32)

        withRoot (fun root _ ->
            withEnrollmentResponderWith (acceptedEnrollmentResponseWithDisplayName (fun displayName -> displayName.Trim())) (fun serverUri requests ->
                withPatEnrollmentEnvironment serverUri pat (fun () ->
                    let arguments =
                        Array.append
                            enrollmentArguments
                            [|
                                "--display-name"
                                "  Seattle cache  "
                            |]

                    let exitCode, output = run arguments
                    Assert.That(exitCode, Is.EqualTo(0), output)
                    assertSuccessfulEnrollment pat root requests
                    let firstRequest: ReceivedRequest = requests.ToArray() |> Array.head
                    let sent = JsonSerializer.Deserialize<CacheEnrollmentRequest>(firstRequest.Body, Constants.JsonSerializerOptions)
                    Assert.That(sent.DisplayName, Is.EqualTo("Seattle cache")))))

    /// Verifies the enrollment success value follows the existing Cache-status renderer in every supported output mode.
    [<Test>]
    let ``Linux cache enroll renders the ready status in every output mode and preserves selectors`` () =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Cache enrollment Product V1 supports Linux only.")

        let pat = Grace.Types.PersonalAccessToken.formatToken "cache-test-user" (Guid.NewGuid()) (Array.zeroCreate 32)

        for mode in
            [
                "Normal"
                "Minimal"
                "Silent"
                "Verbose"
                "Json"
            ] do
            withRoot (fun root _ ->
                withEnrollmentResponder (fun serverUri requests ->
                    withPatEnrollmentEnvironment serverUri pat (fun () ->
                        let arguments = Array.copy enrollmentArguments
                        arguments[1] <- mode
                        let exitCode, output = run arguments
                        Assert.That(exitCode, Is.EqualTo(0), $"{mode}: {output}")
                        assertSuccessfulEnrollment pat root requests

                        match mode with
                        | "Normal" -> Assert.That(output, Does.Contain("Enrollment: enrolled"))
                        | "Verbose" ->
                            Assert.That(output, Does.Contain("Enrollment: enrolled"))
                            Assert.That(output, Does.Contain("EventTime:"))
                        | "Json" -> assertStatus "enrolled" "available" 0 output
                        | "Minimal"
                        | "Silent" -> Assert.That(output, Is.Empty)
                        | _ -> Assert.Fail($"Unexpected output mode {mode}"))))

        for mode in
            [
                "Normal"
                "Minimal"
                "Silent"
                "Verbose"
                "Json"
            ] do
            withRoot (fun root _ ->
                withEnrollmentResponder (fun serverUri requests ->
                    withPatEnrollmentEnvironment serverUri pat (fun () ->
                        let arguments = Array.append enrollmentArguments [| "--select"; "Enrollment" |]
                        arguments[1] <- mode
                        let exitCode, output = run arguments
                        Assert.That(exitCode, Is.EqualTo(0), $"{mode} selector: {output}")
                        assertSuccessfulEnrollment pat root requests
                        Assert.That(output.Trim(), Is.EqualTo("\"enrolled\""), $"{mode} selector must override human output."))))

    /// Verifies bare enrollment introspection bypasses mutation validation and reports the finalized local-and-server contract.
    [<Test>]
    let ``Linux cache enroll schema and examples are inert and describe the completed contract`` () =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Cache enrollment Product V1 supports Linux only.")

        withRoot (fun root _ ->
            let before = snapshot root

            for arguments in
                [
                    [| "cache"; "enroll"; "--schema" |]
                    [| "cache"; "enroll"; "--examples" |]
                ] do
                let exitCode, output = run arguments
                Assert.That(exitCode, Is.EqualTo(0), output)
                use document = JsonDocument.Parse(output)
                let registry = document.RootElement.GetProperty("Registry")
                Assert.That(registry.GetProperty("ExecutionScope").GetString(), Is.EqualTo("composite_local_server"))
                Assert.That(registry.GetProperty("Schema").GetString(), Is.EqualTo("ExistingBehavior"))
                Assert.That(registry.GetProperty("Examples").GetString(), Is.EqualTo("ExistingBehavior"))
                Assert.That(snapshot root = before, Is.True, "Enrollment introspection changed protected state."))
