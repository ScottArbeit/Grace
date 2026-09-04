namespace Grace.Server.Tests

open Grace.Types.RepositoryContentCounter
open Grace.Types.Common
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

module RepositoryContentCounterActor = Grace.Actors.RepositoryContentCounter

/// Covers repository Content Counter Actor behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type RepositoryContentCounterActorTests() =

    let timestamp = Instant.FromUtc(2026, 5, 24, 13, 0)
    let repositoryId = Guid.Parse("75ce5e36-25f6-4da0-afdd-ad4ad56540d5")
    let otherRepositoryId = Guid.Parse("41ff01d0-8f4c-41e7-875d-1c4f7b519c11")
    let storagePoolId = StoragePoolId "pool-main"
    let otherStoragePoolId = StoragePoolId "pool-archive"
    let manifestAddress = "manifest:blake3:alpha"
    let otherManifestAddress = "manifest:blake3:beta"

    /// Constructs metadata fixtures used by the server unit repository Content Counter Actor assertions.
    let metadata correlationId =
        {
            Timestamp = timestamp
            CorrelationId = correlationId
            Principal = "tester"
            ClientType = Microsoft.FSharp.Core.Option.None
            Properties = Dictionary<string, string>()
        }

    let add operationId = RepositoryContentCounterCommand.AddReference(operationId, repositoryId, storagePoolId, manifestAddress)

    let remove operationId = RepositoryContentCounterCommand.RemoveReference(operationId, repositoryId, storagePoolId, manifestAddress)

    /// Builds the repair-only command whose operation identity is bound to the full logical counter transition.
    let repairCommand currentRevision rebuiltCount =
        {
            OperationId = RepositoryContentCounterActor.repairOperationId repositoryId storagePoolId manifestAddress currentRevision rebuiltCount
            RepositoryId = repositoryId
            StoragePoolId = storagePoolId
            ManifestAddress = manifestAddress
            ExpectedRevision = currentRevision
            RebuiltCount = rebuiltCount
        }

    /// Unwraps a counter decision while preserving a useful assertion failure.
    let expectDecision result =
        match result with
        | Ok decision -> decision
        | Error error ->
            Assert.Fail($"Expected counter decision, got {error.Error}.")
            Unchecked.defaultof<RepositoryContentCounterDecision>

    /// Applies all inputs to drive the server unit repository Content Counter Actor state transition under test.
    let applyAll events dto =
        events
        |> List.fold (fun current event -> RepositoryContentCounterDto.UpdateDto event current) dto

    /// Verifies repeated counter changes retain only the latest bounded completion snapshot.
    [<Test>]
    member _.RepeatedChangesRetainOnlyLatestCompletedSnapshot() =
        let addDecision =
            match RepositoryContentCounterActor.decideCommand [] RepositoryContentCounterDto.Default (add "op-add-bounded") (metadata "corr-add-bounded") with
            | Ok decision -> decision
            | Error error ->
                Assert.Fail($"Expected bounded add to succeed, got {error.Error}.")
                Unchecked.defaultof<RepositoryContentCounterDecision>

        let removeDecision =
            match
                RepositoryContentCounterActor.decideCommand addDecision.Events addDecision.Counter (remove "op-remove-bounded") (metadata "corr-remove-bounded")
                with
            | Ok decision -> decision
            | Error error ->
                Assert.Fail($"Expected bounded remove to succeed, got {error.Error}.")
                Unchecked.defaultof<RepositoryContentCounterDecision>

        Assert.That(removeDecision.Counter.Count, Is.EqualTo(0L))
        Assert.That(removeDecision.Counter.Revision, Is.EqualTo(2L))
        Assert.That(removeDecision.Counter.LastCompletedChange.Value.OperationId, Is.EqualTo("op-remove-bounded"))
        Assert.That(removeDecision.Counter.LastCompletedChange.Value.PreviousCount, Is.EqualTo(1L))
        Assert.That(removeDecision.Counter.LastCompletedChange.Value.CurrentCount, Is.EqualTo(0L))

    /// Verifies that zero To One Emits Increment Intent And Retry Is Idempotent.
    [<Test>]
    member _.ZeroToOneEmitsIncrementIntentAndRetryIsIdempotent() =
        let first = RepositoryContentCounterActor.decideCommand [] RepositoryContentCounterDto.Default (add "op-add-1") (metadata "corr-add-1")

        match first with
        | Ok decision ->
            Assert.That(decision.WasIdempotentReplay, Is.False)
            Assert.That(decision.Counter.ReferenceCount, Is.EqualTo(1L))
            Assert.That(decision.Counter.LifecycleState, Is.EqualTo(RepositoryContentCounterLifecycleState.Referenced))
            Assert.That(decision.Events.Length, Is.EqualTo(1))
            Assert.That(decision.Intents.Length, Is.EqualTo(1))

            Assert.That(
                decision.Intents[0],
                Is.EqualTo(RepositoryContentCounterIntent.IncrementManifestReferenceCount(repositoryId, storagePoolId, manifestAddress, 1L))
            )

            let dto = applyAll decision.Events RepositoryContentCounterDto.Default
            let replay = RepositoryContentCounterActor.decideCommand decision.Events dto (add "op-add-1") (metadata "corr-add-retry")

            match replay with
            | Ok replayDecision ->
                Assert.That(replayDecision.WasIdempotentReplay, Is.True)
                Assert.That(replayDecision.Events, Is.Empty)
                Assert.That(replayDecision.Intents.Length, Is.EqualTo(1))
                Assert.That(replayDecision.Counter.ReferenceCount, Is.EqualTo(1L))
            | Error error -> Assert.Fail($"Expected add replay to be idempotent, got {error.Error}.")
        | Error error -> Assert.Fail($"Expected add to succeed, got {error.Error}.")

    /// Verifies that n To N Transitions Do Not Emit Intent And One To Zero Emits Decrement Intent.
    [<Test>]
    member _.NToNTransitionsDoNotEmitIntentAndOneToZeroEmitsDecrementIntent() =
        let first = RepositoryContentCounterActor.decideCommand [] RepositoryContentCounterDto.Default (add "op-add-1") (metadata "corr-add-1")

        let afterFirst, firstEvents =
            match first with
            | Ok decision -> decision.Counter, decision.Events
            | Error error ->
                Assert.Fail($"Expected first add to succeed, got {error.Error}.")
                RepositoryContentCounterDto.Default, []

        let second = RepositoryContentCounterActor.decideCommand firstEvents afterFirst (add "op-add-2") (metadata "corr-add-2")

        let afterSecond, secondEvents =
            match second with
            | Ok decision ->
                Assert.That(decision.Counter.ReferenceCount, Is.EqualTo(2L))
                Assert.That(decision.Intents, Is.Empty)
                decision.Counter, firstEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected second add to succeed, got {error.Error}.")
                RepositoryContentCounterDto.Default, []

        let firstRemove = RepositoryContentCounterActor.decideCommand secondEvents afterSecond (remove "op-remove-1") (metadata "corr-remove-1")

        let afterFirstRemove, removeEvents =
            match firstRemove with
            | Ok decision ->
                Assert.That(decision.Counter.ReferenceCount, Is.EqualTo(1L))
                Assert.That(decision.Counter.LifecycleState, Is.EqualTo(RepositoryContentCounterLifecycleState.Referenced))
                Assert.That(decision.Intents, Is.Empty)
                decision.Counter, secondEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected first remove to succeed, got {error.Error}.")
                RepositoryContentCounterDto.Default, []

        let finalRemove = RepositoryContentCounterActor.decideCommand removeEvents afterFirstRemove (remove "op-remove-2") (metadata "corr-remove-2")

        match finalRemove with
        | Ok decision ->
            Assert.That(decision.Counter.ReferenceCount, Is.EqualTo(0L))
            Assert.That(decision.Counter.LifecycleState, Is.EqualTo(RepositoryContentCounterLifecycleState.NotReferenced))
            Assert.That(decision.Intents.Length, Is.EqualTo(1))

            Assert.That(
                decision.Intents[0],
                Is.EqualTo(RepositoryContentCounterIntent.DecrementManifestReferenceCount(repositoryId, storagePoolId, manifestAddress, 4L))
            )
        | Error error -> Assert.Fail($"Expected final remove to succeed, got {error.Error}.")

    /// Verifies that one To Zero Retry Is Idempotent Without Second Decrement Intent.
    [<Test>]
    member _.OneToZeroRetryIsIdempotentWithoutSecondDecrementIntent() =
        let addDecision = RepositoryContentCounterActor.decideCommand [] RepositoryContentCounterDto.Default (add "op-add-1") (metadata "corr-add-1")

        let addedDto, addEvents =
            match addDecision with
            | Ok decision -> decision.Counter, decision.Events
            | Error error ->
                Assert.Fail($"Expected add to succeed, got {error.Error}.")
                RepositoryContentCounterDto.Default, []

        let removeDecision = RepositoryContentCounterActor.decideCommand addEvents addedDto (remove "op-remove-1") (metadata "corr-remove-1")

        let removedDto, allEvents =
            match removeDecision with
            | Ok decision -> decision.Counter, addEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected remove to succeed, got {error.Error}.")
                RepositoryContentCounterDto.Default, []

        let replay = RepositoryContentCounterActor.decideCommand allEvents removedDto (remove "op-remove-1") (metadata "corr-remove-retry")

        match replay with
        | Ok decision ->
            Assert.That(decision.WasIdempotentReplay, Is.True)
            Assert.That(decision.Events, Is.Empty)
            Assert.That(decision.Intents.Length, Is.EqualTo(1))
            Assert.That(decision.Counter.ReferenceCount, Is.EqualTo(0L))
            Assert.That(decision.Counter.LifecycleState, Is.EqualTo(RepositoryContentCounterLifecycleState.NotReferenced))
        | Error error -> Assert.Fail($"Expected remove replay to be idempotent, got {error.Error}.")

    /// Verifies that first Write Rejects Command Target That Does Not Match Grain Key.
    [<Test>]
    member _.FirstWriteRejectsCommandTargetThatDoesNotMatchGrainKey() =
        let wrongKey = RepositoryContentCounterActor.primaryKey otherRepositoryId storagePoolId manifestAddress

        let result =
            RepositoryContentCounterActor.decideCommandForKey (Some wrongKey) [] RepositoryContentCounterDto.Default (add "op-add-1") (metadata "corr-key")

        match result with
        | Ok _ -> Assert.Fail("Expected target mismatch to reject the first write.")
        | Error error -> Assert.That(error.Error, Is.EqualTo("RepositoryContentCounter command target does not match the grain key."))

    /// Verifies that reused Operation Id With Different Target Rejects Instead Of Replaying.
    [<Test>]
    member _.ReusedOperationIdWithDifferentTargetRejectsInsteadOfReplaying() =
        let first = RepositoryContentCounterActor.decideCommand [] RepositoryContentCounterDto.Default (add "op-add-1") (metadata "corr-add-1")

        let afterFirst, firstEvents =
            match first with
            | Ok decision -> decision.Counter, decision.Events
            | Error error ->
                Assert.Fail($"Expected add to succeed, got {error.Error}.")
                RepositoryContentCounterDto.Default, []

        let mismatchedCommand = RepositoryContentCounterCommand.AddReference("op-add-1", repositoryId, storagePoolId, otherManifestAddress)
        let replay = RepositoryContentCounterActor.decideCommand firstEvents afterFirst mismatchedCommand (metadata "corr-add-reused")

        match replay with
        | Ok _ -> Assert.Fail("Expected reused operation id with a different target to reject.")
        | Error error -> Assert.That(error.Error, Is.EqualTo("RepositoryContentCounter command target does not match the initialized counter."))

    /// Verifies that reused Operation Id With Different Command Rejects Instead Of Replaying.
    [<Test>]
    member _.ReusedOperationIdWithDifferentCommandRejectsInsteadOfReplaying() =
        let first = RepositoryContentCounterActor.decideCommand [] RepositoryContentCounterDto.Default (add "op-shared") (metadata "corr-add")

        let afterFirst, firstEvents =
            match first with
            | Ok decision -> decision.Counter, decision.Events
            | Error error ->
                Assert.Fail($"Expected add to succeed, got {error.Error}.")
                RepositoryContentCounterDto.Default, []

        let reused = RepositoryContentCounterActor.decideCommand firstEvents afterFirst (remove "op-shared") (metadata "corr-remove")

        match reused with
        | Ok _ -> Assert.Fail("Expected reused operation id with a different command to reject.")
        | Error error -> Assert.That(error.Error, Is.EqualTo("RepositoryContentCounter operation id was already used for a different command."))

    /// Verifies that primary Key And Targets Distinguish Same Manifest Address Across Storage Pools.
    [<Test>]
    member _.PrimaryKeyAndTargetsDistinguishSameManifestAddressAcrossStoragePools() =
        let defaultKey = RepositoryContentCounterActor.primaryKey repositoryId storagePoolId manifestAddress
        let archiveKey = RepositoryContentCounterActor.primaryKey repositoryId otherStoragePoolId manifestAddress

        Assert.That(archiveKey, Is.Not.EqualTo(defaultKey))

        let archiveAdd = RepositoryContentCounterCommand.AddReference("op-add-archive", repositoryId, otherStoragePoolId, manifestAddress)

        let result =
            RepositoryContentCounterActor.decideCommandForKey (Some defaultKey) [] RepositoryContentCounterDto.Default archiveAdd (metadata "corr-cross-pool")

        match result with
        | Ok _ -> Assert.Fail("Expected cross-pool command on same manifest address to reject against the wrong grain key.")
        | Error error -> Assert.That(error.Error, Is.EqualTo("RepositoryContentCounter command target does not match the grain key."))

    /// Verifies a superseded operation can replay its original zero transition from Redis without another count write.
    [<Test>]
    member _.SupersededOperationReplaysFromRecentResult() =
        task {
            let first =
                RepositoryContentCounterActor.decideCommand [] RepositoryContentCounterDto.Default (add "op-add-first") (metadata "corr-add-first")
                |> expectDecision

            let second =
                RepositoryContentCounterActor.decideCommand [] first.Counter (add "op-add-second") (metadata "corr-add-second")
                |> expectDecision

            let recent =
                { new Grace.Actors.IRepositoryCounterRecentResult with
                    member _.TryGetAsync(_, _, _, operationId, _) =
                        Task.FromResult(if operationId = "op-add-first" then first.Counter.LastCompletedChange else None)

                    member _.TrySetAsync(_, _, _, _, _) = Task.FromResult true
                }

            let mutable persisted = false

            let! result =
                RepositoryContentCounterActor.handleWithRecentResult
                    recent
                    (fun _ ->
                        persisted <- true
                        Task.CompletedTask)
                    None
                    second.Counter
                    (add "op-add-first")
                    (metadata "corr-replay-first")
                    CancellationToken.None

            match result with
            | Error error -> Assert.Fail($"Expected recent-result replay, got {error.Error}.")
            | Ok decision ->
                Assert.That(decision.WasIdempotentReplay, Is.True)
                Assert.That(decision.Intents.Length, Is.EqualTo(1))
                Assert.That(decision.Counter.Count, Is.EqualTo(2L))
                Assert.That(persisted, Is.False)
        }

    /// Verifies a cached removal replay is resolved before the current zero count can reject it as a new removal.
    [<Test>]
    member _.SupersededRemovalReplaysFromRecentResultAtCurrentZero() =
        task {
            let addedFirst =
                RepositoryContentCounterActor.decideCommand [] RepositoryContentCounterDto.Default (add "op-add-first") (metadata "corr-add-first")
                |> expectDecision

            let removedFirst =
                RepositoryContentCounterActor.decideCommand [] addedFirst.Counter (remove "op-remove-first") (metadata "corr-remove-first")
                |> expectDecision

            let addedSecond =
                RepositoryContentCounterActor.decideCommand [] removedFirst.Counter (add "op-add-second") (metadata "corr-add-second")
                |> expectDecision

            let removedSecond =
                RepositoryContentCounterActor.decideCommand [] addedSecond.Counter (remove "op-remove-second") (metadata "corr-remove-second")
                |> expectDecision

            let recent =
                { new Grace.Actors.IRepositoryCounterRecentResult with
                    member _.TryGetAsync(_, _, _, operationId, _) =
                        Task.FromResult(
                            if operationId = "op-remove-first" then
                                removedFirst.Counter.LastCompletedChange
                            else
                                None
                        )

                    member _.TrySetAsync(_, _, _, _, _) = Task.FromResult true
                }

            let mutable persisted = false

            let! result =
                RepositoryContentCounterActor.handleWithRecentResult
                    recent
                    (fun _ ->
                        persisted <- true
                        Task.CompletedTask)
                    None
                    removedSecond.Counter
                    (remove "op-remove-first")
                    (metadata "corr-replay-remove-first")
                    CancellationToken.None

            match result with
            | Error error -> Assert.Fail($"Expected recent removal replay, got {error.Error}.")
            | Ok decision ->
                Assert.That(decision.WasIdempotentReplay, Is.True)
                Assert.That(decision.Intents.Length, Is.EqualTo(1))
                Assert.That(decision.Counter.Count, Is.EqualTo(0L))
                Assert.That(persisted, Is.False)
        }

    /// Verifies Redis loss pauses a removal before the bounded count is changed.
    [<Test>]
    member _.RedisLossPausesRemovalBeforeCountChange() =
        task {
            let added =
                RepositoryContentCounterActor.decideCommand [] RepositoryContentCounterDto.Default (add "op-add") (metadata "corr-add")
                |> expectDecision

            let recent =
                { new Grace.Actors.IRepositoryCounterRecentResult with
                    member _.TryGetAsync(_, _, _, _, _) = Task.FromResult(None)
                    member _.TrySetAsync(_, _, _, _, _) = Task.FromResult false
                }

            let mutable persisted = false

            let! result =
                RepositoryContentCounterActor.handleWithRecentResult
                    recent
                    (fun _ ->
                        persisted <- true
                        Task.CompletedTask)
                    None
                    added.Counter
                    (remove "op-remove")
                    (metadata "corr-remove")
                    CancellationToken.None

            match result with
            | Ok _ -> Assert.Fail("Expected removal to pause while Redis cannot preserve the previous result.")
            | Error error -> Assert.That(error.Error, Does.Contain("removal paused"))

            Assert.That(persisted, Is.False)
            Assert.That(added.Counter.Count, Is.EqualTo(1L))
        }

    /// Verifies a removal whose Redis reply is lost remains recoverable from LastCompletedChange.
    [<Test>]
    member _.RemovalRecoversAfterCompletedResultWriteReturnsUnknown() =
        task {
            let added =
                RepositoryContentCounterActor.decideCommand [] RepositoryContentCounterDto.Default (add "op-add") (metadata "corr-add")
                |> expectDecision

            let firstRecent =
                { new Grace.Actors.IRepositoryCounterRecentResult with
                    member _.TryGetAsync(_, _, _, _, _) = Task.FromResult(None)

                    member _.TrySetAsync(_, _, _, change, _) = Task.FromResult(change.Operation = RepositoryContentCounterChangeOperation.Added)
                }

            let mutable persisted = RepositoryContentCounterDto.Default

            let! first =
                RepositoryContentCounterActor.handleWithRecentResult
                    firstRecent
                    (fun snapshot ->
                        persisted <- snapshot
                        Task.CompletedTask)
                    None
                    added.Counter
                    (remove "op-remove")
                    (metadata "corr-remove")
                    CancellationToken.None

            match first with
            | Ok _ -> Assert.Fail("Expected the unconfirmed completed-result write to withhold removal intent.")
            | Error error -> Assert.That(error.Error, Does.Contain("retained safely"))

            Assert.That(persisted.Count, Is.EqualTo(0L))
            Assert.That(persisted.LastCompletedChange.Value.OperationId, Is.EqualTo("op-remove"))

            let recoveredRecent =
                { new Grace.Actors.IRepositoryCounterRecentResult with
                    member _.TryGetAsync(_, _, _, _, _) = Task.FromResult(None)
                    member _.TrySetAsync(_, _, _, _, _) = Task.FromResult true
                }

            let! recovered =
                RepositoryContentCounterActor.handleWithRecentResult
                    recoveredRecent
                    (fun _ -> Task.CompletedTask)
                    None
                    persisted
                    (remove "op-remove")
                    (metadata "corr-remove-retry")
                    CancellationToken.None

            match recovered with
            | Error error -> Assert.Fail($"Expected removal recovery, got {error.Error}.")
            | Ok decision ->
                Assert.That(decision.WasIdempotentReplay, Is.True)
                Assert.That(decision.Intents.Length, Is.EqualTo(1))
                Assert.That(decision.Counter.Count, Is.EqualTo(0L))
        }

    /// Verifies a tracked Library add survives cache loss and an intervening reference until its workflow materializes content.
    [<Test>]
    member _.TrackedLibraryAddSurvivesInterveningReferenceUntilWorkflowCompletion() =
        task {
            let libraryOperationId = RepositoryContentCounterOperationId "library-operation"
            let libraryCommand = add libraryOperationId
            let mutable persisted = RepositoryContentCounterDto.Default

            let persist snapshot =
                persisted <- snapshot
                Task.CompletedTask

            let! first =
                RepositoryContentCounterActor.handleTrackedAdd persist None RepositoryContentCounterDto.Default libraryCommand (metadata "corr-library-first")

            let firstDecision = expectDecision first
            Assert.That(firstDecision.Counter.Count, Is.EqualTo(1L))
            Assert.That(firstDecision.Counter.PendingTrackedAdd.Value.OperationId, Is.EqualTo(libraryOperationId))

            let unavailableRecentResult =
                { new Grace.Actors.IRepositoryCounterRecentResult with
                    member _.TryGetAsync(_, _, _, _, _) = Task.FromResult None
                    member _.TrySetAsync(_, _, _, _, _) = Task.FromResult false
                }

            let! intervening =
                RepositoryContentCounterActor.handleWithRecentResult
                    unavailableRecentResult
                    persist
                    None
                    persisted
                    (add "intervening-operation")
                    (metadata "corr-intervening")
                    CancellationToken.None

            let interveningDecision = expectDecision intervening
            Assert.That(interveningDecision.Counter.Count, Is.EqualTo(2L))
            Assert.That(interveningDecision.Counter.PendingTrackedAdd.Value.OperationId, Is.EqualTo(libraryOperationId))

            let! retried = RepositoryContentCounterActor.handleTrackedAdd persist None persisted libraryCommand (metadata "corr-library-retry")

            let retryDecision = expectDecision retried
            let workflowActivations = HashSet<RepositoryContentCounterOperationId * int64>()

            retryDecision.Intents
            |> List.iter (function
                | RepositoryContentCounterIntent.IncrementManifestReferenceCount (_, _, _, counterRevision) ->
                    workflowActivations.Add((libraryOperationId, counterRevision))
                    |> ignore
                | _ -> Assert.Fail("Expected the tracked Library retry to recover its increment workflow."))

            let materialized = workflowActivations.Contains((libraryOperationId, 1L))

            let! completed =
                RepositoryContentCounterActor.completeTrackedAdd persist None retryDecision.Counter libraryOperationId (metadata "corr-library-complete")

            let completedDecision = expectDecision completed

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(retryDecision.WasIdempotentReplay, Is.True)
                    Assert.That(retryDecision.Counter.Count, Is.EqualTo(2L))
                    Assert.That(workflowActivations, Has.Count.EqualTo(1))
                    Assert.That(materialized, Is.True)
                    Assert.That(completedDecision.Counter.Count, Is.EqualTo(2L))
                    Assert.That(completedDecision.Counter.PendingTrackedAdd.IsNone, Is.True))
            )
        }

    /// Verifies one repair-only command replaces the positive logical count in one snapshot without physical intents.
    [<Test>]
    member _.RepairReconcilesPositiveCountInOneRevisionWithoutIntent() =
        task {
            let current =
                { RepositoryContentCounterDto.Default with
                    RepositoryId = repositoryId
                    StoragePoolId = storagePoolId
                    ManifestAddress = manifestAddress
                    Count = 1L
                    Revision = 7L
                }

            let mutable persisted = Array.empty<RepositoryContentCounterDto>

            let! result =
                RepositoryContentCounterActor.handlePositiveCountRepair
                    (fun snapshot ->
                        persisted <- Array.append persisted [| snapshot |]
                        Task.CompletedTask)
                    None
                    current
                    (repairCommand 7L 4L)
                    (metadata "corr-repair")

            let decision = expectDecision result
            Assert.That(persisted, Has.Length.EqualTo(1))
            Assert.That(decision.Counter.Count, Is.EqualTo(4L))
            Assert.That(decision.Counter.Revision, Is.EqualTo(8L))
            Assert.That(decision.Events, Is.Empty)
            Assert.That(decision.Intents, Is.Empty)
            Assert.That(decision.WasIdempotentReplay, Is.False)
            Assert.That(decision.Counter.LastCompletedChange.Value.PreviousCount, Is.EqualTo(1L))
            Assert.That(decision.Counter.LastCompletedChange.Value.CurrentCount, Is.EqualTo(4L))
            Assert.That(decision.Counter.LastCompletedChange.Value.Revision, Is.EqualTo(8L))
        }

    /// Verifies a stale expected revision rejects before the repair-only snapshot write.
    [<Test>]
    member _.RepairRevisionMismatchRejectsBeforePersistence() =
        task {
            let current =
                { RepositoryContentCounterDto.Default with
                    RepositoryId = repositoryId
                    StoragePoolId = storagePoolId
                    ManifestAddress = manifestAddress
                    Count = 2L
                    Revision = 8L
                }

            let mutable persisted = false

            let! result =
                RepositoryContentCounterActor.handlePositiveCountRepair
                    (fun _ ->
                        persisted <- true
                        Task.CompletedTask)
                    None
                    current
                    (repairCommand 7L 3L)
                    (metadata "corr-repair-stale")

            match result with
            | Ok _ -> Assert.Fail("Expected the stale repair revision to reject.")
            | Error error -> Assert.That(error.Error, Does.Contain("expected revision"))

            Assert.That(persisted, Is.False)
        }

    /// Verifies the actor rejects a non-positive rebuilt count without changing logical state.
    [<TestCase(0L, "positive")>]
    [<TestCase(-1L, "positive")>]
    member _.RepairRejectsNonPositiveCount(rebuiltCount, expectedMessage) =
        task {
            let current =
                { RepositoryContentCounterDto.Default with
                    RepositoryId = repositoryId
                    StoragePoolId = storagePoolId
                    ManifestAddress = manifestAddress
                    Count = 2L
                    Revision = 8L
                }

            let mutable persisted = false

            let! result =
                RepositoryContentCounterActor.handlePositiveCountRepair
                    (fun _ ->
                        persisted <- true
                        Task.CompletedTask)
                    None
                    current
                    (repairCommand 8L rebuiltCount)
                    (metadata "corr-repair-invalid")

            match result with
            | Ok _ -> Assert.Fail("Expected the invalid repair command to reject.")
            | Error error -> Assert.That(error.Error, Does.Contain(expectedMessage))

            Assert.That(persisted, Is.False)
        }

    /// Verifies the actor rejects an operation identity that is not derived from the exact repair tuple and transition.
    [<Test>]
    member _.RepairRejectsNonDeterministicOperationIdentity() =
        let current =
            { RepositoryContentCounterDto.Default with
                RepositoryId = repositoryId
                StoragePoolId = storagePoolId
                ManifestAddress = manifestAddress
                Count = 2L
                Revision = 8L
            }

        let command = { repairCommand 8L 3L with OperationId = RepositoryContentCounterOperationId "not-deterministic" }

        match RepositoryContentCounterActor.decideRepairForKey None current command (metadata "corr-repair-identity") with
        | Ok _ -> Assert.Fail("Expected the non-deterministic repair operation to reject.")
        | Error error -> Assert.That(error.Error, Does.Contain("not deterministic"))

    /// Verifies the exact completed repair operation replays without a second snapshot write.
    [<Test>]
    member _.CompletedRepairReplayDoesNotPersistAgain() =
        task {
            let before =
                { RepositoryContentCounterDto.Default with
                    RepositoryId = repositoryId
                    StoragePoolId = storagePoolId
                    ManifestAddress = manifestAddress
                    Count = 1L
                    Revision = 7L
                }

            let command = repairCommand 7L 4L

            let first =
                RepositoryContentCounterActor.decideRepairForKey None before command (metadata "corr-repair-first")
                |> expectDecision

            let mutable persisted = false

            let! replay =
                RepositoryContentCounterActor.handlePositiveCountRepair
                    (fun _ ->
                        persisted <- true
                        Task.CompletedTask)
                    None
                    first.Counter
                    command
                    (metadata "corr-repair-replay")

            let decision = expectDecision replay
            Assert.That(decision.WasIdempotentReplay, Is.True)
            Assert.That(decision.Counter, Is.EqualTo(first.Counter))
            Assert.That(decision.Intents, Is.Empty)
            Assert.That(persisted, Is.False)
        }
