namespace Grace.Server.Tests

open Grace.Shared
open Grace.Types.ContentBlockMetadata
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

    let encodedBlock bytes =
        match ContentBlockFormat.encode [ { PhysicalOffset = 0L; Bytes = bytes } ] with
        | Ok block -> block
        | Error error ->
            Assert.Fail($"Expected test content block to encode, got {error}.")
            Unchecked.defaultof<ContentBlockFormat.EncodedContentBlock>

    let intentAt operationId blockAddress payloadLength logicalOffset : RegisterBlockUploadIntent =
        {
            OperationId = operationId
            ContentBlockAddress = blockAddress
            LogicalOffset = logicalOffset
            LogicalLength = 11L
            ExpectedPayloadLength = payloadLength
        }

    let intent operationId blockAddress payloadLength = intentAt operationId blockAddress payloadLength 0L

    let confirm operationId blockAddress payload : ConfirmBlockUploaded =
        {
            OperationId = operationId
            ContentBlockAddress = blockAddress
            Payload = payload
            StoragePlacement = { ObjectKey = $"cas/content-blocks/{blockAddress}"; ETag = Some "etag-confirmed" }
        }

    let confirmWithPlacement operationId blockAddress payload placement : ConfirmBlockUploaded =
        { OperationId = operationId; ContentBlockAddress = blockAddress; Payload = payload; StoragePlacement = placement }

    let storagePoolId = StoragePoolId "pool-main"
    let reuseBlockAddress = ContentBlockAddress "block-blake3-reuse"
    let discoveryExpiresAt = timestamp.Plus(Duration.FromMinutes(10L))
    let minimumReuseRunLength = 4

    let reusableMetadataRange = { OrdinalStart = 0; OrdinalCount = 4; ActiveManifestCount = 0; PhysicalOffset = 0L; PhysicalLength = 4096L }

    let reuseMetadata metadataVersion ranges : ContentBlockMetadata =
        {
            Class = nameof ContentBlockMetadata
            StoragePoolId = storagePoolId
            ContentBlockAddress = reuseBlockAddress
            BlockFormatVersion = 1s
            StoragePlacement = { ObjectKey = $"cas/content-blocks/{reuseBlockAddress}"; ETag = Some "etag-reuse" }
            Ranges = ranges
            TotalPhysicalBytes = 4096L
            ActivePhysicalBytes = 0L
            MetadataVersion = metadataVersion
            UpdatedAt = timestamp
        }

    let reuseHint =
        {
            StoragePoolId = storagePoolId
            ContentBlockAddress = reuseBlockAddress
            OrdinalStart = reusableMetadataRange.OrdinalStart
            OrdinalCount = reusableMetadataRange.OrdinalCount
            MetadataVersion = 7L
        }

    let discovery operationId hints =
        UploadSessionCommand.IssueDedupeDiscovery
            { OperationId = operationId; ExpiresAt = discoveryExpiresAt; MinimumReuseRunLength = minimumReuseRunLength; Hints = hints }

    let claim operationId hint metadata =
        UploadSessionCommand.ClaimReuseRanges
            {
                OperationId = operationId
                DiscoveryOperationId = "op-discovery"
                Ranges =
                    [|
                        { Hint = hint; Metadata = metadata }
                    |]
            }

    let apply event dto = UploadSessionDto.UpdateDto event dto

    let applyAll events dto =
        events
        |> List.fold (fun current event -> apply event current) dto

    let startedSession () =
        let startDecision = UploadSessionActor.decideCommand [] UploadSessionDto.Default (UploadSessionCommand.Start(start "op-start")) (metadata "corr-start")

        match startDecision with
        | Ok decision -> applyAll decision.Events UploadSessionDto.Default, decision.Events
        | Error error ->
            Assert.Fail($"Expected start to succeed, got {error.Error}.")
            UploadSessionDto.Default, []

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
    member _.AbandonWithSameOperationIdIsIdempotentReplayWithoutCleanupEvent() =
        let startDecision = UploadSessionActor.decideCommand [] UploadSessionDto.Default (UploadSessionCommand.Start(start "op-start")) (metadata "corr-start")

        let startedDto, startEvents =
            match startDecision with
            | Ok decision -> applyAll decision.Events UploadSessionDto.Default, decision.Events
            | Error error ->
                Assert.Fail($"Expected start to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let abandon = UploadSessionActor.decideCommand startEvents startedDto (UploadSessionCommand.Abandon "op-abandon") (metadata "corr-abandon")

        let retainedDto, retainedEvents =
            match abandon with
            | Ok decision -> decision.Session, startEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected abandon to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let replay = UploadSessionActor.decideCommand retainedEvents retainedDto (UploadSessionCommand.Abandon "op-abandon") (metadata "corr-abandon-retry")

        match replay with
        | Ok replayDecision ->
            Assert.That(replayDecision.WasIdempotentReplay, Is.True)
            Assert.That(replayDecision.Events, Is.Empty)
            Assert.That(replayDecision.Session.CleanupReminderOperationId, Is.EqualTo(Some "op-abandon:cleanup"))
        | Error error -> Assert.Fail($"Expected abandon replay to succeed, got {error.Error}.")

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
    member _.BlockUploadIntentMovesStartedSessionToUploadingBlocks() =
        let block = encodedBlock (Text.Encoding.UTF8.GetBytes("hello world"))
        let startedDto, existingEvents = startedSession ()

        let result =
            UploadSessionActor.decideCommand
                existingEvents
                startedDto
                (UploadSessionCommand.RegisterBlockUploadIntent(intent "op-block-intent" block.Address block.Payload.LongLength))
                (metadata "corr-block-intent")

        match result with
        | Ok decision ->
            Assert.That(decision.WasIdempotentReplay, Is.False)
            Assert.That(decision.Events.Length, Is.EqualTo(1))
            Assert.That(decision.Session.LifecycleState, Is.EqualTo(UploadSessionLifecycleState.UploadingBlocks))
            Assert.That(decision.Session.BlockUploadIntents.Length, Is.EqualTo(1))

            Assert.That(
                decision.Session.BlockUploadIntents[0]
                    .ContentBlockAddress,
                Is.EqualTo(block.Address)
            )
        | Error error -> Assert.Fail($"Expected block upload intent to succeed, got {error.Error}.")

    [<Test>]
    member _.BlockUploadIntentPreservesRepeatedBlockAddressAtDifferentLogicalOffsets() =
        let block = encodedBlock (Text.Encoding.UTF8.GetBytes("hello world"))
        let startedDto, startEvents = startedSession ()

        let first =
            UploadSessionActor.decideCommand
                startEvents
                startedDto
                (UploadSessionCommand.RegisterBlockUploadIntent(intentAt "op-block-intent-1" block.Address block.Payload.LongLength 0L))
                (metadata "corr-block-intent-1")

        let firstDto, firstEvents =
            match first with
            | Ok decision -> decision.Session, startEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected first intent to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let second =
            UploadSessionActor.decideCommand
                firstEvents
                firstDto
                (UploadSessionCommand.RegisterBlockUploadIntent(intentAt "op-block-intent-2" block.Address block.Payload.LongLength 4096L))
                (metadata "corr-block-intent-2")

        match second with
        | Ok decision ->
            Assert.That(decision.Session.BlockUploadIntents.Length, Is.EqualTo(2))

            Assert.That(
                decision.Session.BlockUploadIntents[0]
                    .LogicalOffset,
                Is.EqualTo(0L)
            )

            Assert.That(
                decision.Session.BlockUploadIntents[1]
                    .LogicalOffset,
                Is.EqualTo(4096L)
            )
        | Error error -> Assert.Fail($"Expected repeated block address intent to succeed, got {error.Error}.")

    [<Test>]
    member _.ConfirmBlockUploadedValidatesPayloadAndRecordsPhysicalRanges() =
        let block = encodedBlock (Text.Encoding.UTF8.GetBytes("hello world"))
        let startedDto, startEvents = startedSession ()

        let intentDecision =
            UploadSessionActor.decideCommand
                startEvents
                startedDto
                (UploadSessionCommand.RegisterBlockUploadIntent(intent "op-block-intent" block.Address block.Payload.LongLength))
                (metadata "corr-block-intent")

        let intentDto, intentEvents =
            match intentDecision with
            | Ok decision -> decision.Session, startEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected intent to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let result =
            UploadSessionActor.decideCommand
                intentEvents
                intentDto
                (UploadSessionCommand.ConfirmBlockUploaded(confirm "op-block-confirm" block.Address block.Payload))
                (metadata "corr-block-confirm")

        match result with
        | Ok decision ->
            Assert.That(decision.WasIdempotentReplay, Is.False)
            Assert.That(decision.Session.ConfirmedBlockUploads.Length, Is.EqualTo(1))

            Assert.That(
                decision.Session.ConfirmedBlockUploads[0]
                    .ContentBlockAddress,
                Is.EqualTo(block.Address)
            )

            Assert.That(
                decision.Session.ConfirmedBlockUploads[0]
                    .Ranges
                    .Length,
                Is.EqualTo(1)
            )

            Assert.That(
                decision.Session.ConfirmedBlockUploads[0].Ranges[0]
                    .PhysicalLength,
                Is.EqualTo(11L)
            )
        | Error error -> Assert.Fail($"Expected block upload confirmation to succeed, got {error.Error}.")

    [<Test>]
    member _.ConfirmBlockUploadedRejectsNullStoragePlacementAsGraceError() =
        let block = encodedBlock (Text.Encoding.UTF8.GetBytes("hello world"))
        let startedDto, startEvents = startedSession ()

        let intentDto, intentEvents =
            match
                UploadSessionActor.decideCommand
                    startEvents
                    startedDto
                    (UploadSessionCommand.RegisterBlockUploadIntent(intent "op-block-intent" block.Address block.Payload.LongLength))
                    (metadata "corr-block-intent")
                with
            | Ok decision -> decision.Session, startEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected intent to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let result =
            UploadSessionActor.decideCommand
                intentEvents
                intentDto
                (UploadSessionCommand.ConfirmBlockUploaded(
                    confirmWithPlacement "op-block-confirm" block.Address block.Payload Unchecked.defaultof<ContentBlockStoragePlacement>
                ))
                (metadata "corr-block-confirm")

        match result with
        | Ok _ -> Assert.Fail("Expected null storage placement to be rejected.")
        | Error error -> Assert.That(error.Error, Is.EqualTo("StoragePlacement is required."))

    [<Test>]
    member _.ConfirmBlockUploadedMatchesAnyCompatibleDuplicateIntent() =
        let block = encodedBlock (Text.Encoding.UTF8.GetBytes("hello world"))
        let startedDto, startEvents = startedSession ()

        let first =
            UploadSessionActor.decideCommand
                startEvents
                startedDto
                (UploadSessionCommand.RegisterBlockUploadIntent(intentAt "op-block-intent-1" block.Address (block.Payload.LongLength + 1L) 0L))
                (metadata "corr-block-intent-1")

        let firstDto, firstEvents =
            match first with
            | Ok decision -> decision.Session, startEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected first intent to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let second =
            UploadSessionActor.decideCommand
                firstEvents
                firstDto
                (UploadSessionCommand.RegisterBlockUploadIntent(intentAt "op-block-intent-2" block.Address block.Payload.LongLength 4096L))
                (metadata "corr-block-intent-2")

        let secondDto, secondEvents =
            match second with
            | Ok decision -> decision.Session, firstEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected second intent to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let result =
            UploadSessionActor.decideCommand
                secondEvents
                secondDto
                (UploadSessionCommand.ConfirmBlockUploaded(confirm "op-block-confirm" block.Address block.Payload))
                (metadata "corr-block-confirm")

        match result with
        | Ok decision -> Assert.That(decision.Session.ConfirmedBlockUploads.Length, Is.EqualTo(1))
        | Error error -> Assert.Fail($"Expected confirmation to match compatible duplicate intent, got {error.Error}.")

    [<Test>]
    member _.ConfirmBlockUploadedRejectsIntentLogicalLengthMismatch() =
        let block = encodedBlock (Text.Encoding.UTF8.GetBytes("hello world"))
        let startedDto, startEvents = startedSession ()

        let intentDto, intentEvents =
            match
                UploadSessionActor.decideCommand
                    startEvents
                    startedDto
                    (UploadSessionCommand.RegisterBlockUploadIntent
                        { intentAt "op-block-intent" block.Address block.Payload.LongLength 0L with LogicalLength = 10L })
                    (metadata "corr-block-intent")
                with
            | Ok decision -> decision.Session, startEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected intent to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let result =
            UploadSessionActor.decideCommand
                intentEvents
                intentDto
                (UploadSessionCommand.ConfirmBlockUploaded(confirm "op-block-confirm" block.Address block.Payload))
                (metadata "corr-block-confirm")

        match result with
        | Ok _ -> Assert.Fail("Expected logical length mismatch to be rejected.")
        | Error error -> Assert.That(error.Error, Is.EqualTo("ContentBlock logical length mismatch. Expected one of [10], actual 11."))

    [<Test>]
    member _.ConfirmBlockUploadedRejectsCorruptPayloadWithoutConsumingOperationId() =
        let block = encodedBlock (Text.Encoding.UTF8.GetBytes("hello world"))
        let corruptPayload = Array.copy block.Payload
        corruptPayload[0] <- corruptPayload[0] ^^^ 0xffuy
        let startedDto, startEvents = startedSession ()

        let intentDto, intentEvents =
            match
                UploadSessionActor.decideCommand
                    startEvents
                    startedDto
                    (UploadSessionCommand.RegisterBlockUploadIntent(intent "op-block-intent" block.Address block.Payload.LongLength))
                    (metadata "corr-block-intent")
                with
            | Ok decision -> decision.Session, startEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected intent to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let corrupt =
            UploadSessionActor.decideCommand
                intentEvents
                intentDto
                (UploadSessionCommand.ConfirmBlockUploaded(confirm "op-block-confirm" block.Address corruptPayload))
                (metadata "corr-block-confirm-corrupt")

        match corrupt with
        | Ok _ -> Assert.Fail("Expected corrupt ContentBlock payload to be rejected.")
        | Error error -> Assert.That(error.Error, Does.Contain("ContentBlock payload is invalid"))

        let retry =
            UploadSessionActor.decideCommand
                intentEvents
                intentDto
                (UploadSessionCommand.ConfirmBlockUploaded(confirm "op-block-confirm" block.Address block.Payload))
                (metadata "corr-block-confirm-retry")

        match retry with
        | Ok decision -> Assert.That(decision.Session.ConfirmedBlockUploads.Length, Is.EqualTo(1))
        | Error error -> Assert.Fail($"Expected retry after corrupt payload to succeed, got {error.Error}.")

    [<Test>]
    member _.ConfirmBlockUploadedWithSameOperationIdIsIdempotentReplay() =
        let block = encodedBlock (Text.Encoding.UTF8.GetBytes("hello world"))
        let startedDto, startEvents = startedSession ()

        let intentDto, intentEvents =
            match
                UploadSessionActor.decideCommand
                    startEvents
                    startedDto
                    (UploadSessionCommand.RegisterBlockUploadIntent(intent "op-block-intent" block.Address block.Payload.LongLength))
                    (metadata "corr-block-intent")
                with
            | Ok decision -> decision.Session, startEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected intent to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let first =
            UploadSessionActor.decideCommand
                intentEvents
                intentDto
                (UploadSessionCommand.ConfirmBlockUploaded(confirm "op-block-confirm" block.Address block.Payload))
                (metadata "corr-block-confirm")

        match first with
        | Ok decision ->
            let replay =
                UploadSessionActor.decideCommand
                    (intentEvents @ decision.Events)
                    decision.Session
                    (UploadSessionCommand.ConfirmBlockUploaded(confirm "op-block-confirm" block.Address block.Payload))
                    (metadata "corr-block-confirm-replay")

            match replay with
            | Ok replayDecision ->
                Assert.That(replayDecision.WasIdempotentReplay, Is.True)
                Assert.That(replayDecision.Events, Is.Empty)
                Assert.That(replayDecision.Session.ConfirmedBlockUploads.Length, Is.EqualTo(1))
            | Error error -> Assert.Fail($"Expected idempotent replay, got {error.Error}.")
        | Error error -> Assert.Fail($"Expected first confirmation to succeed, got {error.Error}.")

    [<Test>]
    member _.FinalizeCommandRemainsUnimplementedForSkeleton() =
        let startedDto, existingEvents = startedSession ()

        let result =
            UploadSessionActor.decideCommand
                existingEvents
                startedDto
                (UploadSessionCommand.FinalizeManifest("op-finalize", "manifest-blake3"))
                (metadata "corr-finalize")

        match result with
        | Ok _ -> Assert.Fail("Expected finalize to remain unimplemented in this slice.")
        | Error error -> Assert.That(error.Error, Is.EqualTo("UploadSession command FinalizeManifest is not implemented in this skeleton."))

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

    [<Test>]
    member _.DeletePhysicalStateRetryAfterStateClearedDrainsAsIdempotentReplay() =
        let result =
            UploadSessionActor.decideCommand
                []
                UploadSessionDto.Default
                (UploadSessionCommand.DeletePhysicalState "op-abandon:cleanup")
                (metadata "corr-cleanup-retry")

        match result with
        | Ok decision ->
            Assert.That(decision.WasIdempotentReplay, Is.True)
            Assert.That(decision.Events, Is.Empty)
            Assert.That(decision.Session.LifecycleState, Is.EqualTo(UploadSessionLifecycleState.NotStarted))
        | Error error -> Assert.Fail($"Expected cleanup retry to drain, got {error.Error}.")

    [<Test>]
    member _.ClaimReuseRangesRejectsStaleDiscoveryHintVersion() =
        let startedDto, startEvents = startedSession ()

        let issued = UploadSessionActor.decideCommand startEvents startedDto (discovery "op-discovery" [| reuseHint |]) (metadata "corr-discovery")

        let discoveredDto, discoveryEvents =
            match issued with
            | Ok decision -> decision.Session, startEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected discovery to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let staleMetadata = reuseMetadata 8L [| reusableMetadataRange |]

        let result = UploadSessionActor.decideCommand discoveryEvents discoveredDto (claim "op-claim" reuseHint staleMetadata) (metadata "corr-claim")

        match result with
        | Ok _ -> Assert.Fail("Expected stale discovery hint to be rejected.")
        | Error error -> Assert.That(error.Error, Does.Contain("stale"))

    [<Test>]
    member _.ClaimReuseRangesRejectsExpiredDiscovery() =
        let startedDto, startEvents = startedSession ()

        let issued = UploadSessionActor.decideCommand startEvents startedDto (discovery "op-discovery" [| reuseHint |]) (metadata "corr-discovery")

        let discoveredDto, discoveryEvents =
            match issued with
            | Ok decision -> decision.Session, startEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected discovery to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let result =
            UploadSessionActor.decideCommand
                discoveryEvents
                discoveredDto
                (claim "op-claim" reuseHint (reuseMetadata 7L [| reusableMetadataRange |]))
                { metadata "corr-claim" with Timestamp = discoveryExpiresAt }

        match result with
        | Ok _ -> Assert.Fail("Expected expired discovery to be rejected.")
        | Error error -> Assert.That(error.Error, Does.Contain("expired"))

    [<Test>]
    member _.ClaimReuseRangesRejectsMissingAuthoritativePhysicalRange() =
        let startedDto, startEvents = startedSession ()

        let issued = UploadSessionActor.decideCommand startEvents startedDto (discovery "op-discovery" [| reuseHint |]) (metadata "corr-discovery")

        let discoveredDto, discoveryEvents =
            match issued with
            | Ok decision -> decision.Session, startEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected discovery to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let metadataWithoutRange =
            reuseMetadata
                7L
                [|
                    { reusableMetadataRange with OrdinalStart = 8; PhysicalOffset = 8192L }
                |]

        let result = UploadSessionActor.decideCommand discoveryEvents discoveredDto (claim "op-claim" reuseHint metadataWithoutRange) (metadata "corr-claim")

        match result with
        | Ok _ -> Assert.Fail("Expected absent authoritative physical range to be rejected.")
        | Error error -> Assert.That(error.Error, Does.Contain("absent"))

    [<Test>]
    member _.ClaimReuseRangesRejectsRunsBelowMinimumReuseLength() =
        let shortHint = { reuseHint with OrdinalCount = minimumReuseRunLength - 1 }
        let shortRange = { reusableMetadataRange with OrdinalCount = minimumReuseRunLength - 1 }
        let startedDto, startEvents = startedSession ()

        let issued = UploadSessionActor.decideCommand startEvents startedDto (discovery "op-discovery" [| shortHint |]) (metadata "corr-discovery")

        let discoveredDto, discoveryEvents =
            match issued with
            | Ok decision -> decision.Session, startEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected discovery to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let result =
            UploadSessionActor.decideCommand
                discoveryEvents
                discoveredDto
                (claim "op-claim" shortHint (reuseMetadata 7L [| shortRange |]))
                (metadata "corr-claim")

        match result with
        | Ok _ -> Assert.Fail("Expected too-short reuse run to be rejected.")
        | Error error -> Assert.That(error.Error, Does.Contain("minimum reuse run"))

    [<Test>]
    member _.ClaimReuseRangesWithSameOperationIdIsStableIdempotentReplay() =
        let startedDto, startEvents = startedSession ()

        let issued = UploadSessionActor.decideCommand startEvents startedDto (discovery "op-discovery" [| reuseHint |]) (metadata "corr-discovery")

        let discoveredDto, discoveryEvents =
            match issued with
            | Ok decision -> decision.Session, startEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected discovery to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let command = claim "op-claim" reuseHint (reuseMetadata 7L [| reusableMetadataRange |])
        let first = UploadSessionActor.decideCommand discoveryEvents discoveredDto command (metadata "corr-claim")

        match first with
        | Ok decision ->
            Assert.That(decision.WasIdempotentReplay, Is.False)
            Assert.That(decision.Session.LifecycleState, Is.EqualTo(UploadSessionLifecycleState.ClaimingRanges))
            Assert.That(decision.Session.ClaimedReuseRanges.Length, Is.EqualTo(1))

            let replay = UploadSessionActor.decideCommand (discoveryEvents @ decision.Events) decision.Session command (metadata "corr-claim-replay")

            match replay with
            | Ok replayDecision ->
                Assert.That(replayDecision.WasIdempotentReplay, Is.True)
                Assert.That(replayDecision.Events, Is.Empty)
                Assert.That(replayDecision.Session.ClaimedReuseRanges.Length, Is.EqualTo(decision.Session.ClaimedReuseRanges.Length))
                Assert.That(replayDecision.Session.ClaimedReuseRanges[0], Is.EqualTo(decision.Session.ClaimedReuseRanges[0]))
            | Error error -> Assert.Fail($"Expected idempotent claim replay, got {error.Error}.")
        | Error error -> Assert.Fail($"Expected claim to succeed, got {error.Error}.")

    [<Test>]
    member _.ClaimReuseRangesRejectsDuplicateHintsInSameCommand() =
        let startedDto, startEvents = startedSession ()

        let issued = UploadSessionActor.decideCommand startEvents startedDto (discovery "op-discovery" [| reuseHint |]) (metadata "corr-discovery")

        let discoveredDto, discoveryEvents =
            match issued with
            | Ok decision -> decision.Session, startEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected discovery to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let authoritativeMetadata = reuseMetadata 7L [| reusableMetadataRange |]

        let duplicateClaim =
            UploadSessionCommand.ClaimReuseRanges
                {
                    OperationId = "op-claim"
                    DiscoveryOperationId = "op-discovery"
                    Ranges =
                        [|
                            { Hint = reuseHint; Metadata = authoritativeMetadata }
                            { Hint = reuseHint; Metadata = authoritativeMetadata }
                        |]
                }

        let result = UploadSessionActor.decideCommand discoveryEvents discoveredDto duplicateClaim (metadata "corr-claim")

        match result with
        | Ok _ -> Assert.Fail("Expected duplicate reuse hint claims to be rejected.")
        | Error error -> Assert.That(error.Error, Does.Contain("already been claimed"))

    [<Test>]
    member _.ClaimReuseRangesRejectsAlreadyClaimedHintWithNewOperationId() =
        let startedDto, startEvents = startedSession ()

        let issued = UploadSessionActor.decideCommand startEvents startedDto (discovery "op-discovery" [| reuseHint |]) (metadata "corr-discovery")

        let discoveredDto, discoveryEvents =
            match issued with
            | Ok decision -> decision.Session, startEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected discovery to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let command = claim "op-claim" reuseHint (reuseMetadata 7L [| reusableMetadataRange |])
        let first = UploadSessionActor.decideCommand discoveryEvents discoveredDto command (metadata "corr-claim")

        let claimedDto, claimedEvents =
            match first with
            | Ok decision -> decision.Session, discoveryEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected first claim to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let second =
            UploadSessionActor.decideCommand
                claimedEvents
                claimedDto
                (claim "op-claim-again" reuseHint (reuseMetadata 7L [| reusableMetadataRange |]))
                (metadata "corr-claim-again")

        match second with
        | Ok _ -> Assert.Fail("Expected already claimed reuse hint to be rejected.")
        | Error error -> Assert.That(error.Error, Does.Contain("already been claimed"))

    [<Test>]
    member _.IssueDedupeDiscoveryNullPayloadReturnsGraceError() =
        let startedDto, startEvents = startedSession ()

        let result =
            UploadSessionActor.decideCommand
                startEvents
                startedDto
                (UploadSessionCommand.IssueDedupeDiscovery Unchecked.defaultof<IssueDedupeDiscovery>)
                (metadata "corr-null-discovery")

        match result with
        | Ok _ -> Assert.Fail("Expected null discovery payload to be rejected.")
        | Error error -> Assert.That(error.Error, Does.Contain("requires a non-empty operation id"))

    [<Test>]
    member _.ClaimReuseRangesNullPayloadReturnsGraceError() =
        let startedDto, startEvents = startedSession ()

        let result =
            UploadSessionActor.decideCommand
                startEvents
                startedDto
                (UploadSessionCommand.ClaimReuseRanges Unchecked.defaultof<ClaimReuseRanges>)
                (metadata "corr-null-claim")

        match result with
        | Ok _ -> Assert.Fail("Expected null claim payload to be rejected.")
        | Error error -> Assert.That(error.Error, Does.Contain("requires a non-empty operation id"))
