namespace Grace.Server.Tests

open Grace.Actors.Services
open Grace.Actors.Extensions
open Grace.Server
open Grace.Types.Common
open NUnit.Framework
open Orleans.Runtime
open System
open System.Collections.Generic

module DiffActor = ActorProxy.Diff

module DiffActorImplementation = Grace.Actors.Diff

/// Covers diff Actor behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type DiffActorTests() =

    let first = Guid.Parse("15b50c95-7306-4ecb-9850-a0a5dc7419cf")
    let second = Guid.Parse("1e7b6f83-4715-42f8-ba0b-9b0262356f08")

    /// Verifies that get Primary Key Uses Blob Safe Compact Sorted Directory Version Ids.
    [<Test>]
    member _.GetPrimaryKeyUsesBlobSafeCompactSortedDirectoryVersionIds() =
        let primaryKey = DiffActor.GetPrimaryKey second first

        Assert.That(primaryKey, Is.EqualTo($"{first:N}{second:N}"))
        Assert.That(primaryKey, Does.Not.Contain("*"))
        Assert.That(primaryKey, Does.Not.Contain("-"))

    /// Verifies that parse Primary Key Accepts Compact Directory Version Ids.
    [<Test>]
    member _.ParsePrimaryKeyAcceptsCompactDirectoryVersionIds() =
        let directoryVersionId1, directoryVersionId2 = DiffActor.ParsePrimaryKey $"{first:N}{second:N}"

        Assert.That(directoryVersionId1, Is.EqualTo(first))
        Assert.That(directoryVersionId2, Is.EqualTo(second))

    /// Verifies that parse Primary Key Accepts Legacy Reminder Directory Version Ids.
    [<Test>]
    member _.ParsePrimaryKeyAcceptsLegacyReminderDirectoryVersionIds() =
        let directoryVersionId1, directoryVersionId2 = DiffActor.ParsePrimaryKey $"{first:D}*{second:D}"

        Assert.That(directoryVersionId1, Is.EqualTo(first))
        Assert.That(directoryVersionId2, Is.EqualTo(second))

    /// Verifies that try Parse Primary Key Rejects Malformed Diff Reminder Keys.
    [<Test>]
    member _.TryParsePrimaryKeyRejectsMalformedDiffReminderKeys() =
        Assert.That(DiffActor.TryParsePrimaryKey $"{first:N}*{second:N}", Is.EqualTo(None))
        Assert.That(DiffActor.TryParsePrimaryKey $"{first:D}{second:D}", Is.EqualTo(None))
        Assert.That(DiffActor.TryParsePrimaryKey "not-a-diff-key", Is.EqualTo(None))

    /// Verifies that diff Blob Storage Uses Blob Safe Name And Retains Legacy Cleanup Candidate.
    [<Test>]
    member _.DiffBlobStorageUsesBlobSafeNameAndRetainsLegacyCleanupCandidate() =
        let grainId = GrainId.Parse($"diffactor/{first:N}{second:N}")

        let candidates = DiffBlobGrainStorage.candidateBlobNames "Diff" grainId

        Assert.That(candidates[0], Does.StartWith("Diff-"))
        Assert.That(candidates[0], Does.EndWith(".json"))
        Assert.That(candidates[0], Does.Not.Contain("/"))
        Assert.That(candidates[1], Is.EqualTo($"Diff-diffactor/{first:N}{second:N}.json"))

    /// Verifies that diff Blob Storage Keeps Hashed Read Etag Only For Hashed Writes.
    [<Test>]
    member _.DiffBlobStorageKeepsHashedReadEtagOnlyForHashedWrites() =
        let grainId = GrainId.Parse($"diffactor/{first:N}{second:N}")
        let hashedName = DiffBlobGrainStorage.safeBlobName "Diff" grainId
        let legacyName = DiffBlobGrainStorage.legacyBlobName "Diff" grainId

        Assert.That(DiffBlobGrainStorage.etagForWriteTarget hashedName hashedName "\"hashed-etag\"", Is.EqualTo("\"hashed-etag\""))

        Assert.That(DiffBlobGrainStorage.etagForWriteTarget legacyName hashedName "\"legacy-etag\"", Is.Null)

    /// Verifies that scan For Differences Treats Same Sha256 Different Blake3 File As Change.
    [<Test>]
    member _.ScanForDifferencesTreatsSameSha256DifferentBlake3FileAsChange() =
        task {
            let repositoryId = Guid.Parse("62a0b8ef-f3a8-4a99-acb9-9c24c5a2c202")
            let relativePath = RelativePath "/src/same-sha.txt"
            let sharedSha256 = Sha256Hash "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            let olderBlake3 = Blake3Hash "1111111111111111111111111111111111111111111111111111111111111111"
            let newerBlake3 = Blake3Hash "2222222222222222222222222222222222222222222222222222222222222222"

            let olderFile = FileVersion.CreateWithHashes relativePath sharedSha256 olderBlake3 String.Empty false 12L
            let newerFile = FileVersion.CreateWithHashes relativePath sharedSha256 newerBlake3 String.Empty false 12L

            let olderDirectory =
                DirectoryVersion.CreateWithHashes
                    first
                    Guid.Empty
                    Guid.Empty
                    repositoryId
                    "/src/"
                    (Sha256Hash "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
                    (Blake3Hash "3333333333333333333333333333333333333333333333333333333333333333")
                    (List<DirectoryVersionId>())
                    (List<FileVersion>([ olderFile ]))
                    olderFile.Size

            let newerDirectory =
                DirectoryVersion.CreateWithHashes
                    second
                    Guid.Empty
                    Guid.Empty
                    repositoryId
                    "/src/"
                    (Sha256Hash "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
                    (Blake3Hash "4444444444444444444444444444444444444444444444444444444444444444")
                    (List<DirectoryVersionId>())
                    (List<FileVersion>([ newerFile ]))
                    newerFile.Size

            let olderIndex = ServerGraceIndex()
            olderIndex.Add(olderDirectory.RelativePath, olderDirectory)

            let newerIndex = ServerGraceIndex()
            newerIndex.Add(newerDirectory.RelativePath, newerDirectory)

            let! differences = DiffActorImplementation.scanForDifferences newerIndex olderIndex

            Assert.That(differences, Has.Count.EqualTo(2))

            Assert.That(
                differences,
                Has
                    .Exactly(1)
                    .Matches<FileSystemDifference>(fun difference ->
                        difference.FileSystemEntryType = FileSystemEntryType.File
                        && difference.DifferenceType = DifferenceType.Change
                        && difference.RelativePath = relativePath)
            )
        }
