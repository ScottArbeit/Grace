namespace Grace.Cache.Tests

open Grace.Cache
open NUnit.Framework
open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

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
    let acceptedConfiguration publicKey : CacheAcceptedRegistration =
        {
            CacheId = Guid.Parse("11111111-1111-1111-1111-111111111111")
            DisplayName = "Seattle cache"
            BoundaryKind = "Organization"
            OwnerId = Guid.Parse("22222222-2222-2222-2222-222222222222")
            OrganizationId = Some(Guid.Parse("33333333-3333-3333-3333-333333333333"))
            RepositoryScopes =
                [|
                    { OrganizationId = Guid.Parse("33333333-3333-3333-3333-333333333333"); RepositoryId = Guid.Parse("44444444-4444-4444-4444-444444444444") }
                    { OrganizationId = Guid.Parse("33333333-3333-3333-3333-333333333333"); RepositoryId = Guid.Parse("55555555-5555-5555-5555-555555555555") }
                |]
            Endpoint = "https://cache.example.test"
            ProtocolVersion = "v1"
            PublicKey = publicKey
        }

    /// Derives the canonical persisted fingerprint from one expected P-256 public key.
    let expectedFingerprint (publicKey: CacheIdentityPublicKey) =
        let decodeBase64Url (value: string) =
            let padded =
                value.Replace('-', '+').Replace('_', '/')
                + String('=', (4 - value.Length % 4) % 4)

            Convert.FromBase64String padded

        Array.concat [ decodeBase64Url publicKey.PublicKeyX
                       decodeBase64Url publicKey.PublicKeyY ]
        |> SHA256.HashData
        |> Convert.ToBase64String
        |> fun value ->
            value
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_')

    /// Creates and publishes one valid ready identity for inspection tests.
    let readyIdentity root =
        let publicKey = CacheIdentity.createAttempt root |> requireOk

        CacheIdentity.commitReady root (acceptedConfiguration publicKey)
        |> requireOk

        publicKey

    /// Produces canonical base64url coordinates for production writer/parser tests that do not require Linux files.
    let base64Url (bytes: byte array) =
        Convert
            .ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_')

    /// Requires a non-filesystem ready-registration serialization result while reporting rejected facts directly.
    let requireSerializedReadyRegistration configuration =
        match CacheIdentity.trySerializeReadyRegistrationBytes configuration with
        | Some bytes -> bytes
        | None ->
            Assert.Fail("Expected the production ready-registration writer and parser to accept the complete configuration.")
            Array.empty

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
            let publicKey = readyIdentity root
            let ready = Path.Combine(root, "ready")

            Assert.That(Directory.Exists(Path.Combine(root, "attempt")), Is.False)
            Assert.That(File.GetUnixFileMode(ready), Is.EqualTo(directoryMode))
            Assert.That(File.GetUnixFileMode(Path.Combine(ready, "identity.pk8")), Is.EqualTo(privateMode))
            Assert.That(File.GetUnixFileMode(Path.Combine(ready, "registration.json")), Is.EqualTo(privateMode))
            let registrationJson = File.ReadAllText(Path.Combine(ready, "registration.json"))

            Assert.That(registrationJson.Trim(), Is.Not.EqualTo("{}"))
            Assert.That(registrationJson, Does.Not.Contain("identity.pk8"))

            use registrationDocument = JsonDocument.Parse(registrationJson)
            let registration = registrationDocument.RootElement
            let expected = acceptedConfiguration publicKey

            let repositoryScopes =
                registration
                    .GetProperty("RepositoryScopes")
                    .EnumerateArray()
                |> Seq.toArray

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(registration.GetProperty("CacheId").GetString(), Is.EqualTo("11111111-1111-1111-1111-111111111111"))
                    Assert.That(registration.GetProperty("Endpoint").GetString(), Is.EqualTo(expected.Endpoint))

                    Assert.That(
                        registration
                            .GetProperty("BoundaryKind")
                            .GetString(),
                        Is.EqualTo(expected.BoundaryKind)
                    )

                    Assert.That(registration.GetProperty("OwnerId").GetString(), Is.EqualTo("22222222-2222-2222-2222-222222222222"))

                    Assert.That(
                        registration
                            .GetProperty("OrganizationId")
                            .GetString(),
                        Is.EqualTo("33333333-3333-3333-3333-333333333333")
                    )

                    Assert.That(
                        registration
                            .GetProperty("RepositoryScopes")
                            .GetArrayLength(),
                        Is.EqualTo(2)
                    )

                    Assert.That(
                        repositoryScopes[0]
                            .GetProperty("OrganizationId")
                            .GetString(),
                        Is.EqualTo("33333333-3333-3333-3333-333333333333")
                    )

                    Assert.That(
                        repositoryScopes[0]
                            .GetProperty("RepositoryId")
                            .GetString(),
                        Is.EqualTo("44444444-4444-4444-4444-444444444444")
                    )

                    Assert.That(
                        repositoryScopes[1]
                            .GetProperty("OrganizationId")
                            .GetString(),
                        Is.EqualTo("33333333-3333-3333-3333-333333333333")
                    )

                    Assert.That(
                        repositoryScopes[1]
                            .GetProperty("RepositoryId")
                            .GetString(),
                        Is.EqualTo("55555555-5555-5555-5555-555555555555")
                    )

                    Assert.That(
                        registration
                            .GetProperty("DisplayName")
                            .GetString(),
                        Is.EqualTo(expected.DisplayName)
                    )

                    Assert.That(
                        registration
                            .GetProperty("ProtocolVersion")
                            .GetString(),
                        Is.EqualTo(expected.ProtocolVersion)
                    )

                    Assert.That(registration.GetProperty("PublicKeyX").GetString(), Is.EqualTo(expected.PublicKey.PublicKeyX))
                    Assert.That(registration.GetProperty("PublicKeyY").GetString(), Is.EqualTo(expected.PublicKey.PublicKeyY))

                    Assert.That(
                        registration
                            .GetProperty("PublicKeyFingerprint")
                            .GetString(),
                        Is.EqualTo(expectedFingerprint publicKey)
                    ))
            )

            requireInspection CacheIdentityInspection.Ready (CacheIdentity.inspect root))

    /// Verifies a later publication attempt cannot replace an already published protected ready identity.
    [<Test>]
    member _.``commitReady never replaces an existing ready identity``() =
        withLinuxRoot (fun root ->
            let publicKey = readyIdentity root
            let ready = Path.Combine(root, "ready")
            let identityPath = Path.Combine(ready, "identity.pk8")
            let registrationPath = Path.Combine(ready, "registration.json")
            let originalIdentity = File.ReadAllBytes(identityPath)
            let originalRegistration = File.ReadAllBytes(registrationPath)

            CacheIdentity.commitReady root { acceptedConfiguration publicKey with DisplayName = "replacement attempt" }
            |> requireStateUnavailable

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(File.ReadAllBytes(identityPath).Length, Is.EqualTo(originalIdentity.Length))
                    Assert.That(Array.forall2 (=) originalIdentity (File.ReadAllBytes(identityPath)), Is.True)
                    Assert.That(File.ReadAllBytes(registrationPath).Length, Is.EqualTo(originalRegistration.Length))
                    Assert.That(Array.forall2 (=) originalRegistration (File.ReadAllBytes(registrationPath)), Is.True)

                    requireInspection CacheIdentityInspection.Ready (CacheIdentity.inspect root))
            ))

    /// Verifies the production private-file writer and strict parser work without Linux filesystem primitives.
    [<Test>]
    member _.``ready registration writer and parser preserve accepted facts and reject malformed JSON``() =
        use key = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        let parameters = key.ExportParameters(false)

        let publicKey = { PublicKeyX = base64Url parameters.Q.X; PublicKeyY = base64Url parameters.Q.Y }

        let configuration = acceptedConfiguration publicKey
        let bytes = requireSerializedReadyRegistration configuration
        let registrationJson = Encoding.UTF8.GetString(bytes)

        Assert.Multiple(
            Action (fun () ->
                Assert.That(registrationJson.Trim(), Is.Not.EqualTo("{}"))
                Assert.That(CacheIdentity.tryValidateReadyRegistrationBytes bytes, Is.EqualTo(Some(expectedFingerprint publicKey)))

                Assert.That(
                    CacheIdentity.trySerializeReadyRegistrationBytes { configuration with BoundaryKind = "Owner"; OrganizationId = None }
                    |> Option.isSome,
                    Is.True
                ))
        )

        let replace oldValue newValue = registrationJson.Replace(oldValue, newValue, StringComparison.Ordinal)

        let invalidDocuments =
            [|
                "unknown root field",
                "{\"RotationDueAt\":\"2026-08-12T00:00:00Z\","
                + registrationJson.Substring(1)
                "duplicate root field", replace "\"CacheId\":" "\"CacheId\":\"00000000-0000-0000-0000-000000000001\",\"CacheId\":"
                "missing required field", replace "\"ProtocolVersion\":\"v1\"," ""
                "wrong endpoint type", replace "\"Endpoint\":\"https://cache.example.test\"" "\"Endpoint\":123"
                "malformed cache id", replace "11111111-1111-1111-1111-111111111111" "not-a-guid"
                "malformed public coordinate", replace publicKey.PublicKeyX "not-base64url"
                "unsupported endpoint scheme", replace "https://cache.example.test" "ftp://cache.example.test"
                "invalid boundary combination", replace "\"BoundaryKind\":\"Organization\"" "\"BoundaryKind\":\"Owner\""
                "malformed nested repository id", replace "44444444-4444-4444-4444-444444444444" "not-a-guid"
                "duplicate repository id", replace "55555555-5555-5555-5555-555555555555" "44444444-4444-4444-4444-444444444444"
                "unknown nested field",
                replace
                    "\"RepositoryId\":\"44444444-4444-4444-4444-444444444444\""
                    "\"RepositoryId\":\"44444444-4444-4444-4444-444444444444\",\"RotationDueAt\":\"stale\""
                "trailing JSON", registrationJson + " trailing"
            |]

        for name, invalidJson in invalidDocuments do
            Assert.That(CacheIdentity.tryValidateReadyRegistrationBytes (Encoding.UTF8.GetBytes(invalidJson)), Is.EqualTo(None), name)

    /// Verifies strict parsing rejects malformed ready files after protected Linux publication without exposing their contents.
    [<Test>]
    member _.``inspect rejects unknown endpoint and nested scope ready registration fields``() =
        withLinuxRoot (fun root ->
            readyIdentity root |> ignore
            let registrationPath = Path.Combine(root, "ready", "registration.json")
            let originalRegistration = File.ReadAllText(registrationPath)
            let originalRegistrationMode = File.GetUnixFileMode(registrationPath)

            let invalidRegistrations =
                [|
                    "unknown field",
                    "{\"RotationDueAt\":\"stale\","
                    + originalRegistration.Substring(1)
                    "unsupported endpoint", originalRegistration.Replace("https://cache.example.test", "ftp://cache.example.test", StringComparison.Ordinal)
                    "malformed nested scope", originalRegistration.Replace("44444444-4444-4444-4444-444444444444", "not-a-guid", StringComparison.Ordinal)
                |]

            try
                for name, invalidRegistration in invalidRegistrations do
                    File.WriteAllText(registrationPath, invalidRegistration)
                    requireInspection CacheIdentityInspection.Invalid (CacheIdentity.inspect root)
                    Assert.That(File.GetUnixFileMode(registrationPath), Is.EqualTo(originalRegistrationMode), name)
            finally
                if File.Exists(registrationPath) then
                    File.WriteAllText(registrationPath, originalRegistration)
                    File.SetUnixFileMode(registrationPath, originalRegistrationMode))

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

            let result = CacheIdentity.commitReady root (acceptedConfiguration otherPublicKey)
            result |> requireStateUnavailable

            Assert.That(string result, Does.Not.Contain(otherPublicKey.PublicKeyX))
            Assert.That(string result, Does.Not.Contain(otherPublicKey.PublicKeyY))

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
            readyIdentity root |> ignore

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

            readyIdentity root |> ignore

            let ready = Path.Combine(root, "ready")
            let identity = Path.Combine(ready, "identity.pk8")
            let registration = Path.Combine(ready, "registration.json")

            withRestoredMode ready privateMode (fun () -> requireInspection CacheIdentityInspection.Inaccessible (CacheIdentity.inspect root))

            withRestoredMode identity UnixFileMode.UserWrite (fun () -> requireInspection CacheIdentityInspection.Inaccessible (CacheIdentity.inspect root))

            withRestoredMode registration UnixFileMode.UserWrite (fun () -> requireInspection CacheIdentityInspection.Inaccessible (CacheIdentity.inspect root)))

    /// Proves staging faults remove only the newly created residue and do not prevent a later normal enrollment attempt.
    [<Test>]
    member _.``createAttempt cleans partial directory and private key residues``() =
        withLinuxRoot (fun root ->
            for fault in
                [
                    CacheIdentityCreateFault.AfterAttemptDirectory
                    CacheIdentityCreateFault.AfterPrivateKeyFlush
                ] do
                CacheIdentity.createAttemptWithFault root fault
                |> requireStateUnavailable

                Assert.That(Directory.Exists(Path.Combine(root, "attempt")), Is.False)
                requireInspection CacheIdentityInspection.Missing (CacheIdentity.inspect root)

            CacheIdentity.createAttempt root
            |> requireOk
            |> ignore

            requireInspection CacheIdentityInspection.AttemptPresent (CacheIdentity.inspect root))

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
