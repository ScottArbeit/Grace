namespace Grace.Server.Unit.Tests

open System
open System.Security.Cryptography
open System.Text
open Grace.Server
open Grace.Server.Security
open Grace.Shared
open Grace.Shared.Parameters.Cache
open Grace.Types.ArtifactGrant
open NUnit.Framework

/// Exercises the stateless permit's expiry, tamper resistance, and exact Cache process-key binding.
[<TestFixture>]
type CachePermitTests() =

    /// Encodes unpadded base64url values for the public JWK and P1363 signature fixture.
    let encode (bytes: byte array) =
        Convert
            .ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_')

    /// Creates one public JWK from the generated private test key.
    let publicJwk (key: ECDsa) : P256PublicJwk =
        let parameters = key.ExportParameters(false)
        { Kty = "EC"; Crv = "P-256"; X = encode parameters.Q.X; Y = encode parameters.Q.Y }

    /// Creates the exact immutable artifact bound into each test permit.
    let artifact =
        DirectoryVersionZipCacheArtifact.Create(
            Guid.Parse("4cb5fa2c-a145-4c6b-98d7-ee2274230f3e"),
            Guid.Parse("70c90fec-e491-456a-a8e5-971db046ec17"),
            String.replicate 64 "a"
        )

    /// Confirms only the bound process key can redeem the unaltered permit before Server-clock expiry.
    [<Test>]
    member _.``permit accepts only the exact process key before expiry``() =
        use expectedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        use wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        let now = DateTimeOffset.UtcNow
        let permit = Cache.createPermitForTest "user-1" (publicJwk expectedKey) artifact (now.AddSeconds(60.0))

        let sign (key: ECDsa) (value: string) =
            key.SignData(Encoding.UTF8.GetBytes(value), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            |> encode

        Assert.Multiple(
            Action (fun () ->
                Assert.That(Cache.verifyPermitBindingForTest permit (sign expectedKey permit) now, Is.True)
                Assert.That(Cache.verifyPermitBindingForTest permit (sign wrongKey permit) now, Is.False)
                Assert.That(Cache.verifyPermitBindingForTest (permit + "x") (sign expectedKey permit) now, Is.False)
                Assert.That(Cache.verifyPermitBindingForTest permit (sign expectedKey permit) (now.AddSeconds(61.0)), Is.False))
        )

/// Exercises the one-process ES256 signing key without Server hosting or external storage.
[<TestFixture>]
type CacheArtifactGrantSignerTests() =

    /// Confirms one signer publishes only its public key and issues a valid fixed-lifetime grant.
    [<Test>]
    member _.``process signer issues a locally verifiable exact grant``() =
        use signer = CacheArtifactGrantSigner.Create()
        let now = DateTimeOffset.Parse("2026-08-26T12:00:00Z")

        let artifact =
            DirectoryVersionZipCacheArtifact.Create(
                Guid.Parse("4cb5fa2c-a145-4c6b-98d7-ee2274230f3e"),
                Guid.Parse("70c90fec-e491-456a-a8e5-971db046ec17"),
                String.replicate 64 "a"
            )

        let issued = signer.Issue(artifact, now)

        Assert.Multiple(
            Action (fun () ->
                Assert.That(signer.ValidationKey.KeyId, Is.Not.Empty)
                Assert.That(signer.ValidationKey.Algorithm, Is.EqualTo(CacheArtifactGrantContract.Algorithm))
                Assert.That(signer.ValidationKey.PublicJwk.Kty, Is.EqualTo("EC"))
                Assert.That(signer.ValidationKey.PublicJwk.Crv, Is.EqualTo("P-256"))
                Assert.That(signer.ValidationKey.PublicJwk.X, Is.Not.Empty)
                Assert.That(signer.ValidationKey.PublicJwk.Y, Is.Not.Empty)

                Assert.That(
                    ArtifactGrant.validate now signer.ValidationKey (CacheArtifactGrantValidationRequest.Create artifact) issued.Grant,
                    Is.EqualTo<Result<unit, CacheArtifactGrantValidationError>>(Ok())
                )

                Assert.That(issued.ExpiresAt, Is.EqualTo(now.AddMinutes(5.0))))
        )
