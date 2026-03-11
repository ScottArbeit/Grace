namespace Grace.Server.Tests

open Grace.Server
open Grace.Types.DirectoryVersion
open Grace.Types.Events
open Grace.Types.ExternalEvents
open Grace.Types.Owner
open Grace.Types.PromotionSet
open Grace.Types.Reference
open Grace.Types.Types
open NodaTime
open NUnit.Framework
open System
open System.Text.Json

[<Parallelizable(ParallelScope.All)>]
type CanonicalEventBuilderTests() =

    let ownerMetadata correlationId ownerId =
        let metadata = { EventMetadata.New correlationId "tester" with Timestamp = Instant.FromUtc(2026, 3, 9, 20, 0) }

        metadata.Properties[ nameof OwnerId ] <- $"{ownerId}"
        metadata

    let referenceMetadata correlationId repositoryId referenceId =
        let metadata = { EventMetadata.New correlationId "tester" with Timestamp = Instant.FromUtc(2026, 3, 9, 20, 5) }

        metadata.Properties[ nameof RepositoryId ] <- $"{repositoryId}"
        metadata.Properties[ "ActorId" ] <- $"{referenceId}"
        metadata

    let promotionSetMetadata correlationId repositoryId promotionSetId =
        let metadata = { EventMetadata.New correlationId "tester" with Timestamp = Instant.FromUtc(2026, 3, 9, 20, 10) }

        metadata.Properties[ nameof RepositoryId ] <- $"{repositoryId}"
        metadata.Properties[ "ActorId" ] <- $"{promotionSetId}"
        metadata

    [<Test>]
    member _.OwnerNameSetBuildsCanonicalOwnerUpdatedEnvelope() =
        let ownerId = Guid.NewGuid()

        let ownerEvent: Grace.Types.Owner.OwnerEvent =
            { Event = Grace.Types.Owner.OwnerEventType.NameSet "Grace Owner"; Metadata = ownerMetadata "corr-owner-name-set" ownerId }

        match ExternalEvents.buildGraceEvent (GraceEvent.OwnerEvent ownerEvent) with
        | ExternalEvents.Published envelope ->
            Assert.That(envelope.EventName, Is.EqualTo(CanonicalEventName.toString CanonicalEventName.OwnerUpdated))
            Assert.That(envelope.EventId, Is.EqualTo($"Owner_{ownerId}_corr-owner-name-set"))
            let payload = envelope.Payload
            Assert.That(payload.GetProperty("ownerId").GetGuid(), Is.EqualTo(ownerId))
            Assert.That(payload.GetProperty("changeKind").GetString(), Is.EqualTo("name-set"))

            Assert.That(
                payload
                    .GetProperty("changed")
                    .GetProperty("ownerName")
                    .GetString(),
                Is.EqualTo("Grace Owner")
            )
        | outcome -> Assert.Fail($"Expected Published outcome, got {outcome}.")

    [<Test>]
    member _.TerminalPromotionReferenceCreationBuildsPromotionSetAppliedEnvelope() =
        let ownerId = Guid.NewGuid()
        let organizationId = Guid.NewGuid()
        let repositoryId = Guid.NewGuid()
        let branchId = Guid.NewGuid()
        let directoryVersionId = Guid.NewGuid()
        let referenceId = Guid.NewGuid()
        let promotionSetId = Guid.NewGuid()

        let referenceEvent: ReferenceEvent =
            {
                Event =
                    ReferenceEventType.Created(
                        referenceId,
                        ownerId,
                        organizationId,
                        repositoryId,
                        branchId,
                        directoryVersionId,
                        Sha256Hash "abc123",
                        ReferenceType.Promotion,
                        ReferenceText "promotion terminal",
                        seq { ReferenceLinkType.PromotionSetTerminal promotionSetId }
                    )
                Metadata = referenceMetadata "corr-promotion-terminal" repositoryId referenceId
            }

        match ExternalEvents.buildGraceEvent (GraceEvent.ReferenceEvent referenceEvent) with
        | ExternalEvents.Published envelope ->
            Assert.That(envelope.EventName, Is.EqualTo(CanonicalEventName.toString CanonicalEventName.PromotionSetApplied))
            Assert.That(envelope.EventId, Is.EqualTo($"Reference_{referenceId}_corr-promotion-terminal"))

            let payload = envelope.Payload
            let mutable ignored = Unchecked.defaultof<JsonElement>

            Assert.That(payload.GetProperty("promotionSetId").GetGuid(), Is.EqualTo(promotionSetId))
            Assert.That(payload.GetProperty("targetBranchId").GetGuid(), Is.EqualTo(branchId))

            Assert.That(
                payload
                    .GetProperty("terminalPromotionReferenceId")
                    .GetGuid(),
                Is.EqualTo(referenceId)
            )

            Assert.That(payload.TryGetProperty("referenceId", &ignored), Is.False)
        | outcome -> Assert.Fail($"Expected Published outcome, got {outcome}.")

    [<Test>]
    member _.PromotionSetAppliedRemainsInternalOnlyWhenSourcedFromPromotionSetEvent() =
        let repositoryId = Guid.NewGuid()
        let promotionSetId = Guid.NewGuid()
        let referenceId = Guid.NewGuid()

        let promotionSetEvent: PromotionSetEvent =
            { Event = PromotionSetEventType.Applied referenceId; Metadata = promotionSetMetadata "corr-promotion-applied" repositoryId promotionSetId }

        match ExternalEvents.buildGraceEvent (GraceEvent.PromotionSetEvent promotionSetEvent) with
        | ExternalEvents.InternalOnly rawEventCase -> Assert.That(rawEventCase, Is.EqualTo(RawEventCase.promotionSet "Applied"))
        | outcome -> Assert.Fail($"Expected InternalOnly outcome, got {outcome}.")

    [<Test>]
    member _.AllDirectoryVersionRegistryEntriesRemainInternalOnly() =
        let directoryEntries =
            Registry.all
            |> List.filter (fun entry -> entry.RawEventCase.StartsWith("DirectoryVersionEventType.", StringComparison.Ordinal))

        Assert.That(directoryEntries, Is.Not.Empty)

        Assert.That(
            directoryEntries
            |> List.forall (fun entry -> entry.Classification = ExternalEventClassification.InternalOnly),
            Is.True
        )

    [<Test>]
    member _.RuntimeCanonicalRegistryDoesNotPublishBootstrapped() =
        let publishedNames = Registry.publishedEventNameStrings |> Set.ofArray
        Assert.That(publishedNames.Contains("grace.agent.work-started"), Is.True)
        Assert.That(publishedNames.Contains("grace.agent.work-stopped"), Is.True)
        Assert.That(publishedNames.Contains("grace.agent.bootstrapped"), Is.False)
