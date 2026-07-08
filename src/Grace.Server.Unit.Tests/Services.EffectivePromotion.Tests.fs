namespace Grace.Server.Tests

open Grace.Actors
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Reference
open Grace.Types.Common
open Grace.Types.Visibility
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.Threading.Tasks

/// Covers services Effective Promotion behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type ServicesEffectivePromotionTests() =

    /// Constructs promotion Reference fixtures used by the server unit services Effective Promotion assertions.
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

    /// Constructs terminal promotion Reference fixtures with durable visibility facts for actor authority assertions.
    let createVisiblePromotionReference createdAt visibility ownership creatorUserId =
        { createPromotionReference createdAt false true with Visibility = visibility; Ownership = ownership; CreatorUserId = creatorUserId }

    /// Constructs PromotionSet fixtures with durable visibility facts for actor authority assertions.
    let promotionSet visibility ownership creatorUserId =
        { Grace.Types.PromotionSet.PromotionSetDto.Default with
            PromotionSetId = Guid.NewGuid()
            RepositoryId = Guid.NewGuid()
            TargetBranchId = Guid.NewGuid()
            Visibility = visibility
            Ownership = ownership
            CreatorUserId = creatorUserId
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

    /// Builds root Directory Version With Hashes test data for the server unit services Effective Promotion scenarios in this file.
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

    /// Builds child Directory Version With Hashes test data for the server unit services Effective Promotion scenarios in this file.
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

    /// Builds legacy Created Reference Event test data for the server unit services Effective Promotion scenarios in this file.
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

    /// Verifies that latest Effective Promotion Ignores Deleted Terminal References.
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

    /// Verifies that latest Effective Promotion Requires Terminal Link.
    [<Test>]
    member _.LatestEffectivePromotionRequiresTerminalLink() =
        let createdAt = Instant.FromUtc(2026, 2, 21, 12, 0)
        let nonTerminal = createPromotionReference createdAt false false

        let selected = Services.tryGetLatestEffectivePromotionReference ([ nonTerminal ])

        Assert.That(selected, Is.EqualTo(None))

    /// Verifies that public PromotionSet base selection skips hidden private terminal references.
    [<Test>]
    member _.LatestPromotionForPublicPromotionSetSkipsPrivateTerminalReference() =
        let createdAt = Instant.FromUtc(2026, 7, 8, 9, 0)
        let publicWorkflow = promotionSet ResourceVisibility.Public ResourceOwnership.RepositoryOwned Option.None

        let privateTerminal =
            createVisiblePromotionReference
                (createdAt + Duration.FromMinutes(2.0))
                ResourceVisibility.Private
                ResourceOwnership.ContributorOwned
                (Some(UserId "creator-a"))

        let publicTerminal =
            createVisiblePromotionReference (createdAt + Duration.FromMinutes(1.0)) ResourceVisibility.Public ResourceOwnership.RepositoryOwned Option.None

        let selected = Services.tryGetLatestEffectivePromotionReferenceForPromotionSet publicWorkflow [ privateTerminal; publicTerminal ]

        Assert.That(selected, Is.EqualTo(Some publicTerminal))

    /// Verifies that branch base selection keeps repository-owned terminal authority available for default branches.
    [<Test>]
    member _.BranchAuthorityAllowsRepositoryOwnedTerminalReference() =
        let createdAt = Instant.FromUtc(2026, 7, 8, 9, 15)

        let repositoryOwnedTerminal = createVisiblePromotionReference createdAt ResourceVisibility.Private ResourceOwnership.RepositoryOwned Option.None

        Assert.That(Services.terminalPromotionCanPublishBranchAuthority repositoryOwnedTerminal, Is.True)

    /// Verifies that branch base selection still rejects private contributor-owned terminal authority.
    [<Test>]
    member _.BranchAuthorityRejectsPrivateContributorOwnedTerminalReference() =
        let createdAt = Instant.FromUtc(2026, 7, 8, 9, 20)

        let privateContributorTerminal =
            createVisiblePromotionReference createdAt ResourceVisibility.Private ResourceOwnership.ContributorOwned (Some(UserId "creator-a"))

        Assert.That(Services.terminalPromotionCanPublishBranchAuthority privateContributorTerminal, Is.False)

    /// Verifies that private PromotionSet base selection can share private terminal authority inside the same contributor audience.
    [<Test>]
    member _.LatestPromotionForPrivatePromotionSetAllowsSameContributorPrivateTerminalReference() =
        let createdAt = Instant.FromUtc(2026, 7, 8, 9, 30)
        let creator = Some(UserId "creator-a")
        let privateWorkflow = promotionSet ResourceVisibility.Private ResourceOwnership.ContributorOwned creator
        let privateTerminal = createVisiblePromotionReference createdAt ResourceVisibility.Private ResourceOwnership.ContributorOwned creator

        let selected = Services.tryGetLatestEffectivePromotionReferenceForPromotionSet privateWorkflow [ privateTerminal ]

        Assert.That(selected, Is.EqualTo(Some privateTerminal))

    /// Verifies that private PromotionSet base selection rejects hidden terminal authority from a different contributor audience.
    [<Test>]
    member _.LatestPromotionForPrivatePromotionSetRejectsDifferentContributorPrivateTerminalReference() =
        let createdAt = Instant.FromUtc(2026, 7, 8, 10, 0)
        let privateWorkflow = promotionSet ResourceVisibility.Private ResourceOwnership.ContributorOwned (Some(UserId "creator-a"))

        let otherPrivateTerminal =
            createVisiblePromotionReference createdAt ResourceVisibility.Private ResourceOwnership.ContributorOwned (Some(UserId "creator-b"))

        let selected = Services.tryGetLatestEffectivePromotionReferenceForPromotionSet privateWorkflow [ otherPrivateTerminal ]

        Assert.That(selected, Is.EqualTo(Option.None))

    /// Verifies that latest Reference Selection Ignores Deleted References.
    [<Test>]
    member _.LatestReferenceSelectionIgnoresDeletedReferences() =
        let createdAt = Instant.FromUtc(2026, 2, 21, 14, 0)
        let deletedLatest = createPromotionReference (createdAt + Duration.FromMinutes(2.0)) true true
        let previousActive = createPromotionReference (createdAt + Duration.FromMinutes(1.0)) false false

        let selected = Services.tryGetLatestNotDeletedReference ([ deletedLatest; previousActive ])

        Assert.That(selected, Is.EqualTo(Some previousActive))

    /// Verifies that direct Reference Projection Hydrates Legacy Empty Blake3 From Root Sha Prefix.
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

    /// Verifies that direct Reference Projection Leaves Legacy Empty Blake3 For Non Root Or Wrong Sha Prefix.
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
