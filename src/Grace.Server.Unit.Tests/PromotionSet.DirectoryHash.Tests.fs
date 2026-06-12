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
    let childBlake3 = "6f2f7d3de9b92f1f2df7fddf7e7b7b48ab92a0b4e40bb87c13ebac932f8b9e4a"
    let rootFileSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
    let rootFileBlake3 = "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adcd1e8c76d9a8885f16a39f"

    let rootFile =
        LocalFileVersion.CreateWithHashes
            (RelativePath "README.md")
            rootFileSha256
            rootFileBlake3
            false
            12L
            (Instant.FromUtc(2026, 6, 10, 12, 0))
            true
            lastWriteTimeUtc

    [<Test>]
    member _.PromotionHashChildDirectoryPreservesComputedSizeAndBothHashesInParentPreimage() =
        let childMetadata: PromotionSet.ComputedDirectoryMetadata =
            { DirectoryVersionId = childDirectoryId; Sha256Hash = childSha256; Blake3Hash = childBlake3; Size = 27L }

        let childDirectory =
            PromotionSet.localDirectoryVersionForPromotionHash ownerId organizationId repositoryId (RelativePath "src") childMetadata lastWriteTimeUtc

        Assert.That(childDirectory.Size, Is.EqualTo(27L))
        Assert.That(childDirectory.Blake3Hash, Is.EqualTo(childBlake3))

        let actualChildDirectories = List<LocalDirectoryVersion>()
        actualChildDirectories.Add(childDirectory)

        let rootFiles = List<LocalFileVersion>()
        rootFiles.Add(rootFile)

        let actualEntries =
            [
                Services.DirectoryVersionPreimageEntry.Directory (RelativePath "src") 27L childBlake3 childSha256
                Services.DirectoryVersionPreimageEntry.File (RelativePath "README.md") 12L rootFileBlake3 rootFileSha256
            ]

        let actualSha256 = Services.computeSha256ForDirectoryEntries (RelativePath ".") actualEntries
        let actualBlake3 = Services.computeBlake3ForDirectory (RelativePath ".") actualEntries

        let expected = Services.computeSha256ForDirectoryEntries (RelativePath ".") actualEntries

        let expectedBlake3 = Services.computeBlake3ForDirectory (RelativePath ".") actualEntries

        let zeroSizeRegression =
            Services.computeSha256ForDirectoryEntries
                (RelativePath ".")
                [
                    Services.DirectoryVersionPreimageEntry.Directory (RelativePath "src") 0L childBlake3 childSha256
                    Services.DirectoryVersionPreimageEntry.File (RelativePath "README.md") 12L rootFileBlake3 rootFileSha256
                ]

        let shaOnlyBlake3Regression =
            Services.computeBlake3ForDirectory
                (RelativePath ".")
                [
                    Services.DirectoryVersionPreimageEntry.Directory (RelativePath "src") 27L String.Empty childSha256
                    Services.DirectoryVersionPreimageEntry.File (RelativePath "README.md") 12L String.Empty rootFileSha256
                ]

        Assert.That(actualSha256, Is.EqualTo(expected))
        Assert.That(actualBlake3, Is.EqualTo(expectedBlake3))
        Assert.That(actualSha256, Is.Not.EqualTo(zeroSizeRegression))
        Assert.That(actualBlake3, Is.Not.EqualTo(shaOnlyBlake3Regression))
