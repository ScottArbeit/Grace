namespace Grace.Server.Tests

open Grace.Shared
open Grace.Types.ContentBlockMetadata
open Grace.Types.Reminder
open Grace.Types.Common
open Grace.Types.UploadSession
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic

module UploadSessionActor = Grace.Actors.UploadSession
module ContentBlockMetadataActor = Grace.Actors.ContentBlockMetadata

[<Parallelizable(ParallelScope.All)>]
type UploadSessionActorTests() =

    let timestamp = Instant.FromUtc(2026, 5, 24, 12, 0)

    let metadata correlationId =
        {
            Timestamp = timestamp
            CorrelationId = correlationId
            Principal = "tester"
            ClientType = Microsoft.FSharp.Core.Option.None
            Properties = Dictionary<string, string>()
        }

    let sessionId = Guid.Parse("ab6fd828-87a3-4b7a-9c2e-5a83f5e8b1b0")
    let ownerId = Guid.Parse("4f512f0d-d6b0-488a-934c-db16840d2a8d")
    let organizationId = Guid.Parse("2b4ffda8-1129-47df-9c0c-76371153a807")
    let repositoryId = Guid.Parse("75ce5e36-25f6-4da0-afdd-ad4ad56540d5")
    let sessionStoragePoolId = StoragePoolId "pool-session-recorded"

    let start operationId =
        {
            UploadSessionId = sessionId
            OwnerId = ownerId
            OrganizationId = organizationId
            RepositoryId = repositoryId
            StoragePoolId = sessionStoragePoolId
            AuthorizedScope = "/src"
            FileContentHash = "blake3:file"
            ExpectedSize = 1_048_576L
            ChunkingSuiteId = RabinChunking.SuiteName
            SamplingPolicySnapshot = "sparse-key-v1"
            OperationId = operationId
        }

    let startForManifest operationId (fileBytes: byte array) =
        { start operationId with FileContentHash = FileContentHash(ContentAddress.computeBlake3Hex fileBytes); ExpectedSize = int64 fileBytes.Length }

    let encodedBlock bytes =
        match ContentBlockFormat.encode [ { PhysicalOffset = 0L; Bytes = bytes } ] with
        | Ok block -> block
        | Error error ->
            Assert.Fail($"Expected test content block to encode, got {error}.")
            Unchecked.defaultof<ContentBlockFormat.EncodedContentBlock>

    let intentAtWithLength operationId blockAddress payloadLength logicalOffset logicalLength : RegisterBlockUploadIntent =
        {
            OperationId = operationId
            ContentBlockAddress = blockAddress
            LogicalOffset = logicalOffset
            LogicalLength = logicalLength
            ExpectedPayloadLength = payloadLength
        }

    let intentAt operationId blockAddress payloadLength logicalOffset = intentAtWithLength operationId blockAddress payloadLength logicalOffset 11L

    let intent operationId blockAddress payloadLength = intentAt operationId blockAddress payloadLength 0L

    let intentForBlock operationId (block: ContentBlockFormat.EncodedContentBlock) logicalOffset =
        let logicalLength =
            block.Chunks
            |> Array.sumBy (fun chunk -> int64 chunk.Length)

        intentAtWithLength operationId block.Address block.Payload.LongLength logicalOffset logicalLength

    let placementFor blockAddress eTag =
        {
            StorageAccountName = "cas-account"
            StorageContainerName = StorageContainerName "cas-container"
            ObjectKey = StorageKeys.contentBlockObjectKey blockAddress
            ETag = eTag
        }

    let confirm operationId blockAddress payload : ConfirmBlockUploaded =
        {
            OperationId = operationId
            ContentBlockAddress = blockAddress
            Payload = payload
            StoragePlacement = placementFor blockAddress (Some "etag-confirmed")
        }

    let confirmWithPlacement operationId blockAddress payload placement : ConfirmBlockUploaded =
        { OperationId = operationId; ContentBlockAddress = blockAddress; Payload = payload; StoragePlacement = placement }

    let manifestFor (fileBytes: byte array) (blocks: ContentBlockFormat.EncodedContentBlock array) =
        let contentBlocks = ResizeArray<ContentBlock>()
        let mutable offset = 0L

        for block in blocks do
            let size =
                block.Chunks
                |> Array.sumBy (fun chunk -> int64 chunk.Length)

            contentBlocks.Add(ContentBlock.Create(block.Address, offset, size))
            offset <- offset + size

        let fileContentHash = FileContentHash(ContentAddress.computeBlake3Hex fileBytes)

        let manifest =
            FileManifest.Create(ManifestAddress String.Empty, RabinChunking.SuiteName, fileContentHash, int64 fileBytes.Length, List.ofSeq contentBlocks)

        { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }

    let payloadFor (block: ContentBlockFormat.EncodedContentBlock) : FinalizeManifestBlockPayload = { Address = block.Address; Payload = block.Payload }

    let finalize operationId manifest payloads =
        UploadSessionCommand.FinalizeManifest { OperationId = operationId; Manifest = manifest; BlockPayloads = payloads; ClaimedMetadata = Array.empty }

    let finalizeWithClaimedMetadata operationId manifest payloads claimedMetadata =
        UploadSessionCommand.FinalizeManifest { OperationId = operationId; Manifest = manifest; BlockPayloads = payloads; ClaimedMetadata = claimedMetadata }

    let storagePoolId = sessionStoragePoolId
    let reuseBlockAddress = ContentBlockAddress "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
    let discoveryExpiresAt = timestamp.Plus(Duration.FromMinutes(10L))
    let minimumReuseRunLength = 4

    let reusableMetadataRange = { OrdinalStart = 0; OrdinalCount = 4; ActiveManifestCount = 0; PhysicalOffset = 0L; PhysicalLength = 4096L }

    let reuseMetadata metadataVersion ranges : ContentBlockMetadata =
        {
            Class = nameof ContentBlockMetadata
            StoragePoolId = storagePoolId
            ContentBlockAddress = reuseBlockAddress
            BlockFormatVersion = 1s
            StoragePlacement = placementFor reuseBlockAddress (Some "etag-reuse")
            Ranges = ranges
            TotalPhysicalBytes = 4096L
            ActivePhysicalBytes = 0L
            MetadataVersion = metadataVersion
            UpdatedAt = timestamp
        }

    let reuseMetadataFor contentBlockAddress metadataVersion ranges : ContentBlockMetadata =
        { reuseMetadata metadataVersion ranges with
            ContentBlockAddress = contentBlockAddress
            StoragePlacement = placementFor contentBlockAddress (Some "etag-reuse")
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

    let decisionOrFail message result =
        match result with
        | Ok decision -> decision
        | Error error ->
            Assert.Fail($"{message}, got {error.Error}.")
            Unchecked.defaultof<_>

    [<Test>]
    member _.StartWithSameOperationIdIsIdempotentReplay() =
        let command = UploadSessionCommand.Start(start "op-start")
        let first = UploadSessionActor.decideCommand [] UploadSessionDto.Default command (metadata "corr-start-1")

        match first with
        | Ok decision ->
            Assert.That(decision.WasIdempotentReplay, Is.False)
            Assert.That(decision.Events.Length, Is.EqualTo(1))

            let dto = applyAll decision.Events UploadSessionDto.Default
            Assert.That(dto.StoragePoolId, Is.EqualTo(sessionStoragePoolId))

            let replay = UploadSessionActor.decideCommand decision.Events dto command (metadata "corr-start-2")

            match replay with
            | Ok replayDecision ->
                Assert.That(replayDecision.WasIdempotentReplay, Is.True)
                Assert.That(replayDecision.Events, Is.Empty)
                Assert.That(replayDecision.Session.UploadSessionId, Is.EqualTo(sessionId))
                Assert.That(replayDecision.Session.StoragePoolId, Is.EqualTo(sessionStoragePoolId))
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
    member _.FinalizedSessionRetainsLiveManifestForGcSafety() =
        let manifestAddress = ManifestAddress "manifest-blake3-final"

        let finalized =
            { UploadSessionDto.Default with
                UploadSessionId = sessionId
                RepositoryId = repositoryId
                LifecycleState = UploadSessionLifecycleState.StateDeleted
                FinalizedManifestAddress = Some manifestAddress
            }

        let abandoned = { finalized with LifecycleState = UploadSessionLifecycleState.StateDeleted; FinalizedManifestAddress = None }

        Assert.That(retainsFinalizedManifest manifestAddress finalized, Is.True)
        Assert.That(retainsFinalizedManifest manifestAddress abandoned, Is.False)
        Assert.That(retainsFinalizedManifest (ManifestAddress String.Empty) finalized, Is.False)

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
    member _.FinalizeManifestFromUploadedBlockValidatesReconstructionAndFinalizes() =
        let fileBytes = Text.Encoding.UTF8.GetBytes("hello world")
        let block = encodedBlock fileBytes
        let manifest = manifestFor fileBytes [| block |]

        let startDecision =
            UploadSessionActor.decideCommand
                []
                UploadSessionDto.Default
                (UploadSessionCommand.Start(startForManifest "op-start" fileBytes))
                (metadata "corr-start")

        let startedDto, startEvents =
            match startDecision with
            | Ok decision -> decision.Session, decision.Events
            | Error error ->
                Assert.Fail($"Expected start to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let intentDecision =
            UploadSessionActor.decideCommand
                startEvents
                startedDto
                (UploadSessionCommand.RegisterBlockUploadIntent(intentForBlock "op-block-intent" block 0L))
                (metadata "corr-block-intent")

        let intentDto, intentEvents =
            match intentDecision with
            | Ok decision -> decision.Session, startEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected intent to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let confirmDecision =
            UploadSessionActor.decideCommand
                intentEvents
                intentDto
                (UploadSessionCommand.ConfirmBlockUploaded(confirm "op-block-confirm" block.Address block.Payload))
                (metadata "corr-block-confirm")

        let confirmedDto, confirmedEvents =
            match confirmDecision with
            | Ok decision -> decision.Session, intentEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected confirmation to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let result =
            UploadSessionActor.decideCommand confirmedEvents confirmedDto (finalize "op-finalize" manifest [| payloadFor block |]) (metadata "corr-finalize")

        match result with
        | Ok decision ->
            Assert.That(decision.WasIdempotentReplay, Is.False)
            Assert.That(decision.Events.Length, Is.EqualTo(2))
            Assert.That(decision.Session.LifecycleState, Is.EqualTo(UploadSessionLifecycleState.RetentionPending))
            Assert.That(decision.Session.FinalizedManifestAddress, Is.EqualTo(Some manifest.ManifestAddress))
            Assert.That(decision.Session.CleanupReminderOperationId, Is.EqualTo(Some "op-finalize:cleanup"))
        | Error error -> Assert.Fail($"Expected finalize to succeed, got {error.Error}.")

    [<Test>]
    member _.FinalizeManifestCreatesRepositoryPoolMetadataForUploadedBlocks() =
        let firstBytes = Text.Encoding.UTF8.GetBytes("hello first")
        let secondBytes = Text.Encoding.UTF8.GetBytes("hello second")
        let fileBytes = Array.concat [ firstBytes; secondBytes ]
        let firstBlock = encodedBlock firstBytes
        let secondBlock = encodedBlock secondBytes
        let manifest = manifestFor fileBytes [| firstBlock; secondBlock |]

        let session =
            { UploadSessionDto.Default with
                UploadSessionId = sessionId
                ConfirmedBlockUploads =
                    [|
                        {
                            ContentBlockAddress = firstBlock.Address
                            PayloadLength = firstBlock.Payload.LongLength
                            StoragePlacement = placementFor firstBlock.Address (Some "etag-first")
                            Ranges =
                                [|
                                    {
                                        OrdinalStart = 0
                                        OrdinalCount = 1
                                        ActiveManifestCount = 0
                                        PhysicalOffset = 0L
                                        PhysicalLength = int64 firstBytes.Length
                                    }
                                |]
                            ConfirmedAt = timestamp
                        }
                        {
                            ContentBlockAddress = secondBlock.Address
                            PayloadLength = secondBlock.Payload.LongLength
                            StoragePlacement = placementFor secondBlock.Address (Some "etag-second")
                            Ranges =
                                [|
                                    {
                                        OrdinalStart = 0
                                        OrdinalCount = 1
                                        ActiveManifestCount = 0
                                        PhysicalOffset = 0L
                                        PhysicalLength = int64 secondBytes.Length
                                    }
                                |]
                            ConfirmedAt = timestamp
                        }
                    |]
            }

        let expectedStoragePoolId = sessionStoragePoolId

        let commands = UploadSessionActor.createContentBlockMetadataMergeCommandsForFinalizedUploads expectedStoragePoolId "op-finalize" session manifest

        Assert.That(commands, Has.Length.EqualTo(2))

        let assertMerge command (expectedAddress: ContentBlockAddress) (expectedObjectKey: string) =
            match command with
            | ContentBlockMetadataCommand.MergePhysicalRanges merge ->
                Assert.That(merge.OperationId, Is.EqualTo($"op-finalize:upload-session:{sessionId:N}:content-block-metadata:{expectedAddress}"))
                Assert.That(merge.StoragePoolId, Is.EqualTo(expectedStoragePoolId))
                Assert.That(merge.ContentBlockAddress, Is.EqualTo(expectedAddress))
                Assert.That(merge.StoragePlacement.ObjectKey, Is.EqualTo(expectedObjectKey))
                Assert.That(merge.Ranges, Has.Length.EqualTo(1))
                Assert.That(merge.Ranges[0].OrdinalStart, Is.EqualTo(0))
                Assert.That(merge.Ranges[0].OrdinalCount, Is.EqualTo(1))
                Assert.That(merge.Ranges[0].ActiveManifestCount, Is.EqualTo(1))
            | _ -> Assert.Fail("Expected uploaded block finalization to create ContentBlockMetadata MergePhysicalRanges commands.")

        assertMerge commands[0] firstBlock.Address (StorageKeys.contentBlockObjectKey firstBlock.Address)
        assertMerge commands[1] secondBlock.Address (StorageKeys.contentBlockObjectKey secondBlock.Address)

    [<Test>]
    member _.FinalizeManifestCreatesSessionPoolMetadataForClaimedReuseRangesWithoutConfirmedUpload() =
        let fileBytes = Text.Encoding.UTF8.GetBytes("claimed authoritative metadata")
        let block = encodedBlock fileBytes
        let manifest = manifestFor fileBytes [| block |]

        let metadataRange =
            { reusableMetadataRange with
                OrdinalStart = 0
                OrdinalCount = minimumReuseRunLength
                PhysicalLength = int64 fileBytes.Length
                ActiveManifestCount = 2
            }

        let claimedRange =
            {
                StoragePoolId = sessionStoragePoolId
                ContentBlockAddress = block.Address
                OrdinalStart = metadataRange.OrdinalStart
                OrdinalCount = metadataRange.OrdinalCount
                PhysicalOffset = metadataRange.PhysicalOffset
                PhysicalLength = metadataRange.PhysicalLength
                MetadataVersion = 7L
                ClaimedAt = timestamp
            }

        let session =
            { UploadSessionDto.Default with UploadSessionId = sessionId; StoragePoolId = sessionStoragePoolId; ClaimedReuseRanges = [| claimedRange |] }

        let authoritativeMetadata = reuseMetadataFor block.Address 7L [| metadataRange |]

        let commands =
            UploadSessionActor.createContentBlockMetadataMergeCommandsForFinalizedBlocks
                sessionStoragePoolId
                "op-finalize"
                session
                manifest
                [| authoritativeMetadata |]

        Assert.That(commands, Has.Length.EqualTo(1))

        match commands[0] with
        | ContentBlockMetadataCommand.MergePhysicalRanges merge ->
            Assert.That(merge.OperationId, Is.EqualTo($"op-finalize:upload-session:{sessionId:N}:content-block-metadata:{block.Address}"))
            Assert.That(merge.StoragePoolId, Is.EqualTo(sessionStoragePoolId))
            Assert.That(merge.ContentBlockAddress, Is.EqualTo(block.Address))
            Assert.That(merge.StoragePlacement, Is.EqualTo(authoritativeMetadata.StoragePlacement))
            Assert.That(merge.Ranges, Has.Length.EqualTo(1))
            Assert.That(merge.Ranges[0].OrdinalStart, Is.EqualTo(metadataRange.OrdinalStart))
            Assert.That(merge.Ranges[0].OrdinalCount, Is.EqualTo(metadataRange.OrdinalCount))
            Assert.That(merge.Ranges[0].PhysicalOffset, Is.EqualTo(metadataRange.PhysicalOffset))
            Assert.That(merge.Ranges[0].PhysicalLength, Is.EqualTo(metadataRange.PhysicalLength))
            Assert.That(merge.Ranges[0].ActiveManifestCount, Is.EqualTo(1))
        | _ -> Assert.Fail("Expected claimed reuse finalization to create a ContentBlockMetadata MergePhysicalRanges command.")

    [<Test>]
    member _.FinalizeManifestSkipsStaleClaimedMetadataWhenConfirmedUploadSatisfiesBlock() =
        let fileBytes = Text.Encoding.UTF8.GetBytes("uploaded block supersedes stale reuse claim")
        let block = encodedBlock fileBytes
        let manifest = manifestFor fileBytes [| block |]
        let intent = intentForBlock "op-intent" block 0L

        let claimedRange =
            {
                StoragePoolId = sessionStoragePoolId
                ContentBlockAddress = block.Address
                OrdinalStart = 0
                OrdinalCount = minimumReuseRunLength
                PhysicalOffset = 0L
                PhysicalLength = int64 fileBytes.Length
                MetadataVersion = 7L
                ClaimedAt = timestamp
            }

        let session =
            { UploadSessionDto.Default with
                UploadSessionId = sessionId
                StoragePoolId = sessionStoragePoolId
                LifecycleState = UploadSessionLifecycleState.ClaimingRanges
                FileContentHash = manifest.FileContentHash
                ExpectedSize = manifest.Size
                ChunkingSuiteId = manifest.ChunkingSuiteId
                BlockUploadIntents =
                    [|
                        {
                            ContentBlockAddress = intent.ContentBlockAddress
                            LogicalOffset = intent.LogicalOffset
                            LogicalLength = intent.LogicalLength
                            ExpectedPayloadLength = intent.ExpectedPayloadLength
                            RegisteredAt = timestamp
                        }
                    |]
                ConfirmedBlockUploads =
                    [|
                        {
                            ContentBlockAddress = block.Address
                            PayloadLength = block.Payload.LongLength
                            StoragePlacement = placementFor block.Address (Some "etag-confirmed")
                            Ranges =
                                [|
                                    {
                                        OrdinalStart = 0
                                        OrdinalCount = 1
                                        ActiveManifestCount = 0
                                        PhysicalOffset = 0L
                                        PhysicalLength = int64 fileBytes.Length
                                    }
                                |]
                            ConfirmedAt = timestamp
                        }
                    |]
                ClaimedReuseRanges = [| claimedRange |]
            }

        let staleClaimedMetadata =
            reuseMetadataFor
                block.Address
                8L
                [|
                    {
                        OrdinalStart = 0
                        OrdinalCount = minimumReuseRunLength
                        ActiveManifestCount = 0
                        PhysicalOffset = 0L
                        PhysicalLength = int64 fileBytes.Length
                    }
                |]

        let finalizeCommand = finalizeWithClaimedMetadata "op-finalize" manifest [| payloadFor block |] [| staleClaimedMetadata |]
        let result = UploadSessionActor.decideCommand [] session finalizeCommand (metadata "corr-finalize")

        match result with
        | Ok decision ->
            Assert.That(decision.WasIdempotentReplay, Is.False)
            Assert.That(decision.Session.LifecycleState, Is.EqualTo(UploadSessionLifecycleState.RetentionPending))

            let commands =
                UploadSessionActor.createContentBlockMetadataMergeCommandsForFinalizedBlocks
                    sessionStoragePoolId
                    "op-finalize"
                    session
                    manifest
                    [| staleClaimedMetadata |]

            Assert.That(commands, Has.Length.EqualTo(1))

            match commands[0] with
            | ContentBlockMetadataCommand.MergePhysicalRanges merge ->
                Assert.That(merge.ContentBlockAddress, Is.EqualTo(block.Address))
                Assert.That(merge.StoragePlacement.ObjectKey, Is.EqualTo(StorageKeys.contentBlockObjectKey block.Address))
                Assert.That(merge.Ranges[0].ActiveManifestCount, Is.EqualTo(1))
            | _ -> Assert.Fail("Expected confirmed upload to provide the metadata merge command.")
        | Error error -> Assert.Fail($"Expected confirmed upload to supersede stale claimed metadata, got {error.Error}.")

    [<Test>]
    member _.FinalizeManifestSelectsFreshClaimWhenStaleDuplicateClaimForSameBlockExists() =
        let fileBytes = Text.Encoding.UTF8.GetBytes("fresh claim supersedes stale duplicate")
        let block = encodedBlock fileBytes
        let manifest = manifestFor fileBytes [| block |]

        let staleRange =
            { reusableMetadataRange with OrdinalStart = 0; OrdinalCount = minimumReuseRunLength; PhysicalOffset = 0L; PhysicalLength = int64 fileBytes.Length }

        let freshRange = { staleRange with ActiveManifestCount = 2 }

        let staleClaim =
            {
                StoragePoolId = sessionStoragePoolId
                ContentBlockAddress = block.Address
                OrdinalStart = staleRange.OrdinalStart
                OrdinalCount = staleRange.OrdinalCount
                PhysicalOffset = staleRange.PhysicalOffset
                PhysicalLength = staleRange.PhysicalLength
                MetadataVersion = 7L
                ClaimedAt = timestamp
            }

        let freshClaim = { staleClaim with MetadataVersion = 8L; ClaimedAt = timestamp.Plus(Duration.FromSeconds(1L)) }

        let session =
            { UploadSessionDto.Default with
                UploadSessionId = sessionId
                StoragePoolId = sessionStoragePoolId
                LifecycleState = UploadSessionLifecycleState.ClaimingRanges
                FileContentHash = manifest.FileContentHash
                ExpectedSize = manifest.Size
                ChunkingSuiteId = manifest.ChunkingSuiteId
                ClaimedReuseRanges = [| staleClaim; freshClaim |]
            }

        let freshMetadata = reuseMetadataFor block.Address 8L [| freshRange |]
        let finalizeCommand = finalizeWithClaimedMetadata "op-finalize" manifest [| payloadFor block |] [| freshMetadata |]
        let result = UploadSessionActor.decideCommand [] session finalizeCommand (metadata "corr-finalize-fresh-claim")

        match result with
        | Ok decision ->
            Assert.That(decision.Session.LifecycleState, Is.EqualTo(UploadSessionLifecycleState.RetentionPending))

            let commands =
                UploadSessionActor.createContentBlockMetadataMergeCommandsForFinalizedBlocks
                    sessionStoragePoolId
                    "op-finalize"
                    session
                    manifest
                    [| freshMetadata |]

            Assert.That(commands, Has.Length.EqualTo(1))

            match commands[0] with
            | ContentBlockMetadataCommand.MergePhysicalRanges merge ->
                Assert.That(merge.OperationId, Is.EqualTo($"op-finalize:upload-session:{sessionId:N}:content-block-metadata:{block.Address}"))
                Assert.That(merge.ContentBlockAddress, Is.EqualTo(block.Address))
                Assert.That(merge.BlockFormatVersion, Is.EqualTo(freshMetadata.BlockFormatVersion))
                Assert.That(merge.StoragePlacement, Is.EqualTo(freshMetadata.StoragePlacement))
                Assert.That(merge.Ranges, Has.Length.EqualTo(1))
                Assert.That(merge.Ranges[0].PhysicalLength, Is.EqualTo(int64 fileBytes.Length))
                Assert.That(merge.Ranges[0].ActiveManifestCount, Is.EqualTo(1))
            | _ -> Assert.Fail("Expected the fresh claimed range to provide the metadata merge command.")
        | Error error -> Assert.Fail($"Expected fresh duplicate claim to supersede stale claim, got {error.Error}.")

    [<Test>]
    member _.FinalizeManifestRejectsNewerPartialClaimWhenOnlyStaleFullClaimCoversBlock() =
        let fileBytes = Text.Encoding.UTF8.GetBytes("full claimed block coverage")
        let block = encodedBlock fileBytes
        let manifest = manifestFor fileBytes [| block |]

        let fullRange =
            { reusableMetadataRange with OrdinalStart = 0; OrdinalCount = minimumReuseRunLength; PhysicalOffset = 0L; PhysicalLength = int64 fileBytes.Length }

        let partialRange = { fullRange with ActiveManifestCount = 2; PhysicalLength = int64 fileBytes.Length - 1L }

        let staleFullClaim =
            {
                StoragePoolId = sessionStoragePoolId
                ContentBlockAddress = block.Address
                OrdinalStart = fullRange.OrdinalStart
                OrdinalCount = fullRange.OrdinalCount
                PhysicalOffset = fullRange.PhysicalOffset
                PhysicalLength = fullRange.PhysicalLength
                MetadataVersion = 7L
                ClaimedAt = timestamp
            }

        let newerPartialClaim =
            { staleFullClaim with PhysicalLength = partialRange.PhysicalLength; MetadataVersion = 8L; ClaimedAt = timestamp.Plus(Duration.FromSeconds(1L)) }

        let session =
            { UploadSessionDto.Default with
                UploadSessionId = sessionId
                StoragePoolId = sessionStoragePoolId
                LifecycleState = UploadSessionLifecycleState.ClaimingRanges
                FileContentHash = manifest.FileContentHash
                ExpectedSize = manifest.Size
                ChunkingSuiteId = manifest.ChunkingSuiteId
                ClaimedReuseRanges = [| staleFullClaim; newerPartialClaim |]
            }

        let partialMetadata = reuseMetadataFor block.Address 8L [| partialRange |]
        let finalizeCommand = finalizeWithClaimedMetadata "op-finalize" manifest [| payloadFor block |] [| partialMetadata |]
        let result = UploadSessionActor.decideCommand [] session finalizeCommand (metadata "corr-finalize-partial-claim")

        match result with
        | Ok _ -> Assert.Fail("Expected a newer partial claim not to satisfy a full manifest block.")
        | Error error -> Assert.That(error.Error, Does.Contain("claimed reuse range"))

        let commands =
            UploadSessionActor.createContentBlockMetadataMergeCommandsForFinalizedBlocks
                sessionStoragePoolId
                "op-finalize"
                session
                manifest
                [| partialMetadata |]

        Assert.That(commands, Is.Empty)

    [<Test>]
    member _.ClaimedReuseMetadataMergeOperationIdsAreScopedByUploadSession() =
        let fileBytes = Text.Encoding.UTF8.GetBytes("shared claimed block")
        let block = encodedBlock fileBytes
        let manifest = manifestFor fileBytes [| block |]

        let metadataRange =
            { reusableMetadataRange with
                OrdinalStart = 0
                OrdinalCount = minimumReuseRunLength
                PhysicalLength = int64 fileBytes.Length
                ActiveManifestCount = 0
            }

        let claimedRange =
            {
                StoragePoolId = sessionStoragePoolId
                ContentBlockAddress = block.Address
                OrdinalStart = metadataRange.OrdinalStart
                OrdinalCount = metadataRange.OrdinalCount
                PhysicalOffset = metadataRange.PhysicalOffset
                PhysicalLength = metadataRange.PhysicalLength
                MetadataVersion = 7L
                ClaimedAt = timestamp
            }

        let firstSession =
            { UploadSessionDto.Default with UploadSessionId = sessionId; StoragePoolId = sessionStoragePoolId; ClaimedReuseRanges = [| claimedRange |] }

        let secondSession = { firstSession with UploadSessionId = Guid.Parse("d13f445a-627c-428c-80a7-e0743ad8c5da") }

        let authoritativeMetadata = reuseMetadataFor block.Address 7L [| metadataRange |]

        let firstCommands =
            UploadSessionActor.createContentBlockMetadataMergeCommandsForFinalizedBlocks
                sessionStoragePoolId
                "op-finalize"
                firstSession
                manifest
                [| authoritativeMetadata |]

        let secondCommands =
            UploadSessionActor.createContentBlockMetadataMergeCommandsForFinalizedBlocks
                sessionStoragePoolId
                "op-finalize"
                secondSession
                manifest
                [| authoritativeMetadata |]

        Assert.That(firstCommands, Has.Length.EqualTo(1))
        Assert.That(secondCommands, Has.Length.EqualTo(1))

        let firstMergeOperationId =
            match firstCommands[0] with
            | ContentBlockMetadataCommand.MergePhysicalRanges merge -> merge.OperationId
            | _ ->
                Assert.Fail("Expected first claimed reuse command to merge ContentBlockMetadata.")
                String.Empty

        let secondMergeOperationId =
            match secondCommands[0] with
            | ContentBlockMetadataCommand.MergePhysicalRanges merge -> merge.OperationId
            | _ ->
                Assert.Fail("Expected second claimed reuse command to merge ContentBlockMetadata.")
                String.Empty

        Assert.That(firstMergeOperationId, Is.Not.EqualTo(secondMergeOperationId))
        Assert.That(firstMergeOperationId, Does.Contain(sessionId.ToString("N")))
        Assert.That(secondMergeOperationId, Does.Contain(secondSession.UploadSessionId.ToString("N")))

        let firstDecision =
            ContentBlockMetadataActor.decideCommand [] ContentBlockMetadataDto.Empty firstCommands[0] (metadata "corr-metadata-first")
            |> decisionOrFail "Expected first metadata merge to succeed"

        let currentMetadataDto =
            firstDecision.Events
            |> List.fold (fun current event -> ContentBlockMetadataDto.UpdateDto event current) ContentBlockMetadataDto.Empty

        let secondDecision =
            ContentBlockMetadataActor.decideCommand firstDecision.Events currentMetadataDto secondCommands[0] (metadata "corr-metadata-second")
            |> decisionOrFail "Expected second metadata merge to be a distinct contribution"

        Assert.That(secondDecision.WasIdempotentReplay, Is.False)
        Assert.That(secondDecision.Metadata.Ranges, Has.Length.EqualTo(1))

        Assert.That(
            secondDecision.Metadata.Ranges[0]
                .ActiveManifestCount,
            Is.EqualTo(2)
        )

    [<Test>]
    member _.FinalizedUploadRangesEmitSingleReferenceContribution() =
        let ranges =
            [|
                { OrdinalStart = 0; OrdinalCount = 1; ActiveManifestCount = 2; PhysicalOffset = 0L; PhysicalLength = 11L }
            |]

        let activeRanges = UploadSessionActor.activeRangesForFinalizedManifest ranges

        Assert.That(activeRanges[0].ActiveManifestCount, Is.EqualTo(1))
        Assert.That(ranges[0].ActiveManifestCount, Is.EqualTo(2))

    [<Test>]
    member _.FinalizeManifestFromClaimedReuseRangeValidatesReconstructionAndFinalizes() =
        let fileBytes = Text.Encoding.UTF8.GetBytes("reuse range bytes")
        let block = encodedBlock fileBytes
        let manifest = manifestFor fileBytes [| block |]

        let claimedRange =
            { StoragePoolId = storagePoolId; ContentBlockAddress = block.Address; OrdinalStart = 0; OrdinalCount = minimumReuseRunLength; MetadataVersion = 7L }

        let metadataRange = { reusableMetadataRange with OrdinalStart = 0; OrdinalCount = minimumReuseRunLength; PhysicalLength = int64 fileBytes.Length }

        let startDecision =
            UploadSessionActor.decideCommand
                []
                UploadSessionDto.Default
                (UploadSessionCommand.Start(startForManifest "op-start" fileBytes))
                (metadata "corr-start")

        let startedDto, startEvents =
            match startDecision with
            | Ok decision -> decision.Session, decision.Events
            | Error error ->
                Assert.Fail($"Expected start to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let issued = UploadSessionActor.decideCommand startEvents startedDto (discovery "op-discovery" [| claimedRange |]) (metadata "corr-discovery")

        let discoveredDto, discoveryEvents =
            match issued with
            | Ok decision -> decision.Session, startEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected discovery to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let claimCommand = claim "op-claim" claimedRange (reuseMetadataFor block.Address 7L [| metadataRange |])
        let claimed = UploadSessionActor.decideCommand discoveryEvents discoveredDto claimCommand (metadata "corr-claim")

        let claimedDto, claimedEvents =
            match claimed with
            | Ok decision -> decision.Session, discoveryEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected range claim to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let result =
            UploadSessionActor.decideCommand
                claimedEvents
                claimedDto
                (finalizeWithClaimedMetadata
                    "op-finalize"
                    manifest
                    [| payloadFor block |]
                    [|
                        reuseMetadataFor block.Address 7L [| metadataRange |]
                    |])
                (metadata "corr-finalize")

        match result with
        | Ok decision ->
            Assert.That(decision.Session.LifecycleState, Is.EqualTo(UploadSessionLifecycleState.RetentionPending))
            Assert.That(decision.Session.FinalizedManifestAddress, Is.EqualTo(Some manifest.ManifestAddress))
            Assert.That(decision.Session.CleanupReminderOperationId, Is.EqualTo(Some "op-finalize:cleanup"))
        | Error error -> Assert.Fail($"Expected finalize from claimed range to succeed, got {error.Error}.")

    [<Test>]
    member _.FinalizeManifestRejectsClaimedReuseMetadataFromWrongPool() =
        let fileBytes = Text.Encoding.UTF8.GetBytes("wrong pool claimed metadata")
        let block = encodedBlock fileBytes
        let manifest = manifestFor fileBytes [| block |]

        let metadataRange = { reusableMetadataRange with OrdinalStart = 0; OrdinalCount = minimumReuseRunLength; PhysicalLength = int64 fileBytes.Length }

        let claimedRange =
            {
                StoragePoolId = StoragePoolId "pool-other"
                ContentBlockAddress = block.Address
                OrdinalStart = metadataRange.OrdinalStart
                OrdinalCount = metadataRange.OrdinalCount
                PhysicalOffset = metadataRange.PhysicalOffset
                PhysicalLength = metadataRange.PhysicalLength
                MetadataVersion = 7L
                ClaimedAt = timestamp
            }

        let session =
            { UploadSessionDto.Default with
                StoragePoolId = sessionStoragePoolId
                ExpectedSize = manifest.Size
                FileContentHash = manifest.FileContentHash
                ChunkingSuiteId = manifest.ChunkingSuiteId
                LifecycleState = UploadSessionLifecycleState.ClaimingRanges
                ClaimedReuseRanges = [| claimedRange |]
            }

        let wrongPoolMetadata = { reuseMetadataFor block.Address 7L [| metadataRange |] with StoragePoolId = claimedRange.StoragePoolId }

        let result =
            UploadSessionActor.decideCommand
                []
                session
                (finalizeWithClaimedMetadata "op-finalize" manifest [| payloadFor block |] [| wrongPoolMetadata |])
                (metadata "corr-finalize-wrong-pool")

        match result with
        | Ok _ -> Assert.Fail("Expected wrong-pool claimed metadata to fail closed before finalization.")
        | Error error -> Assert.That(error.Error, Does.Contain("StoragePoolId must match the upload session StoragePoolId"))

    [<Test>]
    member _.FinalizeManifestRejectsMissingConfirmedOrClaimedBlockWithoutConsumingOperationId() =
        let fileBytes = Text.Encoding.UTF8.GetBytes("hello world")
        let block = encodedBlock fileBytes
        let manifest = manifestFor fileBytes [| block |]

        let startDecision =
            UploadSessionActor.decideCommand
                []
                UploadSessionDto.Default
                (UploadSessionCommand.Start(startForManifest "op-start" fileBytes))
                (metadata "corr-start")

        let startedDto, startEvents =
            match startDecision with
            | Ok decision -> decision.Session, decision.Events
            | Error error ->
                Assert.Fail($"Expected start to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let missing =
            UploadSessionActor.decideCommand startEvents startedDto (finalize "op-finalize" manifest [| payloadFor block |]) (metadata "corr-finalize-missing")

        match missing with
        | Ok _ -> Assert.Fail("Expected finalize to reject a manifest block that was not uploaded or claimed.")
        | Error error -> Assert.That(error.Error, Does.Contain("not uploaded or claimed"))

        let intentDecision =
            UploadSessionActor.decideCommand
                startEvents
                startedDto
                (UploadSessionCommand.RegisterBlockUploadIntent(intentForBlock "op-block-intent" block 0L))
                (metadata "corr-block-intent")

        let intentDto, intentEvents =
            match intentDecision with
            | Ok decision -> decision.Session, startEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected intent to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let confirmDecision =
            UploadSessionActor.decideCommand
                intentEvents
                intentDto
                (UploadSessionCommand.ConfirmBlockUploaded(confirm "op-block-confirm" block.Address block.Payload))
                (metadata "corr-block-confirm")

        let confirmedDto, confirmedEvents =
            match confirmDecision with
            | Ok decision -> decision.Session, intentEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected confirmation to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let retry =
            UploadSessionActor.decideCommand
                confirmedEvents
                confirmedDto
                (finalize "op-finalize" manifest [| payloadFor block |])
                (metadata "corr-finalize-retry")

        match retry with
        | Ok decision -> Assert.That(decision.Session.LifecycleState, Is.EqualTo(UploadSessionLifecycleState.RetentionPending))
        | Error error -> Assert.Fail($"Expected retry after failed finalize to succeed, got {error.Error}.")

    [<Test>]
    member _.FinalizeManifestRejectsFileHashMismatchWithoutConsumingOperationId() =
        let fileBytes = Text.Encoding.UTF8.GetBytes("hello world")
        let wrongBytes = Text.Encoding.UTF8.GetBytes("wrong bytes")
        let block = encodedBlock fileBytes
        let validManifest = manifestFor fileBytes [| block |]
        let wrongHashManifest = { validManifest with FileContentHash = FileContentHash(ContentAddress.computeBlake3Hex wrongBytes) }

        let startedDto, startEvents =
            match
                UploadSessionActor.decideCommand
                    []
                    UploadSessionDto.Default
                    (UploadSessionCommand.Start(startForManifest "op-start" fileBytes))
                    (metadata "corr-start")
                with
            | Ok decision -> decision.Session, decision.Events
            | Error error ->
                Assert.Fail($"Expected start to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let intentDto, intentEvents =
            match
                UploadSessionActor.decideCommand
                    startEvents
                    startedDto
                    (UploadSessionCommand.RegisterBlockUploadIntent(intentForBlock "op-block-intent" block 0L))
                    (metadata "corr-block-intent")
                with
            | Ok decision -> decision.Session, startEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected intent to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let confirmedDto, confirmedEvents =
            match
                UploadSessionActor.decideCommand
                    intentEvents
                    intentDto
                    (UploadSessionCommand.ConfirmBlockUploaded(confirm "op-block-confirm" block.Address block.Payload))
                    (metadata "corr-block-confirm")
                with
            | Ok decision -> decision.Session, intentEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected confirmation to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let mismatch =
            UploadSessionActor.decideCommand
                confirmedEvents
                confirmedDto
                (finalize "op-finalize" wrongHashManifest [| payloadFor block |])
                (metadata "corr-finalize-mismatch")

        match mismatch with
        | Ok _ -> Assert.Fail("Expected file content hash mismatch to be rejected.")
        | Error error -> Assert.That(error.Error, Does.Contain("FileContentHash"))

        let retry =
            UploadSessionActor.decideCommand
                confirmedEvents
                confirmedDto
                (finalize "op-finalize" validManifest [| payloadFor block |])
                (metadata "corr-finalize-retry")

        match retry with
        | Ok decision -> Assert.That(decision.Session.LifecycleState, Is.EqualTo(UploadSessionLifecycleState.RetentionPending))
        | Error error -> Assert.Fail($"Expected retry after failed hash validation to succeed, got {error.Error}.")

    [<Test>]
    member _.FinalizeManifestRejectsTotalSizeMismatch() =
        let fileBytes = Text.Encoding.UTF8.GetBytes("hello world")
        let block = encodedBlock fileBytes
        let manifest = { manifestFor fileBytes [| block |] with Size = int64 fileBytes.Length + 1L }
        let startedDto, existingEvents = startedSession ()

        let result =
            UploadSessionActor.decideCommand existingEvents startedDto (finalize "op-finalize" manifest [| payloadFor block |]) (metadata "corr-finalize-size")

        match result with
        | Ok _ -> Assert.Fail("Expected total size mismatch to be rejected.")
        | Error error -> Assert.That(error.Error, Does.Contain("ExpectedSize"))

    [<Test>]
    member _.FinalizeManifestWithSameOperationIdIsIdempotentReplay() =
        let fileBytes = Text.Encoding.UTF8.GetBytes("hello world")
        let block = encodedBlock fileBytes
        let manifest = manifestFor fileBytes [| block |]

        let startedDto, startEvents =
            match
                UploadSessionActor.decideCommand
                    []
                    UploadSessionDto.Default
                    (UploadSessionCommand.Start(startForManifest "op-start" fileBytes))
                    (metadata "corr-start")
                with
            | Ok decision -> decision.Session, decision.Events
            | Error error ->
                Assert.Fail($"Expected start to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let intentDto, intentEvents =
            match
                UploadSessionActor.decideCommand
                    startEvents
                    startedDto
                    (UploadSessionCommand.RegisterBlockUploadIntent(intentForBlock "op-block-intent" block 0L))
                    (metadata "corr-block-intent")
                with
            | Ok decision -> decision.Session, startEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected intent to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let confirmedDto, confirmedEvents =
            match
                UploadSessionActor.decideCommand
                    intentEvents
                    intentDto
                    (UploadSessionCommand.ConfirmBlockUploaded(confirm "op-block-confirm" block.Address block.Payload))
                    (metadata "corr-block-confirm")
                with
            | Ok decision -> decision.Session, intentEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected confirmation to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let first =
            UploadSessionActor.decideCommand confirmedEvents confirmedDto (finalize "op-finalize" manifest [| payloadFor block |]) (metadata "corr-finalize")

        match first with
        | Ok decision ->
            let replay =
                UploadSessionActor.decideCommand
                    (confirmedEvents @ decision.Events)
                    decision.Session
                    (finalize "op-finalize" manifest [| payloadFor block |])
                    (metadata "corr-finalize-replay")

            match replay with
            | Ok replayDecision ->
                Assert.That(replayDecision.WasIdempotentReplay, Is.True)
                Assert.That(replayDecision.Events, Is.Empty)
                Assert.That(replayDecision.Session.FinalizedManifestAddress, Is.EqualTo(Some manifest.ManifestAddress))
                Assert.That(replayDecision.Session.CleanupReminderOperationId, Is.EqualTo(Some "op-finalize:cleanup"))
            | Error error -> Assert.Fail($"Expected idempotent finalize replay, got {error.Error}.")
        | Error error -> Assert.Fail($"Expected first finalize to succeed, got {error.Error}.")

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
    member _.DeletePhysicalStateAfterFinalizePreservesManifestEvidenceAndClearsUploadCoordination() =
        let fileBytes = Text.Encoding.UTF8.GetBytes("hello world")
        let block = encodedBlock fileBytes
        let manifest = manifestFor fileBytes [| block |]

        let startDecision =
            UploadSessionActor.decideCommand
                []
                UploadSessionDto.Default
                (UploadSessionCommand.Start(startForManifest "op-start" fileBytes))
                (metadata "corr-start")
            |> decisionOrFail "Expected start to succeed"

        let intentDecision =
            UploadSessionActor.decideCommand
                startDecision.Events
                startDecision.Session
                (UploadSessionCommand.RegisterBlockUploadIntent(intentForBlock "op-block-intent" block 0L))
                (metadata "corr-block-intent")
            |> decisionOrFail "Expected block upload intent to succeed"

        let intentEvents = startDecision.Events @ intentDecision.Events

        let confirmedDecision =
            UploadSessionActor.decideCommand
                intentEvents
                intentDecision.Session
                (UploadSessionCommand.ConfirmBlockUploaded(confirm "op-block-confirm" block.Address block.Payload))
                (metadata "corr-block-confirm")
            |> decisionOrFail "Expected block upload confirmation to succeed"

        let confirmedEvents = intentEvents @ confirmedDecision.Events

        let finalizedDecision =
            UploadSessionActor.decideCommand
                confirmedEvents
                confirmedDecision.Session
                (finalize "op-finalize" manifest [| payloadFor block |])
                (metadata "corr-finalize")
            |> decisionOrFail "Expected finalize to succeed"

        Assert.That(finalizedDecision.Session.FinalizedManifestAddress, Is.EqualTo(Some manifest.ManifestAddress))
        Assert.That(finalizedDecision.Session.BlockUploadIntents, Is.Not.Empty)
        Assert.That(finalizedDecision.Session.ConfirmedBlockUploads, Is.Not.Empty)
        Assert.That(finalizedDecision.Session.CleanupReminderOperationId, Is.EqualTo(Some "op-finalize:cleanup"))

        let finalizedEvents = confirmedEvents @ finalizedDecision.Events

        let cleanupDecision =
            UploadSessionActor.decideCommand
                finalizedEvents
                finalizedDecision.Session
                (UploadSessionCommand.DeletePhysicalState "op-finalize:cleanup")
                (metadata "corr-cleanup")
            |> decisionOrFail "Expected cleanup to succeed"

        Assert.That(cleanupDecision.Session.LifecycleState, Is.EqualTo(UploadSessionLifecycleState.StateDeleted))
        Assert.That(cleanupDecision.Session.UploadSessionId, Is.EqualTo(sessionId))
        Assert.That(cleanupDecision.Session.RepositoryId, Is.EqualTo(repositoryId))
        Assert.That(cleanupDecision.Session.FinalizedManifestAddress, Is.EqualTo(Some manifest.ManifestAddress))
        Assert.That(cleanupDecision.Session.CompletedAt, Is.EqualTo(finalizedDecision.Session.CompletedAt))
        Assert.That(cleanupDecision.Session.BlockUploadIntents, Is.Empty)
        Assert.That(cleanupDecision.Session.ConfirmedBlockUploads, Is.Empty)
        Assert.That(cleanupDecision.Session.DedupeDiscovery, Is.EqualTo(None))
        Assert.That(cleanupDecision.Session.ClaimedReuseRanges, Is.Empty)
        Assert.That(cleanupDecision.Session.CleanupReminderScheduledAt, Is.EqualTo(None))
        Assert.That(cleanupDecision.Session.CleanupReminderOperationId, Is.EqualTo(None))

    [<Test>]
    member _.DeletePhysicalStateAfterAbandonReleasesTemporaryReuseClaims() =
        let startedDto, startEvents = startedSession ()

        let issuedDecision =
            UploadSessionActor.decideCommand startEvents startedDto (discovery "op-discovery" [| reuseHint |]) (metadata "corr-discovery")
            |> decisionOrFail "Expected discovery to succeed"

        let discoveryEvents = startEvents @ issuedDecision.Events

        let claimedDecision =
            UploadSessionActor.decideCommand
                discoveryEvents
                issuedDecision.Session
                (claim "op-claim" reuseHint (reuseMetadata 7L [| reusableMetadataRange |]))
                (metadata "corr-claim")
            |> decisionOrFail "Expected claim to succeed"

        let claimedEvents = discoveryEvents @ claimedDecision.Events

        let abandonedDecision =
            UploadSessionActor.decideCommand claimedEvents claimedDecision.Session (UploadSessionCommand.Abandon "op-abandon") (metadata "corr-abandon")
            |> decisionOrFail "Expected abandon to succeed"

        Assert.That(abandonedDecision.Session.LifecycleState, Is.EqualTo(UploadSessionLifecycleState.RetentionPending))
        Assert.That(abandonedDecision.Session.DedupeDiscovery.IsSome, Is.True)
        Assert.That(abandonedDecision.Session.ClaimedReuseRanges, Is.Not.Empty)
        Assert.That(abandonedDecision.Session.CleanupReminderOperationId, Is.EqualTo(Some "op-abandon:cleanup"))

        let abandonedEvents = claimedEvents @ abandonedDecision.Events

        let cleanupDecision =
            UploadSessionActor.decideCommand
                abandonedEvents
                abandonedDecision.Session
                (UploadSessionCommand.DeletePhysicalState "op-abandon:cleanup")
                (metadata "corr-cleanup")
            |> decisionOrFail "Expected cleanup to succeed"

        Assert.That(cleanupDecision.Session.LifecycleState, Is.EqualTo(UploadSessionLifecycleState.StateDeleted))
        Assert.That(cleanupDecision.Session.FinalizedManifestAddress, Is.EqualTo(None))
        Assert.That(cleanupDecision.Session.DedupeDiscovery, Is.EqualTo(None))
        Assert.That(cleanupDecision.Session.ClaimedReuseRanges, Is.Empty)
        Assert.That(cleanupDecision.Session.BlockUploadIntents, Is.Empty)
        Assert.That(cleanupDecision.Session.ConfirmedBlockUploads, Is.Empty)
        Assert.That(cleanupDecision.Session.CleanupReminderScheduledAt, Is.EqualTo(None))
        Assert.That(cleanupDecision.Session.CleanupReminderOperationId, Is.EqualTo(None))

    [<Test>]
    member _.PhysicalCleanupCompactsPersistedEventsToTombstoneAndDropsCoordinationPayloads() =
        let block = encodedBlock (Text.Encoding.UTF8.GetBytes("hello world"))
        let manifestAddress = ManifestAddress "manifest-blake3-final"
        let cleanupReminderTime = timestamp.Plus(Duration.FromMinutes(5L))

        let blockIntent =
            {
                ContentBlockAddress = block.Address
                LogicalOffset = 0L
                LogicalLength = 11L
                ExpectedPayloadLength = block.Payload.LongLength
                RegisteredAt = timestamp
            }

        let confirmedBlock =
            {
                ContentBlockAddress = block.Address
                PayloadLength = block.Payload.LongLength
                StoragePlacement = placementFor block.Address (Some "etag-confirmed")
                Ranges =
                    [|
                        { OrdinalStart = 0; OrdinalCount = 1; ActiveManifestCount = 0; PhysicalOffset = 0L; PhysicalLength = 11L }
                    |]
                ConfirmedAt = timestamp
            }

        let discoverySnapshot: DedupeDiscoverySnapshot =
            { OperationId = "op-discovery"; ExpiresAt = discoveryExpiresAt; MinimumReuseRunLength = minimumReuseRunLength; Hints = [| reuseHint |] }

        let claimedRange =
            {
                StoragePoolId = storagePoolId
                ContentBlockAddress = reuseBlockAddress
                OrdinalStart = 0
                OrdinalCount = 4
                PhysicalOffset = 0L
                PhysicalLength = 4096L
                MetadataVersion = 7L
                ClaimedAt = timestamp
            }

        let eventStream =
            [
                { Event = UploadSessionEventType.Started(start "op-start"); Metadata = metadata "corr-start" }
                { Event = UploadSessionEventType.BlockUploadIntentRegistered("op-intent", blockIntent); Metadata = metadata "corr-intent" }
                { Event = UploadSessionEventType.BlockUploadConfirmed("op-confirm", confirmedBlock); Metadata = metadata "corr-confirm" }
                { Event = UploadSessionEventType.DedupeDiscoveryIssued("op-discovery", discoverySnapshot); Metadata = metadata "corr-discovery" }
                { Event = UploadSessionEventType.ReuseRangesClaimed("op-claim", [| claimedRange |]); Metadata = metadata "corr-claim" }
                { Event = UploadSessionEventType.Finalized("op-finalize", manifestAddress); Metadata = metadata "corr-finalize" }
                { Event = UploadSessionEventType.CleanupReminderScheduled("op-finalize:cleanup", cleanupReminderTime); Metadata = metadata "corr-retention" }
                { Event = UploadSessionEventType.PhysicalStateDeleted "op-finalize:cleanup"; Metadata = metadata "corr-cleanup" }
            ]

        let compacted = UploadSessionActor.compactEventsForPhysicalStateCleanup eventStream

        Assert.That(compacted.Length, Is.EqualTo(4))

        Assert.That(
            compacted
            |> List.exists (fun uploadSessionEvent ->
                match uploadSessionEvent.Event with
                | UploadSessionEventType.BlockUploadIntentRegistered _
                | UploadSessionEventType.BlockUploadConfirmed _
                | UploadSessionEventType.DedupeDiscoveryIssued _
                | UploadSessionEventType.ReuseRangesClaimed _ -> true
                | _ -> false),
            Is.False
        )

        let rehydrated = applyAll compacted UploadSessionDto.Default

        Assert.That(rehydrated.LifecycleState, Is.EqualTo(UploadSessionLifecycleState.StateDeleted))
        Assert.That(rehydrated.FinalizedManifestAddress, Is.EqualTo(Some manifestAddress))
        Assert.That(rehydrated.BlockUploadIntents, Is.Empty)
        Assert.That(rehydrated.ConfirmedBlockUploads, Is.Empty)
        Assert.That(rehydrated.DedupeDiscovery, Is.EqualTo(None))
        Assert.That(rehydrated.ClaimedReuseRanges, Is.Empty)

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
