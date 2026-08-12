namespace Grace.CLI.Tests

open Grace.CLI
open Grace.CLI.Command
open Grace.CLI.CommandOutputContract
open Grace.Shared
open Grace.Types
open Grace.Types.CacheRegistration
open Grace.Types.Common
open NodaTime
open NUnit.Framework
open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Covers serialized root-command status behavior that shares process console and current-directory state.
[<TestFixture>]
[<NonParallelizable>]
module CacheCliTests =

    /// Records only the target and bearer header needed to prove selected-server enrollment transport behavior.
    type private RecordedRequest = { Target: string; Authorization: string option }

    /// Runs one callback with the supplied environment values and restores every changed variable in a finally block.
    let private withEnvironment (values: (string * string option) list) (action: unit -> 'T) =
        let originals =
            values
            |> List.map (fun (name, _) -> name, Environment.GetEnvironmentVariable(name))

        try
            values
            |> List.iter (fun (name, value) -> Environment.SetEnvironmentVariable(name, value |> Option.toObj))

            action ()
        finally
            originals
            |> List.iter (fun (name, value) -> Environment.SetEnvironmentVariable(name, value))

    /// Creates a Linux-only protected root and restores Cache command process state after the callback returns.
    let private withLinuxCacheRoot (action: string -> 'T) =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Cache enrollment command-path proof requires Linux protected-root semantics.")

        let root = Path.Combine(Path.GetTempPath(), $"grace-cache-cli-root-{Guid.NewGuid():N}")

        try
            Directory.CreateDirectory(root) |> ignore

            File.SetUnixFileMode(
                root,
                UnixFileMode.UserRead
                ||| UnixFileMode.UserWrite
                ||| UnixFileMode.UserExecute
            )

            CacheCommand.setStateRootForTests root
            action root
        finally
            CacheCommand.resetStateRootForTests ()

            if Directory.Exists(root) then Directory.Delete(root, true)

    /// Hosts a loopback HTTP server that records each request and produces route-specific test responses.
    let private withLoopbackServer (respond: RecordedRequest -> int * string) (action: string -> ConcurrentQueue<RecordedRequest> -> 'T) =
        use listener = new TcpListener(IPAddress.Loopback, 0)
        use cancellation = new CancellationTokenSource()
        let requests = ConcurrentQueue<RecordedRequest>()
        listener.Start()

        /// Reads one compact HTTP request used by the focused local test server.
        let readRequest (client: TcpClient) =
            task {
                use client = client
                use stream = client.GetStream()
                let buffer = Array.zeroCreate<byte> 16384
                let! count = stream.ReadAsync(buffer, 0, buffer.Length)
                let request = Encoding.UTF8.GetString(buffer, 0, count)
                let lines = request.Split("\r\n", StringSplitOptions.None)

                let target =
                    lines[0]
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]

                let authorization =
                    lines
                    |> Array.tryPick (fun line ->
                        if line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase) then
                            Some(line.Substring("Authorization:".Length).Trim())
                        else
                            None)

                let recorded = { Target = target; Authorization = authorization }
                requests.Enqueue(recorded)
                let statusCode, body = respond recorded
                let bodyBytes = Encoding.UTF8.GetBytes(body)
                let headers = $"HTTP/1.1 {statusCode} Test\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n"
                let headerBytes = Encoding.ASCII.GetBytes(headers)
                do! stream.WriteAsync(headerBytes, 0, headerBytes.Length)
                do! stream.WriteAsync(bodyBytes, 0, bodyBytes.Length)
            }

        /// Serves test connections until the owning test finishes and cancels the loop.
        let rec serve () =
            task {
                if not cancellation.IsCancellationRequested then
                    try
                        let! client =
                            listener
                                .AcceptTcpClientAsync(cancellation.Token)
                                .AsTask()

                        do! readRequest client
                        return! serve ()
                    with
                    | :? OperationCanceledException -> return ()
                    | :? ObjectDisposedException -> return ()
            }

        let serverTask = Task.Run(Func<Task>(fun () -> serve ()))
        let port = (listener.LocalEndpoint :?> IPEndPoint).Port

        try
            action $"http://127.0.0.1:{port}" requests
        finally
            cancellation.Cancel()
            listener.Stop()

            if not (serverTask.Wait(TimeSpan.FromSeconds(5.0))) then
                Assert.Fail("Timed out waiting for the Cache command test HTTP server to stop.")

    /// Builds an accepted enrollment response with only server-owned facts required by the selected-server facade.
    let private enrolledResponse () =
        let now = SystemClock.Instance.GetCurrentInstant()

        let registration: CacheRegistration =
            {
                Class = nameof CacheRegistration
                CacheId = Guid.NewGuid()
                DisplayName = "test cache"
                BoundaryKind = CacheBoundaryKind.Owner
                OwnerId = Guid.Parse("11111111-1111-1111-1111-111111111111")
                OrganizationId = None
                RepositoryScopes = Array.empty
                PublicKey = CacheIdentityPublicKey.Create(String.replicate 43 "a", String.replicate 43 "a")
                Endpoint = "https://cache.example.test"
                AllowHttpEndpoint = false
                Health = CacheHealthStatus.Unhealthy
                SoftwareVersion = "Grace.Cache"
                ProtocolVersion = "v1"
                PrefetchSupported = false
                EnrolledBy = "test"
                EnrolledAt = now
                LastRefreshedAt = now
                RefreshAfter = now.Plus(Duration.FromHours(1))
                ExpiresAt = now.Plus(Duration.FromHours(2))
                RevokedAt = None
            }

        CacheRegistrationResult.Create(CacheRegistrationRefreshStatus.Enrolled, Some registration, "enrolled")
        |> fun result -> GraceReturnValue.Create result "server-correlation"
        |> fun result -> JsonSerializer.Serialize(result, Constants.JsonSerializerOptions)

    /// Returns explicit, valid owner-boundary enrollment arguments for actual root-command tests.
    let private enrollmentArguments =
        [|
            "--output"
            "Json"
            "cache"
            "enroll"
            "--display-name"
            "test cache"
            "--endpoint"
            "https://cache.example.test"
            "--boundary"
            "owner"
            "--owner-id"
            "11111111-1111-1111-1111-111111111111"
            "--repository-organization-id"
            "22222222-2222-2222-2222-222222222222"
            "--repository-id"
            "33333333-3333-3333-3333-333333333333"
        |]

    /// Supplies a clean credential environment so actual command tests cannot inherit another producer.
    let private credentialEnvironment serverUri token authority clientId clientSecret =
        [
            Constants.EnvironmentVariables.GraceServerUri, Some serverUri
            Constants.EnvironmentVariables.GraceToken, token
            Constants.EnvironmentVariables.GraceTokenFile, None
            Constants.EnvironmentVariables.GraceAuthOidcAuthority, authority
            Constants.EnvironmentVariables.GraceAuthOidcAudience, authority |> Option.map (fun _ -> "test-audience")
            Constants.EnvironmentVariables.GraceAuthOidcM2mClientId, clientId
            Constants.EnvironmentVariables.GraceAuthOidcM2mClientSecret, clientSecret
            Constants.EnvironmentVariables.GraceAuthOidcM2mScopes, None
            Constants.EnvironmentVariables.GraceAuthOidcCliClientId, None
            Constants.EnvironmentVariables.GraceAuthOidcCliRedirectPort, None
            Constants.EnvironmentVariables.GraceAuthOidcCliScopes, None
        ]

    /// Applies a bounded enrollment dependency override and restores the complete prior record in a finally block.
    let private withEnrollmentDependencies (transform: CacheCommand.EnrollmentDependencies -> CacheCommand.EnrollmentDependencies) (action: unit -> 'T) =
        let previous = CacheCommand.getEnrollmentDependenciesForTests ()

        CacheCommand.setEnrollmentDependenciesForTests (transform previous)
        |> ignore

        try
            action ()
        finally
            CacheCommand.setEnrollmentDependenciesForTests (previous)
            |> ignore

    /// Invokes a cache command from a fresh non-repository directory and restores console and directory process state.
    let private invokeOutsideRepository arguments =
        let temporaryDirectory = Path.Combine(Path.GetTempPath(), $"grace-cache-cli-tests-{Guid.NewGuid():N}")
        let originalDirectory = Environment.CurrentDirectory
        let originalOut = Console.Out
        use output = new StringWriter()

        try
            Directory.CreateDirectory(temporaryDirectory)
            |> ignore

            Environment.CurrentDirectory <- temporaryDirectory
            Console.SetOut(output)
            let exitCode = GraceCommand.main arguments
            exitCode, output.ToString(), Directory.Exists(Path.Combine(temporaryDirectory, ".grace"))
        finally
            Console.SetOut(originalOut)
            Environment.CurrentDirectory <- originalDirectory

            if Directory.Exists(temporaryDirectory) then
                Directory.Delete(temporaryDirectory, true)

    /// Verifies the root command dispatches pure cache status without repository discovery or invocation history.
    [<Test>]
    let ``cache status is repository independent and emits one JSON envelope`` () =
        let exitCode, output, createdGraceDirectory =
            invokeOutsideRepository [| "--output"
                                       "Json"
                                       "cache"
                                       "status" |]

        Assert.That(exitCode, Is.EqualTo(1))
        Assert.That(createdGraceDirectory, Is.False)
        Assert.That(output, Does.Not.Contain(".grace"))
        Assert.That(output, Does.Not.Contain("/var/lib"))
        use document = JsonDocument.Parse(output)
        let status = document.RootElement.GetProperty("ReturnValue")
        Assert.That(status.GetProperty("Enrollment").GetString(), Is.Not.EqualTo("enrolled"))
        Assert.That(status.GetProperty("Key").GetString(), Is.Not.Null)

    /// Verifies cache leaves participate in the shared command inventory used by schema and examples introspection.
    [<Test>]
    let ``cache commands are registered in the output contract`` () =
        let commandIds =
            CommandOutputContract.entries
            |> List.map (fun entry -> entry.Identity.CommandId)

        Assert.That(commandIds, Does.Contain("cache.enroll"))
        Assert.That(commandIds, Does.Contain("cache.status"))

    /// Verifies PAT enrollment uses one root-command POST with the exact resolved bearer and no repository state.
    [<Test>]
    let ``Linux PAT enrollment reuses one bearer for one selected-server request`` () =
        let token = PersonalAccessToken.formatToken "cache-cli" (Guid.NewGuid()) (Array.zeroCreate 32)

        withLinuxCacheRoot (fun root ->
            withLoopbackServer
                (fun request -> if request.Target = "/cache/enroll" then 200, enrolledResponse () else 404, "{}")
                (fun serverUri requests ->
                    withEnvironment (credentialEnvironment serverUri (Some token) None None None) (fun () ->
                        let exitCode, output, createdGraceDirectory = invokeOutsideRepository enrollmentArguments
                        Assert.That(exitCode, Is.EqualTo(0))
                        Assert.That(createdGraceDirectory, Is.False)
                        Assert.That(Directory.Exists(Path.Combine(root, "ready")), Is.True)
                        Assert.That(Directory.Exists(Path.Combine(root, "attempt")), Is.False)
                        use document = JsonDocument.Parse(output)

                        Assert.That(
                            document
                                .RootElement
                                .GetProperty("ReturnValue")
                                .GetProperty("Enrollment")
                                .GetString(),
                            Is.EqualTo("enrolled")
                        )

                        let recorded = requests.ToArray()
                        Assert.That(recorded, Has.Length.EqualTo(1))
                        Assert.That(recorded[0].Target, Is.EqualTo("/cache/enroll"))
                        Assert.That(recorded[0].Authorization, Is.EqualTo(Some $"Bearer {token}")))))

    /// Verifies root status observes a ready identity, then reports weak and corrupt state without leaking private facts.
    [<Test>]
    let ``Linux cache status is redacted for ready weak and corrupt identities`` () =
        let token = PersonalAccessToken.formatToken "cache-status" (Guid.NewGuid()) (Array.zeroCreate 32)

        withLinuxCacheRoot (fun root ->
            withLoopbackServer
                (fun request -> if request.Target = "/cache/enroll" then 200, enrolledResponse () else 404, "{}")
                (fun serverUri _ ->
                    withEnvironment (credentialEnvironment serverUri (Some token) None None None) (fun () ->
                        let enrollmentExitCode, _, _ = invokeOutsideRepository enrollmentArguments
                        Assert.That(enrollmentExitCode, Is.EqualTo(0))

                        let readyExitCode, readyOutput, readyCreatedGraceDirectory =
                            invokeOutsideRepository [| "--output"
                                                       "Json"
                                                       "cache"
                                                       "status" |]

                        Assert.That(readyExitCode, Is.EqualTo(0))
                        Assert.That(readyCreatedGraceDirectory, Is.False)
                        use readyDocument = JsonDocument.Parse(readyOutput)

                        let readyFields =
                            readyDocument
                                .RootElement
                                .GetProperty("ReturnValue")
                                .EnumerateObject()
                            |> Seq.map (fun property -> property.Name)
                            |> Set.ofSeq

                        Assert.That(
                            Set.isSubset
                                readyFields
                                (Set.ofList [ "Class"
                                              "Enrollment"
                                              "CacheId"
                                              "Endpoint"
                                              "BoundaryKind"
                                              "RepositoryCount"
                                              "Key" ]),
                            Is.True
                        )

                        let readyDirectory = Path.Combine(root, "ready")

                        try
                            File.SetUnixFileMode(
                                readyDirectory,
                                UnixFileMode.UserRead
                                ||| UnixFileMode.UserWrite
                                ||| UnixFileMode.UserExecute
                                ||| UnixFileMode.GroupRead
                            )

                            let weakExitCode, weakOutput, weakCreatedGraceDirectory =
                                invokeOutsideRepository [| "--output"
                                                           "Json"
                                                           "cache"
                                                           "status" |]

                            Assert.That(weakExitCode, Is.EqualTo(1))
                            Assert.That(weakCreatedGraceDirectory, Is.False)
                            Assert.That(weakOutput, Does.Not.Contain(root))
                            use weakDocument = JsonDocument.Parse(weakOutput)
                            let weakStatus = weakDocument.RootElement.GetProperty("ReturnValue")
                            Assert.That(weakStatus.GetProperty("Enrollment").GetString(), Is.EqualTo("invalid"))
                            Assert.That(weakStatus.GetProperty("Key").GetString(), Is.EqualTo("invalid"))
                        finally
                            File.SetUnixFileMode(
                                readyDirectory,
                                UnixFileMode.UserRead
                                ||| UnixFileMode.UserWrite
                                ||| UnixFileMode.UserExecute
                            )

                        File.WriteAllText(Path.Combine(readyDirectory, "registration.json"), "{}")

                        let corruptExitCode, corruptOutput, corruptCreatedGraceDirectory =
                            invokeOutsideRepository [| "--output"
                                                       "Json"
                                                       "cache"
                                                       "status" |]

                        Assert.That(corruptExitCode, Is.EqualTo(1))
                        Assert.That(corruptCreatedGraceDirectory, Is.False)
                        Assert.That(corruptOutput, Does.Not.Contain(root))
                        Assert.That(corruptOutput, Does.Not.Contain("PublicKey"))
                        use corruptDocument = JsonDocument.Parse(corruptOutput)
                        let corruptStatus = corruptDocument.RootElement.GetProperty("ReturnValue")

                        Assert.That(
                            corruptStatus
                                .GetProperty("Enrollment")
                                .GetString(),
                            Is.EqualTo("invalid")
                        )

                        Assert.That(corruptStatus.GetProperty("Key").GetString(), Is.EqualTo("invalid")))))

    /// Verifies an invalid explicit PAT fails before credential success can stage local identity or contact enrollment.
    [<Test>]
    let ``Linux invalid PAT performs no enrollment request or local mutation`` () =
        withLinuxCacheRoot (fun root ->
            withLoopbackServer
                (fun _ -> 404, "{}")
                (fun serverUri requests ->
                    withEnvironment (credentialEnvironment serverUri (Some "not-a-grace-pat") None None None) (fun () ->
                        let exitCode, output, createdGraceDirectory = invokeOutsideRepository enrollmentArguments
                        Assert.That(exitCode, Is.Not.EqualTo(0))
                        Assert.That(createdGraceDirectory, Is.False)
                        Assert.That(Directory.Exists(Path.Combine(root, "ready")), Is.False)
                        Assert.That(Directory.Exists(Path.Combine(root, "attempt")), Is.False)
                        Assert.That(requests.IsEmpty, Is.True)
                        use document = JsonDocument.Parse(output)

                        Assert.That(
                            document
                                .RootElement
                                .GetProperty("Error")
                                .GetString(),
                            Is.Not.Empty
                        ))))

    /// Verifies definitive server rejection removes only the current attempt and does not expose the PAT in JSON output.
    [<Test>]
    let ``Linux PAT rejection leaves no ready state or attempt`` () =
        let token = PersonalAccessToken.formatToken "cache-cli" (Guid.NewGuid()) (Array.zeroCreate 32)

        withLinuxCacheRoot (fun root ->
            withLoopbackServer
                (fun request ->
                    if request.Target = "/cache/enroll" then
                        403, "{\"Error\":\"rejected\"}"
                    else
                        404, "{}")
                (fun serverUri requests ->
                    withEnvironment (credentialEnvironment serverUri (Some token) None None None) (fun () ->
                        let exitCode, output, createdGraceDirectory = invokeOutsideRepository enrollmentArguments
                        Assert.That(exitCode, Is.Not.EqualTo(0))
                        Assert.That(createdGraceDirectory, Is.False)
                        Assert.That(Directory.Exists(Path.Combine(root, "ready")), Is.False)
                        Assert.That(Directory.Exists(Path.Combine(root, "attempt")), Is.False)
                        Assert.That(output, Does.Not.Contain(token))

                        Assert.That(
                            requests.ToArray()
                            |> Array.filter (fun request -> request.Target = "/cache/enroll"),
                            Has.Length.EqualTo(1)
                        )

                        use document = JsonDocument.Parse(output)

                        Assert.That(
                            document
                                .RootElement
                                .GetProperty("Error")
                                .GetString(),
                            Is.Not.Empty
                        ))))

    /// Verifies M2M acquires one token before one enrollment request and forwards that bearer unchanged.
    [<Test>]
    let ``Linux M2M enrollment acquires one token then sends one enrollment request`` () =
        let bearer = "m2m-test-bearer"

        withLinuxCacheRoot (fun root ->
            withLoopbackServer
                (fun request ->
                    match request.Target with
                    | "/oauth/token" -> 200, "{\"access_token\":\"m2m-test-bearer\",\"expires_in\":3600,\"token_type\":\"Bearer\"}"
                    | "/cache/enroll" -> 200, enrolledResponse ()
                    | _ -> 404, "{}")
                (fun serverUri requests ->
                    withEnvironment (credentialEnvironment serverUri None (Some serverUri) (Some "client") (Some "secret")) (fun () ->
                        let exitCode, _, createdGraceDirectory = invokeOutsideRepository enrollmentArguments
                        Assert.That(exitCode, Is.EqualTo(0))
                        Assert.That(createdGraceDirectory, Is.False)
                        Assert.That(Directory.Exists(Path.Combine(root, "ready")), Is.True)
                        let recorded = requests.ToArray()

                        Assert.That(
                            recorded
                            |> Array.filter (fun request -> request.Target = "/oauth/token"),
                            Has.Length.EqualTo(1)
                        )

                        Assert.That(
                            recorded
                            |> Array.filter (fun request -> request.Target = "/cache/enroll"),
                            Has.Length.EqualTo(1)
                        )

                        let enrollmentRequest =
                            recorded
                            |> Array.find (fun request -> request.Target = "/cache/enroll")

                        Assert.That(enrollmentRequest.Authorization, Is.EqualTo(Some $"Bearer {bearer}")))))

    /// Verifies a failed M2M acquisition performs neither enrollment nor protected-root mutation.
    [<Test>]
    let ``Linux M2M failure performs no enrollment request or local mutation`` () =
        withLinuxCacheRoot (fun root ->
            withLoopbackServer
                (fun request ->
                    if request.Target = "/oauth/token" then
                        (500, "{\"error\":\"temporarily_unavailable\"}")
                    else
                        (404, "{}"))
                (fun serverUri requests ->
                    withEnvironment (credentialEnvironment serverUri None (Some serverUri) (Some "client") (Some "secret")) (fun () ->
                        let exitCode, output, createdGraceDirectory = invokeOutsideRepository enrollmentArguments
                        Assert.That(exitCode, Is.Not.EqualTo(0))
                        Assert.That(createdGraceDirectory, Is.False)
                        Assert.That(Directory.Exists(Path.Combine(root, "ready")), Is.False)
                        Assert.That(Directory.Exists(Path.Combine(root, "attempt")), Is.False)

                        Assert.That(
                            requests.ToArray()
                            |> Array.filter (fun request -> request.Target = "/oauth/token"),
                            Has.Length.EqualTo(1)
                        )

                        Assert.That(
                            requests.ToArray()
                            |> Array.exists (fun request -> request.Target = "/cache/enroll"),
                            Is.False
                        )

                        use document = JsonDocument.Parse(output)

                        Assert.That(
                            document
                                .RootElement
                                .GetProperty("Error")
                                .GetString(),
                            Is.Not.Empty
                        ))))

    /// Verifies missing interactive credentials may use the existing OIDC configuration lookup but cannot stage or enroll.
    [<Test>]
    let ``Linux missing interactive credential performs no enrollment or local mutation`` () =
        withLinuxCacheRoot (fun root ->
            withLoopbackServer
                (fun request ->
                    if request.Target = "/authenticate/oidc/config" then
                        (200,
                         "{\"ReturnValue\":{\"Authority\":\"https://tenant.example.test/\",\"Audience\":\"test-audience\",\"CliClientId\":\"test-client\"},\"EventTime\":\"2026-01-01T00:00:00Z\",\"CorrelationId\":\"test\",\"Properties\":{}}")
                    else
                        (404, "{}"))
                (fun serverUri requests ->
                    withEnvironment (credentialEnvironment serverUri None None None None) (fun () ->
                        let exitCode, output, createdGraceDirectory = invokeOutsideRepository enrollmentArguments
                        Assert.That(exitCode, Is.Not.EqualTo(0))
                        Assert.That(createdGraceDirectory, Is.False)
                        Assert.That(Directory.Exists(Path.Combine(root, "ready")), Is.False)
                        Assert.That(Directory.Exists(Path.Combine(root, "attempt")), Is.False)

                        Assert.That(
                            requests.ToArray()
                            |> Array.exists (fun request -> request.Target = "/cache/enroll"),
                            Is.False
                        )

                        use document = JsonDocument.Parse(output)

                        Assert.That(
                            document
                                .RootElement
                                .GetProperty("Error")
                                .GetString(),
                            Is.Not.Empty
                        ))))

    /// Proves actual root dispatch consumes one deterministic result from the normal credential-resolution boundary for stored-login composition.
    [<Test>]
    let ``Linux root enrollment consumes one stored interactive credential result`` () =
        let bearer = "stored-interactive-bearer"
        let mutable credentialCalls = 0

        withLinuxCacheRoot (fun root ->
            withLoopbackServer
                (fun request -> if request.Target = "/cache/enroll" then 200, enrolledResponse () else 404, "{}")
                (fun serverUri requests ->
                    withEnvironment (credentialEnvironment serverUri None None None None) (fun () ->
                        withEnrollmentDependencies
                            (fun dependencies ->
                                { dependencies with
                                    ResolveBearer =
                                        fun () ->
                                            credentialCalls <- credentialCalls + 1
                                            Task.FromResult(Ok(Some bearer))
                                })
                            (fun () ->
                                let exitCode, output, createdGraceDirectory = invokeOutsideRepository enrollmentArguments
                                Assert.That(exitCode, Is.EqualTo(0))
                                Assert.That(credentialCalls, Is.EqualTo(1))
                                Assert.That(createdGraceDirectory, Is.False)
                                Assert.That(Directory.Exists(Path.Combine(root, "ready")), Is.True)
                                let recorded = requests.ToArray()
                                Assert.That(recorded, Has.Length.EqualTo(1))
                                Assert.That(recorded[0].Authorization, Is.EqualTo(Some $"Bearer {bearer}"))
                                use document = JsonDocument.Parse(output)

                                Assert.That(
                                    document
                                        .RootElement
                                        .GetProperty("ReturnValue")
                                        .GetProperty("Enrollment")
                                        .GetString(),
                                    Is.EqualTo("enrolled")
                                )))))

    /// Proves an expired stored-login result leaves no local attempt or enrollment request when consumed through root dispatch.
    [<Test>]
    let ``Linux expired interactive result performs no enrollment or local mutation`` () =
        let mutable credentialCalls = 0

        withLinuxCacheRoot (fun root ->
            withLoopbackServer
                (fun _ -> 404, "{}")
                (fun serverUri requests ->
                    withEnvironment (credentialEnvironment serverUri None None None None) (fun () ->
                        withEnrollmentDependencies
                            (fun dependencies ->
                                { dependencies with
                                    ResolveBearer =
                                        fun () ->
                                            credentialCalls <- credentialCalls + 1
                                            Task.FromResult(Error "Stored interactive credential is expired.")
                                })
                            (fun () ->
                                let exitCode, output, createdGraceDirectory = invokeOutsideRepository enrollmentArguments
                                Assert.That(exitCode, Is.Not.EqualTo(0))
                                Assert.That(credentialCalls, Is.EqualTo(1))
                                Assert.That(createdGraceDirectory, Is.False)
                                Assert.That(Directory.Exists(Path.Combine(root, "ready")), Is.False)
                                Assert.That(Directory.Exists(Path.Combine(root, "attempt")), Is.False)
                                Assert.That(requests.IsEmpty, Is.True)
                                use document = JsonDocument.Parse(output)

                                Assert.That(
                                    document
                                        .RootElement
                                        .GetProperty("Error")
                                        .GetString(),
                                    Does.Contain("expired")
                                )))))

    /// Proves cancellation after staging removes the attempt without performing an enrollment request or publishing ready state.
    [<Test>]
    let ``Linux cancellation after attempt creation cleans only the current attempt`` () =
        withLinuxCacheRoot (fun root ->
            withLoopbackServer
                (fun _ -> 404, "{}")
                (fun serverUri requests ->
                    withEnvironment (credentialEnvironment serverUri None None None None) (fun () ->
                        withEnrollmentDependencies
                            (fun dependencies ->
                                { dependencies with
                                    ResolveBearer = fun () -> Task.FromResult(Ok(Some "cancellation-bearer"))
                                    AfterAttemptCreated = fun () -> raise (OperationCanceledException("test cancellation"))
                                })
                            (fun () ->
                                let exitCode, output, createdGraceDirectory = invokeOutsideRepository enrollmentArguments
                                Assert.That(exitCode, Is.Not.EqualTo(0))
                                Assert.That(createdGraceDirectory, Is.False)
                                Assert.That(Directory.Exists(Path.Combine(root, "ready")), Is.False)
                                Assert.That(Directory.Exists(Path.Combine(root, "attempt")), Is.False)
                                Assert.That(requests.IsEmpty, Is.True)
                                use document = JsonDocument.Parse(output)

                                Assert.That(
                                    document
                                        .RootElement
                                        .GetProperty("Error")
                                        .GetString(),
                                    Does.Contain("cancelled")
                                )))))

    /// Proves cancellation before protected staging emits a single redacted result and leaves the root unchanged.
    [<Test>]
    let ``Linux cancellation before attempt creation performs no enrollment or local mutation`` () =
        withLinuxCacheRoot (fun root ->
            withLoopbackServer
                (fun _ -> 404, "{}")
                (fun serverUri requests ->
                    withEnvironment (credentialEnvironment serverUri None None None None) (fun () ->
                        withEnrollmentDependencies
                            (fun dependencies -> { dependencies with ResolveBearer = (fun () -> raise (OperationCanceledException("test cancellation"))) })
                            (fun () ->
                                let exitCode, output, createdGraceDirectory = invokeOutsideRepository enrollmentArguments
                                Assert.That(exitCode, Is.Not.EqualTo(0))
                                Assert.That(createdGraceDirectory, Is.False)
                                Assert.That(Directory.Exists(Path.Combine(root, "ready")), Is.False)
                                Assert.That(Directory.Exists(Path.Combine(root, "attempt")), Is.False)
                                Assert.That(requests.IsEmpty, Is.True)
                                use document = JsonDocument.Parse(output)

                                Assert.That(
                                    document
                                        .RootElement
                                        .GetProperty("Error")
                                        .GetString(),
                                    Does.Contain("cancelled")
                                )))))

    /// Proves a forced protected commit failure preserves the enrollment request count and cleans the current attempt.
    [<Test>]
    let ``Linux forced local commit failure cleans attempt after one enrollment request`` () =
        withLinuxCacheRoot (fun root ->
            withLoopbackServer
                (fun request -> if request.Target = "/cache/enroll" then 200, enrolledResponse () else 404, "{}")
                (fun serverUri requests ->
                    withEnvironment (credentialEnvironment serverUri None None None None) (fun () ->
                        withEnrollmentDependencies
                            (fun dependencies ->
                                { dependencies with
                                    ResolveBearer = (fun () -> Task.FromResult(Ok(Some "commit-failure-bearer")))
                                    CommitReady = (fun _ -> false)
                                })
                            (fun () ->
                                let exitCode, output, createdGraceDirectory = invokeOutsideRepository enrollmentArguments
                                Assert.That(exitCode, Is.Not.EqualTo(0))
                                Assert.That(createdGraceDirectory, Is.False)
                                Assert.That(Directory.Exists(Path.Combine(root, "ready")), Is.False)
                                Assert.That(Directory.Exists(Path.Combine(root, "attempt")), Is.False)

                                Assert.That(
                                    requests.ToArray()
                                    |> Array.filter (fun request -> request.Target = "/cache/enroll"),
                                    Has.Length.EqualTo(1)
                                )

                                use document = JsonDocument.Parse(output)

                                Assert.That(
                                    document
                                        .RootElement
                                        .GetProperty("Error")
                                        .GetString(),
                                    Does.Contain("could not be committed")
                                )))))

    /// Proves a malformed nominal-success response is an unknown outcome that cleans the current attempt without retrying.
    [<Test>]
    let ``Linux malformed enrollment response leaves no ready state or attempt`` () =
        let token = PersonalAccessToken.formatToken "cache-malformed" (Guid.NewGuid()) (Array.zeroCreate 32)

        withLinuxCacheRoot (fun root ->
            withLoopbackServer
                (fun request -> if request.Target = "/cache/enroll" then 200, "{}" else 404, "{}")
                (fun serverUri requests ->
                    withEnvironment (credentialEnvironment serverUri (Some token) None None None) (fun () ->
                        let exitCode, output, createdGraceDirectory = invokeOutsideRepository enrollmentArguments
                        Assert.That(exitCode, Is.Not.EqualTo(0))
                        Assert.That(createdGraceDirectory, Is.False)
                        Assert.That(Directory.Exists(Path.Combine(root, "ready")), Is.False)
                        Assert.That(Directory.Exists(Path.Combine(root, "attempt")), Is.False)

                        Assert.That(
                            requests.ToArray()
                            |> Array.filter (fun request -> request.Target = "/cache/enroll"),
                            Has.Length.EqualTo(1)
                        )

                        use document = JsonDocument.Parse(output)

                        Assert.That(
                            document
                                .RootElement
                                .GetProperty("Error")
                                .GetString(),
                            Is.Not.Empty
                        ))))
