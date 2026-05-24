namespace Grace.Server.Tests

open Grace.Types.Reminder
open Grace.Types.Types
open Grace.Types.UploadSession
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic

module UploadSessionActor = Grace.Actors.UploadSession

[<Parallelizable(ParallelScope.All)>]
type UploadSessionActorTests() =

    let timestamp = Instant.FromUtc(2026, 5, 24, 12, 0)

    let metadata correlationId = { Timestamp = timestamp; CorrelationId = correlationId; Principal = "tester"; Properties = Dictionary<string, string>() }

    let sessionId = Guid.Parse("ab6fd828-87a3-4b7a-9c2e-5a83f5e8b1b0")
    let ownerId = Guid.Parse("4f512f0d-d6b0-488a-934c-db16840d2a8d")
    let organizationId = Guid.Parse("2b4ffda8-1129-47df-9c0c-76371153a807")
    let repositoryId = Guid.Parse("75ce5e36-25f6-4da0-afdd-ad4ad56540d5")

    let start operationId =
        {
            UploadSessionId = sessionId
            OwnerId = ownerId
            OrganizationId = organizationId
            RepositoryId = repositoryId
            AuthorizedScope = "/src"
            FileContentHash = "blake3:file"
            ExpectedSize = 1_048_576L
            ChunkingSuiteId = "rabin-blake3-v1"
            SamplingPolicySnapshot = "sparse-key-v1"
            OperationId = operationId
        }

    let apply event dto = UploadSessionDto.UpdateDto event dto

    let applyAll events dto =
        events
        |> List.fold (fun current event -> apply event current) dto

    [<Test>]
    member _.StartWithSameOperationIdIsIdempotentReplay() =
        let command = UploadSessionCommand.Start(start "op-start")
        let first = UploadSessionActor.decideCommand [] UploadSessionDto.Default command (metadata "corr-start-1")

        match first with
        | Ok decision ->
            Assert.That(decision.WasIdempotentReplay, Is.False)
            Assert.That(decision.Events.Length, Is.EqualTo(1))

            let dto = applyAll decision.Events UploadSessionDto.Default
            let replay = UploadSessionActor.decideCommand decision.Events dto command (metadata "corr-start-2")

            match replay with
            | Ok replayDecision ->
                Assert.That(replayDecision.WasIdempotentReplay, Is.True)
                Assert.That(replayDecision.Events, Is.Empty)
                Assert.That(replayDecision.Session.UploadSessionId, Is.EqualTo(sessionId))
                Assert.That(replayDecision.Session.LifecycleState, Is.EqualTo(UploadSessionLifecycleState.Started))
            | Error error -> Assert.Fail($"Expected idempotent replay, got {error.Error}.")
        | Error error -> Assert.Fail($"Expected start to succeed, got {error.Error}.")

    [<Test>]
    member _.AbandonMovesStartedSessionToRetentionPendingAndSchedulesCleanup() =
        let startDecision = UploadSessionActor.decideCommand [] UploadSessionDto.Default (UploadSessionCommand.Start(start "op-start")) (metadata "corr-start")

        let started =
            match startDecision with
            | Ok decision -> applyAll decision.Events UploadSessionDto.Default, decision.Events
            | Error error ->
                Assert.Fail($"Expected start to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let (startedDto, existingEvents) = started
        let abandon = UploadSessionActor.decideCommand existingEvents startedDto (UploadSessionCommand.Abandon "op-abandon") (metadata "corr-abandon")

        match abandon with
        | Ok decision ->
            Assert.That(decision.Events.Length, Is.EqualTo(2))
            Assert.That(decision.Session.LifecycleState, Is.EqualTo(UploadSessionLifecycleState.RetentionPending))
            Assert.That(decision.Session.CleanupReminderOperationId, Is.EqualTo(Some "op-abandon:cleanup"))
        | Error error -> Assert.Fail($"Expected abandon to succeed, got {error.Error}.")

    [<Test>]
    member _.ExpireMovesStartedSessionToRetentionPendingAndSchedulesCleanup() =
        let startDecision = UploadSessionActor.decideCommand [] UploadSessionDto.Default (UploadSessionCommand.Start(start "op-start")) (metadata "corr-start")

        let started =
            match startDecision with
            | Ok decision -> applyAll decision.Events UploadSessionDto.Default, decision.Events
            | Error error ->
                Assert.Fail($"Expected start to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let (startedDto, existingEvents) = started
        let expire = UploadSessionActor.decideCommand existingEvents startedDto (UploadSessionCommand.Expire "op-expire") (metadata "corr-expire")

        match expire with
        | Ok decision ->
            Assert.That(decision.Events.Length, Is.EqualTo(2))
            Assert.That(decision.Session.LifecycleState, Is.EqualTo(UploadSessionLifecycleState.RetentionPending))
            Assert.That(decision.Session.CleanupReminderOperationId, Is.EqualTo(Some "op-expire:cleanup"))
        | Error error -> Assert.Fail($"Expected expire to succeed, got {error.Error}.")

    [<Test>]
    member _.FinalizedSessionRejectsLifecycleMutation() =
        let finalized =
            { UploadSessionDto.Default with
                UploadSessionId = sessionId
                OwnerId = ownerId
                OrganizationId = organizationId
                RepositoryId = repositoryId
                LifecycleState = UploadSessionLifecycleState.Finalized
                FinalizedManifestAddress = Some "manifest-blake3-final"
            }

        let result = UploadSessionActor.decideCommand [] finalized (UploadSessionCommand.Abandon "op-abandon") (metadata "corr-finalized")

        match result with
        | Ok _ -> Assert.Fail("Expected finalized session to reject abandon.")
        | Error error -> Assert.That(error.Error, Is.EqualTo("UploadSession is finalized and cannot be changed by Abandon."))

    [<Test>]
    member _.BlockUploadAndFinalizeCommandsRemainUnimplementedForSkeleton() =
        let startDecision = UploadSessionActor.decideCommand [] UploadSessionDto.Default (UploadSessionCommand.Start(start "op-start")) (metadata "corr-start")

        let started =
            match startDecision with
            | Ok decision -> applyAll decision.Events UploadSessionDto.Default, decision.Events
            | Error error ->
                Assert.Fail($"Expected start to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let (startedDto, existingEvents) = started

        let result =
            UploadSessionActor.decideCommand
                existingEvents
                startedDto
                (UploadSessionCommand.RegisterBlockUploadIntent("op-block", "block-blake3"))
                (metadata "corr-block")

        match result with
        | Ok _ -> Assert.Fail("Expected block upload intent to remain unimplemented in the skeleton.")
        | Error error -> Assert.That(error.Error, Is.EqualTo("UploadSession command RegisterBlockUploadIntent is not implemented in this skeleton."))

    [<Test>]
    member _.CleanupReminderStateCarriesDeletePhysicalStateOperationId() =
        let reminderState = UploadSessionActor.createCleanupReminderState sessionId repositoryId "op-abandon" "corr-abandon"

        Assert.That(reminderState.OperationId, Is.EqualTo("op-abandon:cleanup"))
        Assert.That(reminderState.UploadSessionId, Is.EqualTo(sessionId))
        Assert.That(reminderState.RepositoryId, Is.EqualTo(repositoryId))
        Assert.That(reminderState.CorrelationId, Is.EqualTo("corr-abandon"))

        let state = ReminderState.UploadSessionPhysicalDeletion reminderState

        match state with
        | ReminderState.UploadSessionPhysicalDeletion uploadSessionState -> Assert.That(uploadSessionState.OperationId, Is.EqualTo("op-abandon:cleanup"))
        | _ -> Assert.Fail("Expected UploadSessionPhysicalDeletion reminder state.")
