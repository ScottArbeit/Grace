namespace Grace.CLI.Tests

open Grace.Cache
open Grace.CLI
open Grace.CLI.Command
open Grace.CLI.CommandOutputContract
open Grace.Shared
open Json.Schema
open NUnit.Framework
open Spectre.Console
open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text.Json
open System.Text.Json.Nodes

/// Covers serialized root-command behavior for pure local Cache status.
[<TestFixture>]
[<NonParallelizable>]
module CacheCliTests =

    /// Sets AnsiConsole output for root-command tests that capture stdout.
    let private setAnsiConsoleOutput (writer: TextWriter) =
        let settings = AnsiConsoleSettings()
        settings.Out <- AnsiConsoleOutput(writer)
        AnsiConsole.Console <- AnsiConsole.Create(settings)

    /// Runs the root command while capturing the complete stdout buffer.
    let private runWithCapturedStdout (args: string array) =
        use writer = new StringWriter()
        let originalOut = Console.Out

        try
            Console.SetOut(writer)
            setAnsiConsoleOutput writer
            let exitCode = GraceCommand.main args
            exitCode, writer.ToString()
        finally
            Console.SetOut(originalOut)
            setAnsiConsoleOutput originalOut

    /// Executes an action outside a repository while restoring current directory and Cache root overrides.
    let private withTemporaryRoots action =
        let cwd = Path.Combine(Path.GetTempPath(), $"grace-cache-cli-tests-{Guid.NewGuid():N}")
        let cacheRoot = Path.Combine(Path.GetTempPath(), $"grace-cache-status-{Guid.NewGuid():N}")
        Directory.CreateDirectory(cwd) |> ignore
        Directory.CreateDirectory(cacheRoot) |> ignore

        File.SetUnixFileMode(
            cacheRoot,
            UnixFileMode.UserRead
            ||| UnixFileMode.UserWrite
            ||| UnixFileMode.UserExecute
        )

        let originalDirectory = Environment.CurrentDirectory

        try
            Environment.CurrentDirectory <- cwd
            CacheCommand.setStateRootForTests cacheRoot
            action cwd cacheRoot
        finally
            CacheCommand.resetStateRootForTests ()
            Environment.CurrentDirectory <- originalDirectory

            if Directory.Exists(cwd) then Directory.Delete(cwd, true)

            if Directory.Exists(cacheRoot) then Directory.Delete(cacheRoot, true)

    /// Parses only one complete JSON document, allowing surrounding whitespace but no prefix, suffix, or second document.
    let private parseCompleteJsonDocument (text: string) = JsonDocument.Parse(text)

    /// Captures protected-state contents and mutable metadata without inspecting any private identity values.
    let private snapshotState root =
        Array.append [| root |] (Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories))
        |> Array.sort
        |> Array.map (fun path ->
            let length = if File.Exists(path) then FileInfo(path).Length else -1L
            path, File.GetUnixFileMode(path), File.GetLastWriteTimeUtc(path), length)

    /// Proves one status invocation preserved protected-state contents and mutable metadata.
    let private assertStateUnchanged root before = Assert.That(snapshotState root = before, Is.True, "Cache status must not mutate protected state.")

    /// Fails when local status tries to establish any connection to the persisted Cache endpoint.
    let private withLoopbackRequestRecorder action =
        use listener = new TcpListener(IPAddress.Loopback, 0)
        listener.Start()
        let endpoint = $"http://127.0.0.1:{(listener.LocalEndpoint :?> IPEndPoint).Port}"

        try
            action endpoint (fun () -> Assert.That(listener.Pending(), Is.False, "Cache status must not make a loopback request."))
        finally
            listener.Stop()

    /// Checks whether a JSON object contains one named property without requiring an output value.
    let private hasProperty name (element: JsonElement) =
        element.EnumerateObject()
        |> Seq.exists (fun property -> property.Name = name)

    /// Verifies a non-ready projection contains only the approved common facts and no ready-only registration facts.
    let private assertNonReadyStatus (enrollment: string) (key: string) (status: JsonElement) =
        Assert.That(status.GetProperty("Class").GetString(), Is.EqualTo("Grace.Cache.Status"))
        Assert.That(status.GetProperty("Enrollment").GetString(), Is.EqualTo(enrollment))
        Assert.That(status.GetProperty("Key").GetString(), Is.EqualTo(key))

        [
            "CacheId"
            "Endpoint"
            "BoundaryKind"
            "RepositoryCount"
        ]
        |> List.iter (fun name -> Assert.That(hasProperty name status, Is.False, $"Non-ready status must omit {name}."))

    /// Requires a protected identity operation to succeed in root-command test setup.
    let private requireOk =
        function
        | Ok value -> value
        | Error error ->
            Assert.Fail($"Unexpected protected identity result: {error}")
            Unchecked.defaultof<_>

    /// Builds a valid accepted registration tied to the public half returned by protected attempt creation.
    let private acceptedRegistration endpoint publicKey : CacheAcceptedRegistration =
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

    /// Verifies actual root dispatch creates no repository state or invocation history and returns a complete non-ready document.
    [<Test>]
    let ``cache status is repository independent and emits one complete JSON document`` () =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Cache status Product V1 supports Linux only.")

        withTemporaryRoots (fun cwd _ ->
            let exitCode, output =
                runWithCapturedStdout [| "--output"
                                         "Json"
                                         "cache"
                                         "status" |]

            Assert.That(exitCode, Is.EqualTo(1))
            Assert.That(Directory.Exists(Path.Combine(cwd, ".grace")), Is.False)
            Assert.That(output, Does.Not.Contain("grace-cache-status-"))
            use document = parseCompleteJsonDocument output
            let status = document.RootElement.GetProperty("ReturnValue")

            assertNonReadyStatus "notEnrolled" "missing" status

            Assert.Catch<JsonException>(
                Action (fun () ->
                    parseCompleteJsonDocument $"prefix {output}"
                    |> ignore)
            )
            |> ignore

            Assert.Catch<JsonException>(
                Action (fun () ->
                    parseCompleteJsonDocument $"{output} suffix"
                    |> ignore)
            )
            |> ignore

            Assert.Catch<JsonException>(
                Action (fun () ->
                    parseCompleteJsonDocument $"{output} {output}"
                    |> ignore)
            )
            |> ignore)

    /// Verifies all supported Linux identity states return exact redacted root-command output without mutation.
    [<Test>]
    let ``Linux cache status projects staging and ready identity without mutation`` () =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Cache status Product V1 supports Linux only.")

        withLoopbackRequestRecorder (fun endpoint assertNoRequest ->
            withTemporaryRoots (fun _ root ->
                let before = snapshotState root

                let stagingExitCode, stagingOutput =
                    runWithCapturedStdout [| "--output"
                                             "Json"
                                             "cache"
                                             "status" |]

                assertStateUnchanged root before
                assertNoRequest ()

                Assert.That(stagingExitCode, Is.EqualTo(1))
                use staging = JsonDocument.Parse(stagingOutput)

                let stagingStatus = staging.RootElement.GetProperty("ReturnValue")
                assertNonReadyStatus "notEnrolled" "missing" stagingStatus

                CacheIdentity.createAttempt root
                |> requireOk
                |> ignore

                let attemptBefore = snapshotState root

                let attemptExitCode, attemptOutput =
                    runWithCapturedStdout [| "--output"
                                             "Json"
                                             "cache"
                                             "status" |]

                assertStateUnchanged root attemptBefore
                assertNoRequest ()
                use attempt = JsonDocument.Parse(attemptOutput)
                let attemptStatus = attempt.RootElement.GetProperty("ReturnValue")
                Assert.That(attemptExitCode, Is.EqualTo(1))
                assertNonReadyStatus "notEnrolled" "available" attemptStatus

                CacheIdentity.discardAttempt root
                let publicKey = CacheIdentity.createAttempt root |> requireOk

                CacheIdentity.commitReady root (acceptedRegistration endpoint publicKey)
                |> requireOk

                let readyBefore = snapshotState root

                let readyExitCode, readyOutput =
                    runWithCapturedStdout [| "--output"
                                             "Json"
                                             "cache"
                                             "status" |]

                assertStateUnchanged root readyBefore
                assertNoRequest ()
                use ready = JsonDocument.Parse(readyOutput)
                let readyStatus = ready.RootElement.GetProperty("ReturnValue")
                Assert.That(readyExitCode, Is.EqualTo(0))
                Assert.That(readyStatus.GetProperty("CacheId").GetString(), Is.EqualTo("11111111-1111-1111-1111-111111111111"))
                Assert.That(readyStatus.GetProperty("Endpoint").GetString(), Is.EqualTo(endpoint))

                Assert.That(
                    readyStatus
                        .GetProperty("BoundaryKind")
                        .GetString(),
                    Is.EqualTo("Organization")
                )

                Assert.That(
                    readyStatus
                        .GetProperty("RepositoryCount")
                        .GetInt32(),
                    Is.EqualTo(1)
                )

                let registrationPath = Path.Combine(root, "ready", "registration.json")
                let registrationBytes = File.ReadAllBytes(registrationPath)

                try
                    File.WriteAllText(registrationPath, "{ invalid registration }")
                    let invalidBefore = snapshotState root

                    let invalidExitCode, invalidOutput =
                        runWithCapturedStdout [| "--output"
                                                 "Json"
                                                 "cache"
                                                 "status" |]

                    assertStateUnchanged root invalidBefore
                    assertNoRequest ()
                    use invalid = JsonDocument.Parse(invalidOutput)
                    let invalidStatus = invalid.RootElement.GetProperty("ReturnValue")
                    Assert.That(invalidExitCode, Is.EqualTo(1))

                    assertNonReadyStatus "invalid" "invalid" invalidStatus
                finally
                    File.WriteAllBytes(registrationPath, registrationBytes)

                let identityPath = Path.Combine(root, "ready", "identity.pk8")
                let originalMode = File.GetUnixFileMode(identityPath)

                try
                    File.SetUnixFileMode(identityPath, UnixFileMode.UserWrite)
                    let inaccessibleBefore = snapshotState root

                    let inaccessibleExitCode, inaccessibleOutput =
                        runWithCapturedStdout [| "--output"
                                                 "Json"
                                                 "cache"
                                                 "status" |]

                    assertStateUnchanged root inaccessibleBefore
                    assertNoRequest ()
                    use inaccessible = JsonDocument.Parse(inaccessibleOutput)
                    let inaccessibleStatus = inaccessible.RootElement.GetProperty("ReturnValue")
                    Assert.That(inaccessibleExitCode, Is.EqualTo(1))

                    assertNonReadyStatus "invalid" "inaccessible" inaccessibleStatus
                finally
                    File.SetUnixFileMode(identityPath, originalMode)))

    /// Verifies the published Cache status schema closes every production state pair and ready-only field boundary.
    [<Test>]
    let ``cache status schema accepts exact variants and rejects impossible output`` () =
        let identity = commandIdentity [ "cache" ] "status"

        let entry =
            tryFind identity
            |> Option.defaultWith (fun () -> failwith "cache.status output contract is missing")

        let document = introspectionDocument Schema entry

        let schemaDocument =
            document.Schema
            |> Option.defaultWith (fun () -> failwith "cache.status schema is missing")

        use successSchema = JsonDocument.Parse(Utilities.serialize schemaDocument.SuccessSchema)

        let statusSchema =
            successSchema
                .RootElement
                .GetProperty("properties")
                .GetProperty("ReturnValue")

        let schema = JsonSchema.FromText(statusSchema.GetRawText())

        let status (enrollment: string) (key: string) ready =
            let value = JsonObject()
            value["Class"] <- JsonValue.Create("Grace.Cache.Status")
            value["Enrollment"] <- JsonValue.Create(enrollment)
            value["Key"] <- JsonValue.Create(key)

            if ready then
                value["CacheId"] <- JsonValue.Create("11111111-1111-1111-1111-111111111111")
                value["Endpoint"] <- JsonValue.Create("https://cache.example.test")
                value["BoundaryKind"] <- JsonValue.Create("Organization")
                value["RepositoryCount"] <- JsonValue.Create(2)

            value

        let validates (value: JsonObject) =
            use parsed = JsonDocument.Parse(value.ToJsonString())
            schema.Evaluate(parsed.RootElement).IsValid

        for value in
            [
                status "enrolled" "available" true
                status "notEnrolled" "missing" false
                status "notEnrolled" "available" false
                status "invalid" "invalid" false
                status "invalid" "inaccessible" false
            ] do
            Assert.That(validates value, Is.True, $"Expected schema to accept {value}")

        let extra = status "notEnrolled" "missing" false
        extra["Endpoint"] <- JsonValue.Create("https://cache.example.test")
        let invalidPair = status "notEnrolled" "inaccessible" false
        let incompleteReady = status "enrolled" "available" false

        for value in [ extra; invalidPair; incompleteReady ] do
            Assert.That(validates value, Is.False, $"Expected schema to reject {value}")
