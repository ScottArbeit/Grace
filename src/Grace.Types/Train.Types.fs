namespace Grace.Types

open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Types
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

module Train =

    [<GenerateSerializer>]
    type TrainDto =
        { PromotionTrainId: PromotionTrainId
          RepositoryId: RepositoryId
          TargetBranchId: BranchId
          BaseReferenceId: ReferenceId
          Queue: ChangeId array
          CreatedAt: Instant
          ScheduledAt: Instant option }

        static member Default =
            { PromotionTrainId = PromotionTrainId.Empty
              RepositoryId = RepositoryId.Empty
              TargetBranchId = BranchId.Empty
              BaseReferenceId = ReferenceId.Empty
              Queue = Array.empty
              CreatedAt = Constants.DefaultTimestamp
              ScheduledAt = None }

    [<KnownType("GetKnownTypes")>]
    type TrainCommand =
        | Create of
            promotionTrainId: PromotionTrainId *
            repositoryId: RepositoryId *
            targetBranchId: BranchId *
            baseReferenceId: ReferenceId *
            scheduledAt: Instant option
        | Enqueue of changeId: ChangeId
        | Dequeue of changeId: ChangeId
        | Remove of changeId: ChangeId
        | SetBaseReference of referenceId: ReferenceId
        | Schedule of scheduledAt: Instant option

        static member GetKnownTypes() = GetKnownTypes<TrainCommand>()

    [<KnownType("GetKnownTypes")>]
    type TrainEventType =
        | Created of PromotionTrainId * RepositoryId * BranchId * ReferenceId * Instant option
        | Enqueued of ChangeId
        | Dequeued of ChangeId
        | Removed of ChangeId
        | BaseReferenceSet of ReferenceId
        | Scheduled of Instant option

        static member GetKnownTypes() = GetKnownTypes<TrainEventType>()

    type TrainEvent =
        { Event: TrainEventType
          Metadata: EventMetadata }

    type TrainDto with
        static member UpdateDto trainEvent currentDto =
            match trainEvent.Event with
            | Created(trainId, repositoryId, targetBranchId, baseReferenceId, scheduledAt) ->
                { currentDto with
                    PromotionTrainId = trainId
                    RepositoryId = repositoryId
                    TargetBranchId = targetBranchId
                    BaseReferenceId = baseReferenceId
                    ScheduledAt = scheduledAt
                    CreatedAt = trainEvent.Metadata.Timestamp }
            | Enqueued changeId ->
                { currentDto with Queue = Array.append currentDto.Queue [| changeId |] }
            | Dequeued changeId ->
                { currentDto with Queue = currentDto.Queue |> Array.filter (fun item -> item <> changeId) }
            | Removed changeId ->
                { currentDto with Queue = currentDto.Queue |> Array.filter (fun item -> item <> changeId) }
            | BaseReferenceSet referenceId ->
                { currentDto with BaseReferenceId = referenceId }
            | Scheduled scheduledAt ->
                { currentDto with ScheduledAt = scheduledAt }
