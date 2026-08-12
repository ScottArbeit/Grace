namespace Grace.Cache.Tests

open Grace.Cache
open NUnit.Framework
open System
open System.IO
open System.Text.Json
open System.Threading

/// Exercises the private Linux cache identity ready-state boundary without contacting Grace Server.
[<TestFixture>]
type CacheIdentityTests() =

    /// Creates one isolated state root for a cache identity test.
    let createRoot () =
        let root = Path.Combine(Path.GetTempPath(), "grace-cache-identity-tests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore

        File.SetUnixFileMode(
            root,
            UnixFileMode.UserRead
            ||| UnixFileMode.UserWrite
            ||| UnixFileMode.UserExecute
        )

        root

    /// Removes a test root after the case completes.
    let deleteRoot root = if Directory.Exists(root) then Directory.Delete(root, true)

    /// Extracts a successful local identity operation for concise fixture setup.
    let requireOk =
        function
        | Ok value -> value
        | Error message ->
            Assert.Fail(message)
            Unchecked.defaultof<_>

    /// Uses a non-canceled token for direct local identity tests.
    let cancellationToken = CancellationToken.None

    /// Builds a valid ready configuration for controlled local identity setup.
    let readyConfiguration prepared =
        CacheIdentity.ReadyConfiguration.create
            (Guid.Parse "11111111-1111-1111-1111-111111111111")
            "https://cache.example.test"
            "organization"
            (Guid.Parse "22222222-2222-2222-2222-222222222222")
            (Some(Guid.Parse "33333333-3333-3333-3333-333333333333"))
            [
                Guid.Parse "44444444-4444-4444-4444-444444444444"
            ]
            "Seattle cache"
            "v1"
            prepared.PublicKey

    /// Commits one valid ready identity and returns its ready directory for controlled failure-path setup.
    let commitReadyIdentity root =
        let prepared =
            CacheIdentity.prepare root cancellationToken
            |> requireOk

        let configuration = readyConfiguration prepared

        CacheIdentity.commitReady prepared configuration cancellationToken
        |> requireOk

        Path.Combine(root, "ready")

    /// Verifies a failed local ready commit leaves the enrollment unready and preserves staging for explicit cleanup.
    [<Test>]
    member _.``ready commit collision fails without an enrolled status``() =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Linux file-mode persistence is verified on the supported deployment platform.")

        let root = createRoot ()

        try
            let prepared =
                CacheIdentity.prepare root cancellationToken
                |> requireOk

            let ready = Path.Combine(root, "ready")

            Directory.CreateDirectory(ready) |> ignore

            File.SetUnixFileMode(
                ready,
                UnixFileMode.UserRead
                ||| UnixFileMode.UserWrite
                ||| UnixFileMode.UserExecute
            )

            match CacheIdentity.commitReady prepared (readyConfiguration prepared) cancellationToken with
            | Error message -> Assert.That(message, Is.EqualTo "A local Grace Cache enrollment already exists and requires explicit manual reset.")
            | Ok () -> Assert.Fail("Ready commit unexpectedly succeeded despite an existing ready directory.")

            let status = CacheIdentity.status root cancellationToken
            Assert.That(status.Enrollment, Is.EqualTo "invalid")
            Assert.That(Directory.Exists(prepared.StagingDirectory), Is.True)
        finally
            deleteRoot root

    /// Verifies ready state is the sole enrolled marker and its public status contains no private implementation details.
    [<Test>]
    member _.``ready identity reports only the approved redacted local status``() =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Linux file-mode persistence is verified on the supported deployment platform.")

        let root = createRoot ()

        try
            commitReadyIdentity root |> ignore

            let status = CacheIdentity.status root cancellationToken
            let serialized = JsonSerializer.Serialize(status)

            Assert.That(status.Enrollment, Is.EqualTo "enrolled")
            Assert.That(status.Key, Is.EqualTo "available")
            Assert.That(status.CacheId, Is.EqualTo(Some(Guid.Parse "11111111-1111-1111-1111-111111111111")))
            Assert.That(status.Endpoint, Is.EqualTo(Some "https://cache.example.test"))
            Assert.That(status.RepositoryCount, Is.EqualTo(Some 1))
            Assert.That(serialized, Does.Not.Contain("private"))
            Assert.That(serialized, Does.Not.Contain("staging"))
            Assert.That(serialized, Does.Not.Contain(root))
            Assert.That(serialized, Does.Not.Contain("fingerprint"))
        finally
            deleteRoot root

    /// Verifies a staging-only attempt is never mistaken for a completed enrollment and status never cleans it up.
    [<Test>]
    member _.``staging-only identity is not enrolled and status is non-mutating``() =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Linux file-mode persistence is verified on the supported deployment platform.")

        let root = createRoot ()

        try
            let prepared =
                CacheIdentity.prepare root cancellationToken
                |> requireOk

            let stagingPath = prepared.StagingDirectory
            let status = CacheIdentity.status root cancellationToken

            Assert.That(status.Enrollment, Is.EqualTo "notEnrolled")
            Assert.That(Directory.Exists(stagingPath), Is.True)
        finally
            deleteRoot root

    /// Verifies deployment mode mistakes on every ready-state component fail closed without emitting private identity details.
    [<TestCase("root")>]
    [<TestCase("ready")>]
    [<TestCase("configuration")>]
    [<TestCase("key")>]
    member _.``ready state with a weak protected mode is invalid and redacted``(target: string) =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Linux file-mode persistence is verified on the supported deployment platform.")

        let root = createRoot ()

        try
            let ready = commitReadyIdentity root

            let protectedPath =
                match target with
                | "root" -> root
                | "ready" -> ready
                | "configuration" -> Path.Combine(ready, "configuration.json")
                | "key" -> Path.Combine(ready, "identity.pkcs8")
                | _ ->
                    Assert.Fail("Unexpected protected state target.")
                    String.Empty

            File.SetUnixFileMode(protectedPath, UnixFileMode.UserRead ||| UnixFileMode.GroupRead)

            let status = CacheIdentity.status root cancellationToken
            let serialized = JsonSerializer.Serialize(status)

            Assert.That(status.Enrollment, Is.EqualTo "invalid")
            let expectedKey = if target = "root" || target = "ready" then "inaccessible" else "invalid"
            Assert.That(status.Key, Is.EqualTo expectedKey)
            Assert.That(serialized, Does.Not.Contain(root))
            Assert.That(serialized, Does.Not.Contain("identity.pkcs8"))
        finally
            if target = "root" && Directory.Exists(root) then
                File.SetUnixFileMode(
                    root,
                    UnixFileMode.UserRead
                    ||| UnixFileMode.UserWrite
                    ||| UnixFileMode.UserExecute
                )

            if target = "ready" then
                let ready = Path.Combine(root, "ready")

                if Directory.Exists(ready) then
                    File.SetUnixFileMode(
                        ready,
                        UnixFileMode.UserRead
                        ||| UnixFileMode.UserWrite
                        ||| UnixFileMode.UserExecute
                    )

            deleteRoot root

    /// Verifies a ready directory the supported service account cannot inspect is never presented as not enrolled.
    [<Test>]
    member _.``inaccessible ready state is redacted and fail closed``() =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Linux service-account access is verified on the supported deployment platform.")

        let root = createRoot ()
        let mutable ready = String.Empty

        try
            ready <- commitReadyIdentity root
            File.SetUnixFileMode(ready, UnixFileMode.None)
            let status = CacheIdentity.status root cancellationToken

            Assert.That(status.Enrollment, Is.EqualTo "invalid")
            Assert.That(status.Key, Is.EqualTo "inaccessible")
        finally
            if
                not (String.IsNullOrWhiteSpace(ready))
                && Directory.Exists(ready)
            then
                File.SetUnixFileMode(
                    ready,
                    UnixFileMode.UserRead
                    ||| UnixFileMode.UserWrite
                    ||| UnixFileMode.UserExecute
                )

            deleteRoot root

    /// Verifies missing, corrupt, and mismatched private ready-state files never become an enrolled local identity.
    [<TestCase("missing")>]
    [<TestCase("corrupt")>]
    [<TestCase("mismatched")>]
    member _.``invalid ready-state variants fail closed``(variant: string) =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Linux file-mode persistence is verified on the supported deployment platform.")

        let root = createRoot ()

        try
            let ready = commitReadyIdentity root
            let configurationPath = Path.Combine(ready, "configuration.json")
            let keyPath = Path.Combine(ready, "identity.pkcs8")

            match variant with
            | "missing" -> File.Delete(configurationPath)
            | "corrupt" -> File.WriteAllText(configurationPath, "not-json")
            | "mismatched" ->
                File.WriteAllBytes(keyPath, Array.zeroCreate 32)
                File.SetUnixFileMode(keyPath, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            | _ -> Assert.Fail("Unexpected test variant.")

            let status = CacheIdentity.status root cancellationToken
            Assert.That(status.Enrollment, Is.EqualTo "invalid")
            Assert.That(status.Key, Is.Not.EqualTo "available")
        finally
            deleteRoot root

    /// Verifies stale staging cleanup never touches a valid ready marker and cancellation prevents new local staging.
    [<Test>]
    member _.``stale staging cleanup preserves ready state and canceled preparation creates nothing``() =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Linux file-mode persistence is verified on the supported deployment platform.")

        let root = createRoot ()

        try
            let ready = commitReadyIdentity root
            let stale = Path.Combine(root, "staging-stale")
            Directory.CreateDirectory(stale) |> ignore

            File.SetUnixFileMode(
                stale,
                UnixFileMode.UserRead
                ||| UnixFileMode.UserWrite
                ||| UnixFileMode.UserExecute
            )

            CacheIdentity.cleanupStaleStaging root cancellationToken

            Assert.That(Directory.Exists(ready), Is.True)
            Assert.That(Directory.Exists(stale), Is.False)

            use canceled = new CancellationTokenSource()
            canceled.Cancel()

            let stagingBefore =
                Directory.GetDirectories(root, "staging-*")
                |> Set.ofArray

            Assert.Throws<OperationCanceledException>(
                Action (fun () ->
                    CacheIdentity.prepare root canceled.Token
                    |> ignore)
            )
            |> ignore

            let stagingAfter =
                Directory.GetDirectories(root, "staging-*")
                |> Set.ofArray

            let sameStaging = stagingBefore = stagingAfter
            Assert.That(sameStaging, Is.True)
        finally
            deleteRoot root

    /// Verifies required staging cleanup is independent of a canceled enrollment operation.
    [<Test>]
    member _.``discard removes staged key after cancellation``() =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Linux file-mode persistence is verified on the supported deployment platform.")

        let root = createRoot ()

        try
            let prepared =
                CacheIdentity.prepare root cancellationToken
                |> requireOk

            use canceled = new CancellationTokenSource()
            canceled.Cancel()
            CacheIdentity.discard prepared

            Assert.That(canceled.IsCancellationRequested, Is.True)
            Assert.That(Directory.Exists(prepared.StagingDirectory), Is.False)
        finally
            deleteRoot root
