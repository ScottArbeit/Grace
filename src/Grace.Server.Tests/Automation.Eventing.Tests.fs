namespace Grace.Server.Tests

open Grace.Server
open Grace.Shared.Utilities
open Grace.Types.Automation
open Grace.Types.Events
open Grace.Types.Queue
open Grace.Types.Reference
open Grace.Types.Types
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.Text.Json

[<Parallelizable(ParallelScope.All)>]
type AutomationEventingTests() =

    let metadata correlationId repositoryId =
        let properties = Dictionary<string, string>()
        properties[nameof RepositoryId] <- $"{repositoryId}"

        { Timestamp = Instant.FromUtc(2026, 2, 18, 0, 0); CorrelationId = correlationId; Principal = "tester"; Properties = properties }

    [<Test>]
    member _.ReferencePromotionWithTerminalLinkMapsToPromotionSetApplied() =
        let repositoryId = Guid.NewGuid()
        let referenceId = Guid.NewGuid()
        let promotionSetId = Guid.NewGuid()
        let branchId = Guid.NewGuid()

        let referenceEvent: ReferenceEvent =
            {
                Event =
                    ReferenceEventType.Created(
                        referenceId,
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        repositoryId,
                        branchId,
                        Guid.NewGuid(),
                        Sha256Hash String.Empty,
                        ReferenceType.Promotion,
                        "promotion",
                        [
                            ReferenceLinkType.IncludedInPromotionSet promotionSetId
                            ReferenceLinkType.PromotionSetTerminal promotionSetId
                        ]
                    )
                Metadata = metadata "corr-terminal" repositoryId
            }

        let envelope = EventingPublisher.tryCreateEnvelope (GraceEvent.ReferenceEvent referenceEvent)
        Assert.That(envelope.IsSome, Is.True)

        let eventEnvelope = envelope.Value
        Assert.That(eventEnvelope.EventType, Is.EqualTo(AutomationEventType.PromotionSetApplied))
        Assert.That(eventEnvelope.RepositoryId, Is.EqualTo(repositoryId))

        use payload = JsonDocument.Parse(eventEnvelope.DataJson)

        let payloadPromotionSetId =
            payload
                .RootElement
                .GetProperty("promotionSetId")
                .GetGuid()

        let payloadBranchId =
            payload
                .RootElement
                .GetProperty("targetBranchId")
                .GetGuid()

        let payloadReferenceId =
            payload
                .RootElement
                .GetProperty("terminalPromotionReferenceId")
                .GetGuid()

        Assert.That(payloadPromotionSetId, Is.EqualTo(promotionSetId))
        Assert.That(payloadBranchId, Is.EqualTo(branchId))
        Assert.That(payloadReferenceId, Is.EqualTo(referenceId))

    [<Test>]
    member _.ReferencePromotionWithoutTerminalLinkDoesNotEmitPromotionSetApplied() =
        let repositoryId = Guid.NewGuid()

        let referenceEvent: ReferenceEvent =
            {
                Event =
                    ReferenceEventType.Created(
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        repositoryId,
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        Sha256Hash String.Empty,
                        ReferenceType.Promotion,
                        "promotion",
                        [
                            ReferenceLinkType.IncludedInPromotionSet(Guid.NewGuid())
                        ]
                    )
                Metadata = metadata "corr-non-terminal" repositoryId
            }

        let envelope = EventingPublisher.tryCreateEnvelope (GraceEvent.ReferenceEvent referenceEvent)
        Assert.That(envelope.IsNone, Is.True)

    [<Test>]
    member _.QueueCandidateEnqueuedMapsToPromotionSetEnqueued() =
        let repositoryId = Guid.NewGuid()

        let queueEvent: PromotionQueueEvent =
            { Event = PromotionQueueEventType.CandidateEnqueued(Guid.NewGuid()); Metadata = metadata "corr-queue" repositoryId }

        let envelope = EventingPublisher.tryCreateEnvelope (GraceEvent.QueueEvent queueEvent)
        Assert.That(envelope.IsSome, Is.True)
        Assert.That(envelope.Value.EventType, Is.EqualTo(AutomationEventType.PromotionSetEnqueued))
        Assert.That(envelope.Value.RepositoryId, Is.EqualTo(repositoryId))

    [<Test>]
    member _.QueueCandidateDequeuedMapsToPromotionSetDequeued() =
        let repositoryId = Guid.NewGuid()

        let queueEvent: PromotionQueueEvent =
            { Event = PromotionQueueEventType.CandidateDequeued(Guid.NewGuid()); Metadata = metadata "corr-queue-dequeued" repositoryId }

        let envelope = EventingPublisher.tryCreateEnvelope (GraceEvent.QueueEvent queueEvent)
        Assert.That(envelope.IsSome, Is.True)
        Assert.That(envelope.Value.EventType, Is.EqualTo(AutomationEventType.PromotionSetDequeued))
        Assert.That(envelope.Value.RepositoryId, Is.EqualTo(repositoryId))
