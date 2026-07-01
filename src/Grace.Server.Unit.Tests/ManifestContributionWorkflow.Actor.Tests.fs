namespace Grace.Server.Tests

open Grace.Types.ManifestContributionWorkflow
open Grace.Types.Common
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic

module ManifestContributionWorkflowActor = Grace.Actors.ManifestContributionWorkflow

/// Covers manifest Contribution Workflow Actor behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type ManifestContributionWorkflowActorTests() =

    let timestamp = Instant.FromUtc(2026, 5, 24, 14, 0)
    let repositoryId = Guid.Parse("a6c6b60a-d4d2-40f6-8362-258dbe75a5bf")
    let storagePoolId = StoragePoolId "pool-main"
    let otherStoragePoolId = StoragePoolId "pool-archive"
    let manifestAddress = "manifest:blake3:alpha"

    let range0 = { StoragePoolId = storagePoolId; ContentBlockAddress = ContentBlockAddress "block-a"; OrdinalStart = 0; OrdinalCount = 8 }

    let range1 = { StoragePoolId = storagePoolId; ContentBlockAddress = ContentBlockAddress "block-b"; OrdinalStart = 8; OrdinalCount = 4 }

    /// Constructs metadata fixtures used by the server unit manifest Contribution Workflow Actor assertions.
    let metadata correlationId =
        {
            Timestamp = timestamp
            CorrelationId = correlationId
            Principal = "tester"
            ClientType = Microsoft.FSharp.Core.Option.None
            Properties = Dictionary<string, string>()
        }

    /// Builds start With Ranges test data for the server unit manifest Contribution Workflow Actor scenarios in this file.
    let startWithRanges direction ranges =
        ManifestContributionWorkflowCommand.Start("workflow-start", repositoryId, storagePoolId, manifestAddress, direction, ranges)

    /// Builds start With Operation test data for the server unit manifest Contribution Workflow Actor scenarios in this file.
    let startWithOperation operationId direction ranges =
        ManifestContributionWorkflowCommand.Start(operationId, repositoryId, storagePoolId, manifestAddress, direction, ranges)

    let start direction = startWithRanges direction [| range0; range1 |]

    let succeeded operationId range = ManifestContributionWorkflowCommand.RecordRangeSucceeded(operationId, repositoryId, storagePoolId, manifestAddress, range)

    /// Builds succeeded For test data for the server unit manifest Contribution Workflow Actor scenarios in this file.
    let succeededFor repositoryId manifestAddress operationId range =
        ManifestContributionWorkflowCommand.RecordRangeSucceeded(operationId, repositoryId, storagePoolId, manifestAddress, range)

    /// Builds failed test data for the server unit manifest Contribution Workflow Actor scenarios in this file.
    let failed operationId range message =
        ManifestContributionWorkflowCommand.RecordRangeFailed(operationId, repositoryId, storagePoolId, manifestAddress, range, message)

    /// Applies all inputs to drive the server unit manifest Contribution Workflow Actor state transition under test.
    let applyAll events current =
        events
        |> List.fold (fun state event -> ManifestContributionWorkflowDto.UpdateDto event state) current

    /// Builds expect Ok test data for the server unit manifest Contribution Workflow Actor scenarios in this file.
    let expectOk result =
        match result with
        | Ok decision -> decision
        | Error error ->
            Assert.Fail($"Expected command to succeed, got {error.Error}.")
            Unchecked.defaultof<ManifestContributionWorkflowDecision>

    /// Verifies that workflow Primary Key Combines Repository Id Storage Pool Id And Manifest Address.
    [<Test>]
    member _.WorkflowPrimaryKeyCombinesRepositoryIdStoragePoolIdAndManifestAddress() =
        let key = ManifestContributionWorkflowActor.primaryKey repositoryId storagePoolId manifestAddress

        Assert.That(key, Is.EqualTo("a6c6b60ad4d240f68362258dbe75a5bf|pool-main|manifest:blake3:alpha"))

    /// Verifies that start Rejects Duplicate Ranges.
    [<Test>]
    member _.StartRejectsDuplicateRanges() =
        let duplicateStart = startWithRanges ManifestContributionDirection.Increment [| range0; range0 |]

        let result = ManifestContributionWorkflowActor.decideCommand [] ManifestContributionWorkflowDto.Default duplicateStart (metadata "corr-duplicate")

        match result with
        | Ok _ -> Assert.Fail("Expected duplicate ranges to reject.")
        | Error error -> Assert.That(error.Error, Is.EqualTo("ManifestContributionWorkflow ranges must be unique."))

    /// Verifies that start Rejects Ranges From Different Storage Pool.
    [<Test>]
    member _.StartRejectsRangesFromDifferentStoragePool() =
        let foreignRange = { range1 with StoragePoolId = otherStoragePoolId }
        let crossPoolStart = startWithRanges ManifestContributionDirection.Increment [| range0; foreignRange |]

        let result =
            ManifestContributionWorkflowActor.decideCommand [] ManifestContributionWorkflowDto.Default crossPoolStart (metadata "corr-cross-pool-range")

        match result with
        | Ok _ -> Assert.Fail("Expected workflow start with a foreign storage-pool range to reject.")
        | Error error ->
            Assert.That(
                error.Error,
                Is.EqualTo("ManifestContributionWorkflow range StoragePoolId must match workflow StoragePoolId. Expected pool-main, actual pool-archive.")
            )

    /// Verifies that reused Start Operation Id With Different Payload Rejects Instead Of Replaying.
    [<Test>]
    member _.ReusedStartOperationIdWithDifferentPayloadRejectsInsteadOfReplaying() =
        let started =
            ManifestContributionWorkflowActor.decideCommand
                []
                ManifestContributionWorkflowDto.Default
                (start ManifestContributionDirection.Increment)
                (metadata "corr-start")
            |> expectOk

        let afterStart = applyAll started.Events ManifestContributionWorkflowDto.Default
        let mismatchedStart = startWithRanges ManifestContributionDirection.Increment [| range0 |]

        let replay = ManifestContributionWorkflowActor.decideCommand started.Events afterStart mismatchedStart (metadata "corr-start-reused")

        match replay with
        | Ok _ -> Assert.Fail("Expected reused start operation id with different payload to reject.")
        | Error error -> Assert.That(error.Error, Is.EqualTo("ManifestContributionWorkflow operation id was already used with a different payload."))

    /// Verifies that reused Range Success Operation Id With Different Range Rejects Instead Of Replaying.
    [<Test>]
    member _.ReusedRangeSuccessOperationIdWithDifferentRangeRejectsInsteadOfReplaying() =
        let started =
            ManifestContributionWorkflowActor.decideCommand
                []
                ManifestContributionWorkflowDto.Default
                (start ManifestContributionDirection.Increment)
                (metadata "corr-start")
            |> expectOk

        let afterStart = applyAll started.Events ManifestContributionWorkflowDto.Default

        let firstSuccess =
            ManifestContributionWorkflowActor.decideCommand started.Events afterStart (succeeded "range-shared" range0) (metadata "corr-range-0")
            |> expectOk

        let allEvents = started.Events @ firstSuccess.Events
        let afterSuccess = applyAll firstSuccess.Events afterStart

        let replay = ManifestContributionWorkflowActor.decideCommand allEvents afterSuccess (succeeded "range-shared" range1) (metadata "corr-range-reused")

        match replay with
        | Ok _ -> Assert.Fail("Expected reused range operation id with different range to reject.")
        | Error error -> Assert.That(error.Error, Is.EqualTo("ManifestContributionWorkflow operation id was already used with a different payload."))

    /// Verifies that completed Workflow Can Start Next Cycle For Same Manifest.
    [<Test>]
    member _.CompletedWorkflowCanStartNextCycleForSameManifest() =
        let started =
            ManifestContributionWorkflowActor.decideCommand
                []
                ManifestContributionWorkflowDto.Default
                (start ManifestContributionDirection.Increment)
                (metadata "corr-start")
            |> expectOk

        let afterStart = applyAll started.Events ManifestContributionWorkflowDto.Default

        let range0Done =
            ManifestContributionWorkflowActor.decideCommand started.Events afterStart (succeeded "range-0" range0) (metadata "corr-range-0")
            |> expectOk

        let afterRange0 = applyAll range0Done.Events afterStart

        let range1Done =
            ManifestContributionWorkflowActor.decideCommand
                (started.Events @ range0Done.Events)
                afterRange0
                (succeeded "range-1" range1)
                (metadata "corr-range-1")
            |> expectOk

        let completedEvents =
            started.Events
            @ range0Done.Events @ range1Done.Events

        let restarted =
            ManifestContributionWorkflowActor.decideCommand
                completedEvents
                range1Done.Workflow
                (startWithOperation "workflow-restart" ManifestContributionDirection.Decrement [| range0 |])
                (metadata "corr-restart")
            |> expectOk

        Assert.That(restarted.Workflow.LifecycleState, Is.EqualTo(ManifestContributionWorkflowLifecycleState.InProgress))
        Assert.That(restarted.Workflow.Direction, Is.EqualTo(ManifestContributionDirection.Decrement))
        let restartedPending = ManifestContributionWorkflowActor.pendingRanges restarted.Workflow
        Assert.That(restartedPending.Length, Is.EqualTo(1))
        Assert.That(restartedPending[0], Is.EqualTo(range0))

    /// Verifies that range Progress Rejects When Actor Key Does Not Match Workflow Target.
    [<Test>]
    member _.RangeProgressRejectsWhenActorKeyDoesNotMatchWorkflowTarget() =
        let started =
            ManifestContributionWorkflowActor.decideCommand
                []
                ManifestContributionWorkflowDto.Default
                (start ManifestContributionDirection.Increment)
                (metadata "corr-start")
            |> expectOk

        let afterStart = applyAll started.Events ManifestContributionWorkflowDto.Default
        let wrongKey = ManifestContributionWorkflowActor.primaryKey (Guid.Parse("24560efe-bb67-4be8-b0d1-1a11990e66b1")) storagePoolId manifestAddress

        let result =
            ManifestContributionWorkflowActor.decideCommandForKey
                (Some wrongKey)
                started.Events
                afterStart
                (succeeded "range-wrong-key" range0)
                (metadata "corr-wrong-key")

        match result with
        | Ok _ -> Assert.Fail("Expected range progress for a mismatched grain key to reject.")
        | Error error -> Assert.That(error.Error, Is.EqualTo("ManifestContributionWorkflow command target does not match the grain key."))

    /// Verifies that range Progress Rejects When Command Target Does Not Match Workflow Target.
    [<Test>]
    member _.RangeProgressRejectsWhenCommandTargetDoesNotMatchWorkflowTarget() =
        let started =
            ManifestContributionWorkflowActor.decideCommand
                []
                ManifestContributionWorkflowDto.Default
                (start ManifestContributionDirection.Increment)
                (metadata "corr-start")
            |> expectOk

        let afterStart = applyAll started.Events ManifestContributionWorkflowDto.Default
        let wrongRepositoryId = Guid.Parse("24560efe-bb67-4be8-b0d1-1a11990e66b1")

        let result =
            ManifestContributionWorkflowActor.decideCommandForKey
                (Some(ManifestContributionWorkflowActor.primaryKey repositoryId storagePoolId manifestAddress))
                started.Events
                afterStart
                (succeededFor wrongRepositoryId manifestAddress "range-wrong-target" range0)
                (metadata "corr-wrong-target")

        match result with
        | Ok _ -> Assert.Fail("Expected range progress for a mismatched command target to reject.")
        | Error error -> Assert.That(error.Error, Is.EqualTo("ManifestContributionWorkflow command target does not match the grain key."))

    /// Verifies that workflow Primary Key Distinguishes Same Manifest Address Across Storage Pools.
    [<Test>]
    member _.WorkflowPrimaryKeyDistinguishesSameManifestAddressAcrossStoragePools() =
        let mainKey = ManifestContributionWorkflowActor.primaryKey repositoryId storagePoolId manifestAddress
        let archiveKey = ManifestContributionWorkflowActor.primaryKey repositoryId otherStoragePoolId manifestAddress

        Assert.That(archiveKey, Is.Not.EqualTo(mainKey))

        let archiveRange = { range0 with StoragePoolId = otherStoragePoolId }

        let archiveStart =
            ManifestContributionWorkflowCommand.Start(
                "workflow-archive",
                repositoryId,
                otherStoragePoolId,
                manifestAddress,
                ManifestContributionDirection.Increment,
                [| archiveRange |]
            )

        let result =
            ManifestContributionWorkflowActor.decideCommandForKey
                (Some mainKey)
                []
                ManifestContributionWorkflowDto.Default
                archiveStart
                (metadata "corr-cross-pool")

        match result with
        | Ok _ -> Assert.Fail("Expected cross-pool workflow command on same manifest address to reject against the wrong grain key.")
        | Error error -> Assert.That(error.Error, Is.EqualTo("ManifestContributionWorkflow command target does not match the grain key."))

    /// Verifies that activation Resume Returns Only Uncompleted Ranges.
    [<Test>]
    member _.ActivationResumeReturnsOnlyUncompletedRanges() =
        let started =
            ManifestContributionWorkflowActor.decideCommand
                []
                ManifestContributionWorkflowDto.Default
                (start ManifestContributionDirection.Increment)
                (metadata "corr-start")
            |> expectOk

        let afterStart = applyAll started.Events ManifestContributionWorkflowDto.Default

        let completedRange0 =
            ManifestContributionWorkflowActor.decideCommand started.Events afterStart (succeeded "range-0" range0) (metadata "corr-range-0")
            |> expectOk

        let allEvents = started.Events @ completedRange0.Events
        let afterActivation = applyAll allEvents ManifestContributionWorkflowDto.Default

        Assert.That(afterActivation.LifecycleState, Is.EqualTo(ManifestContributionWorkflowLifecycleState.InProgress))
        let pending = ManifestContributionWorkflowActor.pendingRanges afterActivation
        Assert.That(pending.Length, Is.EqualTo(1))
        Assert.That(pending[0], Is.EqualTo(range1))

    /// Verifies that partial Batch Failure Retries Only Failed Range And Then Completes.
    [<Test>]
    member _.PartialBatchFailureRetriesOnlyFailedRangeAndThenCompletes() =
        let started =
            ManifestContributionWorkflowActor.decideCommand
                []
                ManifestContributionWorkflowDto.Default
                (start ManifestContributionDirection.Increment)
                (metadata "corr-start")
            |> expectOk

        let afterStart = applyAll started.Events ManifestContributionWorkflowDto.Default

        let range0Done =
            ManifestContributionWorkflowActor.decideCommand started.Events afterStart (succeeded "range-0" range0) (metadata "corr-range-0")
            |> expectOk

        let afterRange0 = applyAll range0Done.Events afterStart

        let range1Failed =
            ManifestContributionWorkflowActor.decideCommand
                (started.Events @ range0Done.Events)
                afterRange0
                (failed "range-1-failed" range1 "transient")
                (metadata "corr-range-1-failed")
            |> expectOk

        let afterFailure = applyAll range1Failed.Events afterRange0

        Assert.That(afterFailure.LifecycleState, Is.EqualTo(ManifestContributionWorkflowLifecycleState.InProgress))
        let pendingAfterFailure = ManifestContributionWorkflowActor.pendingRanges afterFailure
        Assert.That(pendingAfterFailure.Length, Is.EqualTo(1))
        Assert.That(pendingAfterFailure[0], Is.EqualTo(range1))
        Assert.That(afterFailure.FailedRanges.ContainsKey(range1), Is.True)

        let retrySucceeded =
            ManifestContributionWorkflowActor.decideCommand
                (started.Events
                 @ range0Done.Events @ range1Failed.Events)
                afterFailure
                (succeeded "range-1-retry" range1)
                (metadata "corr-range-1-retry")
            |> expectOk

        Assert.That(retrySucceeded.Workflow.LifecycleState, Is.EqualTo(ManifestContributionWorkflowLifecycleState.Completed))
        Assert.That(ManifestContributionWorkflowActor.pendingRanges retrySucceeded.Workflow, Is.Empty)

    /// Verifies that range Success Emits One Active Count Intent And Replay Does Not Emit Second Intent.
    [<Test>]
    member _.RangeSuccessEmitsOneActiveCountIntentAndReplayDoesNotEmitSecondIntent() =
        let started =
            ManifestContributionWorkflowActor.decideCommand
                []
                ManifestContributionWorkflowDto.Default
                (start ManifestContributionDirection.Increment)
                (metadata "corr-start")
            |> expectOk

        let afterStart = applyAll started.Events ManifestContributionWorkflowDto.Default

        let firstSuccess =
            ManifestContributionWorkflowActor.decideCommand started.Events afterStart (succeeded "range-0" range0) (metadata "corr-range-0")
            |> expectOk

        Assert.That(firstSuccess.Intents.Length, Is.EqualTo(1))
        Assert.That(firstSuccess.Intents[0], Is.EqualTo(ManifestContributionWorkflowIntent.AdjustRangeActiveManifestCount(range0, 1)))

        let allEvents = started.Events @ firstSuccess.Events
        let afterSuccess = applyAll firstSuccess.Events afterStart

        let replay =
            ManifestContributionWorkflowActor.decideCommand allEvents afterSuccess (succeeded "range-0" range0) (metadata "corr-range-0-replay")
            |> expectOk

        Assert.That(replay.WasIdempotentReplay, Is.True)
        Assert.That(replay.Events, Is.Empty)
        Assert.That(replay.Intents, Is.Empty)

    /// Verifies that increment And Decrement Directions Produce Opposite Active Range Deltas.
    [<Test>]
    member _.IncrementAndDecrementDirectionsProduceOppositeActiveRangeDeltas() =
        /// Builds start And Succeed test data for the server unit manifest Contribution Workflow Actor scenarios in this file.
        let startAndSucceed direction =
            let started =
                ManifestContributionWorkflowActor.decideCommand
                    []
                    ManifestContributionWorkflowDto.Default
                    (start direction)
                    (metadata $"corr-start-{direction}")
                |> expectOk

            let afterStart = applyAll started.Events ManifestContributionWorkflowDto.Default

            ManifestContributionWorkflowActor.decideCommand started.Events afterStart (succeeded "range-0" range0) (metadata $"corr-range-{direction}")
            |> expectOk

        let increment = startAndSucceed ManifestContributionDirection.Increment
        let decrement = startAndSucceed ManifestContributionDirection.Decrement

        Assert.That(increment.Intents.Length, Is.EqualTo(1))
        Assert.That(increment.Intents[0], Is.EqualTo(ManifestContributionWorkflowIntent.AdjustRangeActiveManifestCount(range0, 1)))
        Assert.That(decrement.Intents.Length, Is.EqualTo(1))
        Assert.That(decrement.Intents[0], Is.EqualTo(ManifestContributionWorkflowIntent.AdjustRangeActiveManifestCount(range0, -1)))

    /// Verifies that pending Workflow Blocks Unsafe Deletion Until All Ranges Complete.
    [<Test>]
    member _.PendingWorkflowBlocksUnsafeDeletionUntilAllRangesComplete() =
        let started =
            ManifestContributionWorkflowActor.decideCommand
                []
                ManifestContributionWorkflowDto.Default
                (start ManifestContributionDirection.Decrement)
                (metadata "corr-start")
            |> expectOk

        let afterStart = applyAll started.Events ManifestContributionWorkflowDto.Default

        Assert.That(ManifestContributionWorkflowActor.blocksUnsafeDeletion afterStart range0, Is.True)
        Assert.That(ManifestContributionWorkflowActor.blocksUnsafeDeletion afterStart range1, Is.True)

        let range0Done =
            ManifestContributionWorkflowActor.decideCommand started.Events afterStart (succeeded "range-0" range0) (metadata "corr-range-0")
            |> expectOk

        let afterRange0 = applyAll range0Done.Events afterStart

        Assert.That(ManifestContributionWorkflowActor.blocksUnsafeDeletion afterRange0 range0, Is.False)
        Assert.That(ManifestContributionWorkflowActor.blocksUnsafeDeletion afterRange0 range1, Is.True)

        let range1Done =
            ManifestContributionWorkflowActor.decideCommand
                (started.Events @ range0Done.Events)
                afterRange0
                (succeeded "range-1" range1)
                (metadata "corr-range-1")
            |> expectOk

        Assert.That(ManifestContributionWorkflowActor.blocksUnsafeDeletion range1Done.Workflow range0, Is.False)
        Assert.That(ManifestContributionWorkflowActor.blocksUnsafeDeletion range1Done.Workflow range1, Is.False)
