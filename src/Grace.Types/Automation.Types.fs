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

        static member GetKnownTypes() = GetKnownTypes<AutomationEventType>()

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
