namespace Grace.CLI.Tests

open Grace.Cache
open Grace.CLI
open Grace.CLI.Command
open NUnit.Framework
open Spectre.Console
open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text.Json

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
