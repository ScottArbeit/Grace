namespace Grace.Types.Tests

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.Reference
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic

/// Contains tests covering reference dto behavior.
[<Parallelizable(ParallelScope.All)>]
type ReferenceDtoTests() =

    let timestamp = Instant.FromUtc(2026, 6, 9, 10, 0)
    let referenceId = Guid.Parse("11111111-1111-1111-1111-111111111111")
    let ownerId = Guid.Parse("22222222-2222-2222-2222-222222222222")
    let organizationId = Guid.Parse("33333333-3333-3333-3333-333333333333")
    let repositoryId = Guid.Parse("44444444-4444-4444-4444-444444444444")
    let branchId = Guid.Parse("55555555-5555-5555-5555-555555555555")
    let directoryId = Guid.Parse("66666666-6666-6666-6666-666666666666")
    let promotionSetId = Guid.Parse("77777777-7777-7777-7777-777777777777")
    let rootSha256Hash = Sha256Hash "root-sha256"
    let rootBlake3Hash = Blake3Hash "root-blake3"

    /// Builds a deterministic metadata fixture for the types reference assertions.
    let createMetadata principal timestamp =
        {
            Timestamp = timestamp
            CorrelationId = "reference-created-by-tests"
            Principal = principal
            ClientType = Option.None
            Properties = Dictionary<string, string>()
        }

    /// Builds a deterministic created event fixture for the types reference assertions.
    let createdEvent principal =
        {
            Event =
                ReferenceEventType.Created(
                    referenceId,
                    ownerId,
                    organizationId,
                    repositoryId,
                    branchId,
                    directoryId,
                    rootSha256Hash,
                    rootBlake3Hash,
                    ReferenceType.Commit,
                    ReferenceText "commit reference",
                    Seq.empty
                )
            Metadata = createMetadata principal timestamp
        }

    /// Verifies that created event with principal populates created by.
    [<Test>]
    member _.CreatedEventWithPrincipalPopulatesCreatedBy() =
        let updated = ReferenceDto.UpdateDto (createdEvent "alice@example.test") ReferenceDto.Default

        Assert.That(updated.Sha256Hash, Is.EqualTo(rootSha256Hash))
        Assert.That(updated.Blake3Hash, Is.EqualTo(rootBlake3Hash))
        Assert.That(updated.DirectoryId, Is.EqualTo(directoryId))
        Assert.That(updated.CreatedBy, Is.EqualTo(Some "alice@example.test"))
        Assert.That(updated.CreatedAt, Is.EqualTo(timestamp))

    /// Verifies that default reference dto uses explicit empty blake3 for legacy replay boundary.
    [<Test>]
    member _.DefaultReferenceDtoUsesExplicitEmptyBlake3ForLegacyReplayBoundary() =
        Assert.That(ReferenceDto.Default.Sha256Hash, Is.EqualTo(Sha256Hash String.Empty))
        Assert.That(ReferenceDto.Default.Blake3Hash, Is.EqualTo(Blake3Hash String.Empty))

    /// Verifies that default and blank created principal keep created by missing.
    [<Test>]
    member _.DefaultAndBlankCreatedPrincipalKeepCreatedByMissing() =
        let updated = ReferenceDto.UpdateDto (createdEvent " ") ReferenceDto.Default

        Assert.That(ReferenceDto.Default.CreatedBy, Is.EqualTo(Option.None))
        Assert.That(updated.CreatedBy, Is.EqualTo(Option.None))

    /// Verifies that json serialization writes nullable created by.
    [<Test>]
    member _.JsonSerializationWritesNullableCreatedBy() =
        let withCreator = ReferenceDto.UpdateDto (createdEvent "alice@example.test") ReferenceDto.Default
        let withoutCreator = { withCreator with CreatedBy = Option.None }

        let withCreatorJson = serialize withCreator
        let withoutCreatorJson = serialize withoutCreator

        Assert.That(withCreatorJson, Does.Contain("\"CreatedBy\": \"alice@example.test\""))
        Assert.That(withoutCreatorJson, Does.Contain("\"CreatedBy\": null"))

        let roundTripWithoutCreator = deserialize<ReferenceDto> withoutCreatorJson
        Assert.That(roundTripWithoutCreator.CreatedBy, Is.EqualTo(Option.None))

    /// Verifies that update events do not overwrite created by.
    [<Test>]
    member _.UpdateEventsDoNotOverwriteCreatedBy() =
        let created = ReferenceDto.UpdateDto (createdEvent "alice@example.test") ReferenceDto.Default

        let linkAddedEvent =
            {
                Event = ReferenceEventType.LinkAdded(ReferenceLinkType.PromotionSetTerminal promotionSetId)
                Metadata = createMetadata "bob@example.test" (timestamp + Duration.FromMinutes(1.0))
            }

        let deletedEvent =
            {
                Event = ReferenceEventType.LogicalDeleted(false, "cleanup")
                Metadata = createMetadata "carol@example.test" (timestamp + Duration.FromMinutes(2.0))
            }

        let linked = ReferenceDto.UpdateDto linkAddedEvent created
        let deleted = ReferenceDto.UpdateDto deletedEvent linked

        Assert.That(linked.CreatedBy, Is.EqualTo(Some "alice@example.test"))
        Assert.That(deleted.CreatedBy, Is.EqualTo(Some "alice@example.test"))
        Assert.That(linked.UpdatedAt, Is.EqualTo(Some(timestamp + Duration.FromMinutes(1.0))))
        Assert.That(deleted.UpdatedAt, Is.EqualTo(Some(timestamp + Duration.FromMinutes(2.0))))
