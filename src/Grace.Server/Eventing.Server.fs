namespace Grace.Server

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Artifact
open Grace.Types.Automation
open Grace.Types.Events
open Grace.Types.Policy
open Grace.Types.PromotionGroup
open Grace.Types.PromotionSet
open Grace.Types.Queue
open Grace.Types.Reference
open Grace.Types.Review
open Grace.Types.Types
open Grace.Types.Validation
open Grace.Types.WorkItem
open System

module EventingPublisher =
    let private tryGetRepositoryId (metadata: EventMetadata) =
        match metadata.Properties.TryGetValue(nameof RepositoryId) with
        | true, repositoryId ->
            match Guid.TryParse(repositoryId) with
            | true, value -> Some value
            | _ -> Option.None
        | _ -> Option.None

    let private tryGetActorId (metadata: EventMetadata) (defaultActorId: string) =
        match metadata.Properties.TryGetValue("ActorId") with
        | true, actorId when String.IsNullOrWhiteSpace actorId |> not -> actorId
        | _ -> defaultActorId

    let private envelope
        (eventType: AutomationEventType)
        (metadata: EventMetadata)
        (ownerId: OwnerId)
        (organizationId: OrganizationId)
        (repositoryId: RepositoryId)
        (actorId: string)
        (dataJson: string)
        =
        AutomationEventEnvelope.Create eventType metadata.Timestamp metadata.CorrelationId ownerId organizationId repositoryId actorId dataJson

    let private tryGetTerminalPromotionSetId (links: ReferenceLinkType seq) =
        links
        |> Seq.tryPick (fun link ->
            match link with
            | ReferenceLinkType.PromotionSetTerminal promotionSetId -> Some promotionSetId
            | ReferenceLinkType.PromotionGroupTerminal promotionSetId -> Some promotionSetId
            | _ -> Option.None)

    let private mapPromotionSetEventType (eventType: PromotionSetEventType) =
        match eventType with
        | PromotionSetEventType.Created _ -> AutomationEventType.PromotionSetCreated
        | PromotionSetEventType.InputPromotionsUpdated _ -> AutomationEventType.PromotionSetUpdated
        | PromotionSetEventType.RecomputeStarted _ -> AutomationEventType.PromotionSetRecomputeStarted
        | PromotionSetEventType.StepsUpdated _ -> AutomationEventType.PromotionSetStepsUpdated
        | PromotionSetEventType.RecomputeFailed _ -> AutomationEventType.PromotionSetRecomputeFailed
        | PromotionSetEventType.Blocked _ -> AutomationEventType.PromotionSetBlocked
        | PromotionSetEventType.ApplyStarted -> AutomationEventType.PromotionSetApplyStarted
        | PromotionSetEventType.Applied _ -> AutomationEventType.PromotionSetApplied
        | PromotionSetEventType.ApplyFailed _ -> AutomationEventType.PromotionSetApplyFailed
        | PromotionSetEventType.LogicalDeleted _ -> AutomationEventType.PromotionSetUpdated

    let tryCreateEnvelope (graceEvent: GraceEvent) =
        match graceEvent with
        | PromotionSetEvent promotionSetEvent ->
            let eventType = mapPromotionSetEventType promotionSetEvent.Event

            envelope
                eventType
                promotionSetEvent.Metadata
                OwnerId.Empty
                OrganizationId.Empty
                (tryGetRepositoryId promotionSetEvent.Metadata
                 |> Option.defaultValue RepositoryId.Empty)
                (tryGetActorId promotionSetEvent.Metadata "PromotionSet")
                (serialize promotionSetEvent)
            |> Some
        | ValidationSetEvent validationSetEvent ->
            let eventType =
                match validationSetEvent.Event with
                | ValidationSetEventType.Created _ -> AutomationEventType.ValidationSetCreated
                | ValidationSetEventType.Updated _
                | ValidationSetEventType.LogicalDeleted _ -> AutomationEventType.ValidationSetUpdated

            envelope
                eventType
                validationSetEvent.Metadata
                OwnerId.Empty
                OrganizationId.Empty
                (tryGetRepositoryId validationSetEvent.Metadata
                 |> Option.defaultValue RepositoryId.Empty)
                (tryGetActorId validationSetEvent.Metadata "ValidationSet")
                (serialize validationSetEvent)
            |> Some
        | ValidationResultEvent validationResultEvent ->
            envelope
                AutomationEventType.ValidationResultRecorded
                validationResultEvent.Metadata
                OwnerId.Empty
                OrganizationId.Empty
                (tryGetRepositoryId validationResultEvent.Metadata
                 |> Option.defaultValue RepositoryId.Empty)
                (tryGetActorId validationResultEvent.Metadata "ValidationResult")
                (serialize validationResultEvent)
            |> Some
        | ArtifactEvent artifactEvent ->
            envelope
                AutomationEventType.ArtifactCreated
                artifactEvent.Metadata
                OwnerId.Empty
                OrganizationId.Empty
                (tryGetRepositoryId artifactEvent.Metadata
                 |> Option.defaultValue RepositoryId.Empty)
                (tryGetActorId artifactEvent.Metadata "Artifact")
                (serialize artifactEvent)
            |> Some
        | QueueEvent queueEvent ->
            let eventType =
                match queueEvent.Event with
                | PromotionQueueEventType.PromotionSetEnqueued _ -> Some AutomationEventType.PromotionSetEnqueued
                | PromotionQueueEventType.PromotionSetDequeued _ -> Some AutomationEventType.PromotionSetDequeued
                | _ -> Option.None

            eventType
            |> Option.map (fun mappedType ->
                envelope
                    mappedType
                    queueEvent.Metadata
                    OwnerId.Empty
                    OrganizationId.Empty
                    (tryGetRepositoryId queueEvent.Metadata
                     |> Option.defaultValue RepositoryId.Empty)
                    (tryGetActorId queueEvent.Metadata "PromotionQueue")
                    (serialize queueEvent))
        | CandidateEvent candidateEvent ->
            let eventType =
                match candidateEvent.Event with
                | CandidateEventType.Initialized _ -> AutomationEventType.PromotionSetCreated
                | CandidateEventType.StatusSet _
                | CandidateEventType.RequiredActionsSet _
                | CandidateEventType.RequiredActionAdded _
                | CandidateEventType.RequiredActionsCleared
                | CandidateEventType.GateAttestationAdded _
                | CandidateEventType.ConflictAdded _
                | CandidateEventType.ConflictReceiptAdded _ -> AutomationEventType.PromotionSetUpdated

            envelope
                eventType
                candidateEvent.Metadata
                OwnerId.Empty
                OrganizationId.Empty
                (tryGetRepositoryId candidateEvent.Metadata
                 |> Option.defaultValue RepositoryId.Empty)
                (tryGetActorId candidateEvent.Metadata "IntegrationCandidate")
                (serialize candidateEvent)
            |> Some
        | PromotionGroupEvent promotionGroupEvent ->
            let eventType =
                match promotionGroupEvent.Event with
                | PromotionGroupEventType.Created _ -> AutomationEventType.PromotionSetCreated
                | PromotionGroupEventType.Started -> AutomationEventType.PromotionSetApplyStarted
                | PromotionGroupEventType.Completed success ->
                    if success then
                        AutomationEventType.PromotionSetApplied
                    else
                        AutomationEventType.PromotionSetApplyFailed
                | PromotionGroupEventType.Blocked _ -> AutomationEventType.PromotionSetBlocked
                | PromotionGroupEventType.ConflictAnalyzed _ -> AutomationEventType.PromotionSetBlocked
                | _ -> AutomationEventType.PromotionSetUpdated

            envelope
                eventType
                promotionGroupEvent.Metadata
                OwnerId.Empty
                OrganizationId.Empty
                (tryGetRepositoryId promotionGroupEvent.Metadata
                 |> Option.defaultValue RepositoryId.Empty)
                (tryGetActorId promotionGroupEvent.Metadata "PromotionGroup")
                (serialize promotionGroupEvent)
            |> Some
        | ReviewEvent reviewEvent ->
            let ownerId, organizationId, repositoryId, eventType =
                match reviewEvent.Event with
                | ReviewEventType.PacketUpserted packet -> packet.OwnerId, packet.OrganizationId, packet.RepositoryId, AutomationEventType.ReviewNotesUpdated
                | ReviewEventType.CheckpointAdded _ ->
                    OwnerId.Empty,
                    OrganizationId.Empty,
                    (tryGetRepositoryId reviewEvent.Metadata
                     |> Option.defaultValue RepositoryId.Empty),
                    AutomationEventType.ReviewCheckpointRecorded
                | ReviewEventType.FindingResolved _ ->
                    OwnerId.Empty,
                    OrganizationId.Empty,
                    (tryGetRepositoryId reviewEvent.Metadata
                     |> Option.defaultValue RepositoryId.Empty),
                    AutomationEventType.ReviewNotesUpdated

            envelope eventType reviewEvent.Metadata ownerId organizationId repositoryId (tryGetActorId reviewEvent.Metadata "Review") (serialize reviewEvent)
            |> Some
        | Stage0Event _
        | GateAttestationEvent _ -> Option.None
        | ReferenceEvent referenceEvent ->
            match referenceEvent.Event with
            | ReferenceEventType.Created (referenceId, ownerId, organizationId, repositoryId, branchId, _, _, referenceType, _, links) ->
                if referenceType = ReferenceType.Promotion then
                    match tryGetTerminalPromotionSetId links with
                    | Some promotionSetId ->
                        let payload = {| promotionSetId = promotionSetId; targetBranchId = branchId; terminalPromotionReferenceId = referenceId |}

                        envelope
                            AutomationEventType.PromotionSetApplied
                            referenceEvent.Metadata
                            ownerId
                            organizationId
                            repositoryId
                            (tryGetActorId referenceEvent.Metadata "Reference")
                            (serialize payload)
                        |> Some
                    | Option.None -> Option.None
                else
                    Option.None
            | _ -> Option.None
        | WorkItemEvent workItemEvent ->
            envelope
                AutomationEventType.ReviewNotesUpdated
                workItemEvent.Metadata
                OwnerId.Empty
                OrganizationId.Empty
                (tryGetRepositoryId workItemEvent.Metadata
                 |> Option.defaultValue RepositoryId.Empty)
                (tryGetActorId workItemEvent.Metadata "WorkItem")
                (serialize workItemEvent)
            |> Some
        | PolicyEvent _
        | OwnerEvent _
        | BranchEvent _
        | DirectoryVersionEvent _
        | OrganizationEvent _
        | RepositoryEvent _
        | ConflictReceiptEvent _ -> Option.None
