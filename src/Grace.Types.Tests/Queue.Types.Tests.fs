namespace Grace.Types.Tests

open Grace.Types.Policy
open Grace.Types.Queue
open Grace.Types.Types
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic

[<Parallelizable(ParallelScope.All)>]
type QueueTypesTests() =
    let metadata (timestamp: Instant) : EventMetadata =
        { Timestamp = timestamp; CorrelationId = "corr-queue"; Principal = "tester"; Properties = Dictionary<string, string>() }

    [<Test>]
    member _.PromotionQueueDtoUpdatesDeterministically() =
        let branchId = Guid.NewGuid()
        let promotionSetId = Guid.NewGuid()
        let snapshotId = PolicySnapshotId "policy"

        let events: PromotionQueueEvent list =
            [
                { Event = PromotionQueueEventType.Initialized(branchId, snapshotId); Metadata = metadata (Instant.FromUtc(2025, 1, 1, 0, 0)) }
                { Event = PromotionQueueEventType.PromotionSetEnqueued promotionSetId; Metadata = metadata (Instant.FromUtc(2025, 1, 1, 0, 1)) }
                { Event = PromotionQueueEventType.RunningPromotionSetSet(Some promotionSetId); Metadata = metadata (Instant.FromUtc(2025, 1, 1, 0, 2)) }
                { Event = PromotionQueueEventType.Paused; Metadata = metadata (Instant.FromUtc(2025, 1, 1, 0, 3)) }
                { Event = PromotionQueueEventType.Resumed; Metadata = metadata (Instant.FromUtc(2025, 1, 1, 0, 4)) }
            ]

        let finalState =
            events
            |> List.fold (fun state ev -> PromotionQueueDto.UpdateDto ev state) PromotionQueue.Default

        let secondPass =
            events
            |> List.fold (fun state ev -> PromotionQueueDto.UpdateDto ev state) PromotionQueue.Default

        Assert.That(finalState, Is.EqualTo(secondPass))
        Assert.That(finalState.TargetBranchId, Is.EqualTo(branchId))
        Assert.That(finalState.PromotionSetIds.Length, Is.EqualTo(1))
        Assert.That(finalState.PromotionSetIds[0], Is.EqualTo(promotionSetId))
        Assert.That(finalState.State, Is.EqualTo(QueueState.Running))

    [<Test>]
    member _.PromotionSetDequeuedRemovesFromQueue() =
        let promotionSetId = Guid.NewGuid()

        let queue =
            { PromotionQueue.Default with
                TargetBranchId = Guid.NewGuid()
                PromotionSetIds = [ promotionSetId ]
                RunningPromotionSetId = Some promotionSetId
                State = QueueState.Running
            }

        let event: PromotionQueueEvent =
            { Event = PromotionQueueEventType.PromotionSetDequeued promotionSetId; Metadata = metadata (Instant.FromUtc(2025, 1, 5, 0, 0)) }

        let updated = PromotionQueueDto.UpdateDto event queue

        Assert.That(updated.PromotionSetIds, Is.Empty)
