namespace Grace.Server.Tests

open Grace.Actors
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Reference
open Grace.Types.Common
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.Threading.Tasks

[<Parallelizable(ParallelScope.All)>]
type ServicesEffectivePromotionTests() =

    let createPromotionReference createdAt isDeleted hasTerminalLink =
        let links: ReferenceLinkType seq =
            if hasTerminalLink then
                [
                    ReferenceLinkType.PromotionSetTerminal(Guid.NewGuid())
                ]
            else
                [
                    ReferenceLinkType.IncludedInPromotionSet(Guid.NewGuid())
                ]

        { ReferenceDto.Default with
            ReferenceId = Guid.NewGuid()
            BranchId = Guid.NewGuid()
            DirectoryId = Guid.NewGuid()
            ReferenceType = ReferenceType.Promotion
            CreatedAt = createdAt
            Links = links
            DeletedAt = if isDeleted then Some(createdAt + Duration.FromMinutes(1.0)) else Option.None
        }

    let ownerId = Guid.Parse("11111111-cccc-4444-8888-111111111111")
    let organizationId = Guid.Parse("22222222-cccc-4444-8888-222222222222")
    let repositoryId = Guid.Parse("33333333-cccc-4444-8888-333333333333")
    let branchId = Guid.Parse("44444444-cccc-4444-8888-444444444444")
    let directoryVersionId = Guid.Parse("55555555-cccc-4444-8888-555555555555")
    let referenceId = Guid.Parse("66666666-cccc-4444-8888-666666666666")
    let fullSha256Hash = Sha256Hash "abcdef0123456789"
    let prefixSha256Hash = Sha256Hash "abcdef"
    let blake3Hash = Blake3Hash "root-blake3"
    let correlationId = "services-reference-projection-tests"

    let rootDirectoryVersionWithHashes sha256Hash blake3Hash =
        DirectoryVersion.CreateWithHashes
            directoryVersionId
            ownerId
            organizationId
            repositoryId
            (RelativePath ".")
            sha256Hash
            blake3Hash
            (List<DirectoryVersionId>())
            (List<FileVersion>())
            0L

    let childDirectoryVersionWithHashes sha256Hash blake3Hash =
        DirectoryVersion.CreateWithHashes
            directoryVersionId
            ownerId
            organizationId
            repositoryId
            (RelativePath "child")
            sha256Hash
            blake3Hash
            (List<DirectoryVersionId>())
            (List<FileVersion>())
            0L

    let legacyCreatedReferenceEvent storedSha256Hash =
        {
            Event =
                ReferenceEventType.Created(
                    referenceId,
                    ownerId,
                    organizationId,
                    repositoryId,
                    branchId,
                    directoryVersionId,
                    storedSha256Hash,
                    Blake3Hash String.Empty,
                    ReferenceType.Commit,
                    ReferenceText "legacy projection commit",
                    Seq.empty
                )
            Metadata =
                {
                    Timestamp = Instant.FromUtc(2026, 6, 11, 9, 0)
                    CorrelationId = correlationId
                    Principal = "services-projection-test"
                    ClientType = Option.None
                    Properties = Dictionary<string, string>()
                }
        }

    [<Test>]
    member _.LatestEffectivePromotionIgnoresDeletedTerminalReferences() =
        let createdAt = Instant.FromUtc(2026, 2, 21, 10, 0)

        let deletedLatestTerminal = createPromotionReference (createdAt + Duration.FromMinutes(3.0)) true true
        let latestNonTerminal = createPromotionReference (createdAt + Duration.FromMinutes(2.0)) false false
        let previousTerminal = createPromotionReference (createdAt + Duration.FromMinutes(1.0)) false true

        let selected =
            Services.tryGetLatestEffectivePromotionReference (
                [
                    deletedLatestTerminal
                    latestNonTerminal
                    previousTerminal
                ]
            )

        Assert.That(selected, Is.EqualTo(Some previousTerminal))

    [<Test>]
    member _.LatestEffectivePromotionRequiresTerminalLink() =
        let createdAt = Instant.FromUtc(2026, 2, 21, 12, 0)
        let nonTerminal = createPromotionReference createdAt false false

        let selected = Services.tryGetLatestEffectivePromotionReference ([ nonTerminal ])

        Assert.That(selected, Is.EqualTo(None))

    [<Test>]
    member _.LatestReferenceSelectionIgnoresDeletedReferences() =
        let createdAt = Instant.FromUtc(2026, 2, 21, 14, 0)
        let deletedLatest = createPromotionReference (createdAt + Duration.FromMinutes(2.0)) true true
        let previousActive = createPromotionReference (createdAt + Duration.FromMinutes(1.0)) false false

        let selected = Services.tryGetLatestNotDeletedReference ([ deletedLatest; previousActive ])

        Assert.That(selected, Is.EqualTo(Some previousActive))

    [<Test>]
    member _.DirectReferenceProjectionHydratesLegacyEmptyBlake3FromRootShaPrefix() =
        task {
            let rootDirectoryVersion = rootDirectoryVersionWithHashes fullSha256Hash blake3Hash

            let getDirectoryVersion
                (requestedRepositoryId: RepositoryId)
                (requestedDirectoryVersionId: DirectoryVersionId)
                (requestedCorrelationId: CorrelationId)
                =
                Assert.That(requestedRepositoryId, Is.EqualTo(repositoryId))
                Assert.That(requestedDirectoryVersionId, Is.EqualTo(directoryVersionId))
                Assert.That(requestedCorrelationId, Is.EqualTo(correlationId))
                Task.FromResult(Some rootDirectoryVersion)

            let! referenceDto =
                Services.projectReferenceEventsWithLegacyBlake3Hydration
                    getDirectoryVersion
                    correlationId
                    [|
                        legacyCreatedReferenceEvent prefixSha256Hash
                    |]

            Assert.That(referenceDto.Sha256Hash, Is.EqualTo(fullSha256Hash))
            Assert.That(referenceDto.Blake3Hash, Is.EqualTo(blake3Hash))
        }

    [<Test>]
    member _.DirectReferenceProjectionLeavesLegacyEmptyBlake3ForNonRootOrWrongShaPrefix() =
        task {
            let getChildDirectoryVersion _ _ _ = Task.FromResult(Some(childDirectoryVersionWithHashes fullSha256Hash blake3Hash))

            let! nonRootReferenceDto =
                Services.projectReferenceEventsWithLegacyBlake3Hydration
                    getChildDirectoryVersion
                    correlationId
                    [|
                        legacyCreatedReferenceEvent prefixSha256Hash
                    |]

            let getRootDirectoryVersion _ _ _ = Task.FromResult(Some(rootDirectoryVersionWithHashes fullSha256Hash blake3Hash))

            let! wrongPrefixReferenceDto =
                Services.projectReferenceEventsWithLegacyBlake3Hydration
                    getRootDirectoryVersion
                    correlationId
                    [|
                        legacyCreatedReferenceEvent (Sha256Hash "123456")
                    |]

            Assert.That(nonRootReferenceDto.Blake3Hash, Is.EqualTo(Blake3Hash String.Empty))
            Assert.That(wrongPrefixReferenceDto.Blake3Hash, Is.EqualTo(Blake3Hash String.Empty))
        }
