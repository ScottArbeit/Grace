namespace Grace.Server.Tests

open Grace.Server
open Grace.Types.Common
open Grace.Types.Queue
open NodaTime
open NUnit.Framework
open System

/// Covers queue Promotion Set behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type QueuePromotionSetTests() =
    /// Verifies that queue Initialization Requires Policy Snapshot When Queue Missing.
    [<Test>]
    member _.QueueInitializationRequiresPolicySnapshotWhenQueueMissing() =
        let requiresSnapshot = Grace.Server.Queue.requiresPolicySnapshotForInitialization false ""

        let noRequirementWhenProvided = Grace.Server.Queue.requiresPolicySnapshotForInitialization false "snapshot"

        let noRequirementWhenQueueExists = Grace.Server.Queue.requiresPolicySnapshotForInitialization true ""

        Assert.That(requiresSnapshot, Is.True)
        Assert.That(noRequirementWhenProvided, Is.False)
        Assert.That(noRequirementWhenQueueExists, Is.False)

    /// Verifies that queue status with no caller-visible PromotionSets matches idle no-work shape.
    [<Test>]
    member _.QueueStatusProjectionHidesPrivateOnlyWorkAsIdleNoWork() =
        let hiddenPromotionSetId: PromotionSetId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")

        let queue =
            { PromotionQueue.Default with
                TargetBranchId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
                PromotionSetIds = [ hiddenPromotionSetId ]
                RunningPromotionSetId = Some hiddenPromotionSetId
                State = QueueState.Running
                UpdatedAt = Some(Instant.FromUtc(2026, 7, 8, 8, 0))
            }

        let projected = Grace.Server.Queue.projectObservableQueueStatus queue [] None

        Assert.That(projected.PromotionSetIds, Is.Empty)
        Assert.That(projected.RunningPromotionSetId, Is.EqualTo(None))
        Assert.That(projected.State, Is.EqualTo(QueueState.Idle))
        Assert.That(projected.UpdatedAt, Is.EqualTo(None))

    /// Verifies that queue status preserves explicit visible running work.
    [<Test>]
    member _.QueueStatusProjectionKeepsVisibleRunningWork() =
        let visiblePromotionSetId: PromotionSetId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")
        let hiddenPromotionSetId: PromotionSetId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd")
        let updatedAt = Instant.FromUtc(2026, 7, 8, 8, 30)

        let queue =
            { PromotionQueue.Default with
                PromotionSetIds =
                    [
                        hiddenPromotionSetId
                        visiblePromotionSetId
                    ]
                RunningPromotionSetId = Some visiblePromotionSetId
                State = QueueState.Running
                UpdatedAt = Some updatedAt
            }

        let projected = Grace.Server.Queue.projectObservableQueueStatus queue [ visiblePromotionSetId ] (Some visiblePromotionSetId)

        Assert.That(projected.PromotionSetIds.Length, Is.EqualTo(1))
        Assert.That(projected.PromotionSetIds[0], Is.EqualTo(visiblePromotionSetId))
        Assert.That(projected.RunningPromotionSetId, Is.EqualTo(Some visiblePromotionSetId))
        Assert.That(projected.State, Is.EqualTo(QueueState.Running))
        Assert.That(projected.UpdatedAt, Is.EqualTo(Some updatedAt))
