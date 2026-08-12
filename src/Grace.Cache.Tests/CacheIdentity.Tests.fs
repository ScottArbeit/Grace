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

    /// Requires an inspection state while reporting a returned error or wrong opaque state directly.
    let requireInspection expected =
        function
        | Ok actual when actual = expected -> ()
        | Ok actual -> Assert.Fail($"Expected protected identity inspection {expected}, received {actual}.")
        | Error error -> Assert.Fail($"Expected protected identity inspection {expected}, received error {error}.")

    /// Requires the opaque operation failure used when protected staging cannot publish a ready identity.
    let requireStateUnavailable =
        function
        | Error CacheIdentityError.StateUnavailable -> ()
        | Error error -> Assert.Fail($"Expected StateUnavailable, received error {error}.")
        | Ok value -> Assert.Fail($"Expected StateUnavailable, but the protected operation succeeded with {value}.")

    /// Restores a path's exact mode after one temporary access-denial or weak-mode proof.
    let withRestoredMode (path: string) (temporaryMode: UnixFileMode) (assertion: unit -> unit) =
        let originalMode = File.GetUnixFileMode(path)
        File.SetUnixFileMode(path, temporaryMode)

        try
            assertion ()
        finally
            if File.Exists(path) || Directory.Exists(path) then
                File.SetUnixFileMode(path, originalMode)

    /// Replaces staged key bytes while preserving their original mode for invalid identity inspection proof.
    let assertInvalidStagedKey (replaceBytes: byte array -> byte array) =
        withLinuxRoot (fun root ->
            CacheIdentity.createAttempt root
            |> requireOk
            |> ignore

            let identity = Path.Combine(root, "attempt", "identity.pk8")
            let privateBytes = File.ReadAllBytes(identity)
            let originalIdentityMode = File.GetUnixFileMode(identity)

            try
                File.WriteAllBytes(identity, replaceBytes privateBytes)
                requireInspection CacheIdentityInspection.Invalid (CacheIdentity.inspect root)
            finally
                if File.Exists(identity) then
                    File.SetUnixFileMode(identity, originalIdentityMode))

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
            requireInspection CacheIdentityInspection.AttemptPresent (CacheIdentity.inspect root))

        assertInvalidStagedKey (fun _ -> Array.empty)
        assertInvalidStagedKey (fun privateBytes -> privateBytes[0 .. (privateBytes.Length - 2)])
        assertInvalidStagedKey (fun privateBytes -> Array.append privateBytes [| 0uy |])

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
            requireInspection CacheIdentityInspection.Ready (CacheIdentity.inspect root))

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

            CacheIdentity.commitReady root (acceptedConfiguration otherPublicKey)
            |> requireStateUnavailable

            Assert.That(Directory.Exists(Path.Combine(root, "ready")), Is.False)
            requireInspection CacheIdentityInspection.AttemptPresent (CacheIdentity.inspect root))

    /// Verifies inspect distinguishes missing, invalid concurrent markers, weak readable modes, and inaccessible state without repairing it.
    [<Test>]
    member _.``inspect classifies opaque local identity states without mutation``() =
        withLinuxRoot (fun root ->
            requireInspection CacheIdentityInspection.Missing (CacheIdentity.inspect root)

            CacheIdentity.createAttempt root
            |> requireOk
            |> ignore

            let ready = Path.Combine(root, "ready")
            Directory.CreateDirectory(ready) |> ignore
            File.SetUnixFileMode(ready, directoryMode)
            requireInspection CacheIdentityInspection.Invalid (CacheIdentity.inspect root)

            Directory.Delete(ready, true)
            CacheIdentity.discardAttempt root
            readyIdentity root

            let identity = Path.Combine(root, "ready", "identity.pk8")

            withRestoredMode identity (privateMode ||| UnixFileMode.OtherRead) (fun () ->
                requireInspection CacheIdentityInspection.Invalid (CacheIdentity.inspect root)))

    /// Verifies denied traversal and owner-read access stay distinct from malformed state for an unprivileged Linux process.
    [<Test>]
    member _.``inspect reports inaccessible traversal and owner-read denial without exposing details``() =
        withLinuxRoot (fun root ->
            if String.Equals(Environment.UserName, "root", StringComparison.Ordinal) then
                Assert.Ignore("These mode-denial cases require an unprivileged Linux test process.")

            withRestoredMode root privateMode (fun () -> requireInspection CacheIdentityInspection.Inaccessible (CacheIdentity.inspect root))

            readyIdentity root

            let ready = Path.Combine(root, "ready")
            let identity = Path.Combine(ready, "identity.pk8")
            let registration = Path.Combine(ready, "registration.json")

            withRestoredMode ready privateMode (fun () -> requireInspection CacheIdentityInspection.Inaccessible (CacheIdentity.inspect root))

            withRestoredMode identity UnixFileMode.UserWrite (fun () -> requireInspection CacheIdentityInspection.Inaccessible (CacheIdentity.inspect root))

            withRestoredMode registration UnixFileMode.UserWrite (fun () -> requireInspection CacheIdentityInspection.Inaccessible (CacheIdentity.inspect root)))

    /// Verifies discard has no cancellation/error channel and removes only the fixed attempt marker.
    [<Test>]
    member _.``discardAttempt removes staging without changing ready state``() =
        withLinuxRoot (fun root ->
            CacheIdentity.createAttempt root
            |> requireOk
            |> ignore

            CacheIdentity.discardAttempt root
            requireInspection CacheIdentityInspection.Missing (CacheIdentity.inspect root)
            Assert.DoesNotThrow(Action(fun () -> CacheIdentity.discardAttempt root)))
