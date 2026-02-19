namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Policy
open Grace.Types.Types
open NodaTime
open Orleans
open System.Runtime.Serialization

module Queue =
    /// Queue state for a target branch.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type QueueState =
        | Idle
        | Running
        | Paused
        | Degraded

        static member GetKnownTypes() = GetKnownTypes<QueueState>()

    /// Promotion queue for a target branch.
    [<GenerateSerializer>]
    type PromotionQueue =
        {
            Class: string
            TargetBranchId: BranchId
            PromotionSetIds: PromotionSetId list
            RunningPromotionSetId: PromotionSetId option
            State: QueueState
            PolicySnapshotId: PolicySnapshotId
            UpdatedAt: Instant option
        }

        static member Default =
            {
                Class = nameof PromotionQueue
                TargetBranchId = BranchId.Empty
                PromotionSetIds = []
                RunningPromotionSetId = None
                State = QueueState.Idle
                PolicySnapshotId = PolicySnapshotId ""
                UpdatedAt = None
            }

    /// Defines the commands for the PromotionQueue actor.
    [<KnownType("GetKnownTypes")>]
    type PromotionQueueCommand =
        | Initialize of targetBranchId: BranchId * policySnapshotId: PolicySnapshotId
        | Enqueue of promotionSetId: PromotionSetId
        | Dequeue of promotionSetId: PromotionSetId
        | SetRunning of promotionSetId: PromotionSetId option
        | Pause
        | Resume
        | SetDegraded
        | UpdatePolicySnapshot of policySnapshotId: PolicySnapshotId

        static member GetKnownTypes() = GetKnownTypes<PromotionQueueCommand>()

    /// Defines the events for the PromotionQueue actor.
    [<KnownType("GetKnownTypes")>]
    type PromotionQueueEventType =
        | Initialized of targetBranchId: BranchId * policySnapshotId: PolicySnapshotId
        | PromotionSetEnqueued of promotionSetId: PromotionSetId
        | PromotionSetDequeued of promotionSetId: PromotionSetId
        | RunningPromotionSetSet of promotionSetId: PromotionSetId option
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
                | Initialized (targetBranchId, policySnapshotId) ->
                    { PromotionQueue.Default with TargetBranchId = targetBranchId; PolicySnapshotId = policySnapshotId }
                | PromotionSetEnqueued promotionSetId -> { currentQueue with PromotionSetIds = currentQueue.PromotionSetIds @ [ promotionSetId ] }
                | PromotionSetDequeued promotionSetId ->
                    { currentQueue with
                        PromotionSetIds =
                            currentQueue.PromotionSetIds
                            |> List.filter (fun existing -> existing <> promotionSetId)
                    }
                | RunningPromotionSetSet promotionSetId -> { currentQueue with RunningPromotionSetId = promotionSetId; State = QueueState.Running }
                | Paused -> { currentQueue with State = QueueState.Paused }
                | Resumed ->
                    let newState =
                        match currentQueue.RunningPromotionSetId with
                        | Some _ -> QueueState.Running
                        | None -> QueueState.Idle

                    { currentQueue with State = newState }
                | Degraded -> { currentQueue with State = QueueState.Degraded }
                | PolicySnapshotUpdated policySnapshotId -> { currentQueue with PolicySnapshotId = policySnapshotId }

            { newQueue with UpdatedAt = Some promotionQueueEvent.Metadata.Timestamp }
