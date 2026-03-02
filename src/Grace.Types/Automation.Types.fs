namespace Grace.Types

open Grace.Shared.Utilities
open Grace.Types.Types
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

module Automation =

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

        static member GetKnownTypes() = GetKnownTypes<AutomationEventType>()

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type AgentSessionLifecycleState =
        | Inactive
        | Active
        | Stopping
        | Stopped

        static member GetKnownTypes() = GetKnownTypes<AgentSessionLifecycleState>()

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

    [<GenerateSerializer>]
    type AgentSessionOperationResult =
        {
            Session: AgentSessionInfo
            Message: string
            OperationId: string
            WasIdempotentReplay: bool
        }

        static member Default =
            {
                Session = AgentSessionInfo.Default
                Message = String.Empty
                OperationId = String.Empty
                WasIdempotentReplay = false
            }

    [<GenerateSerializer>]
    type AgentSessionListResult =
        {
            Sessions: AgentSessionInfo list
            Count: int
            Message: string
        }

        static member Default =
            {
                Sessions = []
                Count = 0
                Message = String.Empty
            }

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
