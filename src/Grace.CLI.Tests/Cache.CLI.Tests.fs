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
open Spectre.Console
open System
open System.Collections.Concurrent
open System.Diagnostics
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

                let redirect =
                    if statusCode >= 300 && statusCode < 400 then
                        "Location: /cache/redirected\r\n"
                    else
                        String.Empty

                let headers =
                    $"HTTP/1.1 {statusCode} Test\r\n{redirect}Content-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n"

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

    /// Redirects Spectre.Console to the writer that captures one serialized root-command invocation.
    let private setAnsiConsoleOutput (writer: TextWriter) =
        let settings = AnsiConsoleSettings()
        settings.Out <- AnsiConsoleOutput(writer)
        AnsiConsole.Console <- AnsiConsole.Create(settings)

    /// Invokes a cache command from a fresh non-repository directory and captures both standard streams.
    let private invokeOutsideRepositoryWithStreams arguments =
        let temporaryDirectory = Path.Combine(Path.GetTempPath(), $"grace-cache-cli-tests-{Guid.NewGuid():N}")
        let originalDirectory = Environment.CurrentDirectory
        let originalOut = Console.Out
        let originalError = Console.Error
        use output = new StringWriter()
        use error = new StringWriter()

        try
            Directory.CreateDirectory(temporaryDirectory)
            |> ignore

            Environment.CurrentDirectory <- temporaryDirectory
            Console.SetOut(output)
            Console.SetError(error)
            setAnsiConsoleOutput output
            let exitCode = GraceCommand.main arguments
            exitCode, output.ToString(), error.ToString(), Directory.Exists(Path.Combine(temporaryDirectory, ".grace"))
        finally
            Console.SetOut(originalOut)
            Console.SetError(originalError)
            setAnsiConsoleOutput originalOut
            Environment.CurrentDirectory <- originalDirectory

            if Directory.Exists(temporaryDirectory) then
                Directory.Delete(temporaryDirectory, true)

    /// Invokes a cache command from a fresh non-repository directory while retaining the existing stdout-only test shape.
    let private invokeOutsideRepository arguments =
        let exitCode, output, _, createdGraceDirectory = invokeOutsideRepositoryWithStreams arguments
        exitCode, output, createdGraceDirectory

    /// Lists the ready-only JSON property names that must be absent from every non-ready Cache status result.
    let private readyOnlyStatusFields =
        Set.ofList [ "CacheId"
                     "Endpoint"
                     "BoundaryKind"
                     "RepositoryCount" ]

    /// Asserts the exact presence or omission of ready-only Cache status properties in a parsed command envelope.
    let private assertReadyOnlyStatusFields (shouldBePresent: bool) (status: JsonElement) =
        let presentFields =
            status.EnumerateObject()
            |> Seq.map (fun property -> property.Name)
            |> Set.ofSeq

        for field in readyOnlyStatusFields do
            Assert.That(Set.contains field presentFields = shouldBePresent, Is.True, $"Unexpected Cache status field presence for {field}.")

    /// Locates the test-only direct claim-holder executable built by the test project dependency.
    let private tryFindClaimHolderCommand () =
        try
            let assemblyDirectory =
                DirectoryInfo(
                    Path.GetDirectoryName(
                        System
                            .Reflection
                            .Assembly
                            .GetExecutingAssembly()
                            .Location
                    )
                )

            let configuration = assemblyDirectory.Parent.Name
            let targetFramework = assemblyDirectory.Name
            let mutable current = assemblyDirectory
            let mutable sourceDirectory = Unchecked.defaultof<DirectoryInfo>
            let mutable found = false

            while not found && not (isNull current) do
                if Directory.Exists(Path.Combine(current.FullName, "Grace.Cache.ClaimHolder")) then
                    sourceDirectory <- current
                    found <- true
                else
                    current <- current.Parent

            if not found then
                None
            else
                let holderBinDirectory = Path.Combine(sourceDirectory.FullName, "Grace.Cache.ClaimHolder", "bin", configuration, targetFramework)
                let executablePath = Path.Combine(holderBinDirectory, "Grace.Cache.ClaimHolder.exe")
                let libraryPath = Path.Combine(holderBinDirectory, "Grace.Cache.ClaimHolder.dll")

                if
                    OperatingSystem.IsWindows()
                    && File.Exists(executablePath)
                then
                    Some(executablePath, None)
                elif File.Exists(libraryPath) then
                    Some("dotnet", Some libraryPath)
                else
                    None
        with
        | _ -> None

    /// Starts the direct claim-holder process, verifies its held signal, then terminates that exact descriptor owner.
    let private withDirectClaimHolder root action =
        let signalPath = Path.Combine(Path.GetTempPath(), $"grace-cache-claim-held-{Guid.NewGuid():N}")

        match tryFindClaimHolderCommand () with
        | None -> Assert.Fail("The direct Cache claim-holder executable was not built.")
        | Some (fileName, libraryPath) ->
            use holder = new Process()
            holder.StartInfo.FileName <- fileName
            holder.StartInfo.UseShellExecute <- false
            holder.StartInfo.CreateNoWindow <- true
            holder.StartInfo.RedirectStandardOutput <- true
            holder.StartInfo.RedirectStandardError <- true

            libraryPath
            |> Option.iter holder.StartInfo.ArgumentList.Add

            holder.StartInfo.ArgumentList.Add(root)
            holder.StartInfo.ArgumentList.Add(signalPath)
            let mutable started = false

            try
                started <- holder.Start()
                Assert.That(started, Is.True)
                let deadline = DateTime.UtcNow.AddSeconds(10.0)

                while not (File.Exists(signalPath))
                      && DateTime.UtcNow < deadline do
                    Thread.Sleep(50)

                Assert.That(File.Exists(signalPath), Is.True, "The direct Cache claim holder did not report an acquired claim.")
                action ()
            finally
                if started then
                    if not holder.HasExited then holder.Kill()

                    let exited = holder.WaitForExit(10000)
                    let standardOutput = holder.StandardOutput.ReadToEnd()
                    let standardError = holder.StandardError.ReadToEnd()

                    Assert.That(
                        exited,
                        Is.True,
                        $"Timed out waiting for the direct Cache claim holder to exit. stdout: {standardOutput}; stderr: {standardError}"
                    )

                    Assert.That(holder.HasExited, Is.True, "The direct Cache claim holder remained alive after termination.")

                    Assert.That(
                        holder.ExitCode,
                        Is.Not.EqualTo(0),
                        $"The terminated direct Cache claim holder unexpectedly returned success. stdout: {standardOutput}; stderr: {standardError}"
                    )

                if File.Exists(signalPath) then File.Delete(signalPath)

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
        assertReadyOnlyStatusFields false status

    /// Verifies a staged local identity has one complete non-ready JSON shape with no ready-only identity facts.
    [<Test>]
    let ``cache status omits ready-only JSON fields for staging state`` () =
        withLinuxCacheRoot (fun root ->
            Assert.That(
                Grace.Cache.CacheIdentity.createAttempt root
                |> Result.isOk,
                Is.True
            )

            let exitCode, output, createdGraceDirectory =
                invokeOutsideRepository [| "--output"
                                           "Json"
                                           "cache"
                                           "status" |]

            Assert.That(exitCode, Is.EqualTo(1))
            Assert.That(createdGraceDirectory, Is.False)
            use document = JsonDocument.Parse(output)
            let status = document.RootElement.GetProperty("ReturnValue")
            Assert.That(status.GetProperty("Enrollment").GetString(), Is.EqualTo("notEnrolled"))
            Assert.That(status.GetProperty("Key").GetString(), Is.EqualTo("available"))
            assertReadyOnlyStatusFields false status)

    /// Verifies cache leaves participate in the shared command inventory used by schema and examples introspection.
    [<Test>]
    let ``cache commands are registered in the output contract`` () =
        let commandIds =
            CommandOutputContract.entries
            |> List.map (fun entry -> entry.Identity.CommandId)

        Assert.That(commandIds, Does.Contain("cache.enroll"))
        Assert.That(commandIds, Does.Contain("cache.status"))

    /// Verifies Cache status introspection promises ready-only identity facts only in the enrolled schema variant.
    [<Test>]
    let ``cache status schema examples and help describe conditional ready facts`` () =
        let schemaExitCode, schemaOutput, schemaCreatedGraceDirectory =
            invokeOutsideRepository [| "cache"
                                       "status"
                                       "--schema" |]

        Assert.That(schemaExitCode, Is.EqualTo(0))
        Assert.That(schemaCreatedGraceDirectory, Is.False)
        use schemaDocument = JsonDocument.Parse(schemaOutput)

        let returnValueSchema =
            schemaDocument
                .RootElement
                .GetProperty("Schema")
                .GetProperty("SuccessSchema")
                .GetProperty("properties")
                .GetProperty("ReturnValue")

        let variants =
            returnValueSchema
                .GetProperty("oneOf")
                .EnumerateArray()
            |> Seq.toArray

        Assert.That(variants, Has.Length.EqualTo(2))

        let readyVariant =
            variants
            |> Array.find (fun variant ->
                let enrollment =
                    variant
                        .GetProperty("properties")
                        .GetProperty("Enrollment")
                        .GetProperty("const")
                        .GetString()

                enrollment = "enrolled")

        let readyRequired =
            readyVariant
                .GetProperty("required")
                .EnumerateArray()
            |> Seq.map (fun value -> value.GetString())
            |> Set.ofSeq

        Assert.That(
            (readyRequired = Set.ofList [ "Class"
                                          "Enrollment"
                                          "CacheId"
                                          "Endpoint"
                                          "BoundaryKind"
                                          "RepositoryCount"
                                          "Key" ]),
            Is.True
        )

        let nonReadyVariant =
            variants
            |> Array.find (fun variant ->
                let hasNot, _ =
                    variant
                        .GetProperty("properties")
                        .GetProperty("Enrollment")
                        .TryGetProperty("not")

                hasNot)

        let nonReadyRequired =
            nonReadyVariant
                .GetProperty("required")
                .EnumerateArray()
            |> Seq.map (fun value -> value.GetString())
            |> Set.ofSeq

        Assert.That(
            (nonReadyRequired = Set.ofList [ "Class"
                                             "Enrollment"
                                             "Key" ]),
            Is.True
        )

        let nonReadyProperties =
            nonReadyVariant
                .GetProperty("properties")
                .EnumerateObject()
            |> Seq.map (fun property -> property.Name)
            |> Set.ofSeq

        for field in readyOnlyStatusFields do
            Assert.That(Set.contains field nonReadyProperties, Is.False, $"The non-ready Cache status schema must omit {field}.")

        let examplesExitCode, examplesOutput, examplesCreatedGraceDirectory =
            invokeOutsideRepository [| "cache"
                                       "status"
                                       "--examples" |]

        Assert.That(examplesExitCode, Is.EqualTo(0))
        Assert.That(examplesCreatedGraceDirectory, Is.False)
        use examplesDocument = JsonDocument.Parse(examplesOutput)

        let exampleStatus =
            examplesDocument.RootElement.GetProperty("Examples").[0]
                .GetProperty("Document")
                .GetProperty("ReturnValue")

        Assert.That(
            exampleStatus
                .GetProperty("Enrollment")
                .GetString(),
            Is.EqualTo("enrolled")
        )

        assertReadyOnlyStatusFields true exampleStatus

        let helpExitCode, helpOutput, helpCreatedGraceDirectory =
            invokeOutsideRepository [| "cache"
                                       "status"
                                       "--help" |]

        Assert.That(helpExitCode, Is.EqualTo(0))
        Assert.That(helpCreatedGraceDirectory, Is.False)
        Assert.That(helpOutput, Does.Contain("--schema"))
        Assert.That(helpOutput, Does.Contain("--examples"))

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

    /// Proves an external process claim blocks one root command without a POST, then dies and releases the next enrollment.
    [<Test>]
    let ``Linux external claim blocks root enrollment then process exit releases it`` () =
        let token = PersonalAccessToken.formatToken "cache-claim" (Guid.NewGuid()) (Array.zeroCreate 32)

        withLinuxCacheRoot (fun root ->
            withLoopbackServer
                (fun request -> if request.Target = "/cache/enroll" then 200, enrolledResponse () else 404, "{}")
                (fun serverUri requests ->
                    withEnvironment (credentialEnvironment serverUri (Some token) None None None) (fun () ->
                        withDirectClaimHolder root (fun () ->
                            let exitCode, output, _ = invokeOutsideRepository enrollmentArguments
                            Assert.That(exitCode, Is.Not.EqualTo(0))
                            Assert.That(Directory.Exists(Path.Combine(root, "attempt")), Is.False)
                            Assert.That(requests.IsEmpty, Is.True)
                            use document = JsonDocument.Parse(output)

                            Assert.That(
                                document
                                    .RootElement
                                    .GetProperty("Error")
                                    .GetString(),
                                Is.Not.Empty
                            ))

                        let winnerExitCode, winnerOutput, winnerError, _ = invokeOutsideRepositoryWithStreams enrollmentArguments

                        Assert.That(winnerExitCode, Is.EqualTo(0), $"The enrollment retry failed. stdout: {winnerOutput}; stderr: {winnerError}")

                        Assert.That(Directory.Exists(Path.Combine(root, "ready")), Is.True)
                        Assert.That(Directory.Exists(Path.Combine(root, "attempt")), Is.False)

                        Assert.That(
                            requests.ToArray()
                            |> Array.filter (fun request -> request.Target = "/cache/enroll"),
                            Has.Length.EqualTo(1)
                        ))))

    /// Verifies root status exposes ready-only facts only for ready state and omits them for invalid and inaccessible state.
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

                        let readyFieldNames = String.Join(", ", readyFields)

                        Assert.That(
                            (readyFields = Set.ofList [ "Class"
                                                        "Enrollment"
                                                        "CacheId"
                                                        "Endpoint"
                                                        "BoundaryKind"
                                                        "RepositoryCount"
                                                        "Key" ]),
                            Is.True,
                            $"Unexpected ready Cache status fields: {readyFieldNames}"
                        )

                        assertReadyOnlyStatusFields true (readyDocument.RootElement.GetProperty("ReturnValue"))

                        Assert.That(
                            Guid.TryParse(
                                readyDocument
                                    .RootElement
                                    .GetProperty("ReturnValue")
                                    .GetProperty("CacheId")
                                    .GetString()
                            )
                            |> fst,
                            Is.True
                        )

                        Assert.That(
                            readyDocument
                                .RootElement
                                .GetProperty("ReturnValue")
                                .GetProperty("Endpoint")
                                .GetString(),
                            Is.Not.Empty
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
                            assertReadyOnlyStatusFields false weakStatus
                        finally
                            File.SetUnixFileMode(
                                readyDirectory,
                                UnixFileMode.UserRead
                                ||| UnixFileMode.UserWrite
                                ||| UnixFileMode.UserExecute
                            )

                        try
                            File.SetUnixFileMode(readyDirectory, UnixFileMode.UserWrite)

                            let inaccessibleExitCode, inaccessibleOutput, inaccessibleCreatedGraceDirectory =
                                invokeOutsideRepository [| "--output"
                                                           "Json"
                                                           "cache"
                                                           "status" |]

                            Assert.That(inaccessibleExitCode, Is.EqualTo(1))
                            Assert.That(inaccessibleCreatedGraceDirectory, Is.False)
                            use inaccessibleDocument = JsonDocument.Parse(inaccessibleOutput)
                            let inaccessibleStatus = inaccessibleDocument.RootElement.GetProperty("ReturnValue")

                            Assert.That(
                                inaccessibleStatus
                                    .GetProperty("Enrollment")
                                    .GetString(),
                                Is.EqualTo("invalid")
                            )

                            Assert.That(inaccessibleStatus.GetProperty("Key").GetString(), Is.EqualTo("inaccessible"))
                            assertReadyOnlyStatusFields false inaccessibleStatus
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

                        Assert.That(corruptStatus.GetProperty("Key").GetString(), Is.EqualTo("invalid"))
                        assertReadyOnlyStatusFields false corruptStatus)))

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

    /// Proves a replacement staged attempt cannot be posted or removed by the invocation that created the prior key.
    [<Test>]
    let ``Linux replaced staged attempt prevents POST and preserves replacement state`` () =
        withLinuxCacheRoot (fun root ->
            withLoopbackServer
                (fun request -> if request.Target = "/cache/enroll" then 200, enrolledResponse () else 404, "{}")
                (fun serverUri requests ->
                    withEnvironment (credentialEnvironment serverUri None None None None) (fun () ->
                        withEnrollmentDependencies
                            (fun dependencies ->
                                { dependencies with
                                    ResolveBearer = (fun () -> Task.FromResult(Ok(Some "replacement-bearer")))
                                    AfterAttemptCreated =
                                        (fun () ->
                                            let attempt = Path.Combine(root, "attempt")

                                            Directory.Delete(attempt, true)

                                            Grace.Cache.CacheIdentity.createAttempt root
                                            |> Result.iter ignore)
                                })
                            (fun () ->
                                let exitCode, output, _ = invokeOutsideRepository enrollmentArguments
                                Assert.That(exitCode, Is.Not.EqualTo(0))
                                Assert.That(Directory.Exists(Path.Combine(root, "ready")), Is.False)
                                Assert.That(Directory.Exists(Path.Combine(root, "attempt")), Is.True)
                                Assert.That(requests.IsEmpty, Is.True)
                                use document = JsonDocument.Parse(output)

                                Assert.That(
                                    document
                                        .RootElement
                                        .GetProperty("Error")
                                        .GetString(),
                                    Is.Not.Empty
                                )))))

    /// Proves stale staged state survives failed credential resolution and is recovered only after one bearer resolves.
    [<Test>]
    let ``Linux stale attempt survives failed credentials then recovers into one enrollment request`` () =
        let validToken = PersonalAccessToken.formatToken "cache-recovery" (Guid.NewGuid()) (Array.zeroCreate 32)

        withLinuxCacheRoot (fun root ->
            Assert.That(
                Grace.Cache.CacheIdentity.createAttempt root
                |> Result.isOk,
                Is.True
            )

            withLoopbackServer
                (fun request -> if request.Target = "/cache/enroll" then 200, enrolledResponse () else 404, "{}")
                (fun serverUri requests ->
                    withEnvironment (credentialEnvironment serverUri (Some "not-a-grace-pat") None None None) (fun () ->
                        let failedExit, failedOutput, _ = invokeOutsideRepository enrollmentArguments
                        Assert.That(failedExit, Is.Not.EqualTo(0))
                        Assert.That(Directory.Exists(Path.Combine(root, "attempt")), Is.True)
                        Assert.That(requests.IsEmpty, Is.True)
                        use failedDocument = JsonDocument.Parse(failedOutput)

                        Assert.That(
                            failedDocument
                                .RootElement
                                .GetProperty("Error")
                                .GetString(),
                            Is.Not.Empty
                        ))

                    withEnvironment (credentialEnvironment serverUri (Some validToken) None None None) (fun () ->
                        let recoveredExit, recoveredOutput, _ = invokeOutsideRepository enrollmentArguments
                        Assert.That(recoveredExit, Is.EqualTo(0))
                        Assert.That(Directory.Exists(Path.Combine(root, "attempt")), Is.False)
                        Assert.That(Directory.Exists(Path.Combine(root, "ready")), Is.True)
                        Assert.That(requests.ToArray(), Has.Length.EqualTo(1))
                        use recoveredDocument = JsonDocument.Parse(recoveredOutput)

                        Assert.That(
                            recoveredDocument
                                .RootElement
                                .GetProperty("ReturnValue")
                                .GetProperty("Enrollment")
                                .GetString(),
                            Is.EqualTo("enrolled")
                        ))))

    /// Proves an enrollment redirect is rejected without issuing a second request or publishing ready state.
    [<Test>]
    let ``Linux enrollment redirect sends one POST and returns one safe JSON error`` () =
        let token = PersonalAccessToken.formatToken "cache-redirect" (Guid.NewGuid()) (Array.zeroCreate 32)

        withLinuxCacheRoot (fun root ->
            withLoopbackServer
                (fun request -> if request.Target = "/cache/enroll" then 307, "{}" else 500, "{}")
                (fun serverUri requests ->
                    withEnvironment (credentialEnvironment serverUri (Some token) None None None) (fun () ->
                        let exitCode, output, _ = invokeOutsideRepository enrollmentArguments
                        Assert.That(exitCode, Is.Not.EqualTo(0))
                        Assert.That(requests.ToArray(), Has.Length.EqualTo(1))
                        Assert.That(Directory.Exists(Path.Combine(root, "attempt")), Is.False)
                        Assert.That(Directory.Exists(Path.Combine(root, "ready")), Is.False)
                        Assert.That(output, Does.Not.Contain(token))
                        use document = JsonDocument.Parse(output)

                        Assert.That(
                            document
                                .RootElement
                                .GetProperty("Error")
                                .GetString(),
                            Is.Not.Empty
                        ))))

    /// Proves raw OIDC discovery failures are reduced to a stable Cache credential error document.
    [<Test>]
    let ``Linux unavailable OIDC discovery emits a redacted Cache JSON error`` () =
        withLinuxCacheRoot (fun root ->
            withLoopbackServer
                (fun request ->
                    if request.Target = "/authenticate/oidc/config" then
                        500, "raw discovery failure at /private/path"
                    else
                        404, "{}")
                (fun serverUri requests ->
                    withEnvironment (credentialEnvironment serverUri None None None None) (fun () ->
                        let exitCode, output, _ = invokeOutsideRepository enrollmentArguments
                        Assert.That(exitCode, Is.Not.EqualTo(0))
                        Assert.That(Directory.Exists(Path.Combine(root, "attempt")), Is.False)

                        Assert.That(
                            requests.ToArray()
                            |> Array.exists (fun request -> request.Target = "/cache/enroll"),
                            Is.False
                        )

                        Assert.That(output, Does.Not.Contain("raw discovery failure"))
                        Assert.That(output, Does.Not.Contain("/private/path"))
                        use document = JsonDocument.Parse(output)

                        Assert.That(
                            document
                                .RootElement
                                .GetProperty("Error")
                                .GetString(),
                            Does.Not.Contain("http")
                        ))))

    /// Proves normal interactive login stores one Linux keyring credential that production Cache enrollment later resolves with only GRACE_SERVER_URI.
    [<Test>]
    let ``Linux secure-store interactive login enrolls through production credential resolution`` () =
        if String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")) then
            Assert.Ignore("Requires a Linux D-Bus session and libsecret keyring; the focused Docker harness provides both.")

        let mutable authority = String.Empty

        withLinuxCacheRoot (fun root ->
            withLoopbackServer
                (fun request ->
                    match request.Target with
                    | "/authenticate/oidc/config" ->
                        200,
                        $"{{\"ReturnValue\":{{\"Authority\":\"{authority}\",\"Audience\":\"test-audience\",\"CliClientId\":\"test-client\"}},\"EventTime\":\"2026-01-01T00:00:00Z\",\"CorrelationId\":\"test\",\"Properties\":{{}}}}"
                    | "/oauth/device/code" ->
                        200,
                        "{\"device_code\":\"test-device\",\"user_code\":\"test-user\",\"verification_uri\":\"https://login.example.test\",\"expires_in\":60,\"interval\":1}"
                    | "/oauth/token" ->
                        200,
                        "{\"access_token\":\"interactive-bearer\",\"refresh_token\":\"interactive-refresh\",\"expires_in\":3600,\"token_type\":\"Bearer\",\"scope\":\"openid offline_access\"}"
                    | "/cache/enroll" -> 200, enrolledResponse ()
                    | _ -> 404, "{}")
                (fun serverUri requests ->
                    authority <- serverUri

                    withEnvironment
                        (credentialEnvironment serverUri None None None None
                         @ [
                             Constants.EnvironmentVariables.GraceAuthOidcAuthority, Some serverUri
                             Constants.EnvironmentVariables.GraceAuthOidcAudience, Some "test-audience"
                             Constants.EnvironmentVariables.GraceAuthOidcCliClientId, Some "test-client"
                         ])
                        (fun () ->
                            let loginExitCode, _, _ =
                                invokeOutsideRepository [| "authenticate"
                                                           "login"
                                                           "--auth"
                                                           "device" |]

                            Assert.That(loginExitCode, Is.EqualTo(0)))

                    withEnvironment (credentialEnvironment serverUri None None None None) (fun () ->
                        let enrollExitCode, output, createdGraceDirectory = invokeOutsideRepository enrollmentArguments
                        Assert.That(enrollExitCode, Is.EqualTo(0))
                        Assert.That(createdGraceDirectory, Is.False)
                        Assert.That(Directory.Exists(Path.Combine(root, "ready")), Is.True)
                        let recorded = requests.ToArray()

                        Assert.That(
                            recorded
                            |> Array.filter (fun request -> request.Target = "/cache/enroll"),
                            Has.Length.EqualTo(1)
                        )

                        Assert.That(
                            (recorded
                             |> Array.find (fun request -> request.Target = "/cache/enroll"))
                                .Authorization,
                            Is.EqualTo(Some "Bearer interactive-bearer")
                        )

                        use document = JsonDocument.Parse(output)

                        Assert.That(
                            document
                                .RootElement
                                .GetProperty("ReturnValue")
                                .GetProperty("Enrollment")
                                .GetString(),
                            Is.EqualTo("enrolled")
                        ))))

    /// Proves an expired normal Linux keyring credential attempts one refresh, then fails before Cache enrollment mutates or posts.
    [<Test>]
    let ``Linux secure-store expired interactive credential performs no enrollment request or local mutation`` () =
        if String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")) then
            Assert.Ignore("Requires a Linux D-Bus session and libsecret keyring; the focused Docker harness provides both.")

        let mutable authority = String.Empty
        let mutable tokenRequests = 0

        withLinuxCacheRoot (fun root ->
            withLoopbackServer
                (fun request ->
                    match request.Target with
                    | "/authenticate/oidc/config" ->
                        200,
                        $"{{\"ReturnValue\":{{\"Authority\":\"{authority}\",\"Audience\":\"test-audience\",\"CliClientId\":\"test-client\"}},\"EventTime\":\"2026-01-01T00:00:00Z\",\"CorrelationId\":\"test\",\"Properties\":{{}}}}"
                    | "/oauth/device/code" ->
                        200,
                        "{\"device_code\":\"test-device\",\"user_code\":\"test-user\",\"verification_uri\":\"https://login.example.test\",\"expires_in\":60,\"interval\":1}"
                    | "/oauth/token" ->
                        tokenRequests <- tokenRequests + 1

                        if tokenRequests = 1 then
                            200,
                            "{\"access_token\":\"expired-interactive-bearer\",\"refresh_token\":\"interactive-refresh\",\"expires_in\":0,\"token_type\":\"Bearer\",\"scope\":\"openid offline_access\"}"
                        else
                            500, "{\"error\":\"invalid_grant\",\"error_description\":\"raw refresh detail\"}"
                    | "/cache/enroll" -> 500, "{}"
                    | _ -> 404, "{}")
                (fun serverUri requests ->
                    authority <- serverUri

                    withEnvironment
                        (credentialEnvironment serverUri None None None None
                         @ [
                             Constants.EnvironmentVariables.GraceAuthOidcAuthority, Some serverUri
                             Constants.EnvironmentVariables.GraceAuthOidcAudience, Some "test-audience"
                             Constants.EnvironmentVariables.GraceAuthOidcCliClientId, Some "test-client"
                         ])
                        (fun () ->
                            let loginExitCode, _, _ =
                                invokeOutsideRepository [| "authenticate"
                                                           "login"
                                                           "--auth"
                                                           "device" |]

                            Assert.That(loginExitCode, Is.EqualTo(0)))

                    withEnvironment (credentialEnvironment serverUri None None None None) (fun () ->
                        let enrollExitCode, output, _ = invokeOutsideRepository enrollmentArguments
                        Assert.That(enrollExitCode, Is.Not.EqualTo(0))
                        Assert.That(tokenRequests, Is.EqualTo(2))
                        Assert.That(Directory.Exists(Path.Combine(root, "attempt")), Is.False)
                        Assert.That(Directory.Exists(Path.Combine(root, "ready")), Is.False)

                        Assert.That(
                            requests.ToArray()
                            |> Array.exists (fun request -> request.Target = "/cache/enroll"),
                            Is.False
                        )

                        Assert.That(output, Does.Not.Contain("raw refresh detail"))
                        use document = JsonDocument.Parse(output)

                        Assert.That(
                            document
                                .RootElement
                                .GetProperty("Error")
                                .GetString(),
                            Is.Not.Empty
                        ))))
