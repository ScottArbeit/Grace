namespace Grace.Types

open Grace.Shared.Utilities
open Grace.Types.Common
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

/// Contains automation helpers.
module Automation =

    /// Represents automation event type.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type AutomationEventType =
        | PromotionSetCreated
        | PromotionSetUpdated
        | PromotionSetEnqueued
        | PromotionSetDequeued
        | PromotionSetStepsUpdated
        | PromotionSetRecomputeStarted
        | PromotionSetRecomputeSucceeded
        | PromotionSetRecomputeFailed
        | PromotionSetApplyStarted
        | PromotionSetApplied
        | PromotionSetApplyFailed
        | PromotionSetBlocked
        | ValidationRequested
        | ValidationResultRecorded
        | ValidationSetCreated
        | ValidationSetUpdated
        | ArtifactCreated
        | ReviewNotesUpdated
        | ReviewCheckpointRecorded
        | AgentWorkStarted
        | AgentWorkStopped
        | AgentSummaryAdded
        | AgentBootstrapped

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<AutomationEventType>()

    /// Represents agent session lifecycle state.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type AgentSessionLifecycleState =
        | Inactive
        | Active
        | Stopping
        | Stopped

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<AgentSessionLifecycleState>()

    /// Represents agent session info.
    [<GenerateSerializer>]
    type AgentSessionInfo =
        {
            SessionId: string
            AgentId: string
            AgentDisplayName: string
            WorkItemIdOrNumber: string
            PromotionSetId: string
            Source: string
            LifecycleState: AgentSessionLifecycleState
            StartedAt: Instant option
            LastUpdatedAt: Instant option
            StoppedAt: Instant option
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default =
            {
                SessionId = String.Empty
                AgentId = String.Empty
                AgentDisplayName = String.Empty
                WorkItemIdOrNumber = String.Empty
                PromotionSetId = String.Empty
                Source = String.Empty
                LifecycleState = AgentSessionLifecycleState.Inactive
                StartedAt = None
                LastUpdatedAt = None
                StoppedAt = None
            }

    /// Represents agent session operation result.
    [<GenerateSerializer>]
    type AgentSessionOperationResult =
        {
            Session: AgentSessionInfo
            Message: string
            OperationId: string
            WasIdempotentReplay: bool
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default = { Session = AgentSessionInfo.Default; Message = String.Empty; OperationId = String.Empty; WasIdempotentReplay = false }

    /// Represents agent session list result.
    [<GenerateSerializer>]
    type AgentSessionListResult =
        {
            Sessions: AgentSessionInfo list
            Count: int
            Message: string
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default = { Sessions = []; Count = 0; Message = String.Empty }

    /// Represents automation event envelope.
    [<GenerateSerializer>]
    type AutomationEventEnvelope =
        {
            EventId: Guid
            EventType: AutomationEventType
            EventTime: Instant
            CorrelationId: CorrelationId
            OwnerId: OwnerId
            OrganizationId: OrganizationId
            RepositoryId: RepositoryId
            ActorId: string
            DataJson: string
        }

        /// Builds the contract value from required caller inputs and generated defaults used by this surface.
        static member Create
            (eventType: AutomationEventType)
            (eventTime: Instant)
            (correlationId: CorrelationId)
            (ownerId: OwnerId)
            (organizationId: OrganizationId)
            (repositoryId: RepositoryId)
            (actorId: string)
            (dataJson: string)
            =
            {
                EventId = Guid.NewGuid()
                EventType = eventType
                EventTime = eventTime
                CorrelationId = correlationId
                OwnerId = ownerId
                OrganizationId = organizationId
                RepositoryId = repositoryId
                ActorId = actorId
                DataJson = dataJson
            }
