using System.Net;
using System.Text;
using Blake3;
using Grace.Cache;
using Grace.Cache.Storage;
using NUnit.Framework;

namespace Grace.Cache.Tests;

/// <summary>Exercises process-owned fill coalescing and distinct-fill capacity without a live Grace Server.</summary>
[TestFixture]
public sealed class CacheFillCoordinatorTests
{
    /// <summary>Confirms many exact callers share one redemption and one source download.</summary>
    [Test]
    public async Task ExactConcurrentFillsCoalesceToOneNetworkRetrieval()
    {
        using var fixture = FillCoordinatorFixture.Create(maxConcurrentFills: 4);
        var calls = Enumerable.Range(0, 200)
            .Select(_ => fixture.Coordinator.Fill(fixture.RepositoryId, fixture.DirectoryVersionId, "opaque-permit"))
            .ToArray();

        await fixture.Handler.SourceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Handler.ReleaseSource.TrySetResult();
        var results = await Task.WhenAll(calls);

        Assert.Multiple(() =>
        {
            Assert.That(results.All(result => result.IsOk), Is.True);
            Assert.That(fixture.Handler.RedemptionCount, Is.EqualTo(1));
            Assert.That(fixture.Handler.SourceCount, Is.EqualTo(1));
        });
    }

    /// <summary>Confirms a second distinct fill receives typed backpressure rather than entering a queue.</summary>
    [Test]
    public async Task DistinctFillBeyondCapacityReturnsTypedBackpressure()
    {
        using var fixture = FillCoordinatorFixture.Create(maxConcurrentFills: 1);
        var leader = fixture.Coordinator.Fill(fixture.RepositoryId, fixture.DirectoryVersionId, "opaque-permit");
        await fixture.Handler.SourceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var rejected = await fixture.Coordinator.Fill(
            "5019a12e-8ff2-4f2b-a14a-0400ba875f19",
            "f184f58f-f30e-4d42-886f-95383b63f952",
            "other-permit");

        Assert.That(rejected.IsError && rejected.ErrorValue.IsCapacityExceeded, Is.True);
        fixture.Handler.ReleaseSource.TrySetResult();
        Assert.That((await leader).IsOk, Is.True);
    }

    /// <summary>Confirms a canceled follower detaches while the process-owned exact fill continues to completion.</summary>
    [Test]
    public async Task CanceledFollowerDetachesWithoutCancelingSharedFill()
    {
        using var fixture = FillCoordinatorFixture.Create(maxConcurrentFills: 1);
        var leader = fixture.Coordinator.Fill(fixture.RepositoryId, fixture.DirectoryVersionId, "opaque-permit");
        await fixture.Handler.SourceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var follower = fixture.Coordinator.Fill(
            fixture.RepositoryId,
            fixture.DirectoryVersionId,
            "opaque-permit",
            cancellation.Token);

        cancellation.Cancel();
        Assert.ThrowsAsync<TaskCanceledException>(async () => await follower);
        fixture.Handler.ReleaseSource.TrySetResult();
        Assert.That((await leader).IsOk, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(fixture.Handler.RedemptionCount, Is.EqualTo(1));
            Assert.That(fixture.Handler.SourceCount, Is.EqualTo(1));
        });
    }
}

/// <summary>Owns one isolated writer store and deterministic fake Server/source transport.</summary>
internal sealed class FillCoordinatorFixture : IDisposable
{
    private readonly string root;
    private readonly CacheStore store;
    private readonly CacheProcessKey processKey;
    private readonly HttpClient client;

    private FillCoordinatorFixture(
        string root,
        CacheStore store,
        CacheProcessKey processKey,
        HttpClient client,
        FillHttpHandler handler,
        CacheFillCoordinator coordinator)
    {
        this.root = root;
        this.store = store;
        this.processKey = processKey;
        this.client = client;
        Handler = handler;
        Coordinator = coordinator;
    }

    internal string RepositoryId { get; } = "4cb5fa2c-a145-4c6b-98d7-ee2274230f3e";
    internal string DirectoryVersionId { get; } = "70c90fec-e491-456a-a8e5-971db046ec17";
    internal FillHttpHandler Handler { get; }
    internal CacheFillCoordinator Coordinator { get; }

    internal static FillCoordinatorFixture Create(int maxConcurrentFills)
    {
        var root = Path.Combine(Path.GetTempPath(), "grace-cache-fill-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "cache.db");
        var open = CacheStoreModule.openStore(databasePath);
        if (open is not CacheStoreOpenResult.Opened opened)
            throw new InvalidOperationException("The fill test store did not open.");

        var artifacts = CacheArtifactStoreModule.create(opened.store, Path.Combine(root, "managed"));
        var payload = "grace-cache-directory-version-zip\n"u8.ToArray();
        var handler = new FillHttpHandler(payload);
        var client = new HttpClient(handler);
        var key = CacheProcessKey.Create();
        var coordinator = new CacheFillCoordinator(artifacts, key, new Uri("http://server/"), client, maxConcurrentFills);
        return new FillCoordinatorFixture(root, opened.store, key, client, handler, coordinator);
    }

    public void Dispose()
    {
        ((IDisposable)Coordinator).Dispose();
        client.Dispose();
        ((IDisposable)processKey).Dispose();
        CacheStoreModule.disposeStore(store);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }
}

/// <summary>Returns one descriptor/source envelope and blocks its source until concurrency assertions are arranged.</summary>
internal sealed class FillHttpHandler(byte[] payload) : HttpMessageHandler
{
    private int redemptionCount;
    private int sourceCount;

    internal int RedemptionCount => redemptionCount;
    internal int SourceCount => sourceCount;
    internal TaskCompletionSource SourceStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ReleaseSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsolutePath == "/cache/redeemDirectoryVersionZipFill")
        {
            Interlocked.Increment(ref redemptionCount);
            var blake3 = Hasher.Hash(payload).ToString();
            var body = $$"""
                {
                  "returnValue": {
                    "artifact": {
                      "repositoryId": "4cb5fa2c-a145-4c6b-98d7-ee2274230f3e",
                      "directoryVersionId": "70c90fec-e491-456a-a8e5-971db046ec17",
                      "blake3Hash": "{{blake3}}"
                    },
                    "sourceUri": "http://source/artifact.zip",
                    "sourceExpiresAt": "2030-01-01T00:00:00Z"
                  },
                  "eventTime": "2030-01-01T00:00:00Z",
                  "correlationId": "fill-test",
                  "properties": {}
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        if (request.RequestUri?.Host == "source")
        {
            Interlocked.Increment(ref sourceCount);
            SourceStarted.TrySetResult();
            await ReleaseSource.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}
