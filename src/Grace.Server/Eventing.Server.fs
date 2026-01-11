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
open Grace.Types.WorkItem
open System

module EventingPublisher =
    let private tryGetRepositoryId (metadata: EventMetadata) =
        match metadata.Properties.TryGetValue(nameof RepositoryId) with
        | true, repositoryId ->
            match Guid.TryParse(repositoryId) with
            | true, value -> Some value
            | _ -> None
        | _ -> None

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
        {
            EventType = eventType
            Timestamp = metadata.Timestamp
            CorrelationId = metadata.CorrelationId
            OwnerId = ownerId
            OrganizationId = organizationId
            RepositoryId = repositoryId
            TargetBranchId = targetBranchId
            CandidateId = candidateId
            WorkItemId = workItemId
            PromotionGroupId = promotionGroupId
            Payload = payload
        }

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
            | Acknowledged (policySnapshotId, acknowledgedBy, note) ->
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
            let ownerId, organizationId, repositoryId =
                match workItemEvent.Event with
                | WorkItemEventType.Created (_, ownerId, organizationId, repositoryId, _, _) -> ownerId, organizationId, repositoryId
                | _ ->
                    OwnerId.Empty,
                    OrganizationId.Empty,
                    (tryGetRepositoryId workItemEvent.Metadata
                     |> Option.defaultValue RepositoryId.Empty)

            envelope EventType.WorkItemUpdated workItemEvent.Metadata ownerId organizationId repositoryId None None None None (serialize workItemEvent)
            |> Some
        | ReviewEvent reviewEvent ->
            let ownerId, organizationId, repositoryId =
                match reviewEvent.Event with
                | ReviewEventType.PacketUpserted packet -> packet.OwnerId, packet.OrganizationId, packet.RepositoryId
                | _ ->
                    OwnerId.Empty,
                    OrganizationId.Empty,
                    (tryGetRepositoryId reviewEvent.Metadata
                     |> Option.defaultValue RepositoryId.Empty)

            envelope EventType.ReviewPacketUpdated reviewEvent.Metadata ownerId organizationId repositoryId None None None None (serialize reviewEvent)
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
                    (tryGetRepositoryId candidateEvent.Metadata
                     |> Option.defaultValue RepositoryId.Empty)
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
                    (tryGetRepositoryId candidateEvent.Metadata
                     |> Option.defaultValue RepositoryId.Empty)
                    None
                    None
                    None
                    None
                    (serialize candidateEvent)
                |> Some
        | GateAttestationEvent gateEvent ->
            let ownerId, organizationId, repositoryId =
                match gateEvent.Event with
                | GateAttestationEventType.Created attestation -> attestation.OwnerId, attestation.OrganizationId, attestation.RepositoryId

            envelope EventType.GateCompleted gateEvent.Metadata ownerId organizationId repositoryId None None None None (serialize gateEvent)
            |> Some
        | QueueEvent queueEvent ->
            let eventType =
                match queueEvent.Event with
                | PromotionQueueEventType.Paused -> EventType.QueuePaused
                | PromotionQueueEventType.Resumed -> EventType.QueueResumed
                | _ -> EventType.CandidateStateChanged

            let repositoryId =
                tryGetRepositoryId queueEvent.Metadata
                |> Option.defaultValue RepositoryId.Empty

            envelope eventType queueEvent.Metadata OwnerId.Empty OrganizationId.Empty repositoryId None None None None (serialize queueEvent)
            |> Some
        | ConflictReceiptEvent conflictEvent ->
            let ownerId, organizationId, repositoryId =
                match conflictEvent.Event with
                | ConflictReceiptEventType.Created receipt -> receipt.OwnerId, receipt.OrganizationId, receipt.RepositoryId

            envelope EventType.ConflictResolved conflictEvent.Metadata ownerId organizationId repositoryId None None None None (serialize conflictEvent)
            |> Some
        | ReferenceEvent referenceEvent ->
            match referenceEvent.Event with
            | ReferenceEventType.Created (referenceId, ownerId, organizationId, repositoryId, branchId, _, _, referenceType, _, _) ->
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
