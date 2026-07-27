namespace Grace.Server.Tests

open Grace.Actors
open Grace.Server.RepositoryCounterRecentResult
open Grace.Shared
open Grace.Types.Common
open Grace.Types.RepositoryContentCounter
open NUnit.Framework
open System
open System.Threading

/// Covers the provider-neutral Redis recent-result wire contract without starting Redis.
[<Parallelizable(ParallelScope.All)>]
type RepositoryCounterRecentResultTests() =

    /// Verifies recent results use the accepted exact ten-minute expiry.
    [<Test>]
    member _.RecentResultExpiryIsExactlyTenMinutes() = Assert.That(expiry, Is.EqualTo(TimeSpan.FromMinutes 10.0))

    /// Verifies the one-time Redis handshake remains bounded but is not limited to a command-sized CI window.
    [<Test>]
    member _.InitialRedisConnectionAllowsBoundedContainerHandshake() = Assert.That(connectionTimeout, Is.EqualTo(TimeSpan.FromSeconds 10.0))

    /// Verifies a disconnected lazy multiplexer receives the full readiness window observed missing in CI before bounded commands begin.
    [<Test>]
    member _.DisconnectedInitialMultiplexerUsesReadinessProbeConfiguration() =
        let configuration = configurationForEndpoint "127.0.0.1" 6379

        Assert.That(configuration.AbortOnConnectFail, Is.False)
        Assert.That(configuration.ConnectTimeout, Is.EqualTo(int connectionTimeout.TotalMilliseconds))
        Assert.That(configuration.AsyncTimeout, Is.EqualTo(int connectionTimeout.TotalMilliseconds))
        Assert.That(requiresReadinessProbe false, Is.True)
        Assert.That(requiresReadinessProbe true, Is.False)

    /// Verifies cached changes round-trip without inventing a zero for missing or malformed values.
    [<Test>]
    member _.RecentResultRoundTripsAndMalformedValueIsMiss() =
        let change =
            {
                OperationId = "directory-version:1:add"
                Operation = RepositoryContentCounterChangeOperation.Added
                PreviousCount = 0L
                CurrentCount = 1L
                Revision = 1L
            }

        Assert.That(tryDeserialize (serialize change), Is.EqualTo(Some change))
        Assert.That(tryDeserialize "not-a-result", Is.EqualTo(None))
        Assert.That(tryDeserialize String.Empty, Is.EqualTo(None))

    /// Verifies cache keys partition identical manifest text by repository and StoragePool.
    [<Test>]
    member _.RecentResultKeyUsesExactRepositoryManifestDimensions() =
        let repositoryId = Guid.Parse("75ce5e36-25f6-4da0-afdd-ad4ad56540d5")
        let otherRepositoryId = Guid.Parse("41ff01d0-8f4c-41e7-875d-1c4f7b519c11")
        let operationId = "directory-version:1:add"
        let main = key repositoryId (StoragePoolId "pool-main") "manifest:alpha" operationId
        let otherPool = key repositoryId (StoragePoolId "pool-archive") "manifest:alpha" operationId
        let otherRepository = key otherRepositoryId (StoragePoolId "pool-main") "manifest:alpha" operationId

        Assert.That(otherPool, Is.Not.EqualTo(main))
        Assert.That(otherRepository, Is.Not.EqualTo(main))

    /// Verifies missing Redis is represented as unknown rather than a synthetic zero result.
    [<Test>]
    member _.UnavailableRedisReturnsMiss() =
        task {
            let recent = UnavailableRepositoryCounterRecentResult() :> IRepositoryCounterRecentResult

            let! result =
                recent.TryGetAsync(
                    Guid.Parse("75ce5e36-25f6-4da0-afdd-ad4ad56540d5"),
                    StoragePoolId "pool-main",
                    "manifest:alpha",
                    "directory-version:1:add",
                    CancellationToken.None
                )

            Assert.That(result, Is.EqualTo(None))
        }

    /// Verifies absent Redis reports an unconfirmed write so removal work can pause safely.
    [<Test>]
    member _.UnavailableRedisDoesNotConfirmWrite() =
        task {
            let recent = UnavailableRepositoryCounterRecentResult() :> IRepositoryCounterRecentResult

            let change =
                {
                    OperationId = "directory-version:1:remove"
                    Operation = RepositoryContentCounterChangeOperation.Removed
                    PreviousCount = 1L
                    CurrentCount = 0L
                    Revision = 2L
                }

            let! confirmed =
                recent.TrySetAsync(
                    Guid.Parse("75ce5e36-25f6-4da0-afdd-ad4ad56540d5"),
                    StoragePoolId "pool-main",
                    "manifest:alpha",
                    change,
                    CancellationToken.None
                )

            Assert.That(confirmed, Is.False)
        }
