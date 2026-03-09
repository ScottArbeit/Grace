namespace Grace.Types.Tests

open Grace.Types.ExternalEvents
open Grace.Types.Types
open NodaTime
open NUnit.Framework
open System
open System.Text.Json

[<Parallelizable(ParallelScope.All)>]
type CanonicalExternalEventEnvelopeTests() =

    [<Test>]
    member _.PublishedCanonicalEventNamesExcludeBootstrapped() =
        let publishedNames = CanonicalEventName.publishedNames |> Set.ofArray

        Assert.That(publishedNames.Contains("grace.agent.work-started"), Is.True)
        Assert.That(publishedNames.Contains("grace.agent.work-stopped"), Is.True)
        Assert.That(publishedNames.Contains("grace.agent.bootstrapped"), Is.False)

    [<Test>]
    member _.TryCreateUsesDeterministicEventIdAndOmitsEmptyTopLevelIds() =
        let payload = CanonicalExternalEventEnvelope.toPayloadElement {| ownerId = "owner-123" |}

        let envelopeResult =
            CanonicalExternalEventEnvelope.tryCreate
                CanonicalEventName.OwnerCreated
                (Instant.FromUtc(2026, 3, 9, 19, 0))
                "corr-owner-created"
                OwnerId.Empty
                OrganizationId.Empty
                RepositoryId.Empty
                { ActorType = "Owner"; ActorId = "owner-123" }
                payload

        match envelopeResult with
        | Ok envelope ->
            Assert.That(envelope.EventId, Is.EqualTo("Owner_owner-123_corr-owner-created"))
            Assert.That(envelope.OwnerId, Is.EqualTo(None))
            Assert.That(envelope.OrganizationId, Is.EqualTo(None))
            Assert.That(envelope.RepositoryId, Is.EqualTo(None))
            Assert.That(envelope.EventVersion, Is.EqualTo(CanonicalExternalEventEnvelope.EventVersion))
        | Error reason -> Assert.Fail($"Expected Ok envelope, got Error: {reason}")

    [<Test>]
    member _.TryCreateRejectsNonObjectPayloads() =
        let payload = JsonSerializer.SerializeToElement(42, CanonicalExternalEventEnvelope.canonicalJsonSerializerOptions)

        let envelopeResult =
            CanonicalExternalEventEnvelope.tryCreate
                CanonicalEventName.OwnerCreated
                (Instant.FromUtc(2026, 3, 9, 19, 5))
                "corr-invalid-payload"
                OwnerId.Empty
                OrganizationId.Empty
                RepositoryId.Empty
                { ActorType = "Owner"; ActorId = "owner-123" }
                payload

        match envelopeResult with
        | Ok _ -> Assert.Fail("Expected Error for non-object payload.")
        | Error reason -> Assert.That(reason, Does.Contain("JSON objects"))

    [<Test>]
    member _.TryCreateTrimsActorIdentityAndUsesCanonicalNameString() =
        let payload = CanonicalExternalEventEnvelope.toPayloadElement {| workItemId = "work-item-123" |}

        let envelopeResult =
            CanonicalExternalEventEnvelope.tryCreate
                CanonicalEventName.WorkItemUpdated
                (Instant.FromUtc(2026, 3, 9, 19, 10))
                "corr-work-item-updated"
                OwnerId.Empty
                OrganizationId.Empty
                RepositoryId.Empty
                { ActorType = "  WorkItem "; ActorId = " work-item-123  " }
                payload

        match envelopeResult with
        | Ok envelope ->
            Assert.That(envelope.ActorType, Is.EqualTo(Some "WorkItem"))
            Assert.That(envelope.ActorId, Is.EqualTo(Some "work-item-123"))
            Assert.That(envelope.EventName, Is.EqualTo("grace.work-item.updated"))
            Assert.That(envelope.EventId, Is.EqualTo("WorkItem_work-item-123_corr-work-item-updated"))
        | Error reason -> Assert.Fail($"Expected Ok envelope, got Error: {reason}")

    [<Test>]
    member _.MeasureSizeUsesStrictLessThanDocumentLimit() =
        let payload = CanonicalExternalEventEnvelope.toPayloadElement {| note = String('a', 256) |}

        let envelopeResult =
            CanonicalExternalEventEnvelope.tryCreate
                CanonicalEventName.OwnerCreated
                (Instant.FromUtc(2026, 3, 9, 19, 15))
                "corr-size-check"
                OwnerId.Empty
                OrganizationId.Empty
                RepositoryId.Empty
                { ActorType = "Owner"; ActorId = "owner-123" }
                payload

        match envelopeResult with
        | Ok envelope ->
            let size = CanonicalExternalEventEnvelope.measureSize envelope
            Assert.That(size.LimitBytes, Is.EqualTo(1_000_000))
            Assert.That(size.ByteCount, Is.LessThan(size.LimitBytes))
            Assert.That(CanonicalExternalEventEnvelope.isWithinSizeLimit envelope, Is.True)
        | Error reason -> Assert.Fail($"Expected Ok envelope, got Error: {reason}")
