using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grace.Cache;
using NUnit.Framework;
using ArtifactGrantTypes = Grace.Types.ArtifactGrant;

namespace Grace.Cache.Tests;

/// <summary>Exercises local Cache admission and the single unknown-key refresh rule.</summary>
[TestFixture]
public sealed class CacheArtifactGrantValidatorTests
{
    /// <summary>Confirms a cached known key admits requests without a Server call and an unknown key refreshes once.</summary>
    [Test]
    public async Task KnownKeyIsLocalAndUnknownKeyRefreshesExactlyOnce()
    {
        var now = DateTimeOffset.Parse("2026-08-26T12:00:00Z");
        using var first = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var second = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var artifact = ArtifactGrantTypes.DirectoryVersionZipCacheArtifact.Create(
            Guid.Parse("4cb5fa2c-a145-4c6b-98d7-ee2274230f3e"),
            Guid.Parse("70c90fec-e491-456a-a8e5-971db046ec17"),
            new string('a', 64));
        var handler = new ValidationKeyHandler(("first", first), ("second", second));
        using var client = new HttpClient(handler);
        var validator = new CacheArtifactGrantValidator(new Uri("http://server/"), client, () => now);

        var firstGrant = CreateGrant("first", first, artifact, now);
        Assert.That((await validator.ValidateAsync($"Bearer {firstGrant}", artifact.Route)).IsOk, Is.True);
        Assert.That((await validator.ValidateAsync($"Bearer {firstGrant}", artifact.Route)).IsOk, Is.True);
        Assert.That(handler.RequestCount, Is.EqualTo(1));

        handler.UseSecond = true;
        var secondGrant = CreateGrant("second", second, artifact, now);
        Assert.That((await validator.ValidateAsync($"Bearer {secondGrant}", artifact.Route)).IsOk, Is.True);
        Assert.That(handler.RequestCount, Is.EqualTo(2));
    }

    internal static string CreateGrant(string keyId, ECDsa key, ArtifactGrantTypes.DirectoryVersionZipCacheArtifact artifact, DateTimeOffset now)
    {
        static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = keyId, typ = "JWT" }));
        var issuedAt = now.ToUnixTimeSeconds();
        var claims = Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = "Grace.Server.CacheArtifactGrant.v1", aud = "Grace.Cache.Artifact.v1", iat = issuedAt, nbf = issuedAt, exp = issuedAt + 300,
            artifactKind = "DirectoryVersionZip", repositoryId = artifact.RepositoryId, directoryVersionId = artifact.DirectoryVersionId,
            blake3Hash = artifact.Blake3Hash, method = "GET", route = artifact.Route,
        }));
        var input = $"{header}.{claims}";
        var signature = key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{input}.{Encode(signature)}";
    }
}

/// <summary>Publishes the selected public validation key and counts refreshes.</summary>
internal sealed class ValidationKeyHandler((string Id, ECDsa Key) first, (string Id, ECDsa Key) second) : HttpMessageHandler
{
    internal bool UseSecond { get; set; }
    internal int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        var selected = UseSecond ? second : first;
        var parameters = selected.Key.ExportParameters(false);
        static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var body = JsonSerializer.Serialize(new
        {
            returnValue = new
            {
                issuer = "Grace.Server.CacheArtifactGrant.v1", audience = "Grace.Cache.Artifact.v1", algorithm = "ES256", keyId = selected.Id,
                publicJwk = new { kty = "EC", crv = "P-256", x = Encode(parameters.Q.X!), y = Encode(parameters.Q.Y!) },
            },
            eventTime = "2026-08-26T12:00:00Z", correlationId = "grant-test", properties = new { },
        });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
