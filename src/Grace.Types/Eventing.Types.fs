namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Types
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

module Eventing =
    /// Event types for orchestrator-facing notifications.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type EventType =
        | PolicySnapshotCreated
        | PolicyAckRequired
        | WorkItemUpdated
        | CandidateEnqueued
        | CandidateStateChanged
        | ReviewPacketUpdated
        | GateCompleted
        | QueuePaused
        | QueueResumed
        | ConflictDetected
        | ConflictResolved
        | PromotionApplied

        static member GetKnownTypes() = GetKnownTypes<EventType>()

    /// Envelope for push events (SignalR/webhook).
    [<GenerateSerializer>]
    type EventEnvelope =
        { EventType: EventType
          Timestamp: Instant
          CorrelationId: CorrelationId
          OwnerId: OwnerId
          OrganizationId: OrganizationId
          RepositoryId: RepositoryId
          TargetBranchId: BranchId option
          CandidateId: CandidateId option
          WorkItemId: WorkItemId option
          PromotionGroupId: PromotionGroup.PromotionGroupId option
          Payload: string }
