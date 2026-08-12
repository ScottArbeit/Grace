namespace Grace.CLI.Tests

open FsUnit
open Grace.Cache
open Grace.CLI
open Grace.CLI.Command
open Grace.CLI.CommandOutputContract
open Grace.SDK
open Grace.Shared
open Grace.Types.CacheRegistration
open Grace.Types.Common
open NodaTime
open NUnit.Framework
open Spectre.Console
open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Groups output-contract and process-static command invocation coverage for static Cache commands.
[<NonParallelizable>]
module CacheCliTests =

    /// Configures Spectre output so command invocation assertions can inspect only the current process output.
    let private setAnsiConsoleOutput writer =
        let settings = AnsiConsoleSettings()
        settings.Out <- AnsiConsoleOutput(writer)
        AnsiConsole.Console <- AnsiConsole.Create(settings)

    /// Executes a CLI command in a fresh service directory that contains no repository graceconfig.
    let private invokeWithoutRepositoryConfig args =
        let temporaryDirectory = Path.Combine(Path.GetTempPath(), $"grace-cache-cli-tests-{Guid.NewGuid():N}")

        Directory.CreateDirectory(temporaryDirectory)
        |> ignore

        let originalDirectory = Environment.CurrentDirectory
        use output = new StringWriter()
        let originalOutput = Console.Out

        try
            Environment.CurrentDirectory <- temporaryDirectory
            Console.SetOut(output)
            setAnsiConsoleOutput output
            let exitCode = GraceCommand.main args
            exitCode, output.ToString(), Directory.Exists(Path.Combine(temporaryDirectory, ".grace"))
        finally
            Environment.CurrentDirectory <- originalDirectory
            Console.SetOut(originalOutput)
            setAnsiConsoleOutput originalOutput
            Directory.Delete(temporaryDirectory, true)

    /// Creates an isolated protected cache root for cancellation cleanup coverage on the supported Linux profile.
    let private createCacheRoot () =
        let root = Path.Combine(Path.GetTempPath(), "grace-cache-cli-tests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore

        File.SetUnixFileMode(
            root,
            UnixFileMode.UserRead
            ||| UnixFileMode.UserWrite
            ||| UnixFileMode.UserExecute
        )

        root

    /// Runs a command-path test against an isolated protected cache root instead of the fixed service-account deployment root.
    let private withCacheStateRoot action =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Linux cache enrollment command-path behavior is verified on the supported deployment platform.")

        let root = createCacheRoot ()
        CacheCommand.setStateRootForTests root

        try
            action root
        finally
            CacheCommand.resetStateRootForTests ()

            if Directory.Exists(root) then Directory.Delete(root, true)

    /// Applies process environment values for one serialized CLI invocation and restores the caller's values afterward.
    let private withEnvironmentOverrides (overrides: (string * string option) list) action =
        let originalValues =
            overrides
            |> List.map (fun (name, _) -> name, Environment.GetEnvironmentVariable(name))

        try
            overrides
            |> List.iter (fun (name, value) -> Environment.SetEnvironmentVariable(name, value |> Option.toObj))

            action ()
        finally
            originalValues
            |> List.iter (fun (name, value) -> Environment.SetEnvironmentVariable(name, value))

    /// Supplies valid owner-scoped cache enrollment input for command-path behavior tests.
    let private validEnrollmentArguments =
        [|
            "--output"
            "Json"
            "cache"
            "enroll"
            "--display-name"
            "Loopback cache"
            "--endpoint"
            "http://127.0.0.1:5001"
            "--allow-http"
            "--boundary"
            "owner"
            "--owner-id"
            "11111111-1111-1111-1111-111111111111"
            "--repository-organization-id"
            "22222222-2222-2222-2222-222222222222"
            "--repository-id"
            "33333333-3333-3333-3333-333333333333"
        |]

    /// Runs a command against a loopback endpoint and exposes the number of attempted HTTP connections.
    let private withRequestCounter action =
        use listener = new TcpListener(IPAddress.Loopback, 0)
        use cancellation = new CancellationTokenSource()
        let mutable requestCount = 0
        listener.Start()

        let rec serve () =
            task {
                if not cancellation.IsCancellationRequested then
                    try
                        let! client =
                            listener
                                .AcceptTcpClientAsync(cancellation.Token)
                                .AsTask()

                        use client = client
                        Interlocked.Increment(&requestCount) |> ignore
                        return! serve ()
                    with
                    | :? OperationCanceledException -> return ()
                    | :? ObjectDisposedException -> return ()
            }

        let serverTask = Task.Run(Func<Task>(fun () -> serve ()))
        let port = (listener.LocalEndpoint :?> IPEndPoint).Port
        let originalServerUri = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri)
        Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri, $"http://127.0.0.1:{port}")

        try
            action (fun () -> Volatile.Read(&requestCount))
        finally
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri, originalServerUri)
            cancellation.Cancel()
            listener.Stop()

            if not (serverTask.Wait(TimeSpan.FromSeconds(5.0))) then
                Assert.Fail("Timed out waiting for the loopback request counter to stop.")

    /// Runs an authenticated enrollment request against a loopback endpoint and returns the request path and bearer header it received.
    let private withEnrollmentEndpoint action =
        use probe = new TcpListener(IPAddress.Loopback, 0)
        probe.Start()
        let port = (probe.LocalEndpoint :?> IPEndPoint).Port
        probe.Stop()

        use listener = new HttpListener()
        let serverUri = Uri($"http://127.0.0.1:{port}")
        listener.Prefixes.Add(serverUri.AbsoluteUri)
        listener.Start()

        let mutable requestPath = String.Empty
        let mutable authorization = String.Empty
        let mutable responseBody = String.Empty

        let serverTask =
            Task.Run(
                Func<Task> (fun () ->
                    task {
                        let! context = listener.GetContextAsync()
                        requestPath <- context.Request.RawUrl
                        authorization <- context.Request.Headers["Authorization"]

                        let now = SystemClock.Instance.GetCurrentInstant()

                        let registration: Grace.Types.CacheRegistration.CacheRegistration =
                            {
                                Class = nameof Grace.Types.CacheRegistration.CacheRegistration
                                CacheId = Guid.Parse "11111111-1111-1111-1111-111111111111"
                                DisplayName = "Loopback cache"
                                BoundaryKind = CacheBoundaryKind.Owner
                                OwnerId = Guid.Parse "22222222-2222-2222-2222-222222222222"
                                OrganizationId = None
                                RepositoryScopes = [||]
                                PublicKey = Grace.Types.CacheRegistration.CacheIdentityPublicKey.Create("x", "y")
                                Endpoint = "http://127.0.0.1:5001"
                                AllowHttpEndpoint = true
                                Health = CacheHealthStatus.Unhealthy
                                SoftwareVersion = "test"
                                ProtocolVersion = "v1"
                                PrefetchSupported = false
                                EnrolledBy = "cache-cli-test"
                                EnrolledAt = now
                                LastRefreshedAt = now
                                RefreshAfter = now
                                ExpiresAt = now
                                RevokedAt = None
                            }

                        let envelope: GraceReturnValue<CacheRegistrationResult> =
                            {
                                ReturnValue = CacheRegistrationResult.Create(CacheRegistrationRefreshStatus.Enrolled, Some registration, "enrolled")
                                EventTime = now
                                CorrelationId = "cache-cli-test"
                                Properties = Dictionary<string, obj>()
                            }

                        let response = JsonSerializer.Serialize<GraceReturnValue<CacheRegistrationResult>>(envelope, Constants.JsonSerializerOptions)

                        responseBody <- response
                        let responseBytes = Encoding.UTF8.GetBytes(response)
                        context.Response.StatusCode <- int HttpStatusCode.OK
                        context.Response.ContentType <- "application/json"
                        context.Response.ContentLength64 <- int64 responseBytes.Length
                        context.Response.OutputStream.Write(responseBytes, 0, responseBytes.Length)
                        context.Response.Close()
                    })
            )

        try
            let result = action serverUri

            if not (serverTask.Wait(TimeSpan.FromSeconds(5.0))) then
                Assert.Fail("Timed out waiting for the loopback enrollment endpoint to receive a request.")

            result, requestPath, authorization, responseBody
        finally
            listener.Stop()

    /// Verifies that cache enrollment and redacted local status are registered with standard CLI output handling.
    [<Test>]
    let ``cache commands are registered in the output contract`` () =
        let commandIds =
            entries
            |> List.map (fun entry -> entry.Identity.CommandId)

        commandIds |> should contain "cache.enroll"
        commandIds |> should contain "cache.status"

    /// Verifies local status is a repository-independent redacted observation in both human and JSON output modes.
    [<Test>]
    let ``cache status runs without repository config and emits redacted status`` () =
        withRequestCounter (fun requestCount ->
            let jsonExitCode, jsonOutput, jsonCreatedGraceDirectory =
                invokeWithoutRepositoryConfig [| "--output"
                                                 "Json"
                                                 "cache"
                                                 "status" |]

            let humanExitCode, humanOutput, humanCreatedGraceDirectory =
                invokeWithoutRepositoryConfig [| "cache"
                                                 "status" |]

            jsonExitCode |> should equal 1
            humanExitCode |> should equal 1

            jsonOutput
            |> should not' (contain "graceconfig.json")

            humanOutput
            |> should not' (contain "graceconfig.json")

            jsonOutput
            |> should not' (contain "identity.pkcs8")

            jsonOutput |> should not' (contain "staging-")
            jsonCreatedGraceDirectory |> should equal false
            humanCreatedGraceDirectory |> should equal false

            use document = JsonDocument.Parse(jsonOutput)
            let status = document.RootElement.GetProperty("ReturnValue")

            status.GetProperty("Enrollment").GetString()
            |> should equal "notEnrolled"

            status.GetProperty("Key").GetString()
            |> should equal "missing"

            requestCount () |> should equal 0)

    /// Verifies invalid enrollment reaches the cache handler before any repository configuration lookup or local key staging.
    [<Test>]
    let ``cache enroll validates before repository config or key staging`` () =
        withRequestCounter (fun requestCount ->
            let exitCode, output, createdGraceDirectory =
                invokeWithoutRepositoryConfig [| "--output"
                                                 "Json"
                                                 "cache"
                                                 "enroll"
                                                 "--display-name"
                                                 "invalid"
                                                 "--endpoint"
                                                 "not-a-uri"
                                                 "--boundary"
                                                 "owner"
                                                 "--owner-id"
                                                 "11111111-1111-1111-1111-111111111111"
                                                 "--repository-organization-id"
                                                 "22222222-2222-2222-2222-222222222222"
                                                 "--repository-id"
                                                 "33333333-3333-3333-3333-333333333333" |]

            exitCode |> should equal -1
            output |> should not' (contain "graceconfig.json")
            output |> should not' (contain "staging-")
            createdGraceDirectory |> should equal false

            use document = JsonDocument.Parse(output)

            document
                .RootElement
                .GetProperty("Error")
                .GetString()
            |> should contain "Endpoint"

            requestCount () |> should equal 0)

    /// Verifies valid cache enrollment input rejects an absent standalone server URI before protected-state staging.
    [<Test>]
    let ``cache enroll requires a configured standalone server before local staging`` () =
        let originalServerUri = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri)

        try
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri, null)

            let args =
                [|
                    "--output"
                    "Json"
                    "cache"
                    "enroll"
                    "--display-name"
                    "valid"
                    "--endpoint"
                    "http://127.0.0.1:5001"
                    "--allow-http"
                    "--boundary"
                    "owner"
                    "--owner-id"
                    "11111111-1111-1111-1111-111111111111"
                    "--repository-organization-id"
                    "22222222-2222-2222-2222-222222222222"
                    "--repository-id"
                    "33333333-3333-3333-3333-333333333333"
                |]

            let exitCode, output, createdGraceDirectory = invokeWithoutRepositoryConfig args

            exitCode |> should equal -1
            createdGraceDirectory |> should equal false

            use document = JsonDocument.Parse(output)

            document
                .RootElement
                .GetProperty("Error")
                .GetString()
            |> should contain (Constants.EnvironmentVariables.GraceServerUri)
        finally
            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri, originalServerUri)

    /// Verifies credential failure stops the actual standalone command before staging, repository state, or endpoint transport.
    [<Test>]
    let ``cache enroll resolves credential before protected staging or endpoint transport`` () =
        let noCredentialOverrides =
            [
                Constants.EnvironmentVariables.GraceToken, None
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

        withCacheStateRoot (fun root ->
            withEnvironmentOverrides noCredentialOverrides (fun () ->
                withRequestCounter (fun requestCount ->
                    let exitCode, output, createdGraceDirectory = invokeWithoutRepositoryConfig validEnrollmentArguments
                    let outputLines = output.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)

                    Assert.That(exitCode, Is.EqualTo(-1))
                    Assert.That(outputLines, Has.Length.EqualTo 1)
                    Assert.That(createdGraceDirectory, Is.False)
                    Assert.That(requestCount (), Is.Zero)
                    Assert.That(Directory.GetFileSystemEntries(root), Is.Empty)

                    use document = JsonDocument.Parse(output)

                    Assert.That(
                        document.RootElement.TryGetProperty("Error")
                        |> fst,
                        Is.True
                    )

                    Assert.That(
                        document
                            .RootElement
                            .GetProperty("Error")
                            .GetString(),
                        Does.Contain("Authentication")
                    ))))

    /// Verifies actual standalone enrollment uses the configured Grace Server, normal bearer authentication, and one protected ready commit.
    [<Test>]
    let ``cache enroll uses configured server and normal authentication without repository configuration`` () =
        let token = Grace.Types.PersonalAccessToken.formatToken "cache-cli-test" (Guid.NewGuid()) (Array.zeroCreate 32)

        withCacheStateRoot (fun root ->
            let (exitCode, output, createdGraceDirectory), requestPath, authorization, responseBody =
                withEnrollmentEndpoint (fun serverUri ->
                    withEnvironmentOverrides
                        [
                            Constants.EnvironmentVariables.GraceServerUri, Some serverUri.AbsoluteUri
                            Constants.EnvironmentVariables.GraceToken, Some token
                            Constants.EnvironmentVariables.GraceTokenFile, None
                        ]
                        (fun () -> invokeWithoutRepositoryConfig validEnrollmentArguments))

            let outputLines = output.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            let ready = Path.Combine(root, "ready")

            Assert.That(exitCode, Is.Zero, responseBody)
            Assert.That(outputLines, Has.Length.EqualTo 1)
            Assert.That(requestPath, Is.EqualTo("/cache/enroll"))
            Assert.That(authorization, Is.EqualTo($"Bearer {token}"))
            Assert.That(createdGraceDirectory, Is.False)
            Assert.That(Directory.Exists(ready), Is.True)
            Assert.That(Directory.GetDirectories(root, "staging-*"), Is.Empty)

            Assert.That(
                (CacheIdentity.status root CancellationToken.None)
                    .Enrollment,
                Is.EqualTo("enrolled")
            )

            use document = JsonDocument.Parse(output)

            Assert.That(
                document.RootElement.TryGetProperty("ReturnValue")
                |> fst,
                Is.True
            ))

    /// Verifies cancellation immediately after private-key staging neither starts transport nor leaves a staging directory.
    [<Test>]
    let ``post staging cancellation skips transport and removes private staging`` () =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Linux file-mode persistence is verified on the supported deployment platform.")

        let root = createCacheRoot ()

        try
            let prepared =
                CacheIdentity.prepare root CancellationToken.None
                |> function
                    | Ok value -> value
                    | Error message ->
                        Assert.Fail(message)
                        Unchecked.defaultof<_>

            use cancellation = new CancellationTokenSource()
            let mutable transportConnections = 0
            cancellation.Cancel()

            Assert.Throws<OperationCanceledException>(
                Action (fun () ->
                    CacheCommand.completePreparedEnrollment prepared cancellation.Token (fun () ->
                        task {
                            transportConnections <- transportConnections + 1
                            return 0, false
                        })
                    |> fun task -> task.GetAwaiter().GetResult() |> ignore)
            )
            |> ignore

            Assert.That(transportConnections, Is.Zero)
            Assert.That(Directory.Exists(prepared.StagingDirectory), Is.False)
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)

    /// Verifies cancellation after a response but before ready commit removes staging and prevents ready publication.
    [<Test>]
    let ``post response cancellation skips ready commit and removes private staging`` () =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Linux file-mode persistence is verified on the supported deployment platform.")

        let root = createCacheRoot ()

        try
            let prepared =
                CacheIdentity.prepare root CancellationToken.None
                |> function
                    | Ok value -> value
                    | Error message ->
                        Assert.Fail(message)
                        Unchecked.defaultof<_>

            use cancellation = new CancellationTokenSource()
            let mutable transportResponses = 0
            let mutable readyCommitAttempts = 0

            Assert.Throws<OperationCanceledException>(
                Action (fun () ->
                    CacheCommand.completePreparedEnrollment prepared cancellation.Token (fun () ->
                        task {
                            transportResponses <- transportResponses + 1
                            cancellation.Cancel()
                            cancellation.Token.ThrowIfCancellationRequested()
                            readyCommitAttempts <- readyCommitAttempts + 1
                            return 0, true
                        })
                    |> fun task -> task.GetAwaiter().GetResult() |> ignore)
            )
            |> ignore

            Assert.That(transportResponses, Is.EqualTo 1)
            Assert.That(readyCommitAttempts, Is.Zero)
            Assert.That(Directory.Exists(prepared.StagingDirectory), Is.False)
            Assert.That(Directory.Exists(Path.Combine(root, "ready")), Is.False)
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)
