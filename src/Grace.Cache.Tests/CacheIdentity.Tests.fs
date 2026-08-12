namespace Grace.Cache.Tests

open Grace.Cache
open NUnit.Framework
open System
open System.IO
open System.Text.Json

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

    /// Verifies ready state is the sole enrolled marker and its public status contains no private implementation details.
    [<Test>]
    member _.``ready identity reports only the approved redacted local status``() =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("Linux file-mode persistence is verified on the supported deployment platform.")

        let root = createRoot ()

        try
            let prepared = CacheIdentity.prepare root |> requireOk

            let configuration =
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

            CacheIdentity.commitReady prepared configuration
            |> requireOk

            let status = CacheIdentity.status root
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
            let prepared = CacheIdentity.prepare root |> requireOk
            let stagingPath = prepared.StagingDirectory
            let status = CacheIdentity.status root

            Assert.That(status.Enrollment, Is.EqualTo "notEnrolled")
            Assert.That(Directory.Exists(stagingPath), Is.True)
        finally
            deleteRoot root
