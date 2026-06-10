namespace Grace.Server.Tests

open Grace.Actors
open Grace.Shared
open Grace.Shared.Services
open Grace.Types.Common
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic

[<Parallelizable(ParallelScope.All)>]
type PromotionSetDirectoryHashTests() =

    let ownerId = Guid.Parse("11111111-1111-1111-1111-111111111111")
    let organizationId = Guid.Parse("22222222-2222-2222-2222-222222222222")
    let repositoryId = Guid.Parse("33333333-3333-3333-3333-333333333333")
    let childDirectoryId = Guid.Parse("44444444-4444-4444-4444-444444444444")
    let lastWriteTimeUtc = DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc)

    let childSha256 = "27a1f6b711aa88117824253726920929081e2bc06e66b40bdd62f75c12d1c809"
    let rootFileSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"

    let rootFile = LocalFileVersion.Create (RelativePath "README.md") rootFileSha256 false 12L (Instant.FromUtc(2026, 6, 10, 12, 0)) true lastWriteTimeUtc

    [<Test>]
    member _.PromotionHashChildDirectoryPreservesComputedSizeInParentSha256Preimage() =
        let childMetadata: PromotionSet.ComputedDirectoryMetadata = { DirectoryVersionId = childDirectoryId; Sha256Hash = childSha256; Size = 27L }

        let childDirectory =
            PromotionSet.localDirectoryVersionForPromotionHash ownerId organizationId repositoryId (RelativePath "src") childMetadata lastWriteTimeUtc

        Assert.That(childDirectory.Size, Is.EqualTo(27L))

        let actualChildDirectories = List<LocalDirectoryVersion>()
        actualChildDirectories.Add(childDirectory)

        let rootFiles = List<LocalFileVersion>()
        rootFiles.Add(rootFile)

        let actual = Services.computeSha256ForDirectory (RelativePath ".") actualChildDirectories rootFiles

        let expected =
            Services.computeSha256ForDirectoryEntries
                (RelativePath ".")
                [
                    Services.DirectoryVersionPreimageEntry.Directory (RelativePath "src") 27L String.Empty childSha256
                    Services.DirectoryVersionPreimageEntry.File (RelativePath "README.md") 12L String.Empty rootFileSha256
                ]

        let zeroSizeRegression =
            Services.computeSha256ForDirectoryEntries
                (RelativePath ".")
                [
                    Services.DirectoryVersionPreimageEntry.Directory (RelativePath "src") 0L String.Empty childSha256
                    Services.DirectoryVersionPreimageEntry.File (RelativePath "README.md") 12L String.Empty rootFileSha256
                ]

        Assert.That(actual, Is.EqualTo(expected))
        Assert.That(actual, Is.Not.EqualTo(zeroSizeRegression))
