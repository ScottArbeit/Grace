namespace Grace.Server.Tests

open Grace.Server
open Grace.Shared.Utilities
open Grace.Types.Artifact
open Grace.Types.Automation
open Grace.Types.Events
open Grace.Types.PromotionSet
open Grace.Types.Queue
open Grace.Types.Reference
open Grace.Types.Review
open Grace.Types.Common
open Grace.Types.Validation
open Grace.Types.Visibility
open Grace.Types.WorkItem
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.Text.Json

/// Covers automation Eventing behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type AutomationEventingTests() =

    /// Constructs metadata fixtures used by the server unit eventing assertions.
    let metadata correlationId repositoryId =
        let properties = Dictionary<string, string>()
        properties[nameof RepositoryId] <- $"{repositoryId}"

        {
            Timestamp = Instant.FromUtc(2026, 2, 18, 0, 0)
            CorrelationId = correlationId
            Principal = "tester"
            ClientType = Microsoft.FSharp.Core.Option.None
            Properties = properties
        }

    /// Constructs reveal metadata for terminal PromotionSet public publication assertions.
    let terminalRevealMetadata correlationId ownerId organizationId repositoryId branchId promotionSetId terminalReferenceId =
        let eventMetadata = metadata correlationId repositoryId
        eventMetadata.Properties[ nameof OwnerId ] <- $"{ownerId}"
        eventMetadata.Properties[ nameof OrganizationId ] <- $"{organizationId}"
        eventMetadata.Properties[ nameof BranchId ] <- $"{branchId}"
        eventMetadata.Properties[ nameof PromotionSetId ] <- $"{promotionSetId}"
        eventMetadata.Properties[ "TerminalPromotionReferenceId" ] <- $"{terminalReferenceId}"
        eventMetadata.Properties[ "ReferenceRevealOperationId" ] <- "reveal-op-eventing"
        eventMetadata

    /// Adds private workflow visibility metadata to an event fixture.
    let markPrivateContributorOwned (metadata: EventMetadata) =
        metadata.Properties[ "Visibility" ] <- $"{ResourceVisibility.Private}"
        metadata.Properties[ "Ownership" ] <- $"{ResourceOwnership.ContributorOwned}"
        metadata

    /// Adds private inherited reference visibility metadata to an event fixture.
    let markPrivateInheritedReference (metadata: EventMetadata) =
        metadata.Properties[ "InheritedVisibility" ] <- $"{ResourceVisibility.Private}"
        metadata.Properties[ "InheritedOwnership" ] <- $"{ResourceOwnership.ContributorOwned}"
        metadata

    /// Verifies that inherited private review events are suppressed before public automation fanout.
    [<Test>]
    member _.PrivateInheritedReviewEventDoesNotCreateAutomationEnvelope() =
        let repositoryId = Guid.NewGuid()
        let promotionSetId = Guid.NewGuid()

        let notes =
            { ReviewNotes.Default with
                OwnerId = Guid.NewGuid()
                OrganizationId = Guid.NewGuid()
                RepositoryId = repositoryId
                PromotionSetId = Some promotionSetId
            }

        let reviewEvent: ReviewEvent =
            {
                Event = ReviewEventType.NotesUpserted notes
                Metadata =
                    metadata "corr-private-review" repositoryId
                    |> markPrivateInheritedReference
            }

        let envelope = EventingPublisher.tryCreateEnvelope (GraceEvent.ReviewEvent reviewEvent)
        Assert.That(envelope.IsNone, Is.True)

    /// Verifies that reference Promotion With Terminal Link Maps To Promotion Set Applied.
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
                        Blake3Hash String.Empty,
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

    /// Verifies that reference Promotion Without Terminal Link Does Not Emit Promotion Set Applied.
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
                        Blake3Hash String.Empty,
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

    /// Verifies that private terminal Promotion references do not emit public applied automation events.
    [<Test>]
    member _.PrivateTerminalPromotionReferenceDoesNotMapToPromotionSetApplied() =
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
                        Blake3Hash String.Empty,
                        ReferenceType.Promotion,
                        "promotion",
                        [
                            ReferenceLinkType.IncludedInPromotionSet promotionSetId
                            ReferenceLinkType.PromotionSetTerminal promotionSetId
                        ]
                    )
                Metadata =
                    metadata "corr-private-terminal" repositoryId
                    |> markPrivateInheritedReference
            }

        let envelope = EventingPublisher.tryCreateEnvelope (GraceEvent.ReferenceEvent referenceEvent)
        Assert.That(envelope.IsNone, Is.True)

    /// Verifies that terminal reference reveal maps to public PromotionSet applied automation.
    [<Test>]
    member _.TerminalReferenceRevealMapsToPromotionSetApplied() =
        let ownerId = Guid.NewGuid()
        let organizationId = Guid.NewGuid()
        let repositoryId = Guid.NewGuid()
        let branchId = Guid.NewGuid()
        let promotionSetId = Guid.NewGuid()
        let terminalReferenceId = Guid.NewGuid()

        let referenceEvent: ReferenceEvent =
            {
                Event = ReferenceEventType.Revealed("reveal-op-eventing", "tester", "accepted", ResourceVisibility.Private, ResourceVisibility.Public)
                Metadata = terminalRevealMetadata "corr-terminal-reveal" ownerId organizationId repositoryId branchId promotionSetId terminalReferenceId
            }

        let envelope = EventingPublisher.tryCreateEnvelope (GraceEvent.ReferenceEvent referenceEvent)
        Assert.That(envelope.IsSome, Is.True)

        let eventEnvelope = envelope.Value
        Assert.That(eventEnvelope.EventType, Is.EqualTo(AutomationEventType.PromotionSetApplied))
        Assert.That(eventEnvelope.OwnerId, Is.EqualTo(ownerId))
        Assert.That(eventEnvelope.OrganizationId, Is.EqualTo(organizationId))
        Assert.That(eventEnvelope.RepositoryId, Is.EqualTo(repositoryId))

        use payload = JsonDocument.Parse(eventEnvelope.DataJson)
        let root = payload.RootElement
        Assert.That(root.GetProperty("promotionSetId").GetGuid(), Is.EqualTo(promotionSetId))
        Assert.That(root.GetProperty("targetBranchId").GetGuid(), Is.EqualTo(branchId))

        Assert.That(
            root
                .GetProperty("terminalPromotionReferenceId")
                .GetGuid(),
            Is.EqualTo(terminalReferenceId)
        )

    /// Verifies that reveal retry does not create another automation projection without a durable reveal event.
    [<Test>]
    member _.AlreadyPublicRevealRetryDoesNotMapToPromotionSetApplied() =
        let metadata =
            terminalRevealMetadata
                "corr-terminal-reveal-retry"
                (Guid.NewGuid())
                (Guid.NewGuid())
                (Guid.NewGuid())
                (Guid.NewGuid())
                (Guid.NewGuid())
                (Guid.NewGuid())

        let referenceEvent: ReferenceEvent =
            {
                Event = ReferenceEventType.Revealed("reveal-op-eventing", "tester", "accepted", ResourceVisibility.Public, ResourceVisibility.Public)
                Metadata = metadata
            }

        let envelope = EventingPublisher.tryCreateEnvelope (GraceEvent.ReferenceEvent referenceEvent)
        Assert.That(envelope.IsNone, Is.True)

    /// Verifies that generic reference reveal does not map to PromotionSet applied automation.
    [<Test>]
    member _.GenericReferenceRevealDoesNotMapToPromotionSetApplied() =
        let eventMetadata = metadata "corr-generic-reveal" (Guid.NewGuid())
        eventMetadata.Properties[ nameof OwnerId ] <- $"{Guid.NewGuid()}"
        eventMetadata.Properties[ nameof OrganizationId ] <- $"{Guid.NewGuid()}"
        eventMetadata.Properties[ nameof BranchId ] <- $"{Guid.NewGuid()}"

        let referenceEvent: ReferenceEvent =
            {
                Event = ReferenceEventType.Revealed("reveal-op-eventing", "tester", "accepted", ResourceVisibility.Private, ResourceVisibility.Public)
                Metadata = eventMetadata
            }

        let envelope = EventingPublisher.tryCreateEnvelope (GraceEvent.ReferenceEvent referenceEvent)
        Assert.That(envelope.IsNone, Is.True)

    /// Verifies that queue Promotion Set Enqueued Maps To Promotion Set Enqueued.
    [<Test>]
    member _.QueuePromotionSetEnqueuedMapsToPromotionSetEnqueued() =
        let repositoryId = Guid.NewGuid()

        let queueEvent: PromotionQueueEvent =
            { Event = PromotionQueueEventType.PromotionSetEnqueued(Guid.NewGuid()); Metadata = metadata "corr-queue" repositoryId }

        let envelope = EventingPublisher.tryCreateEnvelope (GraceEvent.QueueEvent queueEvent)
        Assert.That(envelope.IsSome, Is.True)
        Assert.That(envelope.Value.EventType, Is.EqualTo(AutomationEventType.PromotionSetEnqueued))
        Assert.That(envelope.Value.RepositoryId, Is.EqualTo(repositoryId))

    /// Verifies that queue Promotion Set Dequeued Maps To Promotion Set Dequeued.
    [<Test>]
    member _.QueuePromotionSetDequeuedMapsToPromotionSetDequeued() =
        let repositoryId = Guid.NewGuid()

        let queueEvent: PromotionQueueEvent =
            { Event = PromotionQueueEventType.PromotionSetDequeued(Guid.NewGuid()); Metadata = metadata "corr-queue-dequeued" repositoryId }

        let envelope = EventingPublisher.tryCreateEnvelope (GraceEvent.QueueEvent queueEvent)
        Assert.That(envelope.IsSome, Is.True)
        Assert.That(envelope.Value.EventType, Is.EqualTo(AutomationEventType.PromotionSetDequeued))
        Assert.That(envelope.Value.RepositoryId, Is.EqualTo(repositoryId))

    /// Verifies that private queue workflow events do not emit public automation events.
    [<Test>]
    member _.PrivateQueuePromotionSetEventDoesNotMapToAutomationEvent() =
        let repositoryId = Guid.NewGuid()

        let queueEvent: PromotionQueueEvent =
            {
                Event = PromotionQueueEventType.PromotionSetEnqueued(Guid.NewGuid())
                Metadata =
                    metadata "corr-private-queue" repositoryId
                    |> markPrivateInheritedReference
            }

        let envelope = EventingPublisher.tryCreateEnvelope (GraceEvent.QueueEvent queueEvent)
        Assert.That(envelope.IsNone, Is.True)

    /// Verifies that promotion Set Recompute Started Maps To Automation Event.
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

    /// Verifies that private PromotionSet creation does not create a public automation event.
    [<Test>]
    member _.PrivatePromotionSetCreatedDoesNotMapToAutomationEvent() =
        let repositoryId = Guid.NewGuid()

        let promotionSetEvent: PromotionSetEvent =
            {
                Event =
                    PromotionSetEventType.Created(
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        repositoryId,
                        Guid.NewGuid(),
                        ResourceVisibility.Private,
                        ResourceOwnership.ContributorOwned,
                        Some(UserId "creator@example.test")
                    )
                Metadata =
                    metadata "corr-private-created" repositoryId
                    |> markPrivateContributorOwned
            }

        let envelope = EventingPublisher.tryCreateEnvelope (GraceEvent.PromotionSetEvent promotionSetEvent)
        Assert.That(envelope.IsNone, Is.True)

    /// Verifies that promotion Set Steps Updated Maps To Automation Event.
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

    /// Verifies that private PromotionSet apply does not create a public automation event.
    [<Test>]
    member _.PrivatePromotionSetAppliedDoesNotMapToAutomationEvent() =
        let repositoryId = Guid.NewGuid()

        let promotionSetEvent: PromotionSetEvent =
            {
                Event = PromotionSetEventType.Applied(Guid.NewGuid())
                Metadata =
                    metadata "corr-private-apply" repositoryId
                    |> markPrivateContributorOwned
            }

        let envelope = EventingPublisher.tryCreateEnvelope (GraceEvent.PromotionSetEvent promotionSetEvent)
        Assert.That(envelope.IsNone, Is.True)

    /// Verifies that current source visibility can suppress stale public event metadata before automation fanout.
    [<Test>]
    member _.CurrentHiddenPromotionSetSuppressesPublicAutomationProjection() =
        let repositoryId = Guid.NewGuid()
        let metadata = metadata "corr-current-hidden-apply" repositoryId
        metadata.Properties[ "ActorId" ] <- $"{Guid.NewGuid()}"

        let promotionSetEvent: PromotionSetEvent = { Event = PromotionSetEventType.Applied(Guid.NewGuid()); Metadata = metadata }

        let envelope = EventingPublisher.tryCreateEnvelopeWithCurrentVisibility (Some false) (GraceEvent.PromotionSetEvent promotionSetEvent)

        Assert.That(envelope.IsNone, Is.True)

    /// Verifies that current public visibility never replays old private PromotionSet apply metadata.
    [<Test>]
    member _.CurrentPublicPromotionSetDoesNotReplayPrivateAutomationProjection() =
        let repositoryId = Guid.NewGuid()

        let promotionSetEvent: PromotionSetEvent =
            {
                Event = PromotionSetEventType.Applied(Guid.NewGuid())
                Metadata =
                    metadata "corr-current-public-private-apply" repositoryId
                    |> markPrivateContributorOwned
            }

        let envelope = EventingPublisher.tryCreateEnvelopeWithCurrentVisibility (Some true) (GraceEvent.PromotionSetEvent promotionSetEvent)

        Assert.That(envelope.IsNone, Is.True)

    /// Verifies that private validation results do not emit public automation events.
    [<Test>]
    member _.PrivateValidationResultDoesNotMapToAutomationEvent() =
        let repositoryId = Guid.NewGuid()

        let validationResultEvent: ValidationResultEvent =
            {
                Event =
                    ValidationResultEventType.Recorded
                        { ValidationResultDto.Default with
                            ValidationResultId = Guid.NewGuid()
                            RepositoryId = repositoryId
                            PromotionSetId = Some(Guid.NewGuid())
                            StepsComputationAttempt = Some 1
                            ValidationName = "private-check"
                        }
                Metadata =
                    metadata "corr-private-validation-result" repositoryId
                    |> markPrivateInheritedReference
            }

        let envelope = EventingPublisher.tryCreateEnvelope (GraceEvent.ValidationResultEvent validationResultEvent)
        Assert.That(envelope.IsNone, Is.True)

    /// Verifies that private conflict artifacts do not emit public automation events.
    [<Test>]
    member _.PrivateArtifactDoesNotMapToAutomationEvent() =
        let ownerId = Guid.NewGuid()
        let organizationId = Guid.NewGuid()
        let repositoryId = Guid.NewGuid()

        let artifact =
            { ArtifactCreated.FromMetadata
                  { ArtifactMetadata.Default with
                      ArtifactId = Guid.NewGuid()
                      OwnerId = ownerId
                      OrganizationId = organizationId
                      RepositoryId = repositoryId
                      ArtifactType = ArtifactType.ConflictReport
                      BlobPath = "private/conflict.json"
                      CreatedAt = Instant.FromUtc(2026, 2, 18, 2, 0)
                      CreatedBy = UserId "creator@example.test"
                  } with
                ArtifactType = "ConflictReport"
            }

        let artifactEvent =
            ArtifactEvent.FromCreated(
                "Created",
                artifact,
                metadata "corr-private-artifact" repositoryId
                |> markPrivateInheritedReference
            )

        let envelope = EventingPublisher.tryCreateEnvelope (GraceEvent.ArtifactEvent artifactEvent)
        Assert.That(envelope.IsNone, Is.True)

    /// Verifies that work Item Artifact Linked Maps To Agent Summary Added.
    [<Test>]
    member _.WorkItemArtifactLinkedMapsToAgentSummaryAdded() =
        let repositoryId = Guid.NewGuid()

        let workItemEvent: WorkItemEvent =
            { Event = WorkItemEventType.ArtifactLinked(Guid.NewGuid()); Metadata = metadata "corr-work-item-summary" repositoryId }

        let envelope = EventingPublisher.tryCreateEnvelope (GraceEvent.WorkItemEvent workItemEvent)
        Assert.That(envelope.IsSome, Is.True)
        Assert.That(envelope.Value.EventType, Is.EqualTo(AutomationEventType.AgentSummaryAdded))
        Assert.That(envelope.Value.RepositoryId, Is.EqualTo(repositoryId))

    /// Verifies that private work-item PromotionSet link events do not emit public automation events.
    [<Test>]
    member _.PrivateWorkItemPromotionSetLinkDoesNotMapToAutomationEvent() =
        let repositoryId = Guid.NewGuid()

        let workItemEvent: WorkItemEvent =
            {
                Event = WorkItemEventType.PromotionSetLinked(Guid.NewGuid())
                Metadata =
                    metadata "corr-private-work-item-promotion-set" repositoryId
                    |> markPrivateInheritedReference
            }

        let envelope = EventingPublisher.tryCreateEnvelope (GraceEvent.WorkItemEvent workItemEvent)
        Assert.That(envelope.IsNone, Is.True)

    /// Verifies that agent Session Envelope Retains Correlation And Identity Metadata.
    [<Test>]
    member _.AgentSessionEnvelopeRetainsCorrelationAndIdentityMetadata() =
        let ownerId = Guid.NewGuid()
        let organizationId = Guid.NewGuid()
        let repositoryId = Guid.NewGuid()
        let correlationId = "corr-agent-session"
        let metadata = metadata correlationId repositoryId
        metadata.Properties[ nameof OwnerId ] <- $"{ownerId}"
        metadata.Properties[ nameof OrganizationId ] <- $"{organizationId}"
        metadata.Properties[ "ActorId" ] <- "agent-session"

        let operationResult =
            { AgentSessionOperationResult.Default with
                Session =
                    { AgentSessionInfo.Default with
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

        let envelope = EventingPublisher.tryCreateAgentSessionEnvelope AutomationEventType.AgentBootstrapped metadata operationResult

        Assert.That(envelope.IsSome, Is.True)
        Assert.That(envelope.Value.EventType, Is.EqualTo(AutomationEventType.AgentBootstrapped))
        Assert.That(envelope.Value.CorrelationId, Is.EqualTo(correlationId))
        Assert.That(envelope.Value.OwnerId, Is.EqualTo(ownerId))
        Assert.That(envelope.Value.OrganizationId, Is.EqualTo(organizationId))
        Assert.That(envelope.Value.RepositoryId, Is.EqualTo(repositoryId))
        Assert.That(envelope.Value.ActorId, Is.EqualTo(operationResult.Session.AgentId))

        use payload = JsonDocument.Parse(envelope.Value.DataJson)

        let payloadSessionId =
            payload
                .RootElement
                .GetProperty("Session")
                .GetProperty("SessionId")
                .GetString()

        Assert.That(payloadSessionId, Is.EqualTo(operationResult.Session.SessionId))
