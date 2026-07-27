namespace Grace.Server.Tests

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

    /// Verifies cached changes round-trip without inventing a zero for missing or malformed values.
    [<Test>]
    member _.RecentResultRoundTripsAndMalformedValueIsMiss() =
        let change =
            { OperationId = "directory-version:1:add"; Operation = RepositoryContentCounterChangeOperation.Added; PreviousCount = 0L; CurrentCount = 1L }

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
