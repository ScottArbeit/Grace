namespace Grace.Server.Unit.Tests

open System
open System.Security.Cryptography
open System.Text
open Grace.Server
open Grace.Shared.Parameters.Cache
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
    let publicJwk (key: ECDsa) =
        let parameters = key.ExportParameters(false)
        { Kty = "EC"; Crv = "P-256"; X = encode parameters.Q.X; Y = encode parameters.Q.Y }

    /// Creates the exact immutable descriptor bound into each test permit.
    let descriptor =
        {
            RepositoryId = "4cb5fa2c-a145-4c6b-98d7-ee2274230f3e"
            DirectoryVersionId = "70c90fec-e491-456a-a8e5-971db046ec17"
            Kind = "DirectoryVersionZip"
            Sha256 = String.replicate 64 "a"
            Size = 34L
        }

    /// Confirms only the bound process key can redeem the unaltered permit before Server-clock expiry.
    [<Test>]
    member _.``permit accepts only the exact process key before expiry``() =
        use expectedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        use wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        let now = DateTimeOffset.UtcNow
        let permit = Cache.createPermitForTest "user-1" (publicJwk expectedKey) descriptor (now.AddSeconds(60.0))

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
