namespace Grace.Cache.Tests

open Grace.Cache
open NUnit.Framework
open System
open System.IO
open System.Security.Cryptography

/// Exercises the protected Linux static identity boundary without starting the Cache host or contacting Grace Server.
[<TestFixture>]
type CacheIdentityTests() =

    let privateMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite
    let directoryMode = privateMode ||| UnixFileMode.UserExecute

    /// Executes an identity assertion below a fresh root while restoring its original mode before cleanup.
    let withLinuxRoot assertion =
        if not (OperatingSystem.IsLinux()) then
            Assert.Ignore("CacheIdentity is supported only on Linux.")

        let root = Path.Combine(Path.GetTempPath(), "grace-cache-identity-tests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        let originalRootMode = File.GetUnixFileMode(root)
        File.SetUnixFileMode(root, directoryMode)

        try
            assertion root
        finally
            if Directory.Exists(root) then
                File.SetUnixFileMode(root, originalRootMode)
                Directory.Delete(root, true)

    /// Requires an expected successful protected identity operation.
    let requireOk =
        function
        | Ok value -> value
        | Error error ->
            Assert.Fail($"Unexpected protected identity result: {error}")
            Unchecked.defaultof<_>

    /// Builds accepted configuration from the public key that the staged private key must match.
    let acceptedConfiguration publicKey =
        { CacheId = Guid.Parse("11111111-1111-1111-1111-111111111111"); Endpoint = "https://cache.example.test"; PublicKey = publicKey }

    /// Creates and publishes one valid ready identity for inspection tests.
    let readyIdentity root =
        let publicKey = CacheIdentity.createAttempt root |> requireOk

        CacheIdentity.commitReady root (acceptedConfiguration publicKey)
        |> requireOk

    /// Verifies one fresh P-256 key becomes an attempt with exact protected modes and no ready marker.
    [<Test>]
    member _.``createAttempt writes one protected P-256 key and reports an opaque attempt``() =
        withLinuxRoot (fun root ->
            let publicKey = CacheIdentity.createAttempt root |> requireOk
            let attempt = Path.Combine(root, "attempt")
            let identity = Path.Combine(attempt, "identity.pk8")

            Assert.That(publicKey.PublicKeyX, Has.Length.EqualTo(43))
            Assert.That(publicKey.PublicKeyY, Has.Length.EqualTo(43))
            Assert.That(File.GetUnixFileMode(root), Is.EqualTo(directoryMode))
            Assert.That(File.GetUnixFileMode(attempt), Is.EqualTo(directoryMode))
            Assert.That(File.GetUnixFileMode(identity), Is.EqualTo(privateMode))
            Assert.That(CacheIdentity.inspect root, Is.EqualTo(Ok CacheIdentityInspection.AttemptPresent)))

    /// Verifies matching accepted facts publish one same-parent ready marker with protected key and configuration modes.
    [<Test>]
    member _.``commitReady publishes only a matching protected ready identity``() =
        withLinuxRoot (fun root ->
            readyIdentity root
            let ready = Path.Combine(root, "ready")

            Assert.That(Directory.Exists(Path.Combine(root, "attempt")), Is.False)
            Assert.That(File.GetUnixFileMode(ready), Is.EqualTo(directoryMode))
            Assert.That(File.GetUnixFileMode(Path.Combine(ready, "identity.pk8")), Is.EqualTo(privateMode))
            Assert.That(File.GetUnixFileMode(Path.Combine(ready, "registration.json")), Is.EqualTo(privateMode))
            Assert.That(CacheIdentity.inspect root, Is.EqualTo(Ok CacheIdentityInspection.Ready)))

    /// Verifies an accepted configuration carrying a different P-256 identity cannot create a ready marker.
    [<Test>]
    member _.``commitReady rejects a mismatched accepted public key without publishing ready``() =
        withLinuxRoot (fun root ->
            CacheIdentity.createAttempt root
            |> requireOk
            |> ignore

            use otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256)
            let parameters = otherKey.ExportParameters(false)

            let otherPublicKey =
                {
                    PublicKeyX =
                        Convert
                            .ToBase64String(parameters.Q.X)
                            .TrimEnd('=')
                            .Replace('+', '-')
                            .Replace('/', '_')
                    PublicKeyY =
                        Convert
                            .ToBase64String(parameters.Q.Y)
                            .TrimEnd('=')
                            .Replace('+', '-')
                            .Replace('/', '_')
                }

            Assert.That(CacheIdentity.commitReady root (acceptedConfiguration otherPublicKey), Is.EqualTo(Error CacheIdentityError.StateUnavailable))
            Assert.That(Directory.Exists(Path.Combine(root, "ready")), Is.False)
            Assert.That(CacheIdentity.inspect root, Is.EqualTo(Ok CacheIdentityInspection.AttemptPresent)))

    /// Verifies inspect distinguishes missing, invalid concurrent markers, weak readable modes, and inaccessible state without repairing it.
    [<Test>]
    member _.``inspect classifies opaque local identity states without mutation``() =
        withLinuxRoot (fun root ->
            Assert.That(CacheIdentity.inspect root, Is.EqualTo(Ok CacheIdentityInspection.Missing))

            CacheIdentity.createAttempt root
            |> requireOk
            |> ignore

            let ready = Path.Combine(root, "ready")
            Directory.CreateDirectory(ready) |> ignore
            File.SetUnixFileMode(ready, directoryMode)
            Assert.That(CacheIdentity.inspect root, Is.EqualTo(Ok CacheIdentityInspection.Invalid))

            Directory.Delete(ready, true)
            CacheIdentity.discardAttempt root
            readyIdentity root

            let identity = Path.Combine(root, "ready", "identity.pk8")
            let originalIdentityMode = File.GetUnixFileMode(identity)
            File.SetUnixFileMode(identity, privateMode ||| UnixFileMode.OtherRead)

            try
                Assert.That(CacheIdentity.inspect root, Is.EqualTo(Ok CacheIdentityInspection.Invalid))
            finally
                File.SetUnixFileMode(identity, originalIdentityMode))

    /// Verifies denied root traversal stays distinct from absent or malformed state when the test process is not privileged.
    [<Test>]
    member _.``inspect reports inaccessible root traversal without exposing details``() =
        withLinuxRoot (fun root ->
            let originalRootMode = File.GetUnixFileMode(root)
            File.SetUnixFileMode(root, UnixFileMode.None)

            try
                match CacheIdentity.inspect root with
                | Ok CacheIdentityInspection.Inaccessible -> ()
                | _ when String.Equals(Environment.UserName, "root", StringComparison.Ordinal) ->
                    Assert.Ignore("A privileged Linux test process can traverse mode-000 directories.")
                | result -> Assert.Fail($"Expected inaccessible protected state, received {result}.")
            finally
                File.SetUnixFileMode(root, originalRootMode))

    /// Verifies discard has no cancellation/error channel and removes only the fixed attempt marker.
    [<Test>]
    member _.``discardAttempt removes staging without changing ready state``() =
        withLinuxRoot (fun root ->
            CacheIdentity.createAttempt root
            |> requireOk
            |> ignore

            CacheIdentity.discardAttempt root
            Assert.That(CacheIdentity.inspect root, Is.EqualTo(Ok CacheIdentityInspection.Missing))
            Assert.DoesNotThrow(Action(fun () -> CacheIdentity.discardAttempt root)))
