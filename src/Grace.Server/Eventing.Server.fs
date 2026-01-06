namespace Grace.Server

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Eventing
open Grace.Types.Events
open Grace.Types.Policy
open Grace.Types.Queue
open Grace.Types.Reference
open Grace.Types.Review
open Grace.Types.Types
open System

module EventingPublisher =
    let private envelope
        eventType
        (metadata: EventMetadata)
        ownerId
        organizationId
        repositoryId
        targetBranchId
        candidateId
        workItemId
        promotionGroupId
        payload
        =
        { EventType = eventType
          Timestamp = metadata.Timestamp
          CorrelationId = metadata.CorrelationId
          OwnerId = ownerId
          OrganizationId = organizationId
          RepositoryId = repositoryId
          TargetBranchId = targetBranchId
          CandidateId = candidateId
          WorkItemId = workItemId
          PromotionGroupId = promotionGroupId
          Payload = payload }

    let tryCreateEnvelope (graceEvent: GraceEvent) =
        match graceEvent with
        | PolicyEvent policyEvent ->
            match policyEvent.Event with
            | SnapshotCreated snapshot ->
                envelope
                    EventType.PolicySnapshotCreated
                    policyEvent.Metadata
                    snapshot.OwnerId
                    snapshot.OrganizationId
                    snapshot.RepositoryId
                    (Some snapshot.TargetBranchId)
                    None
                    None
                    None
                    (serialize snapshot)
                |> Some
            | Acknowledged(policySnapshotId, acknowledgedBy, note) ->
                envelope
                    EventType.PolicyAckRequired
                    policyEvent.Metadata
                    OwnerId.Empty
                    OrganizationId.Empty
                    RepositoryId.Empty
                    None
                    None
                    None
                    None
                    (serialize {| PolicySnapshotId = policySnapshotId; AcknowledgedBy = acknowledgedBy; Note = note |})
                |> Some
        | WorkItemEvent workItemEvent ->
            envelope
                EventType.WorkItemUpdated
                workItemEvent.Metadata
                OwnerId.Empty
                OrganizationId.Empty
                RepositoryId.Empty
                None
                None
                None
                None
                (serialize workItemEvent)
            |> Some
        | ReviewEvent reviewEvent ->
            envelope
                EventType.ReviewPacketUpdated
                reviewEvent.Metadata
                OwnerId.Empty
                OrganizationId.Empty
                RepositoryId.Empty
                None
                None
                None
                None
                (serialize reviewEvent)
            |> Some
        | CandidateEvent candidateEvent ->
            match candidateEvent.Event with
            | CandidateEventType.Initialized candidate ->
                envelope
                    EventType.CandidateEnqueued
                    candidateEvent.Metadata
                    candidate.OwnerId
                    candidate.OrganizationId
                    candidate.RepositoryId
                    (Some candidate.TargetBranchId)
                    (Some candidate.CandidateId)
                    (Some candidate.WorkItemId)
                    candidate.PromotionGroupId
                    (serialize candidate)
                |> Some
            | CandidateEventType.StatusSet _ ->
                envelope
                    EventType.CandidateStateChanged
                    candidateEvent.Metadata
                    OwnerId.Empty
                    OrganizationId.Empty
                    RepositoryId.Empty
                    None
                    None
                    None
                    None
                    (serialize candidateEvent)
                |> Some
            | _ ->
                envelope
                    EventType.CandidateStateChanged
                    candidateEvent.Metadata
                    OwnerId.Empty
                    OrganizationId.Empty
                    RepositoryId.Empty
                    None
                    None
                    None
                    None
                    (serialize candidateEvent)
                |> Some
        | GateAttestationEvent gateEvent ->
            envelope EventType.GateCompleted gateEvent.Metadata OwnerId.Empty OrganizationId.Empty RepositoryId.Empty None None None None (serialize gateEvent)
            |> Some
        | QueueEvent queueEvent ->
            let eventType =
                match queueEvent.Event with
                | PromotionQueueEventType.Paused -> EventType.QueuePaused
                | PromotionQueueEventType.Resumed -> EventType.QueueResumed
                | _ -> EventType.CandidateStateChanged

            envelope eventType queueEvent.Metadata OwnerId.Empty OrganizationId.Empty RepositoryId.Empty None None None None (serialize queueEvent)
            |> Some
        | ConflictReceiptEvent conflictEvent ->
            envelope
                EventType.ConflictResolved
                conflictEvent.Metadata
                OwnerId.Empty
                OrganizationId.Empty
                RepositoryId.Empty
                None
                None
                None
                None
                (serialize conflictEvent)
            |> Some
        | ReferenceEvent referenceEvent ->
            match referenceEvent.Event with
            | ReferenceEventType.Created(referenceId, ownerId, organizationId, repositoryId, branchId, _, _, referenceType, _, _) ->
                if referenceType = ReferenceType.Promotion then
                    envelope
                        EventType.PromotionApplied
                        referenceEvent.Metadata
                        ownerId
                        organizationId
                        repositoryId
                        (Some branchId)
                        None
                        None
                        None
                        (serialize referenceEvent)
                    |> Some
                else
                    None
            | _ -> None
        | _ -> None
