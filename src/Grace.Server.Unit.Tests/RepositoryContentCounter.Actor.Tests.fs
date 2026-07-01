namespace Grace.Server.Tests

open Grace.Types.RepositoryContentCounter
open Grace.Types.Common
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic

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

    /// Applies all inputs to drive the server unit repository Content Counter Actor state transition under test.
    let applyAll events dto =
        events
        |> List.fold (fun current event -> RepositoryContentCounterDto.UpdateDto event current) dto

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
                Is.EqualTo(RepositoryContentCounterIntent.IncrementManifestReferenceCount(repositoryId, storagePoolId, manifestAddress))
            )

            let dto = applyAll decision.Events RepositoryContentCounterDto.Default
            let replay = RepositoryContentCounterActor.decideCommand decision.Events dto (add "op-add-1") (metadata "corr-add-retry")

            match replay with
            | Ok replayDecision ->
                Assert.That(replayDecision.WasIdempotentReplay, Is.True)
                Assert.That(replayDecision.Events, Is.Empty)
                Assert.That(replayDecision.Intents, Is.Empty)
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
                Is.EqualTo(RepositoryContentCounterIntent.DecrementManifestReferenceCount(repositoryId, storagePoolId, manifestAddress))
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
            Assert.That(decision.Intents, Is.Empty)
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
