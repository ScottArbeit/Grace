namespace Grace.Types.Tests

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Grace.Shared
open Grace.Types.ArtifactGrant
open NUnit.Framework

/// Exercises the fixed ES256 grant shape and exact DirectoryVersion ZIP request binding.
[<TestFixture>]
type CacheArtifactGrantTests() =

    let repositoryId = Guid.Parse("4cb5fa2c-a145-4c6b-98d7-ee2274230f3e")
    let directoryVersionId = Guid.Parse("70c90fec-e491-456a-a8e5-971db046ec17")
    let blake3Hash = String.replicate 64 "a"
    let issuedAt = DateTimeOffset.Parse("2026-08-26T12:00:00Z")

    /// Decodes one compact-token segment for direct wire-shape assertions.
    let decodeSegment (segment: string) =
        let padded =
            segment.Replace('-', '+').Replace('_', '/')
            + String.replicate ((4 - segment.Length % 4) % 4) "="

        Convert.FromBase64String(padded)

    /// Encodes one compact-token segment after deterministic byte substitution.
    let encodeSegment (bytes: byte array) =
        Convert
            .ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_')

    /// Compares one validation result without losing the F# result generic types in NUnit inference.
    let assertValidation (expected: Result<unit, CacheArtifactGrantValidationError>) (actual: Result<unit, CacheArtifactGrantValidationError>) =
        Assert.That(actual, Is.EqualTo(expected))

    /// Creates the exact supported ZIP artifact.
    let artifact () = DirectoryVersionZipCacheArtifact.Create(repositoryId, directoryVersionId, blake3Hash)

    /// Confirms issuance uses the fixed header, claims, route, and five-minute interval.
    [<Test>]
    member _.``issued grant has the fixed compact ES256 contract``() =
        use key = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        let request = CacheArtifactGrantIssueRequest.Create(artifact (), issuedAt)
        let issued = ArtifactGrant.issue "server-process-key" key request
        let parts = issued.Grant.Value.Split('.')

        Assert.That(parts, Has.Length.EqualTo(3))

        use header = JsonDocument.Parse(decodeSegment parts[0])
        use claims = JsonDocument.Parse(decodeSegment parts[1])

        Assert.Multiple(
            Action (fun () ->
                Assert.That(header.RootElement.GetProperty("alg").GetString(), Is.EqualTo("ES256"))
                Assert.That(header.RootElement.GetProperty("kid").GetString(), Is.EqualTo("server-process-key"))
                Assert.That(header.RootElement.GetProperty("typ").GetString(), Is.EqualTo("JWT"))
                Assert.That(claims.RootElement.GetProperty("iss").GetString(), Is.EqualTo("Grace.Server.CacheArtifactGrant.v1"))
                Assert.That(claims.RootElement.GetProperty("aud").GetString(), Is.EqualTo("Grace.Cache.Artifact.v1"))

                Assert.That(
                    claims
                        .RootElement
                        .GetProperty("artifactKind")
                        .GetString(),
                    Is.EqualTo("DirectoryVersionZip")
                )

                Assert.That(
                    claims
                        .RootElement
                        .GetProperty("repositoryId")
                        .GetString(),
                    Is.EqualTo(string repositoryId)
                )

                Assert.That(
                    claims
                        .RootElement
                        .GetProperty("directoryVersionId")
                        .GetString(),
                    Is.EqualTo(string directoryVersionId)
                )

                Assert.That(
                    claims
                        .RootElement
                        .GetProperty("blake3Hash")
                        .GetString(),
                    Is.EqualTo(blake3Hash)
                )

                Assert.That(
                    claims
                        .RootElement
                        .GetProperty("method")
                        .GetString(),
                    Is.EqualTo("GET")
                )

                Assert.That(
                    claims
                        .RootElement
                        .GetProperty("route")
                        .GetString(),
                    Is.EqualTo((artifact ()).Route)
                )

                Assert.That(
                    claims.RootElement.GetProperty("exp").GetInt64()
                    - claims.RootElement.GetProperty("iat").GetInt64(),
                    Is.EqualTo(300L)
                )

                Assert.That(issued.ExpiresAt, Is.EqualTo(issuedAt.AddMinutes(5.0))))
        )

    /// Confirms validation accepts only the exact request and does not extend the declared expiry.
    [<Test>]
    member _.``validation binds the exact request and rejects the expiry instant``() =
        use key = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        let artifact = artifact ()
        let issueRequest = CacheArtifactGrantIssueRequest.Create(artifact, issuedAt)
        let issued = ArtifactGrant.issue "server-process-key" key issueRequest
        let validationKey = ArtifactGrant.createValidationKey "server-process-key" key

        Assert.That(ArtifactGrant.tryReadArtifact issued.Grant, Is.EqualTo(Some artifact))

        let validate now request = ArtifactGrant.validate now validationKey request issued.Grant

        Assert.Multiple(
            Action (fun () ->
                assertValidation (Ok()) (validate issuedAt (CacheArtifactGrantValidationRequest.Create artifact))

                validate issued.ExpiresAt (CacheArtifactGrantValidationRequest.Create artifact)
                |> assertValidation (Error CacheArtifactGrantValidationError.Expired)

                validate issuedAt { CacheArtifactGrantValidationRequest.Create artifact with HttpMethod = "POST" }
                |> assertValidation (Error CacheArtifactGrantValidationError.WrongMethod)

                validate issuedAt { CacheArtifactGrantValidationRequest.Create artifact with Route = artifact.Route + "/wrong" }
                |> assertValidation (Error CacheArtifactGrantValidationError.WrongRoute)

                let otherGeneration = DirectoryVersionZipCacheArtifact.Create(repositoryId, directoryVersionId, String.replicate 64 "b")

                validate issuedAt (CacheArtifactGrantValidationRequest.Create otherGeneration)
                |> assertValidation (Error CacheArtifactGrantValidationError.WrongBlake3))
        )

    /// Confirms malformed artifact identities and token substitution fail closed.
    [<Test>]
    member _.``malformed artifact and substituted token are rejected``() =
        use key = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        let artifact = artifact ()
        let request = CacheArtifactGrantIssueRequest.Create(artifact, issuedAt)
        let issued = ArtifactGrant.issue "server-process-key" key request
        let validationKey = ArtifactGrant.createValidationKey "server-process-key" key

        let parts = issued.Grant.Value.Split('.')
        let substitutedSignature = decodeSegment parts[2]
        substitutedSignature[0] <- substitutedSignature[0] ^^^ 0x01uy
        let substituted = $"{parts[0]}.{parts[1]}.{encodeSegment substitutedSignature}"

        Assert.Multiple(
            Action (fun () ->
                Assert.Throws<ArgumentException>(
                    Action (fun () ->
                        DirectoryVersionZipCacheArtifact.Create(repositoryId, directoryVersionId, "ABC")
                        |> ignore)
                )
                |> ignore

                ArtifactGrant.validate issuedAt validationKey (CacheArtifactGrantValidationRequest.Create artifact) (CacheArtifactGrant.Create substituted)
                |> assertValidation (Error CacheArtifactGrantValidationError.InvalidSignature))
        )
