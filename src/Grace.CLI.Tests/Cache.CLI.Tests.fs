namespace Grace.CLI.Tests

open Grace.Cache
open Grace.CLI
open Grace.CLI.Command
open NUnit.Framework
open Spectre.Console
open System
open System.IO
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
    let private run args =
        use writer = new StringWriter()
        let originalOut = Console.Out

        try
            Console.SetOut(writer)
            setAnsiConsoleOutput writer
            GraceCommand.main args, writer.ToString()
        finally
            Console.SetOut(originalOut)
            setAnsiConsoleOutput originalOut

    /// Creates an isolated protected state root and restores the cache command override.
    let private withRoot action =
        let root = Path.Combine(Path.GetTempPath(), $"grace-cache-status-{Guid.NewGuid():N}")
        Directory.CreateDirectory(root) |> ignore

        File.SetUnixFileMode(
            root,
            UnixFileMode.UserRead
            ||| UnixFileMode.UserWrite
            ||| UnixFileMode.UserExecute
        )

        try
            CacheCommand.setStateRootForTests root
            action root
        finally
            CacheCommand.resetStateRootForTests ()
            if Directory.Exists(root) then Directory.Delete(root, true)

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

    /// Captures protected-state metadata without reading private content.
    let private snapshot root =
        Array.append [| root |] (Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories))
        |> Array.sort
        |> Array.map (fun path -> path, File.GetUnixFileMode(path), File.GetLastWriteTimeUtc(path), (if File.Exists(path) then FileInfo(path).Length else -1L))

    /// Verifies one JSON status contains only redacted fields for its state.
    let private assertStatus (enrollment: string) (key: string) (exitCode: int) (output: string) =
        Assert.That(exitCode, Is.EqualTo(if enrollment = "enrolled" then 0 else 1))
        use document = JsonDocument.Parse(output)
        let status = document.RootElement.GetProperty("ReturnValue")
        Assert.That(status.GetProperty("Class").GetString(), Is.EqualTo("Grace.Cache.Status"))
        Assert.That(status.GetProperty("Enrollment").GetString(), Is.EqualTo(enrollment))
        Assert.That(status.GetProperty("Key").GetString(), Is.EqualTo(key))

        if enrollment <> "enrolled" then
            [
                "CacheId"
                "Endpoint"
                "BoundaryKind"
                "RepositoryCount"
            ]
            |> List.iter (fun name ->
                let mutable ignored = Unchecked.defaultof<JsonElement>
                Assert.That(status.TryGetProperty(name, &ignored), Is.False))

    /// Proves root dispatch has no Linux identity implementation until running in the real service-user environment.
    [<Test>]
    let ``cache status maps unsupported hosts to inaccessible without repository state`` () =
        if OperatingSystem.IsLinux() then
            Assert.Ignore("This direct branch proof is for unsupported hosts.")

        let status = CacheIdentity.status CacheIdentity.StateRoot
        Assert.That(status.Enrollment, Is.EqualTo("invalid"))
        Assert.That(status.Key, Is.EqualTo("inaccessible"))

    /// Proves every parser-accepted output mode uses root dispatch and leaves protected state unchanged.
    [<Test>]
    let ``Linux cache status honors shared output modes selectors and read-only states`` () =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Cache status Product V1 supports Linux only.")

        withRoot (fun root ->
            let missingBefore = snapshot root
            let normalExit, normal = run [| "cache"; "status" |]
            Assert.That(normalExit, Is.EqualTo(1))
            Assert.That(normal, Does.Contain("Enrollment: notEnrolled"))
            Assert.That(snapshot root = missingBefore, Is.True)

            let minimalExit, minimal =
                run [| "--output"
                       "Minimal"
                       "cache"
                       "status" |]

            Assert.That(minimalExit, Is.EqualTo(1))
            Assert.That(minimal, Is.Empty)

            let silentExit, silent =
                run [| "--output"
                       "Silent"
                       "cache"
                       "status" |]

            Assert.That(silentExit, Is.EqualTo(1))
            Assert.That(silent, Is.Empty)

            let verboseExit, verbose =
                run [| "--output"
                       "Verbose"
                       "cache"
                       "status" |]

            Assert.That(verboseExit, Is.EqualTo(1))
            Assert.That(verbose, Does.Contain("Enrollment: notEnrolled"))
            Assert.That(verbose, Does.Contain("EventTime:"))

            let missingExit, missing =
                run [| "--output"
                       "Json"
                       "cache"
                       "status" |]

            assertStatus "notEnrolled" "missing" missingExit missing
            Assert.That(snapshot root = missingBefore, Is.True)

            let selectedExit, selected =
                run [| "--output"
                       "Minimal"
                       "--select"
                       "Enrollment"
                       "cache"
                       "status" |]

            Assert.That(selectedExit, Is.EqualTo(1))
            Assert.That(selected.Trim(), Is.EqualTo("\"notEnrolled\""))

            let missingSelectExit, missingSelect =
                run [| "--select"
                       "CacheId"
                       "cache"
                       "status" |]

            Assert.That(missingSelectExit, Is.Not.EqualTo(0))
            Assert.That(missingSelect, Does.Contain("GraceError"))

            let publicKey = CacheIdentity.createAttempt root |> requireOk

            CacheIdentity.commitReady root (registration "https://cache.example.test" publicKey)
            |> requireOk

            let readyBefore = snapshot root

            let readyExit, ready =
                run [| "--output"
                       "Json"
                       "cache"
                       "status" |]

            assertStatus "enrolled" "available" readyExit ready
            Assert.That(ready, Does.Contain("CacheId"))
            Assert.That(snapshot root = readyBefore, Is.True)

            let readySelectExit, readySelect =
                run [| "--output"
                       "Silent"
                       "--select"
                       "CacheId"
                       "cache"
                       "status" |]

            Assert.That(readySelectExit, Is.EqualTo(0))
            Assert.That(readySelect, Does.Contain("11111111-1111-1111-1111-111111111111"))

            let malformedExit, malformed =
                run [| "--select"
                       "CacheId[0]"
                       "cache"
                       "status" |]

            Assert.That(malformedExit, Is.Not.EqualTo(0))
            Assert.That(malformed, Does.Contain("GraceError")))
