namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Policy
open Grace.Types.PromotionGroup
open Grace.Types.RequiredAction
open Grace.Types.Review
open Grace.Types.Types
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

module Queue =
    /// Current status of an integration candidate.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type CandidateStatus =
        | Pending
        | Ready
        | Running
        | Blocked
        | Failed
        | Succeeded
        | Quarantined
        | Canceled

        static member GetKnownTypes() = GetKnownTypes<CandidateStatus>()

    /// Queue state for a target branch.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type QueueState =
        | Idle
        | Running
        | Paused
        | Degraded

        static member GetKnownTypes() = GetKnownTypes<QueueState>()

    /// Execution mode for a gate.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type GateExecutionMode =
        | Synchronous
        | AsyncCallback

        static member GetKnownTypes() = GetKnownTypes<GateExecutionMode>()

    /// Outcome for a gate run.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type GateResult =
        | Pass
        | Fail
        | Block
        | Skipped

        static member GetKnownTypes() = GetKnownTypes<GateResult>()

    /// Definition for a gate.
    [<GenerateSerializer>]
    type GateDefinition = { Name: string; Version: string; ExecutionMode: GateExecutionMode }

    /// Attestation record for a gate run.
    [<GenerateSerializer>]
    type GateAttestation =
        { GateAttestationId: GateAttestationId
          OwnerId: OwnerId
          OrganizationId: OrganizationId
          RepositoryId: RepositoryId
          CandidateId: CandidateId
          PolicySnapshotId: PolicySnapshotId
          BaselineHeadReferenceId: ReferenceId
          GateName: string
          GateVersion: string
          Result: GateResult
          ArtifactUris: string list
          Summary: string
          Timestamp: Instant
          Principal: string }

        static member Default =
            { GateAttestationId = Guid.Empty
              OwnerId = OwnerId.Empty
              OrganizationId = OrganizationId.Empty
              RepositoryId = RepositoryId.Empty
              CandidateId = CandidateId.Empty
              PolicySnapshotId = PolicySnapshotId String.Empty
              BaselineHeadReferenceId = ReferenceId.Empty
              GateName = String.Empty
              GateVersion = String.Empty
              Result = GateResult.Skipped
              ArtifactUris = []
              Summary = String.Empty
              Timestamp = Constants.DefaultTimestamp
              Principal = String.Empty }

    /// Defines the commands for the GateAttestation actor.
    [<KnownType("GetKnownTypes")>]
    type GateAttestationCommand =
        | Create of attestation: GateAttestation

        static member GetKnownTypes() = GetKnownTypes<GateAttestationCommand>()

    /// Defines the events for the GateAttestation actor.
    [<KnownType("GetKnownTypes")>]
    type GateAttestationEventType =
        | Created of attestation: GateAttestation

        static member GetKnownTypes() = GetKnownTypes<GateAttestationEventType>()

    /// Record that holds the event type and metadata for a GateAttestation event.
    type GateAttestationEvent =
        {
            /// The GateAttestationEventType case that describes the event.
            Event: GateAttestationEventType
            /// The EventMetadata for the event. EventMetadata includes the Timestamp, CorrelationId, Principal, and a Properties dictionary.
            Metadata: EventMetadata
        }

    /// Updates the GateAttestation based on the GateAttestationEvent.
    module GateAttestationDto =
        let UpdateDto (attestationEvent: GateAttestationEvent) (current: GateAttestation) =
            match attestationEvent.Event with
            | Created attestation -> attestation

    /// Conflict resolution receipt for a candidate.
    [<GenerateSerializer>]
    type ConflictReceipt =
        { ConflictReceiptId: ConflictReceiptId
          OwnerId: OwnerId
          OrganizationId: OrganizationId
          RepositoryId: RepositoryId
          CandidateId: CandidateId
          FilePath: string
          Resolution: ConflictResolutionOutcome
          AcceptedBy: UserId option
          Timestamp: Instant
          Notes: string option }

        static member Default =
            { ConflictReceiptId = Guid.Empty
              OwnerId = OwnerId.Empty
              OrganizationId = OrganizationId.Empty
              RepositoryId = RepositoryId.Empty
              CandidateId = CandidateId.Empty
              FilePath = String.Empty
              Resolution = ConflictResolutionOutcome.Default
              AcceptedBy = None
              Timestamp = Constants.DefaultTimestamp
              Notes = None }

    /// Defines the commands for the ConflictReceipt actor.
    [<KnownType("GetKnownTypes")>]
    type ConflictReceiptCommand =
        | Create of receipt: ConflictReceipt

        static member GetKnownTypes() = GetKnownTypes<ConflictReceiptCommand>()

    /// Defines the events for the ConflictReceipt actor.
    [<KnownType("GetKnownTypes")>]
    type ConflictReceiptEventType =
        | Created of receipt: ConflictReceipt

        static member GetKnownTypes() = GetKnownTypes<ConflictReceiptEventType>()

    /// Record that holds the event type and metadata for a ConflictReceipt event.
    type ConflictReceiptEvent =
        {
            /// The ConflictReceiptEventType case that describes the event.
            Event: ConflictReceiptEventType
            /// The EventMetadata for the event. EventMetadata includes the Timestamp, CorrelationId, Principal, and a Properties dictionary.
            Metadata: EventMetadata
        }

    /// Updates the ConflictReceipt based on the ConflictReceiptEvent.
    module ConflictReceiptDto =
        let UpdateDto (receiptEvent: ConflictReceiptEvent) (current: ConflictReceipt) =
            match receiptEvent.Event with
            | Created receipt -> receipt

    /// Represents an integration candidate queued for promotion.
    [<GenerateSerializer>]
    type IntegrationCandidate =
        { Class: string
          CandidateId: CandidateId
          OwnerId: OwnerId
          OrganizationId: OrganizationId
          RepositoryId: RepositoryId
          WorkItemId: WorkItemId
          PromotionGroupId: PromotionGroupId option
          TargetBranchId: BranchId
          PolicySnapshotId: PolicySnapshotId
          BaselineHeadReferenceId: ReferenceId
          Status: CandidateStatus
          RequiredActions: RequiredActionDto list
          ReviewPacketId: ReviewPacketId option
          LastCheckpointId: ReviewCheckpointId option
          GateAttestationIds: GateAttestationId list
          Conflicts: ConflictAnalysis list
          ConflictReceiptIds: ConflictReceiptId list
          CreatedAt: Instant
          UpdatedAt: Instant option }

        static member Default =
            { Class = nameof IntegrationCandidate
              CandidateId = CandidateId.Empty
              OwnerId = OwnerId.Empty
              OrganizationId = OrganizationId.Empty
              RepositoryId = RepositoryId.Empty
              WorkItemId = WorkItemId.Empty
              PromotionGroupId = None
              TargetBranchId = BranchId.Empty
              PolicySnapshotId = PolicySnapshotId String.Empty
              BaselineHeadReferenceId = ReferenceId.Empty
              Status = CandidateStatus.Pending
              RequiredActions = []
              ReviewPacketId = None
              LastCheckpointId = None
              GateAttestationIds = []
              Conflicts = []
              ConflictReceiptIds = []
              CreatedAt = Constants.DefaultTimestamp
              UpdatedAt = None }

    /// Defines the commands for the IntegrationCandidate actor.
    [<KnownType("GetKnownTypes")>]
    type CandidateCommand =
        | Initialize of candidate: IntegrationCandidate
        | SetStatus of status: CandidateStatus
        | SetRequiredActions of actions: RequiredActionDto list
        | AddRequiredAction of action: RequiredActionDto
        | ClearRequiredActions
        | AddGateAttestation of gateAttestationId: GateAttestationId
        | AddConflict of conflict: ConflictAnalysis
        | AddConflictReceipt of conflictReceiptId: ConflictReceiptId

        static member GetKnownTypes() = GetKnownTypes<CandidateCommand>()

    /// Defines the events for the IntegrationCandidate actor.
    [<KnownType("GetKnownTypes")>]
    type CandidateEventType =
        | Initialized of candidate: IntegrationCandidate
        | StatusSet of status: CandidateStatus
        | RequiredActionsSet of actions: RequiredActionDto list
        | RequiredActionAdded of action: RequiredActionDto
        | RequiredActionsCleared
        | GateAttestationAdded of gateAttestationId: GateAttestationId
        | ConflictAdded of conflict: ConflictAnalysis
        | ConflictReceiptAdded of conflictReceiptId: ConflictReceiptId

        static member GetKnownTypes() = GetKnownTypes<CandidateEventType>()

    /// Record that holds the event type and metadata for a candidate event.
    type CandidateEvent =
        {
            /// The CandidateEventType case that describes the event.
            Event: CandidateEventType
            /// The EventMetadata for the event. EventMetadata includes the Timestamp, CorrelationId, Principal, and a Properties dictionary.
            Metadata: EventMetadata
        }

    /// Updates the IntegrationCandidate based on a CandidateEvent.
    module IntegrationCandidateDto =
        let UpdateDto (candidateEvent: CandidateEvent) (currentCandidate: IntegrationCandidate) =
            let updated =
                match candidateEvent.Event with
                | Initialized candidate -> candidate
                | StatusSet status -> { currentCandidate with Status = status }
                | RequiredActionsSet actions -> { currentCandidate with RequiredActions = actions }
                | RequiredActionAdded action -> { currentCandidate with RequiredActions = currentCandidate.RequiredActions @ [ action ] }
                | RequiredActionsCleared -> { currentCandidate with RequiredActions = [] }
                | GateAttestationAdded gateAttestationId ->
                    { currentCandidate with
                        GateAttestationIds =
                            currentCandidate.GateAttestationIds
                            |> List.append [ gateAttestationId ]
                            |> List.distinct }
                | ConflictAdded conflict -> { currentCandidate with Conflicts = currentCandidate.Conflicts |> List.append [ conflict ] }
                | ConflictReceiptAdded conflictReceiptId ->
                    { currentCandidate with
                        ConflictReceiptIds =
                            currentCandidate.ConflictReceiptIds
                            |> List.append [ conflictReceiptId ]
                            |> List.distinct }

            { updated with UpdatedAt = Some candidateEvent.Metadata.Timestamp }

    /// Promotion queue for a target branch.
    [<GenerateSerializer>]
    type PromotionQueue =
        { Class: string
          TargetBranchId: BranchId
          CandidateIds: CandidateId list
          RunningCandidateId: CandidateId option
          State: QueueState
          PolicySnapshotId: PolicySnapshotId
          UpdatedAt: Instant option }

        static member Default =
            { Class = nameof PromotionQueue
              TargetBranchId = BranchId.Empty
              CandidateIds = []
              RunningCandidateId = None
              State = QueueState.Idle
              PolicySnapshotId = PolicySnapshotId String.Empty
              UpdatedAt = None }

    /// Defines the commands for the PromotionQueue actor.
    [<KnownType("GetKnownTypes")>]
    type PromotionQueueCommand =
        | Initialize of targetBranchId: BranchId * policySnapshotId: PolicySnapshotId
        | Enqueue of candidateId: CandidateId
        | Dequeue of candidateId: CandidateId
        | SetRunning of candidateId: CandidateId option
        | Pause
        | Resume
        | SetDegraded
        | UpdatePolicySnapshot of policySnapshotId: PolicySnapshotId

        static member GetKnownTypes() = GetKnownTypes<PromotionQueueCommand>()

    /// Defines the events for the PromotionQueue actor.
    [<KnownType("GetKnownTypes")>]
    type PromotionQueueEventType =
        | Initialized of targetBranchId: BranchId * policySnapshotId: PolicySnapshotId
        | CandidateEnqueued of candidateId: CandidateId
        | CandidateDequeued of candidateId: CandidateId
        | RunningCandidateSet of candidateId: CandidateId option
        | Paused
        | Resumed
        | Degraded
        | PolicySnapshotUpdated of policySnapshotId: PolicySnapshotId

        static member GetKnownTypes() = GetKnownTypes<PromotionQueueEventType>()

    /// Record that holds the event type and metadata for a PromotionQueue event.
    type PromotionQueueEvent =
        {
            /// The PromotionQueueEventType case that describes the event.
            Event: PromotionQueueEventType
            /// The EventMetadata for the event. EventMetadata includes the Timestamp, CorrelationId, Principal, and a Properties dictionary.
            Metadata: EventMetadata
        }

    /// Updates the PromotionQueue based on the PromotionQueueEvent.
    module PromotionQueueDto =
        let UpdateDto (promotionQueueEvent: PromotionQueueEvent) (currentQueue: PromotionQueue) =
            let newQueue =
                match promotionQueueEvent.Event with
                | Initialized(targetBranchId, policySnapshotId) ->
                    { PromotionQueue.Default with TargetBranchId = targetBranchId; PolicySnapshotId = policySnapshotId }
                | CandidateEnqueued candidateId -> { currentQueue with CandidateIds = currentQueue.CandidateIds @ [ candidateId ] }
                | CandidateDequeued candidateId ->
                    { currentQueue with
                        CandidateIds =
                            currentQueue.CandidateIds
                            |> List.filter (fun existing -> existing <> candidateId) }
                | RunningCandidateSet candidateId -> { currentQueue with RunningCandidateId = candidateId; State = QueueState.Running }
                | Paused -> { currentQueue with State = QueueState.Paused }
                | Resumed ->
                    let newState =
                        match currentQueue.RunningCandidateId with
                        | Some _ -> QueueState.Running
                        | None -> QueueState.Idle

                    { currentQueue with State = newState }
                | Degraded -> { currentQueue with State = QueueState.Degraded }
                | PolicySnapshotUpdated policySnapshotId -> { currentQueue with PolicySnapshotId = policySnapshotId }

            { newQueue with UpdatedAt = Some promotionQueueEvent.Metadata.Timestamp }
