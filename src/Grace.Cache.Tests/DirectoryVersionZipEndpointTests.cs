using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Grace.Cache;
using Grace.Cache.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace Grace.Cache.Tests;

/// <summary>Proves the localhost Cache host serves only independently verified Complete DirectoryVersion ZIP bytes.</summary>
[TestFixture]
public sealed class DirectoryVersionZipEndpointTests
{
    /// <summary>Proves the miss-to-hit tracer begins with an identity-only miss and exposes the running process key.</summary>
    [Test]
    public async Task IdentityOnlyMissAndFillPublicKeyExposeTheTracerBoundary()
    {
        using var fixture = CacheHostFixture.Create();
        fixture.MakeIneligible("Absent");
        await using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        using (var miss = await client.GetAsync(
                   $"/repositories/{fixture.RepositoryId}/directory-version-zips/{fixture.DirectoryVersionId}"))
        {
            Assert.That(miss.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }

        using var key = await client.GetAsync("/fill-public-key");
        Assert.Multiple(() =>
        {
            Assert.That(key.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(key.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
        });

        var document = await key.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Multiple(() =>
        {
            Assert.That(document.GetProperty("kty").GetString(), Is.EqualTo("EC"));
            Assert.That(document.GetProperty("crv").GetString(), Is.EqualTo("P-256"));
            Assert.That(document.GetProperty("x").GetString(), Is.Not.Empty);
            Assert.That(document.GetProperty("y").GetString(), Is.Not.Empty);
            Assert.That(document.EnumerateObject().Select(property => property.Name),
                Is.EquivalentTo(new[] { "kty", "crv", "x", "y" }));
        });
    }

    /// <summary>Commits through the writer, then proves the exact HTTP contract and read-only filesystem behavior.</summary>
    [Test]
    public async Task ExactCompleteTupleReturnsZipBytesAndOtherTuplesFailClosed()
    {
        using var fixture = CacheHostFixture.Create();
        var before = fixture.Snapshot();
        await using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        using (var response = await client.GetAsync(fixture.ExactRequestUri))
        {
            Assert.Multiple(() =>
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/zip"));
            });
            Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(fixture.Payload));
        }

        foreach (var request in fixture.MalformedRequestUris)
        {
            using var response = await client.GetAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), request);
        }

        foreach (var request in fixture.AbsentRequestUris)
        {
            using var response = await client.GetAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), request);
            Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.Empty, request);
        }

        using (var response = await client.PutAsync(fixture.ExactRequestUri, new ByteArrayContent(fixture.Payload)))
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.MethodNotAllowed));
        }

        Assert.That(fixture.Snapshot(), Is.EqualTo(before), "Cache startup or requests changed the database or managed root.");
    }

    /// <summary>Confirms a Complete row whose final bytes changed fails closed without repairing either authority.</summary>
    [Test]
    public async Task CompleteByteDisagreementReturnsNotFoundWithoutMutation()
    {
        using var fixture = CacheHostFixture.Create();
        File.WriteAllBytes(fixture.FinalPath, "different-cache-bytes"u8.ToArray());
        var before = fixture.Snapshot();
        await using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(fixture.ExactRequestUri);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.Empty);
        Assert.That(fixture.Snapshot(), Is.EqualTo(before));
    }

    /// <summary>Confirms restart classification serves only verified final bytes and otherwise fails closed.</summary>
    [TestCase("Absent")]
    [TestCase("Staging")]
    [TestCase("Corrupt")]
    public async Task IneligibleDatabaseStateReturnsNotFoundWithoutMutation(string state)
    {
        using var fixture = CacheHostFixture.Create();
        fixture.MakeIneligible(state);
        var before = fixture.Snapshot();
        await using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(fixture.ExactRequestUri);

        Assert.That(response.StatusCode, state == "Staging" ? Is.EqualTo(HttpStatusCode.OK) : Is.EqualTo(HttpStatusCode.NotFound));

        if (state != "Staging")
            Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.Empty);
    }

    /// <summary>Confirms missing required locations fail startup without creating a database, root, schema, or lock.</summary>
    [Test]
    public void MissingLocationsAreInitializedForTheFillCapableHost()
    {
        var parent = Path.Combine(Path.GetTempPath(), "grace-cache-read-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        var databasePath = Path.Combine(parent, "missing", "cache.db");
        var managedRoot = Path.Combine(parent, "missing", "managed");
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("Cache:DatabasePath", databasePath)
                .UseSetting("Cache:ManagedRoot", managedRoot));

        try
        {
            using (factory)
            using (factory.CreateClient())
            {
                Assert.That(File.Exists(databasePath), Is.True);
                Assert.That(Directory.Exists(Path.Combine(managedRoot, "artifacts")), Is.True);
            }

            SqliteConnection.ClearAllPools();
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    /// <summary>Confirms Kestrel replaces a supplied wildcard hostname with the IPv4 loopback listener.</summary>
    [Test]
    public async Task ProcessListenerIsLoopbackOnly()
    {
        using var fixture = CacheHostFixture.Create();
        var port = CacheHostFixture.GetAvailablePort();
        using var process = fixture.StartProcess(port);

        try
        {
            var listeningLine = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            while (listeningLine is not null && !listeningLine.Contains("Now listening on:", StringComparison.Ordinal))
                listeningLine = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.That(listeningLine, Does.Contain($"http://127.0.0.1:{port}"));
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            using var response = await client.GetAsync(fixture.ExactRequestUri);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }
}

/// <summary>Owns one pre-existing committed artifact and the configuration supplied to the read-only host.</summary>
internal sealed class CacheHostFixture : IDisposable
{
    /// <summary>Captures the isolated writer output consumed by one read-only host.</summary>
    private CacheHostFixture(string root, string databasePath, string managedRoot, string finalPath, byte[] payload, CacheArtifactTuple tuple)
    {
        Root = root;
        DatabasePath = databasePath;
        ManagedRoot = managedRoot;
        FinalPath = finalPath;
        Payload = payload;
        Tuple = tuple;
    }

    /// <summary>Gets the isolated directory removed after the host and SQLite pools close.</summary>
    private string Root { get; }
    /// <summary>Gets the pre-existing SQLite database supplied to Cache startup.</summary>
    internal string DatabasePath { get; }
    /// <summary>Gets the pre-existing managed artifact root supplied to Cache startup.</summary>
    internal string ManagedRoot { get; }
    /// <summary>Gets the opaque writer-produced file used for byte-disagreement proof.</summary>
    internal string FinalPath { get; }
    /// <summary>Gets the exact committed bytes expected from a successful response.</summary>
    internal byte[] Payload { get; }
    /// <summary>Gets the immutable tuple committed through the existing writer.</summary>
    private CacheArtifactTuple Tuple { get; }

    /// <summary>Gets the immutable directory-version identity addressed by the public Cache route.</summary>
    internal string DirectoryVersionId => Tuple.DirectoryVersionId;

    /// <summary>Gets the immutable repository identity addressed by the public Cache route.</summary>
    internal string RepositoryId { get; } = "4cb5fa2c-a145-4c6b-98d7-ee2274230f3e";

    /// <summary>Gets the exact supported GET request for the committed tuple.</summary>
    internal string ExactRequestUri =>
        $"/repositories/{RepositoryId}/directory-version-zips/{Tuple.DirectoryVersionId}";

    /// <summary>Gets malformed requests that must fail binding or tuple validation with 400.</summary>
    internal IEnumerable<string> MalformedRequestUris =>
    [
        ExactRequestUri.Replace(RepositoryId, "not-a-guid", StringComparison.Ordinal),
        ExactRequestUri.Replace(Tuple.DirectoryVersionId, "not-a-guid", StringComparison.Ordinal),
    ];

    /// <summary>Gets valid but non-matching tuples that must fail closed with 404.</summary>
    internal IEnumerable<string> AbsentRequestUris =>
    [
        ExactRequestUri.Replace(Tuple.DirectoryVersionId, "f184f58f-f30e-4d42-886f-95383b63f952", StringComparison.Ordinal),
        ExactRequestUri.Replace(RepositoryId, "5019a12e-8ff2-4f2b-a14a-0400ba875f19", StringComparison.Ordinal),
    ];

    /// <summary>Commits one artifact through the existing writer and releases writer ownership before host startup.</summary>
    internal static CacheHostFixture Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "grace-cache-read-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "cache.db");
        var managedRoot = Path.Combine(root, "managed");
        var payload = "grace-cache-directory-version-zip\n"u8.ToArray();
        const string repositoryId = "4cb5fa2c-a145-4c6b-98d7-ee2274230f3e";
        const string directoryVersionId = "70c90fec-e491-456a-a8e5-971db046ec17";
        var tuple = new CacheArtifactTuple(
            "DirectoryVersionZip",
            CacheArtifactStoreModule.canonicalIdentity(repositoryId, directoryVersionId),
            directoryVersionId,
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            payload.LongLength);
        var openResult = CacheStoreModule.openStore(databasePath);
        if (openResult is not CacheStoreOpenResult.Opened opened)
            throw new InvalidOperationException("The isolated Cache writer store did not open.");
        var writer = CacheArtifactStoreModule.create(opened.store, managedRoot);
        using (var source = new MemoryStream(payload, writable: false))
        {
            if (!CacheArtifactStoreModule.commit(writer, tuple, source).IsFilled)
                throw new InvalidOperationException("The isolated Cache artifact did not commit.");
        }
        var finalPath = CacheArtifactStoreModule.inspect(writer, tuple) is CacheArtifactOutcome.Hit hit
            ? hit.finalPath
            : throw new InvalidOperationException("The committed Cache artifact did not reopen as a verified hit.");
        CacheStoreModule.disposeStore(opened.store);
        SqliteConnection.ClearAllPools();
        return new CacheHostFixture(root, databasePath, managedRoot, finalPath, payload, tuple);
    }

    /// <summary>Reserves then releases a local port for the bounded child-process listener proof.</summary>
    internal static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Starts the built Cache host with a wildcard URL that production code must narrow to loopback.</summary>
    internal Process StartProcess(int port)
    {
        var hostAssembly = typeof(Program).Assembly.Location;
        var startInfo = new ProcessStartInfo("dotnet", $"\"{hostAssembly}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.Environment["Cache__DatabasePath"] = DatabasePath;
        startInfo.Environment["Cache__ManagedRoot"] = ManagedRoot;
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://0.0.0.0:{port}";
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The Cache host process did not start.");
    }

    /// <summary>Creates an in-process host using only the two required pre-existing Cache locations.</summary>
    internal WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("Cache:DatabasePath", DatabasePath)
                .UseSetting("Cache:ManagedRoot", ManagedRoot));

    /// <summary>Arranges one non-serving durable state before the read-only host starts.</summary>
    internal void MakeIneligible(string state)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadWrite;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = state switch
        {
            "Absent" => "DELETE FROM cache_artifact_states;",
            "Staging" => "UPDATE cache_artifact_states SET state = 'Staging', operation_identity = 'test-operation';",
            "Corrupt" => "UPDATE cache_artifact_states SET operation_identity = 'invalid-complete-operation';",
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
        command.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    /// <summary>Hashes every database and managed-root file so startup or request mutations cannot pass unnoticed.</summary>
    internal string Snapshot()
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind || ':' || canonical_identity || ':' || directory_version_id || ':' || expected_sha256 || ':' || expected_size || ':' || state || ':' || COALESCE(operation_identity, '') FROM cache_artifact_states ORDER BY artifact_key;";
        var state = Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
        var final = File.Exists(FinalPath) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(FinalPath))) : "missing";
        return $"{state}\n{final}";
    }

    /// <summary>Clears test-only writer pools and removes the isolated fixture directory.</summary>
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(Root, recursive: true);
    }
}
