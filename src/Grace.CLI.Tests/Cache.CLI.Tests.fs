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

    /// Locates repository-root documentation from this test project without relying on the process current directory.
    let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

    /// Identifies the canonical cache completion audit that records the current status-leaf split.
    let private cacheImplementationAuditPath = Path.Combine(repoRoot, "docs", "Grace Cache implementation audit.md")

    /// Extracts one audit section so its status language is checked independently of other planned leaves.
    let private auditSection (heading: string) (markdown: string) =
        let sectionStart = markdown.IndexOf(heading, StringComparison.Ordinal)
        Assert.That(sectionStart, Is.GreaterThanOrEqualTo(0), $"Expected audit heading {heading}.")

        let headingPrefix = heading[.. (heading.IndexOf(' ') - 1)]
        let nextSectionStart = markdown.IndexOf($"\n{headingPrefix} ", sectionStart + heading.Length, StringComparison.Ordinal)

        if nextSectionStart < 0 then
            markdown[sectionStart..]
        else
            markdown[sectionStart .. (nextSectionStart - 1)]

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

    /// Captures history files and metadata so a Cache status root command cannot create or change either history path.
    let private snapshotHistoryFile path =
        if File.Exists(path) then
            Some(File.ReadAllBytes(path), File.GetUnixFileMode(path), File.GetLastWriteTimeUtc(path))
        else
            None

    /// Captures the actual global invocation-history file and lock paths used by the root command.
    let private snapshotInvocationHistory () =
        let historyPath = HistoryStorage.getHistoryFilePath ()
        let historyLockPath = HistoryStorage.getHistoryLockPath ()
        historyPath, snapshotHistoryFile historyPath, historyLockPath, snapshotHistoryFile historyLockPath

    /// Runs Cache status and proves the actual global history file and lock remain absent or byte-for-byte unchanged.
    let private runStatusWithoutHistoryMutation (args: string array) =
        let historyPath, historyBefore, historyLockPath, historyLockBefore = snapshotInvocationHistory ()
        let result = runWithCapturedStdout args

        Assert.That(snapshotHistoryFile historyPath, Is.EqualTo(historyBefore), "Cache status must not create or mutate the invocation history file.")

        Assert.That(snapshotHistoryFile historyLockPath, Is.EqualTo(historyLockBefore), "Cache status must not create or mutate the invocation history lock.")

        result

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

    /// Verifies human root-command output exposes only approved redacted facts for one status classification.
    let private assertHumanStatus (enrollment: string) (key: string) (ready: bool) (root: string) (output: string) =
        [
            $"Class: Grace.Cache.Status"
            $"Enrollment: {enrollment}"
            $"Key: {key}"
        ]
        |> List.iter (fun expected -> Assert.That(output, Does.Contain(expected)))

        let readyFacts =
            [
                "CacheId: 11111111-1111-1111-1111-111111111111"
                "Endpoint: http://127.0.0.1:"
                "BoundaryKind: Organization"
                "RepositoryCount: 1"
            ]

        if ready then
            readyFacts
            |> List.iter (fun expected -> Assert.That(output, Does.Contain(expected)))
        else
            readyFacts
            |> List.iter (fun expected -> Assert.That(output, Does.Not.Contain(expected)))

        [
            root
            "identity.pk8"
            "registration.json"
            "PrivateKey"
            "PublicKey"
            "Fingerprint"
            "KeyReference"
            "Token"
            "Path:"
            "Reference"
            "Error:"
            "Exception"
            "IOException"
            "UnauthorizedAccessException"
        ]
        |> List.iter (fun forbidden -> Assert.That(output, Does.Not.Contain(forbidden), $"Human status must not expose {forbidden}."))

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
                runStatusWithoutHistoryMutation [| "--output"
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

                let missingHumanExitCode, missingHumanOutput =
                    runStatusWithoutHistoryMutation [| "cache"
                                                       "status" |]

                assertStateUnchanged root before
                assertNoRequest ()
                Assert.That(missingHumanExitCode, Is.EqualTo(1))
                assertHumanStatus "notEnrolled" "missing" false root missingHumanOutput

                let stagingExitCode, stagingOutput =
                    runStatusWithoutHistoryMutation [| "--output"
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
                    runStatusWithoutHistoryMutation [| "--output"
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
                    runStatusWithoutHistoryMutation [| "--output"
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

                let readyHumanBefore = snapshotState root

                let readyHumanExitCode, readyHumanOutput =
                    runStatusWithoutHistoryMutation [| "cache"
                                                       "status" |]

                assertStateUnchanged root readyHumanBefore
                assertNoRequest ()
                Assert.That(readyHumanExitCode, Is.EqualTo(0))
                assertHumanStatus "enrolled" "available" true root readyHumanOutput

                let registrationPath = Path.Combine(root, "ready", "registration.json")
                let registrationBytes = File.ReadAllBytes(registrationPath)

                try
                    File.WriteAllText(registrationPath, "{ invalid registration }")
                    let invalidBefore = snapshotState root

                    let invalidExitCode, invalidOutput =
                        runStatusWithoutHistoryMutation [| "--output"
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
                        runStatusWithoutHistoryMutation [| "--output"
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

    /// Verifies root dispatch gives output failures precedence and preserves valid projections for ready and non-ready states.
    [<Test>]
    let ``Linux cache status preserves select failures and valid projections`` () =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Cache status Product V1 supports Linux only.")

        withLoopbackRequestRecorder (fun endpoint assertNoRequest ->
            withTemporaryRoots (fun _ root ->
                let missingBefore = snapshotState root

                let missingExitCode, missingOutput =
                    runStatusWithoutHistoryMutation [| "--output"
                                                       "Json"
                                                       "--select"
                                                       "Key"
                                                       "cache"
                                                       "status" |]

                assertStateUnchanged root missingBefore
                assertNoRequest ()
                Assert.That(missingExitCode, Is.EqualTo(1))
                Assert.That(missingOutput.Trim(), Is.EqualTo("\"missing\""))

                let publicKey = CacheIdentity.createAttempt root |> requireOk

                CacheIdentity.commitReady root (acceptedRegistration endpoint publicKey)
                |> requireOk

                let readyBefore = snapshotState root

                let readyExitCode, readyOutput =
                    runStatusWithoutHistoryMutation [| "--output"
                                                       "Json"
                                                       "--select"
                                                       "CacheId"
                                                       "cache"
                                                       "status" |]

                assertStateUnchanged root readyBefore
                assertNoRequest ()
                Assert.That(readyExitCode, Is.EqualTo(0))
                Assert.That(readyOutput.Trim(), Is.EqualTo("\"11111111-1111-1111-1111-111111111111\""))

                let failedProjectionExitCode, failedProjectionOutput =
                    runStatusWithoutHistoryMutation [| "--output"
                                                       "Json"
                                                       "--select"
                                                       "Missing"
                                                       "cache"
                                                       "status" |]

                assertStateUnchanged root readyBefore
                assertNoRequest ()
                Assert.That(failedProjectionExitCode, Is.Not.EqualTo(0))

                use failedProjection = parseCompleteJsonDocument failedProjectionOutput
                let error = failedProjection.RootElement
                Assert.That(error.GetProperty("Error").GetString(), Does.Contain("was not found in ReturnValue"))

                let mutable returnValue = Unchecked.defaultof<JsonElement>
                Assert.That(error.TryGetProperty("ReturnValue", &returnValue), Is.False)

                let exceptionDetails = error.GetProperty("Exception")

                Assert.That(
                    exceptionDetails
                        .GetProperty("Message")
                        .GetString(),
                    Is.EqualTo(String.Empty)
                )

                Assert.That(
                    exceptionDetails
                        .GetProperty("StackTrace")
                        .GetString(),
                    Is.EqualTo(String.Empty)
                )

                [
                    root
                    "identity.pk8"
                    "registration.json"
                    "PrivateKey"
                    "PublicKey"
                    "Fingerprint"
                    "KeyReference"
                    "Token"
                    "Path:"
                    "Reference"
                ]
                |> List.iter (fun forbidden ->
                    Assert.That(failedProjectionOutput, Does.Not.Contain(forbidden), $"Failed Cache status projection must not expose {forbidden}."))))

    /// Verifies built root introspection reports the implemented Cache status schema and examples dispositions.
    [<Test>]
    let ``cache status built schema and examples report existing behavior`` () =
        for optionName, expectedKind in
            [
                "--schema", "schema"
                "--examples", "examples"
            ] do
            let exitCode, output =
                runWithCapturedStdout [| "cache"
                                         "status"
                                         optionName |]

            Assert.That(exitCode, Is.EqualTo(0))

            use document = parseCompleteJsonDocument output
            let root = document.RootElement
            let registry = root.GetProperty("Registry")
            Assert.That(root.GetProperty("Kind").GetString(), Is.EqualTo(expectedKind))
            Assert.That(registry.GetProperty("Schema").GetString(), Is.EqualTo("ExistingBehavior"))
            Assert.That(registry.GetProperty("Examples").GetString(), Is.EqualTo("ExistingBehavior"))

    /// Verifies the canonical audit retains one completed R1A classification and the current R1B leaf split.
    [<Test>]
    let ``cache implementation audit keeps R1 completion ownership current`` () =
        let audit = File.ReadAllText(cacheImplementationAuditPath)
        let r1a = auditSection "### R1A: static enrollment identity foundation (#886)" audit
        let r1aValidation = auditSection "## R1A completed validation history" audit

        [
            "| [Issue #856](https://github.com/ScottArbeit/Grace/issues/856) | Superseded by the narrower #886, #904, and #905 R1 sequence."
            "| [Issue #886](https://github.com/ScottArbeit/Grace/issues/886) and [PR #888](https://github.com/ScottArbeit/Grace/pull/888) | Implemented and proven R1A static enrollment identity foundation."
            "| [Issue #887](https://github.com/ScottArbeit/Grace/issues/887) and [PR #896](https://github.com/ScottArbeit/Grace/pull/896) | Superseded mixed R1B status/enrollment evidence."
            "| [Issue #904](https://github.com/ScottArbeit/Grace/issues/904) | Current pure local redacted cache status leaf."
            "| [Issue #905](https://github.com/ScottArbeit/Grace/issues/905) | Planned one-shot manual cache enrollment leaf."
            "- **Status classification:** `implemented and proven`."
            "- **R1B split:** #904 is the current pure local redacted status leaf, including its closed schema and examples. #905 is"
            "## R1A completed validation history"
        ]
        |> List.iter (fun landmark -> Assert.That(audit, Does.Contain(landmark)))

        [
            "implementation leaf"
            "still requires the separate R1A identity"
            "**Required result:**"
            "**Proof:**"
        ]
        |> List.iter (fun obsolete -> Assert.That(r1a, Does.Not.Contain(obsolete)))

        [
            "required evidence"
            "remain part of that focused proof"
            "pending"
        ]
        |> List.iter (fun pending -> Assert.That(r1aValidation, Does.Not.Contain(pending)))

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
        let unknownClass = status "notEnrolled" "missing" false
        unknownClass["Class"] <- JsonValue.Create("Grace.Cache.Unknown")
        let unknownEnrollment = status "unknown" "missing" false
        let unknownKey = status "notEnrolled" "unknown" false
        let unknownBoundaryKind = status "enrolled" "available" true
        unknownBoundaryKind["BoundaryKind"] <- JsonValue.Create("Unknown")

        for value in
            [
                extra
                invalidPair
                incompleteReady
                unknownClass
                unknownEnrollment
                unknownKey
                unknownBoundaryKind
            ] do
            Assert.That(validates value, Is.False, $"Expected schema to reject {value}")
