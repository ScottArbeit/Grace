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

    /// Verifies that latest Reference Selection Ignores Deleted References.
    [<Test>]
    member _.LatestReferenceSelectionIgnoresDeletedReferences() =
        let createdAt = Instant.FromUtc(2026, 2, 21, 14, 0)
        let deletedLatest = createPromotionReference (createdAt + Duration.FromMinutes(2.0)) true true
        let previousActive = createPromotionReference (createdAt + Duration.FromMinutes(1.0)) false false

        let selected = Services.tryGetLatestNotDeletedReference ([ deletedLatest; previousActive ])

        Assert.That(selected, Is.EqualTo(Some previousActive))

    /// Verifies that direct Reference projection rejects a Created event without BLAKE3.
    [<Test>]
    member _.DirectReferenceProjectionRejectsCreatedEventWithoutBlake3() =
        Assert.ThrowsAsync<ArgumentException>(Func<Task>(fun () -> Services.projectReferenceEvents [| legacyCreatedReferenceEvent prefixSha256Hash |] :> Task))
        |> ignore
