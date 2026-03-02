namespace Grace.Server.Tests

open Grace.Server
open Grace.Shared.Utilities
open Grace.Types.Automation
open Grace.Types.Events
open Grace.Types.PromotionSet
open Grace.Types.Queue
open Grace.Types.Reference
open Grace.Types.Types
open Grace.Types.WorkItem
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
    member _.QueuePromotionSetEnqueuedMapsToPromotionSetEnqueued() =
        let repositoryId = Guid.NewGuid()

        let queueEvent: PromotionQueueEvent =
            { Event = PromotionQueueEventType.PromotionSetEnqueued(Guid.NewGuid()); Metadata = metadata "corr-queue" repositoryId }

        let envelope = EventingPublisher.tryCreateEnvelope (GraceEvent.QueueEvent queueEvent)
        Assert.That(envelope.IsSome, Is.True)
        Assert.That(envelope.Value.EventType, Is.EqualTo(AutomationEventType.PromotionSetEnqueued))
        Assert.That(envelope.Value.RepositoryId, Is.EqualTo(repositoryId))

    [<Test>]
    member _.QueuePromotionSetDequeuedMapsToPromotionSetDequeued() =
        let repositoryId = Guid.NewGuid()

        let queueEvent: PromotionQueueEvent =
            { Event = PromotionQueueEventType.PromotionSetDequeued(Guid.NewGuid()); Metadata = metadata "corr-queue-dequeued" repositoryId }

        let envelope = EventingPublisher.tryCreateEnvelope (GraceEvent.QueueEvent queueEvent)
        Assert.That(envelope.IsSome, Is.True)
        Assert.That(envelope.Value.EventType, Is.EqualTo(AutomationEventType.PromotionSetDequeued))
        Assert.That(envelope.Value.RepositoryId, Is.EqualTo(repositoryId))

    [<Test>]
    member _.PromotionSetRecomputeStartedMapsToAutomationEvent() =
        let repositoryId = Guid.NewGuid()
        let metadata = metadata "corr-ps-recompute-started" repositoryId
        metadata.Properties[ "ActorId" ] <- $"{Guid.NewGuid()}"

        let promotionSetEvent: PromotionSetEvent = { Event = PromotionSetEventType.RecomputeStarted(Guid.NewGuid()); Metadata = metadata }

        let envelope = EventingPublisher.tryCreateEnvelope (GraceEvent.PromotionSetEvent promotionSetEvent)
        Assert.That(envelope.IsSome, Is.True)
        Assert.That(envelope.Value.EventType, Is.EqualTo(AutomationEventType.PromotionSetRecomputeStarted))
        Assert.That(envelope.Value.RepositoryId, Is.EqualTo(repositoryId))

    [<Test>]
    member _.PromotionSetStepsUpdatedMapsToAutomationEvent() =
        let repositoryId = Guid.NewGuid()
        let metadata = metadata "corr-ps-steps-updated" repositoryId
        metadata.Properties[ "ActorId" ] <- $"{Guid.NewGuid()}"

        let step: PromotionSetStep =
            {
                StepId = Guid.NewGuid()
                Order = 0
                OriginalPromotion = { BranchId = Guid.NewGuid(); ReferenceId = Guid.NewGuid(); DirectoryVersionId = Guid.NewGuid() }
                OriginalBasePromotionReferenceId = Guid.NewGuid()
                OriginalBaseDirectoryVersionId = Guid.NewGuid()
                ComputedAgainstBaseDirectoryVersionId = Guid.NewGuid()
                AppliedDirectoryVersionId = Guid.NewGuid()
                ConflictSummaryArtifactId = Option.None
                ConflictStatus = StepConflictStatus.NoConflicts
            }

        let promotionSetEvent: PromotionSetEvent = { Event = PromotionSetEventType.StepsUpdated([ step ], Guid.NewGuid()); Metadata = metadata }

        let envelope = EventingPublisher.tryCreateEnvelope (GraceEvent.PromotionSetEvent promotionSetEvent)
        Assert.That(envelope.IsSome, Is.True)
        Assert.That(envelope.Value.EventType, Is.EqualTo(AutomationEventType.PromotionSetStepsUpdated))
        Assert.That(envelope.Value.RepositoryId, Is.EqualTo(repositoryId))

    [<Test>]
    member _.WorkItemArtifactLinkedMapsToAgentSummaryAdded() =
        let repositoryId = Guid.NewGuid()

        let workItemEvent: WorkItemEvent =
            {
                Event = WorkItemEventType.ArtifactLinked(Guid.NewGuid())
                Metadata = metadata "corr-work-item-summary" repositoryId
            }

        let envelope = EventingPublisher.tryCreateEnvelope (GraceEvent.WorkItemEvent workItemEvent)
        Assert.That(envelope.IsSome, Is.True)
        Assert.That(envelope.Value.EventType, Is.EqualTo(AutomationEventType.AgentSummaryAdded))
        Assert.That(envelope.Value.RepositoryId, Is.EqualTo(repositoryId))

    [<Test>]
    member _.AgentSessionEnvelopeRetainsCorrelationAndIdentityMetadata() =
        let ownerId = Guid.NewGuid()
        let organizationId = Guid.NewGuid()
        let repositoryId = Guid.NewGuid()
        let correlationId = "corr-agent-session"
        let metadata = metadata correlationId repositoryId
        metadata.Properties[nameof OwnerId] <- $"{ownerId}"
        metadata.Properties[nameof OrganizationId] <- $"{organizationId}"
        metadata.Properties["ActorId"] <- "agent-session"

        let operationResult =
            {
                AgentSessionOperationResult.Default with
                    Session =
                        {
                            AgentSessionInfo.Default with
                                SessionId = "session-1"
                                AgentId = "agent-123"
                                AgentDisplayName = "Agent 123"
                                WorkItemIdOrNumber = "42"
                                Source = "cli"
                                LifecycleState = AgentSessionLifecycleState.Active
                                StartedAt = Some(Instant.FromUtc(2026, 2, 18, 1, 0))
                        }
                    OperationId = "op-1"
                    Message = "started"
            }

        let envelope =
            EventingPublisher.tryCreateAgentSessionEnvelope
                AutomationEventType.AgentBootstrapped
                metadata
                operationResult

        Assert.That(envelope.IsSome, Is.True)
        Assert.That(envelope.Value.EventType, Is.EqualTo(AutomationEventType.AgentBootstrapped))
        Assert.That(envelope.Value.CorrelationId, Is.EqualTo(correlationId))
        Assert.That(envelope.Value.OwnerId, Is.EqualTo(ownerId))
        Assert.That(envelope.Value.OrganizationId, Is.EqualTo(organizationId))
        Assert.That(envelope.Value.RepositoryId, Is.EqualTo(repositoryId))
        Assert.That(envelope.Value.ActorId, Is.EqualTo(operationResult.Session.AgentId))

        use payload = JsonDocument.Parse(envelope.Value.DataJson)
        let payloadSessionId = payload.RootElement.GetProperty("Session").GetProperty("SessionId").GetString()
        Assert.That(payloadSessionId, Is.EqualTo(operationResult.Session.SessionId))
