namespace Grace.Server.Tests

open Grace.Actors.Extensions
open Grace.Server
open NUnit.Framework
open Orleans.Runtime
open System

module DiffActor = ActorProxy.Diff

[<Parallelizable(ParallelScope.All)>]
type DiffActorTests() =

    let first = Guid.Parse("15b50c95-7306-4ecb-9850-a0a5dc7419cf")
    let second = Guid.Parse("1e7b6f83-4715-42f8-ba0b-9b0262356f08")

    [<Test>]
    member _.GetPrimaryKeyUsesBlobSafeCompactSortedDirectoryVersionIds() =
        let primaryKey = DiffActor.GetPrimaryKey second first

        Assert.That(primaryKey, Is.EqualTo($"{first:N}{second:N}"))
        Assert.That(primaryKey, Does.Not.Contain("*"))
        Assert.That(primaryKey, Does.Not.Contain("-"))

    [<Test>]
    member _.ParsePrimaryKeyAcceptsCompactDirectoryVersionIds() =
        let directoryVersionId1, directoryVersionId2 = DiffActor.ParsePrimaryKey $"{first:N}{second:N}"

        Assert.That(directoryVersionId1, Is.EqualTo(first))
        Assert.That(directoryVersionId2, Is.EqualTo(second))

    [<Test>]
    member _.ParsePrimaryKeyAcceptsLegacyReminderDirectoryVersionIds() =
        let directoryVersionId1, directoryVersionId2 = DiffActor.ParsePrimaryKey $"{first:D}*{second:D}"

        Assert.That(directoryVersionId1, Is.EqualTo(first))
        Assert.That(directoryVersionId2, Is.EqualTo(second))

    [<Test>]
    member _.TryParsePrimaryKeyRejectsMalformedDiffReminderKeys() =
        Assert.That(DiffActor.TryParsePrimaryKey $"{first:N}*{second:N}", Is.EqualTo(None))
        Assert.That(DiffActor.TryParsePrimaryKey $"{first:D}{second:D}", Is.EqualTo(None))
        Assert.That(DiffActor.TryParsePrimaryKey "not-a-diff-key", Is.EqualTo(None))

    [<Test>]
    member _.DiffBlobStorageUsesBlobSafeNameAndRetainsLegacyCleanupCandidate() =
        let grainId = GrainId.Parse($"diffactor/{first:N}{second:N}")

        let candidates = DiffBlobGrainStorage.candidateBlobNames "Diff" grainId

        Assert.That(candidates[0], Does.StartWith("Diff-"))
        Assert.That(candidates[0], Does.EndWith(".json"))
        Assert.That(candidates[0], Does.Not.Contain("/"))
        Assert.That(candidates[1], Is.EqualTo($"Diff-diffactor/{first:N}{second:N}.json"))

    [<Test>]
    member _.DiffBlobStorageKeepsHashedReadEtagOnlyForHashedWrites() =
        let grainId = GrainId.Parse($"diffactor/{first:N}{second:N}")
        let hashedName = DiffBlobGrainStorage.safeBlobName "Diff" grainId
        let legacyName = DiffBlobGrainStorage.legacyBlobName "Diff" grainId

        Assert.That(DiffBlobGrainStorage.etagForWriteTarget hashedName hashedName "\"hashed-etag\"", Is.EqualTo("\"hashed-etag\""))

        Assert.That(DiffBlobGrainStorage.etagForWriteTarget legacyName hashedName "\"legacy-etag\"", Is.Null)
