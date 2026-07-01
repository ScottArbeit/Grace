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
open System.IO

module UploadSessionActor = Grace.Actors.UploadSession

module ContentBlockMetadataActor = Grace.Actors.ContentBlockMetadata

/// Covers upload Session Actor behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type UploadSessionActorTests() =

    let timestamp = Instant.FromUtc(2026, 5, 24, 12, 0)

    /// Constructs metadata fixtures used by the server unit upload Session Actor assertions.
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

    /// Builds start test data for the server unit upload Session Actor scenarios in this file.
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

    /// Builds start For Manifest test data for the server unit upload Session Actor scenarios in this file.
    let startForManifest operationId (fileBytes: byte array) =
        { start operationId with FileContentHash = FileContentHash(ContentAddress.computeBlake3Hex fileBytes); ExpectedSize = int64 fileBytes.Length }

    /// Builds encoded Block test data for the server unit upload Session Actor scenarios in this file.
    let encodedBlock bytes =
        match ContentBlockFormat.encode [ { PhysicalOffset = 0L; Bytes = bytes } ] with
        | Ok block -> block
        | Error error ->
            Assert.Fail($"Expected test content block to encode, got {error}.")
            Unchecked.defaultof<ContentBlockFormat.EncodedContentBlock>

    /// Builds encoded Block From Chunks test data for the server unit upload Session Actor scenarios in this file.
    let encodedBlockFromChunks chunks =
        match ContentBlockFormat.encode chunks with
        | Ok block -> block
        | Error error ->
            Assert.Fail($"Expected test content block to encode, got {error}.")
            Unchecked.defaultof<ContentBlockFormat.EncodedContentBlock>

    /// Builds intent At With Length test data for the server unit upload Session Actor scenarios in this file.
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

    /// Builds intent For Block test data for the server unit upload Session Actor scenarios in this file.
    let intentForBlock operationId (block: ContentBlockFormat.EncodedContentBlock) logicalOffset =
        let logicalLength =
            block.Chunks
            |> Array.sumBy (fun chunk -> int64 chunk.Length)

        intentAtWithLength operationId block.Address block.Payload.LongLength logicalOffset logicalLength

    /// Builds placement For test data for the server unit upload Session Actor scenarios in this file.
    let placementFor blockAddress eTag =
        {
            StorageAccountName = "cas-account"
            StorageContainerName = StorageContainerName "cas-container"
            ObjectKey = StorageKeys.contentBlockObjectKey blockAddress
            ETag = eTag
        }

    /// Builds confirm test data for the server unit upload Session Actor scenarios in this file.
    let confirm operationId blockAddress payload : ConfirmBlockUploaded =
        {
            OperationId = operationId
            ContentBlockAddress = blockAddress
            Payload = payload
            StoragePlacement = placementFor blockAddress (Some "etag-confirmed")
        }

    /// Builds confirm With Placement test data for the server unit upload Session Actor scenarios in this file.
    let confirmWithPlacement operationId blockAddress payload placement : ConfirmBlockUploaded =
        { OperationId = operationId; ContentBlockAddress = blockAddress; Payload = payload; StoragePlacement = placement }

    /// Builds manifest For test data for the server unit upload Session Actor scenarios in this file.
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
            FileManifest.Create(
                ManifestAddress String.Empty,
                RabinChunking.SuiteName,
                fileContentHash,
                int64 fileBytes.Length,
                sessionStoragePoolId,
                List.ofSeq contentBlocks
            )

        { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }

    let payloadFor (block: ContentBlockFormat.EncodedContentBlock) : FinalizeManifestBlockPayload = { Address = block.Address; Payload = block.Payload }

    /// Builds finalize test data for the server unit upload Session Actor scenarios in this file.
    let finalize operationId manifest payloads =
        UploadSessionCommand.FinalizeManifest { OperationId = operationId; Manifest = manifest; BlockPayloads = payloads; ClaimedMetadata = Array.empty }

    /// Builds finalize With Claimed Metadata test data for the server unit upload Session Actor scenarios in this file.
    let finalizeWithClaimedMetadata operationId manifest payloads claimedMetadata =
        UploadSessionCommand.FinalizeManifest { OperationId = operationId; Manifest = manifest; BlockPayloads = payloads; ClaimedMetadata = claimedMetadata }

    let storagePoolId = sessionStoragePoolId
    let reuseBlockAddress = ContentBlockAddress "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
    let discoveryExpiresAt = timestamp.Plus(Duration.FromMinutes(10L))
    let minimumReuseRunLength = 4

    let reusableMetadataRange = { OrdinalStart = 0; OrdinalCount = 4; ActiveManifestCount = 0; PhysicalOffset = 0L; PhysicalLength = 4096L }

    /// Builds reuse Metadata test data for the server unit upload Session Actor scenarios in this file.
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

    /// Builds reuse Metadata For test data for the server unit upload Session Actor scenarios in this file.
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

    /// Builds discovery test data for the server unit upload Session Actor scenarios in this file.
    let discovery operationId hints =
        UploadSessionCommand.IssueDedupeDiscovery
            { OperationId = operationId; ExpiresAt = discoveryExpiresAt; MinimumReuseRunLength = minimumReuseRunLength; Hints = hints }

    /// Builds claim test data for the server unit upload Session Actor scenarios in this file.
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

    /// Applies all inputs to drive the server unit upload Session Actor state transition under test.
    let applyAll events dto =
        events
        |> List.fold (fun current event -> apply event current) dto

    /// Builds started Session test data for the server unit upload Session Actor scenarios in this file.
    let startedSession () =
        let startDecision = UploadSessionActor.decideCommand [] UploadSessionDto.Default (UploadSessionCommand.Start(start "op-start")) (metadata "corr-start")

        match startDecision with
        | Ok decision -> applyAll decision.Events UploadSessionDto.Default, decision.Events
        | Error error ->
            Assert.Fail($"Expected start to succeed, got {error.Error}.")
            UploadSessionDto.Default, []

    /// Builds decision Or Fail test data for the server unit upload Session Actor scenarios in this file.
    let decisionOrFail message result =
        match result with
        | Ok decision -> decision
        | Error error ->
            Assert.Fail($"{message}, got {error.Error}.")
            Unchecked.defaultof<_>

    /// Verifies that finalize Prevalidates All Metadata Merge Plans Before Side Effecting Merge Calls.
    [<Test>]
    member _.FinalizePrevalidatesAllMetadataMergePlansBeforeSideEffectingMergeCalls() =
        let actorPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Actors", "UploadSession.Actor.fs"))
        let actorSource = File.ReadAllText(actorPath)
        let prevalidateStart = actorSource.IndexOf("member private this.PrevalidateFinalizedContentBlockMetadata", StringComparison.Ordinal)
        let mergeStart = actorSource.IndexOf("member private this.MergePrevalidatedContentBlockMetadata", StringComparison.Ordinal)

        Assert.That(prevalidateStart, Is.GreaterThanOrEqualTo(0))
        Assert.That(mergeStart, Is.GreaterThan(prevalidateStart))

        let prevalidateSource = actorSource.Substring(prevalidateStart, mergeStart - prevalidateStart)
        let mergeSource = actorSource.Substring(mergeStart)

        Assert.That(
            prevalidateSource,
            Does
                .Contain("let! currentMetadata = metadataActor.Get metadata.CorrelationId")
                .And.Contain("ContentBlockMetadata.createMergedMetadata metadata.CorrelationId currentMetadata merge metadata.Timestamp"),
            "Finalize must validate uploaded metadata merges against current authoritative metadata before side-effecting merge calls."
        )

        Assert.That(
            prevalidateSource,
            Does
                .Contain("tryCreateContentBlockMetadataMergeCommandsForFinalizedBlocks")
                .And.Contain("rebaseUploadedMergeOnCurrentMetadata currentMetadata")
                .And.Contain("withFinalizeMergePrecondition")
                .And.Not.Contain("ExpectedMetadataVersion = Some authoritativeMetadata.MetadataVersion")
                .And.Not.Contain("RequireMissingMetadata = true"),
            "Uploaded finalize contributions must merge against compatible current metadata instead of freezing a stale prevalidation snapshot."
        )

        Assert.That(
            mergeSource,
            Does.Contain("metadataActor.MergePhysicalRanges merge metadata"),
            "The side-effecting metadata merge must remain isolated behind prevalidation."
        )

    /// Verifies that start With Same Operation Id Is Idempotent Replay.
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

    /// Verifies that abandon Moves Started Session To Retention Pending And Schedules Cleanup.
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

    /// Verifies that abandon With Same Operation Id Is Idempotent Replay Without Cleanup Event.
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

    /// Verifies that finalize Replay Reschedules Cleanup When Only Cleanup Event Was Persisted.
    [<Test>]
    member _.FinalizeReplayReschedulesCleanupWhenOnlyCleanupEventWasPersisted() =
        let finalize = { OperationId = "op-finalize"; Manifest = FileManifest.Default; BlockPayloads = Array.empty; ClaimedMetadata = Array.empty }

        let eventOnlySession =
            { UploadSessionDto.Default with
                LifecycleState = UploadSessionLifecycleState.RetentionPending
                CleanupReminderOperationId = Some "op-finalize:cleanup"
            }

        Assert.That(
            UploadSessionActor.shouldScheduleFinalizeCleanupReminder eventOnlySession finalize,
            Is.True,
            "Replay must reschedule when durable state has the cleanup event but the reminder creation may have crashed."
        )

        let stateDeletedSession = { eventOnlySession with LifecycleState = UploadSessionLifecycleState.StateDeleted }

        Assert.That(UploadSessionActor.shouldScheduleFinalizeCleanupReminder stateDeletedSession finalize, Is.False)

        let wrongCleanupOperation = { eventOnlySession with CleanupReminderOperationId = Some "op-other:cleanup" }

        Assert.That(UploadSessionActor.shouldScheduleFinalizeCleanupReminder wrongCleanupOperation finalize, Is.False)

    /// Verifies that expire Moves Started Session To Retention Pending And Schedules Cleanup.
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

    /// Verifies that finalized Session Rejects Lifecycle Mutation.
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

    /// Verifies that finalized Session Retains Live Manifest For Gc Safety.
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

    /// Verifies that block Upload Intent Moves Started Session To Uploading Blocks.
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

    /// Verifies that block Upload Intent Preserves Repeated Block Address At Different Logical Offsets.
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

    /// Verifies that confirm Block Uploaded Validates Payload And Records Physical Ranges.
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

    /// Verifies that confirm Block Uploaded Rejects Null Storage Placement As Grace Error.
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

    /// Verifies that confirm Block Uploaded Matches Any Compatible Duplicate Intent.
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

    /// Verifies that confirm Block Uploaded Rejects Intent Logical Length Mismatch.
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

    /// Verifies that confirm Block Uploaded Rejects Corrupt Payload Without Consuming Operation Id.
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

    /// Verifies that confirm Block Uploaded With Same Operation Id Is Idempotent Replay.
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

    /// Verifies that confirm Block Uploaded Rejects Operation Id Reused From Different Upload Session Command.
    [<Test>]
    member _.ConfirmBlockUploadedRejectsOperationIdReusedFromDifferentUploadSessionCommand() =
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

        let replayCollision =
            UploadSessionActor.decideCommand
                intentEvents
                intentDto
                (UploadSessionCommand.ConfirmBlockUploaded(confirm "op-block-intent" block.Address block.Payload))
                (metadata "corr-confirm-reused-intent-id")

        match replayCollision with
        | Ok _ -> Assert.Fail("Expected confirm to reject an OperationId already used by a non-confirm event.")
        | Error error ->
            Assert.That(error.Error, Does.Contain("already applied to a non-confirm event"))
            Assert.That(intentDto.ConfirmedBlockUploads, Is.Empty)

    /// Verifies that finalize Manifest From Uploaded Block Validates Reconstruction And Finalizes.
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

    /// Verifies that finalize Manifest Creates Repository Pool Metadata For Uploaded Blocks.
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

        /// Asserts the merge condition so failures identify the violated server unit upload Session Actor invariant.
        let assertMerge command (expectedAddress: ContentBlockAddress) (expectedObjectKey: string) =
            match command with
            | ContentBlockMetadataCommand.MergePhysicalRanges merge ->
                Assert.That(
                    merge.OperationId,
                    Is.EqualTo($"op-finalize:repository:{session.RepositoryId:N}:upload-session:{sessionId:N}:content-block-metadata:{expectedAddress}")
                )

                Assert.That(merge.StoragePoolId, Is.EqualTo(expectedStoragePoolId))
                Assert.That(merge.ContentBlockAddress, Is.EqualTo(expectedAddress))
                Assert.That(merge.StoragePlacement.ObjectKey, Is.EqualTo(expectedObjectKey))
                Assert.That(merge.Ranges, Has.Length.EqualTo(1))
                Assert.That(merge.Ranges[0].OrdinalStart, Is.EqualTo(0))
                Assert.That(merge.Ranges[0].OrdinalCount, Is.EqualTo(1))
                Assert.That(merge.Ranges[0].ActiveManifestCount, Is.EqualTo(1))
                Assert.That(merge.ExpectedMetadataVersion, Is.EqualTo(None))
                Assert.That(merge.RequireMissingMetadata, Is.False)
                Assert.That(merge.ExpectedRanges, Is.Empty)
                Assert.That(merge.IsFinalizeContribution, Is.True)
            | _ -> Assert.Fail("Expected uploaded block finalization to create ContentBlockMetadata MergePhysicalRanges commands.")

        assertMerge commands[0] firstBlock.Address (StorageKeys.contentBlockObjectKey firstBlock.Address)
        assertMerge commands[1] secondBlock.Address (StorageKeys.contentBlockObjectKey secondBlock.Address)

    /// Verifies that finalize Manifest Creates Session Pool Metadata For Claimed Reuse Ranges Without Confirmed Upload.
    [<Test>]
    member _.FinalizeManifestCreatesSessionPoolMetadataForClaimedReuseRangesWithoutConfirmedUpload() =
        let fileBytes = Text.Encoding.UTF8.GetBytes("claimed authoritative metadata")
        let block = encodedBlockFromChunks [ ContentBlockFormat.createChunk 4096L fileBytes ]
        let manifest = manifestFor fileBytes [| block |]

        let metadataRange =
            { reusableMetadataRange with
                OrdinalStart = 0
                OrdinalCount = minimumReuseRunLength
                PhysicalOffset = 4096L
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
            Assert.That(
                merge.OperationId,
                Is.EqualTo($"op-finalize:repository:{session.RepositoryId:N}:upload-session:{sessionId:N}:content-block-metadata:{block.Address}")
            )

            Assert.That(merge.StoragePoolId, Is.EqualTo(sessionStoragePoolId))
            Assert.That(merge.ContentBlockAddress, Is.EqualTo(block.Address))
            Assert.That(merge.StoragePlacement, Is.EqualTo(authoritativeMetadata.StoragePlacement))
            Assert.That(merge.Ranges, Has.Length.EqualTo(1))
            Assert.That(merge.Ranges[0].OrdinalStart, Is.EqualTo(metadataRange.OrdinalStart))
            Assert.That(merge.Ranges[0].OrdinalCount, Is.EqualTo(metadataRange.OrdinalCount))
            Assert.That(merge.Ranges[0].PhysicalOffset, Is.EqualTo(metadataRange.PhysicalOffset))
            Assert.That(merge.Ranges[0].PhysicalLength, Is.EqualTo(metadataRange.PhysicalLength))
            Assert.That(merge.Ranges[0].ActiveManifestCount, Is.EqualTo(1))
            Assert.That(merge.ExpectedMetadataVersion, Is.EqualTo(None))
            Assert.That(merge.RequireMissingMetadata, Is.False)
            Assert.That(merge.ExpectedRanges, Is.EquivalentTo([| metadataRange |]))
            Assert.That(merge.IsFinalizeContribution, Is.True)

            let mergeDecision =
                ContentBlockMetadataActor.decideCommand
                    []
                    { ContentBlockMetadataDto.Empty with Metadata = Some authoritativeMetadata }
                    (ContentBlockMetadataCommand.MergePhysicalRanges merge)
                    (metadata "corr-claimed-reuse-merge")
                |> decisionOrFail "Expected claimed reuse finalize contribution to merge"

            Assert.That(mergeDecision.Metadata.Ranges, Has.Length.EqualTo(1))

            Assert.That(
                mergeDecision.Metadata.Ranges[0]
                    .ActiveManifestCount,
                Is.EqualTo(3)
            )
        | _ -> Assert.Fail("Expected claimed reuse finalization to create a ContentBlockMetadata MergePhysicalRanges command.")

    /// Verifies that finalize Manifest Accepts Covering Evidence For Claimed Reuse Subwindow.
    [<Test>]
    member _.FinalizeManifestAcceptsCoveringEvidenceForClaimedReuseSubwindow() =
        let fileBytes = Array.init 512 (fun index -> byte (index % 251))
        let block = encodedBlockFromChunks [ ContentBlockFormat.createChunk 256L fileBytes ]
        let manifest = manifestFor fileBytes [| block |]

        let coveringRange =
            { reusableMetadataRange with OrdinalStart = 0; OrdinalCount = 512; ActiveManifestCount = 2; PhysicalOffset = 0L; PhysicalLength = 512L }

        let firstHalfClaim =
            {
                StoragePoolId = sessionStoragePoolId
                ContentBlockAddress = block.Address
                OrdinalStart = 0
                OrdinalCount = 256
                PhysicalOffset = 0L
                PhysicalLength = 256L
                MetadataVersion = 7L
                ClaimedAt = timestamp
            }

        let subwindowClaim =
            {
                StoragePoolId = sessionStoragePoolId
                ContentBlockAddress = block.Address
                OrdinalStart = 256
                OrdinalCount = 256
                PhysicalOffset = 256L
                PhysicalLength = 256L
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
                ClaimedReuseRanges = [| firstHalfClaim; subwindowClaim |]
            }

        let authoritativeMetadata = { reuseMetadataFor block.Address 7L [| coveringRange |] with TotalPhysicalBytes = 512L; ActivePhysicalBytes = 512L }

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
            Assert.That(merge.ContentBlockAddress, Is.EqualTo(block.Address))
            Assert.That(merge.Ranges, Has.Length.EqualTo(2))

            let subwindowContribution =
                merge.Ranges
                |> Array.find (fun range ->
                    range.OrdinalStart = subwindowClaim.OrdinalStart
                    && range.OrdinalCount = subwindowClaim.OrdinalCount)

            Assert.That(subwindowContribution.PhysicalOffset, Is.EqualTo(subwindowClaim.PhysicalOffset))
            Assert.That(subwindowContribution.PhysicalLength, Is.EqualTo(subwindowClaim.PhysicalLength))
            Assert.That(subwindowContribution.ActiveManifestCount, Is.EqualTo(1))
            Assert.That(merge.ExpectedRanges, Is.EquivalentTo([| coveringRange; coveringRange |]))

            let mergeDecision =
                ContentBlockMetadataActor.decideCommand
                    []
                    { ContentBlockMetadataDto.Empty with Metadata = Some authoritativeMetadata }
                    (ContentBlockMetadataCommand.MergePhysicalRanges merge)
                    (metadata "corr-claimed-subwindow-merge")
                |> decisionOrFail "Expected claimed reuse subwindow finalize contribution to merge with covering evidence"

            let subwindowRange =
                mergeDecision.Metadata.Ranges
                |> Array.find (fun range ->
                    range.OrdinalStart = subwindowClaim.OrdinalStart
                    && range.OrdinalCount = subwindowClaim.OrdinalCount)

            Assert.That(subwindowRange.PhysicalOffset, Is.EqualTo(subwindowClaim.PhysicalOffset))
            Assert.That(subwindowRange.PhysicalLength, Is.EqualTo(subwindowClaim.PhysicalLength))
            Assert.That(subwindowRange.ActiveManifestCount, Is.EqualTo(coveringRange.ActiveManifestCount + 1))
        | _ -> Assert.Fail("Expected claimed reuse subwindow finalization to create a ContentBlockMetadata MergePhysicalRanges command.")

    /// Verifies that finalize Manifest Accepts Claimed Reuse Range Cover For Multi Chunk Block.
    [<Test>]
    member _.FinalizeManifestAcceptsClaimedReuseRangeCoverForMultiChunkBlock() =
        let firstBytes = Text.Encoding.UTF8.GetBytes("first claimed chunk")
        let secondBytes = Text.Encoding.UTF8.GetBytes("second claimed chunk")
        let fileBytes = Array.concat [ firstBytes; secondBytes ]

        let block =
            encodedBlockFromChunks [ ContentBlockFormat.createChunk 0L firstBytes
                                     ContentBlockFormat.createChunk (int64 firstBytes.Length) secondBytes ]

        let manifest = manifestFor fileBytes [| block |]

        let firstRange = { OrdinalStart = 0; OrdinalCount = 1; ActiveManifestCount = 2; PhysicalOffset = 0L; PhysicalLength = int64 firstBytes.Length }

        let secondRange =
            { OrdinalStart = 1; OrdinalCount = 1; ActiveManifestCount = 2; PhysicalOffset = int64 firstBytes.Length; PhysicalLength = int64 secondBytes.Length }

        /// Builds claimed Range test data for the server unit upload Session Actor scenarios in this file.
        let claimedRange (range: ContentBlockMetadataRange) : ClaimedReuseRange =
            {
                StoragePoolId = sessionStoragePoolId
                ContentBlockAddress = block.Address
                OrdinalStart = range.OrdinalStart
                OrdinalCount = range.OrdinalCount
                PhysicalOffset = range.PhysicalOffset
                PhysicalLength = range.PhysicalLength
                MetadataVersion = 7L
                ClaimedAt = timestamp.Plus(Duration.FromSeconds(int64 range.OrdinalStart))
            }

        let claimedRanges =
            [|
                claimedRange secondRange
                claimedRange firstRange
            |]

        let session =
            { UploadSessionDto.Default with
                UploadSessionId = sessionId
                StoragePoolId = sessionStoragePoolId
                LifecycleState = UploadSessionLifecycleState.ClaimingRanges
                FileContentHash = manifest.FileContentHash
                ExpectedSize = manifest.Size
                ChunkingSuiteId = manifest.ChunkingSuiteId
                ClaimedReuseRanges = claimedRanges
            }

        let authoritativeMetadata = reuseMetadataFor block.Address 7L [| firstRange; secondRange |]

        let decision =
            UploadSessionActor.decideCommand
                []
                session
                (finalizeWithClaimedMetadata "op-finalize" manifest [| payloadFor block |] [| authoritativeMetadata |])
                (metadata "corr-finalize-cover")

        match decision with
        | Ok decision ->
            Assert.That(decision.Session.LifecycleState, Is.EqualTo(UploadSessionLifecycleState.RetentionPending))
            Assert.That(decision.Session.FinalizedManifestAddress, Is.EqualTo(Some manifest.ManifestAddress))
        | Error error -> Assert.Fail($"Expected multi-range claimed reuse cover to finalize, got {error.Error}.")

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
            Assert.That(merge.ContentBlockAddress, Is.EqualTo(block.Address))
            Assert.That(merge.Ranges, Has.Length.EqualTo(2))

            Assert.That(
                merge.Ranges
                |> Array.map (fun range -> range.OrdinalStart),
                Is.EquivalentTo([| 0; 1 |])
            )

            Assert.That(
                merge.Ranges
                |> Array.map (fun range -> range.ActiveManifestCount),
                Is.All.EqualTo(1)
            )

            Assert.That(merge.ExpectedRanges, Is.EquivalentTo([| firstRange; secondRange |]))
        | _ -> Assert.Fail("Expected multi-range claimed reuse finalization to create one MergePhysicalRanges command.")

    /// Verifies that finalize Manifest Rejects Mixed Version Claimed Reuse Range Cover.
    [<Test>]
    member _.FinalizeManifestRejectsMixedVersionClaimedReuseRangeCover() =
        let firstBytes = Text.Encoding.UTF8.GetBytes("first mixed claimed chunk")
        let secondBytes = Text.Encoding.UTF8.GetBytes("second mixed claimed chunk")
        let fileBytes = Array.concat [ firstBytes; secondBytes ]

        let block =
            encodedBlockFromChunks [ ContentBlockFormat.createChunk 4096L firstBytes
                                     ContentBlockFormat.createChunk 16384L secondBytes ]

        let manifest = manifestFor fileBytes [| block |]

        let firstRange = { OrdinalStart = 0; OrdinalCount = 1; ActiveManifestCount = 2; PhysicalOffset = 4096L; PhysicalLength = int64 firstBytes.Length }

        let secondRange = { OrdinalStart = 1; OrdinalCount = 1; ActiveManifestCount = 2; PhysicalOffset = 16384L; PhysicalLength = int64 secondBytes.Length }

        /// Builds claimed Range test data for the server unit upload Session Actor scenarios in this file.
        let claimedRange metadataVersion (range: ContentBlockMetadataRange) : ClaimedReuseRange =
            {
                StoragePoolId = sessionStoragePoolId
                ContentBlockAddress = block.Address
                OrdinalStart = range.OrdinalStart
                OrdinalCount = range.OrdinalCount
                PhysicalOffset = range.PhysicalOffset
                PhysicalLength = range.PhysicalLength
                MetadataVersion = metadataVersion
                ClaimedAt = timestamp.Plus(Duration.FromSeconds(int64 range.OrdinalStart))
            }

        let session =
            { UploadSessionDto.Default with
                UploadSessionId = sessionId
                StoragePoolId = sessionStoragePoolId
                LifecycleState = UploadSessionLifecycleState.ClaimingRanges
                FileContentHash = manifest.FileContentHash
                ExpectedSize = manifest.Size
                ChunkingSuiteId = manifest.ChunkingSuiteId
                ClaimedReuseRanges =
                    [|
                        claimedRange 7L firstRange
                        claimedRange 8L secondRange
                    |]
            }

        let currentMetadata = reuseMetadataFor block.Address 8L [| firstRange; secondRange |]
        let finalizeCommand = finalizeWithClaimedMetadata "op-finalize" manifest [| payloadFor block |] [| currentMetadata |]
        let result = UploadSessionActor.decideCommand [] session finalizeCommand (metadata "corr-finalize-mixed-cover")

        match result with
        | Ok _ -> Assert.Fail("Expected mixed-version claimed reuse cover to be rejected.")
        | Error error -> Assert.That(error.Error, Does.Contain("must come from one authoritative metadata version"))

        let commands =
            UploadSessionActor.tryCreateContentBlockMetadataMergeCommandsForFinalizedBlocks
                "corr-finalize-mixed-cover"
                sessionStoragePoolId
                "op-finalize"
                session
                manifest
                [| currentMetadata |]

        match commands with
        | Ok _ -> Assert.Fail("Expected mixed-version claimed reuse cover command creation to be rejected.")
        | Error error -> Assert.That(error.Error, Does.Contain("must come from one authoritative metadata version"))

    /// Verifies that finalize Manifest Rejects Split Claimed Metadata Evidence Instead Of Dropping Merge.
    [<Test>]
    member _.FinalizeManifestRejectsSplitClaimedMetadataEvidenceInsteadOfDroppingMerge() =
        let firstBytes = Text.Encoding.UTF8.GetBytes("first split claimed chunk")
        let secondBytes = Text.Encoding.UTF8.GetBytes("second split claimed chunk")
        let fileBytes = Array.concat [ firstBytes; secondBytes ]

        let block =
            encodedBlockFromChunks [ ContentBlockFormat.createChunk 4096L firstBytes
                                     ContentBlockFormat.createChunk 16384L secondBytes ]

        let manifest = manifestFor fileBytes [| block |]

        let firstRange = { OrdinalStart = 0; OrdinalCount = 1; ActiveManifestCount = 2; PhysicalOffset = 4096L; PhysicalLength = int64 firstBytes.Length }

        let secondRange = { OrdinalStart = 1; OrdinalCount = 1; ActiveManifestCount = 2; PhysicalOffset = 16384L; PhysicalLength = int64 secondBytes.Length }

        /// Builds claimed Range test data for the server unit upload Session Actor scenarios in this file.
        let claimedRange (range: ContentBlockMetadataRange) : ClaimedReuseRange =
            {
                StoragePoolId = sessionStoragePoolId
                ContentBlockAddress = block.Address
                OrdinalStart = range.OrdinalStart
                OrdinalCount = range.OrdinalCount
                PhysicalOffset = range.PhysicalOffset
                PhysicalLength = range.PhysicalLength
                MetadataVersion = 7L
                ClaimedAt = timestamp.Plus(Duration.FromSeconds(int64 range.OrdinalStart))
            }

        let session =
            { UploadSessionDto.Default with
                UploadSessionId = sessionId
                StoragePoolId = sessionStoragePoolId
                LifecycleState = UploadSessionLifecycleState.ClaimingRanges
                FileContentHash = manifest.FileContentHash
                ExpectedSize = manifest.Size
                ChunkingSuiteId = manifest.ChunkingSuiteId
                ClaimedReuseRanges =
                    [|
                        claimedRange firstRange
                        claimedRange secondRange
                    |]
            }

        let firstEvidence = reuseMetadataFor block.Address 7L [| firstRange |]
        let secondEvidence = reuseMetadataFor block.Address 7L [| secondRange |]

        let result =
            UploadSessionActor.tryCreateContentBlockMetadataMergeCommandsForFinalizedBlocks
                "corr-finalize-split-cover"
                sessionStoragePoolId
                "op-finalize"
                session
                manifest
                [| firstEvidence; secondEvidence |]

        match result with
        | Ok _ -> Assert.Fail("Expected split claimed metadata evidence to be rejected.")
        | Error error -> Assert.That(error.Error, Does.Contain("range cover is split or changed"))

    /// Verifies that finalize Manifest Rejects Gapped Claimed Reuse Range Cover For Multi Chunk Block.
    [<Test>]
    member _.FinalizeManifestRejectsGappedClaimedReuseRangeCoverForMultiChunkBlock() =
        let firstBytes = Text.Encoding.UTF8.GetBytes("first claimed chunk")
        let secondBytes = Text.Encoding.UTF8.GetBytes("second claimed chunk")
        let fileBytes = Array.concat [ firstBytes; secondBytes ]

        let block =
            encodedBlockFromChunks [ ContentBlockFormat.createChunk 0L firstBytes
                                     ContentBlockFormat.createChunk (int64 firstBytes.Length) secondBytes ]

        let manifest = manifestFor fileBytes [| block |]

        let firstRange = { OrdinalStart = 0; OrdinalCount = 1; ActiveManifestCount = 2; PhysicalOffset = 0L; PhysicalLength = int64 firstBytes.Length }

        let gappedSecondRange =
            { OrdinalStart = 2; OrdinalCount = 1; ActiveManifestCount = 2; PhysicalOffset = int64 firstBytes.Length; PhysicalLength = int64 secondBytes.Length }

        /// Builds claimed Range test data for the server unit upload Session Actor scenarios in this file.
        let claimedRange (range: ContentBlockMetadataRange) : ClaimedReuseRange =
            {
                StoragePoolId = sessionStoragePoolId
                ContentBlockAddress = block.Address
                OrdinalStart = range.OrdinalStart
                OrdinalCount = range.OrdinalCount
                PhysicalOffset = range.PhysicalOffset
                PhysicalLength = range.PhysicalLength
                MetadataVersion = 7L
                ClaimedAt = timestamp.Plus(Duration.FromSeconds(int64 range.OrdinalStart))
            }

        let session =
            { UploadSessionDto.Default with
                UploadSessionId = sessionId
                StoragePoolId = sessionStoragePoolId
                LifecycleState = UploadSessionLifecycleState.ClaimingRanges
                FileContentHash = manifest.FileContentHash
                ExpectedSize = manifest.Size
                ChunkingSuiteId = manifest.ChunkingSuiteId
                ClaimedReuseRanges =
                    [|
                        claimedRange firstRange
                        claimedRange gappedSecondRange
                    |]
            }

        let authoritativeMetadata = reuseMetadataFor block.Address 7L [| firstRange; gappedSecondRange |]

        let decision =
            UploadSessionActor.decideCommand
                []
                session
                (finalizeWithClaimedMetadata "op-finalize" manifest [| payloadFor block |] [| authoritativeMetadata |])
                (metadata "corr-finalize-gap")

        match decision with
        | Ok _ -> Assert.Fail("Expected gapped claimed reuse cover to be rejected.")
        | Error error -> Assert.That(error.Error, Does.Contain("was not uploaded or claimed by this UploadSession"))

        let commands =
            UploadSessionActor.createContentBlockMetadataMergeCommandsForFinalizedBlocks
                sessionStoragePoolId
                "op-finalize"
                session
                manifest
                [| authoritativeMetadata |]

        Assert.That(commands, Is.Empty)

    /// Verifies that finalize Manifest Skips Stale Claimed Metadata When Confirmed Upload Satisfies Block.
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
                Assert.That(merge.ExpectedMetadataVersion, Is.EqualTo(None))
                Assert.That(merge.RequireMissingMetadata, Is.False)
                Assert.That(merge.ExpectedRanges, Is.Empty)
                Assert.That(merge.IsFinalizeContribution, Is.True)
            | _ -> Assert.Fail("Expected confirmed upload to provide the metadata merge command.")
        | Error error -> Assert.Fail($"Expected confirmed upload to supersede stale claimed metadata, got {error.Error}.")

    /// Verifies that finalize Manifest Selects Fresh Claim When Stale Duplicate Claim For Same Block Exists.
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
                Assert.That(
                    merge.OperationId,
                    Is.EqualTo($"op-finalize:repository:{session.RepositoryId:N}:upload-session:{sessionId:N}:content-block-metadata:{block.Address}")
                )

                Assert.That(merge.ContentBlockAddress, Is.EqualTo(block.Address))
                Assert.That(merge.BlockFormatVersion, Is.EqualTo(freshMetadata.BlockFormatVersion))
                Assert.That(merge.StoragePlacement, Is.EqualTo(freshMetadata.StoragePlacement))
                Assert.That(merge.Ranges, Has.Length.EqualTo(1))
                Assert.That(merge.Ranges[0].PhysicalLength, Is.EqualTo(int64 fileBytes.Length))
                Assert.That(merge.Ranges[0].ActiveManifestCount, Is.EqualTo(1))
                Assert.That(merge.ExpectedMetadataVersion, Is.EqualTo(None))
                Assert.That(merge.ExpectedRanges, Is.EquivalentTo([| freshRange |]))
                Assert.That(merge.IsFinalizeContribution, Is.True)
            | _ -> Assert.Fail("Expected the fresh claimed range to provide the metadata merge command.")
        | Error error -> Assert.Fail($"Expected fresh duplicate claim to supersede stale claim, got {error.Error}.")

    /// Verifies that finalize Manifest Selects Matching Exact Physical Range When Historical Copy Also Exists.
    [<Test>]
    member _.FinalizeManifestSelectsMatchingExactPhysicalRangeWhenHistoricalCopyAlsoExists() =
        let fileBytes = Text.Encoding.UTF8.GetBytes("matching active exact physical range wins")
        let block = encodedBlock fileBytes
        let manifest = manifestFor fileBytes [| block |]

        let inactiveHistoricalRange =
            { OrdinalStart = 0; OrdinalCount = minimumReuseRunLength; ActiveManifestCount = 0; PhysicalOffset = 0L; PhysicalLength = int64 fileBytes.Length }

        let activeCurrentRange = { inactiveHistoricalRange with ActiveManifestCount = 2; PhysicalOffset = 8192L }

        let claimedRange =
            {
                StoragePoolId = sessionStoragePoolId
                ContentBlockAddress = block.Address
                OrdinalStart = activeCurrentRange.OrdinalStart
                OrdinalCount = activeCurrentRange.OrdinalCount
                PhysicalOffset = activeCurrentRange.PhysicalOffset
                PhysicalLength = activeCurrentRange.PhysicalLength
                MetadataVersion = 9L
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
                ClaimedReuseRanges = [| claimedRange |]
            }

        let currentMetadata =
            reuseMetadataFor
                block.Address
                9L
                [|
                    inactiveHistoricalRange
                    activeCurrentRange
                |]

        let finalizeCommand = finalizeWithClaimedMetadata "op-finalize" manifest [| payloadFor block |] [| currentMetadata |]
        let result = UploadSessionActor.decideCommand [] session finalizeCommand (metadata "corr-finalize-matching-exact-range")

        match result with
        | Ok decision ->
            Assert.That(decision.Session.LifecycleState, Is.EqualTo(UploadSessionLifecycleState.RetentionPending))

            let commands =
                UploadSessionActor.createContentBlockMetadataMergeCommandsForFinalizedBlocks
                    sessionStoragePoolId
                    "op-finalize"
                    session
                    manifest
                    [| currentMetadata |]

            Assert.That(commands, Has.Length.EqualTo(1))

            match commands[0] with
            | ContentBlockMetadataCommand.MergePhysicalRanges merge ->
                Assert.That(merge.Ranges, Has.Length.EqualTo(1))
                Assert.That(merge.Ranges[0].PhysicalOffset, Is.EqualTo(activeCurrentRange.PhysicalOffset))
                Assert.That(merge.Ranges[0].PhysicalLength, Is.EqualTo(activeCurrentRange.PhysicalLength))
                Assert.That(merge.Ranges[0].ActiveManifestCount, Is.EqualTo(1))
                Assert.That(merge.ExpectedRanges, Is.EquivalentTo([| activeCurrentRange |]))
                Assert.That(merge.IsFinalizeContribution, Is.True)
            | _ -> Assert.Fail("Expected the matching active exact claimed range to provide the metadata merge command.")
        | Error error -> Assert.Fail($"Expected matching active exact range to finalize, got {error.Error}.")

    /// Verifies that finalize Manifest Backtracks Duplicate Active Range Starts To Select Contiguous Physical Chain.
    [<Test>]
    member _.FinalizeManifestBacktracksDuplicateActiveRangeStartsToSelectContiguousPhysicalChain() =
        let firstBytes = Text.Encoding.UTF8.GetBytes("first duplicate active range")
        let secondBytes = Text.Encoding.UTF8.GetBytes("second duplicate active range")
        let fileBytes = Array.concat [ firstBytes; secondBytes ]

        let block =
            encodedBlockFromChunks [ ContentBlockFormat.createChunk 1024L firstBytes
                                     ContentBlockFormat.createChunk (1024L + int64 firstBytes.Length) secondBytes ]

        let manifest = manifestFor fileBytes [| block |]

        let abandonedFirstCopy = { OrdinalStart = 0; OrdinalCount = 1; ActiveManifestCount = 1; PhysicalOffset = 0L; PhysicalLength = int64 firstBytes.Length }

        let selectedFirstCopy = { abandonedFirstCopy with PhysicalOffset = 1024L }

        let selectedSecondCopy =
            {
                OrdinalStart = 1
                OrdinalCount = 1
                ActiveManifestCount = 1
                PhysicalOffset = 1024L + int64 firstBytes.Length
                PhysicalLength = int64 secondBytes.Length
            }

        let claimedRange =
            {
                StoragePoolId = sessionStoragePoolId
                ContentBlockAddress = block.Address
                OrdinalStart = 0
                OrdinalCount = 2
                PhysicalOffset = selectedFirstCopy.PhysicalOffset
                PhysicalLength = int64 fileBytes.Length
                MetadataVersion = 10L
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
                ClaimedReuseRanges = [| claimedRange |]
            }

        let currentMetadata =
            reuseMetadataFor
                block.Address
                10L
                [|
                    abandonedFirstCopy
                    selectedFirstCopy
                    selectedSecondCopy
                |]

        let finalizeCommand = finalizeWithClaimedMetadata "op-finalize" manifest [| payloadFor block |] [| currentMetadata |]
        let result = UploadSessionActor.decideCommand [] session finalizeCommand (metadata "corr-finalize-duplicate-active-chain")

        match result with
        | Ok decision -> Assert.That(decision.Session.LifecycleState, Is.EqualTo(UploadSessionLifecycleState.RetentionPending))
        | Error error -> Assert.Fail($"Expected duplicate active range chain to finalize, got {error.Error}.")

        let commands =
            UploadSessionActor.createContentBlockMetadataMergeCommandsForFinalizedBlocks
                sessionStoragePoolId
                "op-finalize"
                session
                manifest
                [| currentMetadata |]

        Assert.That(commands, Has.Length.EqualTo(1))

        match commands[0] with
        | ContentBlockMetadataCommand.MergePhysicalRanges merge ->
            Assert.That(merge.Ranges, Has.Length.EqualTo(2))
            Assert.That(merge.Ranges[0].PhysicalOffset, Is.EqualTo(selectedFirstCopy.PhysicalOffset))
            Assert.That(merge.Ranges[1].PhysicalOffset, Is.EqualTo(selectedSecondCopy.PhysicalOffset))

            Assert.That(
                merge.ExpectedRanges,
                Is.EquivalentTo(
                    [|
                        selectedFirstCopy
                        selectedSecondCopy
                    |]
                )
            )
        | _ -> Assert.Fail("Expected duplicate active range chain finalization to create one MergePhysicalRanges command.")

    /// Verifies that finalize Manifest Rejects Newer Partial Claim When Only Stale Full Claim Covers Block.
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

    /// Verifies that claimed Reuse Metadata Merge Operation Ids Are Scoped By Upload Session.
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

        let firstMetadataDto = { ContentBlockMetadataDto.Empty with Metadata = Some authoritativeMetadata }

        let firstDecision =
            ContentBlockMetadataActor.decideCommand [] firstMetadataDto firstCommands[0] (metadata "corr-metadata-first")
            |> decisionOrFail "Expected first metadata merge to succeed"

        let currentMetadataDto =
            firstDecision.Events
            |> List.fold (fun current event -> ContentBlockMetadataDto.UpdateDto event current) firstMetadataDto

        let revalidatedSecondCommand =
            match
                UploadSessionActor.createRevalidatedClaimedMetadataMergeCommand
                    "corr-metadata-second"
                    sessionStoragePoolId
                    "op-finalize"
                    secondSession
                    manifest
                    [| authoritativeMetadata |]
                    firstDecision.Metadata
                with
            | Ok (Some command) -> command
            | Ok None ->
                Assert.Fail("Expected second claimed reuse command to revalidate against current ContentBlockMetadata.")
                secondCommands[0]
            | Error error ->
                Assert.Fail($"Expected second claimed reuse command to revalidate, got {error.Error}.")
                secondCommands[0]

        let secondDecision =
            ContentBlockMetadataActor.decideCommand firstDecision.Events currentMetadataDto revalidatedSecondCommand (metadata "corr-metadata-second")
            |> decisionOrFail "Expected second metadata merge to be a distinct contribution"

        Assert.That(secondDecision.WasIdempotentReplay, Is.False)
        Assert.That(secondDecision.Metadata.Ranges, Has.Length.EqualTo(1))

        Assert.That(
            secondDecision.Metadata.Ranges[0]
                .ActiveManifestCount,
            Is.EqualTo(2)
        )

    /// Verifies that finalize Metadata Merge Operation Ids Include Repository Scope For Shared Pool Sessions.
    [<Test>]
    member _.FinalizeMetadataMergeOperationIdsIncludeRepositoryScopeForSharedPoolSessions() =
        let fileBytes = Text.Encoding.UTF8.GetBytes("same upload session id across repositories")
        let block = encodedBlock fileBytes
        let manifest = manifestFor fileBytes [| block |]
        let confirmedRange = { OrdinalStart = 0; OrdinalCount = 1; ActiveManifestCount = 0; PhysicalOffset = 0L; PhysicalLength = int64 fileBytes.Length }
        let alternateRepositoryId = Guid.Parse("a50fe532-42fa-4a50-9894-53fdad0374f2")

        /// Builds session For test data for the server unit upload Session Actor scenarios in this file.
        let sessionFor repositoryId =
            { UploadSessionDto.Default with
                UploadSessionId = sessionId
                RepositoryId = repositoryId
                StoragePoolId = sessionStoragePoolId
                ConfirmedBlockUploads =
                    [|
                        {
                            ContentBlockAddress = block.Address
                            PayloadLength = block.Payload.LongLength
                            StoragePlacement = placementFor block.Address (Some "etag-confirmed")
                            Ranges = [| confirmedRange |]
                            ConfirmedAt = timestamp
                        }
                    |]
            }

        let firstCommands =
            UploadSessionActor.createContentBlockMetadataMergeCommandsForFinalizedUploads sessionStoragePoolId "op-finalize" (sessionFor repositoryId) manifest

        let secondCommands =
            UploadSessionActor.createContentBlockMetadataMergeCommandsForFinalizedUploads
                sessionStoragePoolId
                "op-finalize"
                (sessionFor alternateRepositoryId)
                manifest

        /// Builds operation Id test data for the server unit upload Session Actor scenarios in this file.
        let operationId command =
            match command with
            | ContentBlockMetadataCommand.MergePhysicalRanges merge -> merge.OperationId
            | _ ->
                Assert.Fail("Expected uploaded block finalization to create a ContentBlockMetadata MergePhysicalRanges command.")
                String.Empty

        let firstOperationId = operationId firstCommands[0]
        let secondOperationId = operationId secondCommands[0]

        Assert.That(firstOperationId, Is.Not.EqualTo(secondOperationId))
        Assert.That(firstOperationId, Does.Contain(repositoryId.ToString("N")))
        Assert.That(secondOperationId, Does.Contain(alternateRepositoryId.ToString("N")))
        Assert.That(firstOperationId, Does.Contain(sessionId.ToString("N")))
        Assert.That(secondOperationId, Does.Contain(sessionId.ToString("N")))

    /// Verifies that reminder Dispatch Uses Persisted Upload Session Primary Key Without Rehashing.
    [<Test>]
    member _.ReminderDispatchUsesPersistedUploadSessionPrimaryKeyWithoutRehashing() =
        let reminderPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Actors", "Reminder.Actor.fs"))
        let reminderSource = File.ReadAllText(reminderPath)
        let actorProxyPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Actors", "ActorProxy.Extensions.Actor.fs"))
        let actorProxySource = File.ReadAllText(actorProxyPath)

        Assert.That(actorProxySource, Does.Contain("let CreateActorProxyForPrimaryKey"))
        Assert.That(reminderSource, Does.Contain("UploadSession.CreateActorProxyForPrimaryKey actorId reminderDto.RepositoryId correlationId"))
        Assert.That(reminderSource, Does.Not.Contain("UploadSession.CreateActorProxy actorId reminderDto.RepositoryId correlationId"))

    /// Verifies that finalize Metadata Retry After Partial Post Finalized Failure Does Not Double Increment Earlier Block.
    [<Test>]
    member _.FinalizeMetadataRetryAfterPartialPostFinalizedFailureDoesNotDoubleIncrementEarlierBlock() =
        let firstBytes = Text.Encoding.UTF8.GetBytes("first finalized block")
        let secondBytes = Text.Encoding.UTF8.GetBytes("second finalized block")
        let fileBytes = Array.append firstBytes secondBytes
        let firstBlock = encodedBlock firstBytes
        let secondBlock = encodedBlock secondBytes
        let manifest = manifestFor fileBytes [| firstBlock; secondBlock |]

        /// Builds confirmed Range test data for the server unit upload Session Actor scenarios in this file.
        let confirmedRange (bytes: byte array) =
            { OrdinalStart = 0; OrdinalCount = 1; ActiveManifestCount = 0; PhysicalOffset = 0L; PhysicalLength = int64 (Array.length bytes) }

        let secondRange = confirmedRange secondBytes

        /// Builds confirmed Block test data for the server unit upload Session Actor scenarios in this file.
        let confirmedBlock (block: ContentBlockFormat.EncodedContentBlock) (bytes: byte array) eTag =
            {
                ContentBlockAddress = block.Address
                PayloadLength = block.Payload.LongLength
                StoragePlacement = placementFor block.Address (Some eTag)
                Ranges = [| confirmedRange bytes |]
                ConfirmedAt = timestamp
            }

        let session =
            { UploadSessionDto.Default with
                UploadSessionId = sessionId
                StoragePoolId = sessionStoragePoolId
                LifecycleState = UploadSessionLifecycleState.UploadingBlocks
                FileContentHash = manifest.FileContentHash
                ExpectedSize = manifest.Size
                ChunkingSuiteId = manifest.ChunkingSuiteId
                ConfirmedBlockUploads =
                    [|
                        confirmedBlock firstBlock firstBytes "etag-first"
                        confirmedBlock secondBlock secondBytes "etag-second"
                    |]
            }

        let commands = UploadSessionActor.createContentBlockMetadataMergeCommandsForFinalizedUploads sessionStoragePoolId "op-finalize" session manifest

        Assert.That(commands, Has.Length.EqualTo(2))

        /// Builds merge At test data for the server unit upload Session Actor scenarios in this file.
        let mergeAt index =
            match commands[index] with
            | ContentBlockMetadataCommand.MergePhysicalRanges merge -> merge
            | _ ->
                Assert.Fail("Expected uploaded block finalization to create ContentBlockMetadata merge commands.")
                Unchecked.defaultof<MergeContentBlockPhysicalRanges>

        let firstMerge = UploadSessionActor.withFinalizeMergePrecondition (mergeAt 0)
        let secondMetadataAtPrevalidation = reuseMetadataFor secondBlock.Address 7L [| secondRange |]
        let secondMerge = UploadSessionActor.withFinalizeMergePrecondition (mergeAt 1)

        let firstResult =
            ContentBlockMetadataActor.decideCommand
                []
                ContentBlockMetadataDto.Empty
                (ContentBlockMetadataCommand.MergePhysicalRanges firstMerge)
                (metadata "corr-first-merge")
            |> decisionOrFail "Expected first metadata merge to succeed"

        Assert.That(firstResult.Metadata.Ranges, Has.Length.EqualTo(1))
        Assert.That(firstResult.Metadata.Ranges[0].ActiveManifestCount, Is.EqualTo(1))

        let finalizedSession =
            apply { Event = UploadSessionEventType.Finalized("op-finalize", manifest.ManifestAddress); Metadata = metadata "corr-finalized" } session

        Assert.That(finalizedSession.FinalizedManifestAddress, Is.EqualTo(Some manifest.ManifestAddress))

        let secondMetadataAtMergeTime = { secondMetadataAtPrevalidation with MetadataVersion = 8L }

        let advancedSecondResult =
            ContentBlockMetadataActor.decideCommand
                []
                { ContentBlockMetadataDto.Empty with Metadata = Some secondMetadataAtMergeTime }
                (ContentBlockMetadataCommand.MergePhysicalRanges secondMerge)
                (metadata "corr-second-stale")

        match advancedSecondResult with
        | Ok decision ->
            Assert.That(decision.WasIdempotentReplay, Is.False)
            Assert.That(decision.Metadata.Ranges[0].ActiveManifestCount, Is.EqualTo(1))
        | Error error -> Assert.Fail($"Expected versionless finalized merge to tolerate advanced metadata, got {error.Error}.")

        let firstMetadataDto =
            firstResult.Events
            |> List.fold (fun current event -> ContentBlockMetadataDto.UpdateDto event current) ContentBlockMetadataDto.Empty

        let firstRetry =
            ContentBlockMetadataActor.decideCommand
                firstResult.Events
                firstMetadataDto
                (ContentBlockMetadataCommand.MergePhysicalRanges firstMerge)
                (metadata "corr-first-retry")
            |> decisionOrFail "Expected first metadata merge retry to be idempotent"

        Assert.That(firstRetry.WasIdempotentReplay, Is.True)
        Assert.That(firstRetry.Events, Is.Empty)
        Assert.That(firstRetry.Metadata.Ranges[0].ActiveManifestCount, Is.EqualTo(1))

        let secondRetryMerge = UploadSessionActor.withFinalizeMergePrecondition (mergeAt 1)

        let secondRetry =
            ContentBlockMetadataActor.decideCommand
                []
                { ContentBlockMetadataDto.Empty with Metadata = Some secondMetadataAtMergeTime }
                (ContentBlockMetadataCommand.MergePhysicalRanges secondRetryMerge)
                (metadata "corr-second-retry")
            |> decisionOrFail "Expected second metadata merge retry to use the fresh metadata version"

        Assert.That(secondRetry.WasIdempotentReplay, Is.False)
        Assert.That(secondRetry.Metadata.Ranges, Has.Length.EqualTo(1))
        Assert.That(secondRetry.Metadata.Ranges[0].ActiveManifestCount, Is.EqualTo(1))

    /// Verifies that finalized Manifest Ranges Emit Single Reference Contribution Delta.
    [<Test>]
    member _.FinalizedManifestRangesEmitSingleReferenceContributionDelta() =
        let ranges =
            [|
                { OrdinalStart = 0; OrdinalCount = 1; ActiveManifestCount = 2; PhysicalOffset = 0L; PhysicalLength = 11L }
            |]

        let activeRanges = UploadSessionActor.finalizedManifestContributionRanges ranges

        Assert.That(activeRanges[0].ActiveManifestCount, Is.EqualTo(1))
        Assert.That(ranges[0].ActiveManifestCount, Is.EqualTo(2))

    /// Verifies that finalize Manifest Replay Repairs Metadata Before Dedupe Registration.
    [<Test>]
    member _.FinalizeManifestReplayRepairsMetadataBeforeDedupeRegistration() =
        let actorPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Actors", "UploadSession.Actor.fs"))
        let actorSource = File.ReadAllText actorPath
        let handleStart = actorSource.IndexOf("member this.Handle command metadata", StringComparison.Ordinal)

        Assert.That(handleStart, Is.GreaterThanOrEqualTo(0), "The UploadSessionActor.Handle implementation must be present.")

        let finalizeBranchStart = actorSource.IndexOf("| UploadSessionCommand.FinalizeManifest finalize ->", handleStart, StringComparison.Ordinal)

        Assert.That(finalizeBranchStart, Is.GreaterThan(handleStart), "The Handle finalize branch must be present.")

        let nextBranchStart = actorSource.IndexOf("| _ ->", finalizeBranchStart, StringComparison.Ordinal)

        Assert.That(nextBranchStart, Is.GreaterThan(finalizeBranchStart), "The Handle finalize branch must have a bounded source slice.")

        let finalizeBranch = actorSource.Substring(finalizeBranchStart, nextBranchStart - finalizeBranchStart)

        let replayGuardIndex = finalizeBranch.IndexOf("if decision.WasIdempotentReplay then", StringComparison.Ordinal)

        let replayManifestValidateIndex =
            finalizeBranch.IndexOf("validateFinalizeReplayManifestAgainstDurableState decision.Session finalize metadata", StringComparison.Ordinal)

        let replayMetadataRepairIndex =
            finalizeBranch.IndexOf("this.MergeFinalizedContentBlockMetadata decision finalize metadata", replayGuardIndex, StringComparison.Ordinal)

        let stateDeletedReplayRefreshIndex =
            finalizeBranch.IndexOf("this.LoadAuthoritativeFinalizedManifestMetadata decision finalize metadata", replayGuardIndex, StringComparison.Ordinal)

        let replayDedupeRepairIndex =
            finalizeBranch.IndexOf("this.RegisterFinalizedManifestInDedupe decision finalize replayMetadata metadata", StringComparison.Ordinal)

        let replayCleanupEnsureIndex =
            finalizeBranch.IndexOf("this.EnsureFinalizeCleanupReminder decision finalize metadata", replayGuardIndex, StringComparison.Ordinal)

        let replayReturnIndex = finalizeBranch.IndexOf("return Ok returnValue", replayGuardIndex, StringComparison.Ordinal)

        let prevalidateIndex = finalizeBranch.IndexOf("this.PrevalidateFinalizedContentBlockMetadata decision finalize metadata", StringComparison.Ordinal)

        let revalidateIndex = finalizeBranch.IndexOf("this.RevalidatePrevalidatedContentBlockMetadata prevalidatedMerges metadata", StringComparison.Ordinal)

        let splitEventsIndex = finalizeBranch.IndexOf("let finalizationEvents, retentionEvents = splitFinalizeEvents decision.Events", StringComparison.Ordinal)

        let applyFinalizedIndex = finalizeBranch.IndexOf("this.ApplyEvents finalizationEvents", StringComparison.Ordinal)

        let mergePrevalidatedIndex = finalizeBranch.IndexOf("this.MergePrevalidatedContentBlockMetadata prevalidatedMerges metadata", StringComparison.Ordinal)

        let applyRetentionIndex = finalizeBranch.IndexOf("this.ApplyEvents retentionEvents", StringComparison.Ordinal)

        let scheduleCleanupIndex = finalizeBranch.IndexOf("this.ScheduleFinalizeCleanupReminder decision finalize metadata", StringComparison.Ordinal)

        let dedupeIndex = finalizeBranch.IndexOf("this.RegisterFinalizedManifestInDedupe decision finalize mergedMetadata metadata", StringComparison.Ordinal)

        Assert.That(replayGuardIndex, Is.GreaterThanOrEqualTo(0), "Finalize replays must not derive metadata merge side effects from the replay command body.")

        Assert.That(
            replayManifestValidateIndex,
            Is.GreaterThan(replayGuardIndex),
            "Finalize replays must validate the command manifest against durable finalized state before repairing side effects."
        )

        Assert.That(
            replayMetadataRepairIndex,
            Is.GreaterThan(replayManifestValidateIndex),
            "Finalize replays must repair ContentBlockMetadata side effects before repairing DedupeIndex metadata records."
        )

        Assert.That(
            stateDeletedReplayRefreshIndex,
            Is.GreaterThan(replayManifestValidateIndex),
            "Finalize replays after upload coordination cleanup must still load current authoritative ContentBlockMetadata."
        )

        Assert.That(
            replayDedupeRepairIndex,
            Is.GreaterThan(replayMetadataRepairIndex),
            "Finalize replays must repair DedupeIndex registration with repaired/current authoritative ContentBlockMetadata before returning success."
        )

        Assert.That(
            replayCleanupEnsureIndex,
            Is.GreaterThan(replayMetadataRepairIndex),
            "Finalize replays that repair post-finalization side effects must ensure retention cleanup before returning success."
        )

        Assert.That(replayDedupeRepairIndex, Is.GreaterThan(replayCleanupEnsureIndex), "Replay dedupe repair must run after cleanup retention is ensured.")

        Assert.That(replayReturnIndex, Is.GreaterThan(replayDedupeRepairIndex), "Finalize replay dedupe repair must complete before returning replay success.")

        Assert.That(prevalidateIndex, Is.GreaterThan(replayReturnIndex), "New finalization must prevalidate metadata before durable finalization.")
        Assert.That(revalidateIndex, Is.GreaterThan(prevalidateIndex), "New finalization must revalidate metadata before durable finalization.")
        Assert.That(splitEventsIndex, Is.GreaterThan(revalidateIndex), "Finalize events must be split only after all metadata checks pass.")
        Assert.That(applyFinalizedIndex, Is.GreaterThan(splitEventsIndex), "The durable Finalized event must be persisted before metadata side effects.")
        Assert.That(scheduleCleanupIndex, Is.GreaterThan(applyFinalizedIndex), "Cleanup scheduling must follow the durable Finalized event.")
        Assert.That(applyRetentionIndex, Is.GreaterThan(scheduleCleanupIndex), "Retention cleanup must be persisted after cleanup scheduling succeeds.")
        Assert.That(mergePrevalidatedIndex, Is.GreaterThan(scheduleCleanupIndex), "Metadata merge side effects must run after cleanup scheduling is durable.")
        Assert.That(dedupeIndex, Is.GreaterThan(mergePrevalidatedIndex), "Dedupe registration should still use the persisted finalize decision.")

        let loadStart = actorSource.IndexOf("member private this.LoadAuthoritativeFinalizedManifestMetadata", StringComparison.Ordinal)
        let registerStart = actorSource.IndexOf("member private this.RegisterFinalizedManifestInDedupe", loadStart, StringComparison.Ordinal)

        Assert.That(loadStart, Is.GreaterThanOrEqualTo(0), "Finalize replay must have a dedicated authoritative metadata refresh helper.")
        Assert.That(registerStart, Is.GreaterThan(loadStart), "The replay metadata refresh source slice must be bounded before Dedupe registration.")

        let loadSource = actorSource.Substring(loadStart, registerStart - loadStart)

        Assert.That(
            loadSource,
            Does.Contain("finalizedManifestContentBlockAddresses finalize.Manifest"),
            "Replay repair should refresh metadata for accepted manifest blocks, not for replay ClaimedMetadata entries."
        )

        Assert.That(
            loadSource,
            Does.Contain("metadataActor.Get metadata.CorrelationId"),
            "Replay repair must read current durable ContentBlockMetadata state before publishing Dedupe metadata records."
        )

        Assert.That(
            loadSource,
            Does.Not.Contain("ClaimedMetadata"),
            "Replay repair must not use mismatched replay command body claimed metadata for metadata side effects."
        )

    /// Verifies that finalize Manifest Replay Validation Allows Payloadless Sdk Style Retry.
    [<Test>]
    member _.FinalizeManifestReplayValidationAllowsPayloadlessSdkStyleRetry() =
        let actorPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Actors", "UploadSession.Actor.fs"))
        let actorSource = File.ReadAllText actorPath
        let replayValidationStart = actorSource.IndexOf("let private validateFinalizeReplayManifestAgainstDurableState", StringComparison.Ordinal)
        let confirmStart = actorSource.IndexOf("let private confirmBlockUpload", replayValidationStart, StringComparison.Ordinal)

        Assert.That(replayValidationStart, Is.GreaterThanOrEqualTo(0), "Replay validation helper must be present.")
        Assert.That(confirmStart, Is.GreaterThan(replayValidationStart), "Replay validation source slice must be bounded.")

        let replayValidationSource = actorSource.Substring(replayValidationStart, confirmStart - replayValidationStart)

        Assert.That(
            replayValidationSource,
            Does
                .Contain("validateFinalizeReplayManifestIdentity")
                .And.Contain("Durable finalized manifest address")
                .And.Not.Contain("ManifestValidation.validate")
                .And.Not.Contain("finalize.BlockPayloads"),
            "Finalize replay must validate durable manifest identity without requiring replay payload bytes."
        )

    /// Verifies that finalize Manifest Prevalidates Claimed Metadata Before Applying Any Metadata Merge.
    [<Test>]
    member _.FinalizeManifestPrevalidatesClaimedMetadataBeforeApplyingAnyMetadataMerge() =
        let actorPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Actors", "UploadSession.Actor.fs"))
        let actorSource = File.ReadAllText actorPath
        let mergeStart = actorSource.IndexOf("member private this.MergeFinalizedContentBlockMetadata", StringComparison.Ordinal)

        Assert.That(mergeStart, Is.GreaterThanOrEqualTo(0), "The finalized metadata merge orchestrator must be present.")

        let registerStart = actorSource.IndexOf("member private this.RegisterFinalizedManifestInDedupe", mergeStart, StringComparison.Ordinal)

        Assert.That(registerStart, Is.GreaterThan(mergeStart), "The finalized metadata merge source slice must be bounded before dedupe registration.")

        let mergeSource = actorSource.Substring(mergeStart, registerStart - mergeStart)
        let prevalidateIndex = mergeSource.IndexOf("this.PrevalidateFinalizedContentBlockMetadata decision finalize metadata", StringComparison.Ordinal)
        let revalidateIndex = mergeSource.IndexOf("this.RevalidatePrevalidatedContentBlockMetadata prevalidatedMerges metadata", StringComparison.Ordinal)
        let mergePrevalidatedIndex = mergeSource.IndexOf("this.MergePrevalidatedContentBlockMetadata prevalidatedMerges metadata", StringComparison.Ordinal)

        Assert.That(prevalidateIndex, Is.GreaterThanOrEqualTo(0), "Finalize must validate all claimed/current metadata before side-effecting merges.")

        Assert.That(
            revalidateIndex,
            Is.GreaterThan(prevalidateIndex),
            "Finalize must re-check all prevalidated metadata snapshots before side-effecting merges."
        )

        Assert.That(mergePrevalidatedIndex, Is.GreaterThan(revalidateIndex), "Finalize must apply metadata merges only after all revalidation succeeds.")

    /// Verifies that finalize Uploaded Merge Preconditions Do Not Freeze Current Snapshot At Merge Time.
    [<Test>]
    member _.FinalizeUploadedMergePreconditionsDoNotFreezeCurrentSnapshotAtMergeTime() =
        let fileBytes = Text.Encoding.UTF8.GetBytes("uploaded merge preconditions")
        let block = encodedBlock fileBytes
        let manifest = manifestFor fileBytes [| block |]

        let confirmedRange = { OrdinalStart = 0; OrdinalCount = 1; ActiveManifestCount = 0; PhysicalOffset = 0L; PhysicalLength = int64 fileBytes.Length }

        let session =
            { UploadSessionDto.Default with
                UploadSessionId = sessionId
                StoragePoolId = sessionStoragePoolId
                ConfirmedBlockUploads =
                    [|
                        {
                            ContentBlockAddress = block.Address
                            PayloadLength = block.Payload.LongLength
                            StoragePlacement = placementFor block.Address (Some "etag-confirmed")
                            Ranges = [| confirmedRange |]
                            ConfirmedAt = timestamp
                        }
                    |]
            }

        let commands = UploadSessionActor.createContentBlockMetadataMergeCommandsForFinalizedUploads sessionStoragePoolId "op-finalize" session manifest

        match commands[0] with
        | ContentBlockMetadataCommand.MergePhysicalRanges merge ->
            let missingPrecondition = UploadSessionActor.withFinalizeMergePrecondition merge

            Assert.That(missingPrecondition.RequireMissingMetadata, Is.False)
            Assert.That(missingPrecondition.ExpectedMetadataVersion, Is.EqualTo(None))

            let currentPrecondition = UploadSessionActor.withFinalizeMergePrecondition merge

            Assert.That(currentPrecondition.RequireMissingMetadata, Is.False)
            Assert.That(currentPrecondition.ExpectedMetadataVersion, Is.EqualTo(None))
        | _ -> Assert.Fail("Expected uploaded block finalization to create a ContentBlockMetadata MergePhysicalRanges command.")

    /// Verifies that finalize Uploaded Merge Rebase Does Not Reactivate Historical Duplicate Ranges.
    [<Test>]
    member _.FinalizeUploadedMergeRebaseDoesNotReactivateHistoricalDuplicateRanges() =
        let fileBytes = Text.Encoding.UTF8.GetBytes("uploaded duplicate authoritative metadata")
        let block = encodedBlock fileBytes
        let manifest = manifestFor fileBytes [| block |]

        let confirmedRange = { OrdinalStart = 0; OrdinalCount = 1; ActiveManifestCount = 0; PhysicalOffset = 0L; PhysicalLength = int64 fileBytes.Length }

        let session =
            { UploadSessionDto.Default with
                UploadSessionId = sessionId
                StoragePoolId = sessionStoragePoolId
                ConfirmedBlockUploads =
                    [|
                        {
                            ContentBlockAddress = block.Address
                            PayloadLength = block.Payload.LongLength
                            StoragePlacement = placementFor block.Address (Some "etag-confirmed")
                            Ranges = [| confirmedRange |]
                            ConfirmedAt = timestamp
                        }
                    |]
            }

        let commands = UploadSessionActor.createContentBlockMetadataMergeCommandsForFinalizedUploads sessionStoragePoolId "op-finalize" session manifest

        let uploadedMerge =
            match commands[0] with
            | ContentBlockMetadataCommand.MergePhysicalRanges merge -> merge
            | _ ->
                Assert.Fail("Expected uploaded block finalization to create a ContentBlockMetadata MergePhysicalRanges command.")
                Unchecked.defaultof<MergeContentBlockPhysicalRanges>

        let activeCurrentRange = { confirmedRange with ActiveManifestCount = 2; PhysicalOffset = 8192L }

        let inactiveHistoricalDuplicate = { confirmedRange with ActiveManifestCount = 0; PhysicalOffset = 16384L }

        let authoritativeMetadata =
            { reuseMetadataFor
                  block.Address
                  8L
                  [|
                      activeCurrentRange
                      inactiveHistoricalDuplicate
                  |] with
                StoragePlacement = placementFor block.Address (Some "etag-current")
                ActivePhysicalBytes = activeCurrentRange.PhysicalLength
                TotalPhysicalBytes =
                    activeCurrentRange.PhysicalLength
                    + inactiveHistoricalDuplicate.PhysicalLength
            }

        let rebasedMerge = UploadSessionActor.rebaseUploadedMergeOnCurrentMetadata (Some authoritativeMetadata) uploadedMerge

        Assert.That(rebasedMerge.StoragePlacement.ETag, Is.EqualTo(authoritativeMetadata.StoragePlacement.ETag))
        Assert.That(rebasedMerge.Ranges, Has.Length.EqualTo(1))
        Assert.That(rebasedMerge.Ranges[0].OrdinalStart, Is.EqualTo(activeCurrentRange.OrdinalStart))
        Assert.That(rebasedMerge.Ranges[0].OrdinalCount, Is.EqualTo(activeCurrentRange.OrdinalCount))
        Assert.That(rebasedMerge.Ranges[0].PhysicalOffset, Is.EqualTo(activeCurrentRange.PhysicalOffset))
        Assert.That(rebasedMerge.Ranges[0].PhysicalLength, Is.EqualTo(activeCurrentRange.PhysicalLength))
        Assert.That(rebasedMerge.Ranges[0].ActiveManifestCount, Is.EqualTo(1))

        Assert.That(
            rebasedMerge.Ranges
            |> Array.exists (fun range -> range.PhysicalOffset = inactiveHistoricalDuplicate.PhysicalOffset),
            Is.False
        )

        Assert.That(rebasedMerge.ExpectedRanges, Is.Empty)
        Assert.That(rebasedMerge.IsFinalizeContribution, Is.True)

    /// Verifies that revalidated Claimed Metadata Merge Rejects Removed Range Before Side Effect.
    [<Test>]
    member _.RevalidatedClaimedMetadataMergeRejectsRemovedRangeBeforeSideEffect() =
        let fileBytes = Text.Encoding.UTF8.GetBytes("reuse range bytes")
        let block = encodedBlock fileBytes
        let manifest = manifestFor fileBytes [| block |]

        let metadataRange = { reusableMetadataRange with OrdinalStart = 0; OrdinalCount = minimumReuseRunLength; PhysicalLength = int64 fileBytes.Length }

        let claimedRange =
            {
                StoragePoolId = storagePoolId
                ContentBlockAddress = block.Address
                OrdinalStart = metadataRange.OrdinalStart
                OrdinalCount = metadataRange.OrdinalCount
                PhysicalOffset = metadataRange.PhysicalOffset
                PhysicalLength = metadataRange.PhysicalLength
                MetadataVersion = 7L
                ClaimedAt = timestamp
            }

        let session = { UploadSessionDto.Default with UploadSessionId = sessionId; StoragePoolId = storagePoolId; ClaimedReuseRanges = [| claimedRange |] }

        let hydratedMetadata = reuseMetadataFor block.Address 7L [| metadataRange |]
        let advancedCurrentMetadata = { hydratedMetadata with MetadataVersion = 8L }

        let advancedResult =
            UploadSessionActor.createRevalidatedClaimedMetadataMergeCommand
                "corr-revalidate-advanced-version"
                storagePoolId
                "op-finalize"
                session
                manifest
                [| hydratedMetadata |]
                advancedCurrentMetadata

        match advancedResult with
        | Ok (Some (ContentBlockMetadataCommand.MergePhysicalRanges merge)) ->
            Assert.That(merge.Ranges, Has.Length.EqualTo(1))
            Assert.That(merge.StoragePlacement.ETag, Is.EqualTo(advancedCurrentMetadata.StoragePlacement.ETag))
            Assert.That(merge.ExpectedMetadataVersion, Is.EqualTo(None))
            Assert.That(merge.ExpectedRanges, Is.EquivalentTo([| metadataRange |]))
            Assert.That(merge.IsFinalizeContribution, Is.True)
        | Ok _ -> Assert.Fail("Expected current authoritative metadata to produce a claimed merge command.")
        | Error error -> Assert.Fail($"Expected metadata-version advancement to be tolerated, got {error.Error}.")

        let activeRelocatedRange = { metadataRange with ActiveManifestCount = 1; PhysicalOffset = metadataRange.PhysicalOffset + 128L }
        let activeRelocatedMetadata = { hydratedMetadata with MetadataVersion = 8L; Ranges = [| activeRelocatedRange |] }

        let activeRelocatedResult =
            UploadSessionActor.createRevalidatedClaimedMetadataMergeCommand
                "corr-revalidate-active-relocated-range"
                storagePoolId
                "op-finalize"
                session
                manifest
                [| hydratedMetadata |]
                activeRelocatedMetadata

        match activeRelocatedResult with
        | Ok (Some (ContentBlockMetadataCommand.MergePhysicalRanges merge)) ->
            Assert.That(merge.Ranges, Has.Length.EqualTo(1))
            Assert.That(merge.Ranges[0].PhysicalOffset, Is.EqualTo(activeRelocatedRange.PhysicalOffset))
            Assert.That(merge.Ranges[0].ActiveManifestCount, Is.EqualTo(1))
            Assert.That(merge.ExpectedMetadataVersion, Is.EqualTo(None))
            Assert.That(merge.ExpectedRanges, Is.EquivalentTo([| activeRelocatedRange |]))
        | Ok _ -> Assert.Fail("Expected active relocated metadata to produce a claimed merge command.")
        | Error error -> Assert.Fail($"Expected active relocated metadata to be accepted for replay repair, got {error.Error}.")

        let relocatedRange = { metadataRange with PhysicalOffset = metadataRange.PhysicalOffset + 128L }
        let relocatedMetadata = { hydratedMetadata with MetadataVersion = 8L; Ranges = [| relocatedRange |] }

        let staleResult =
            UploadSessionActor.createRevalidatedClaimedMetadataMergeCommand
                "corr-revalidate-removed-range"
                storagePoolId
                "op-finalize"
                session
                manifest
                [| hydratedMetadata |]
                relocatedMetadata

        match staleResult with
        | Ok _ -> Assert.Fail("Expected stale claimed range revalidation to fail closed before metadata merge side effects.")
        | Error error -> Assert.That(error.Error, Does.Contain("range is absent or changed"))

    /// Verifies that finalize Manifest From Claimed Reuse Range Validates Reconstruction And Finalizes.
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

    /// Verifies that finalize Manifest Rejects Claimed Reuse Metadata From Wrong Pool.
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

    /// Verifies that finalize Manifest Rejects Missing Confirmed Or Claimed Block Without Consuming Operation Id.
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

    /// Verifies that finalize Manifest Rejects File Hash Mismatch Without Consuming Operation Id.
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

    /// Verifies that finalize Manifest Rejects Total Size Mismatch.
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

    /// Verifies that finalize Manifest With Same Operation Id Is Idempotent Replay.
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

    /// Verifies that cleanup Reminder State Carries Delete Physical State Operation Id.
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

    /// Verifies that delete Physical State After Finalize Preserves Manifest Evidence And Clears Upload Coordination.
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

    /// Verifies that delete Physical State After Abandon Releases Temporary Reuse Claims.
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

    /// Verifies that physical Cleanup Compacts Persisted Events To Tombstone And Drops Coordination Payloads.
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

    /// Verifies that delete Physical State Retry After State Cleared Drains As Idempotent Replay.
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

    /// Verifies that claim Reuse Ranges Rejects Stale Discovery Hint Version.
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

    /// Verifies that claim Reuse Ranges Rejects Expired Discovery.
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

    /// Verifies that claim Reuse Ranges Rejects Missing Authoritative Physical Range.
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

    /// Verifies that claim Reuse Ranges Accepts Later Subwindow With Exact Active Authoritative Range.
    [<Test>]
    member _.ClaimReuseRangesAcceptsLaterSubwindowWithExactActiveAuthoritativeRange() =
        let activeCoveringRange = { reusableMetadataRange with OrdinalCount = 8; ActiveManifestCount = 1; PhysicalLength = 8192L }

        let activeExactSubwindow =
            { reusableMetadataRange with OrdinalStart = 4; OrdinalCount = 4; ActiveManifestCount = 1; PhysicalOffset = 4096L; PhysicalLength = 4096L }

        let subwindowHint = { reuseHint with OrdinalStart = 4; OrdinalCount = 4 }

        let authoritativeMetadata =
            { reuseMetadata
                  7L
                  [|
                      activeExactSubwindow
                      activeCoveringRange
                  |] with
                TotalPhysicalBytes = 8192L
                ActivePhysicalBytes = 8192L
            }

        let startedDto, startEvents = startedSession ()

        let issued = UploadSessionActor.decideCommand startEvents startedDto (discovery "op-discovery" [| subwindowHint |]) (metadata "corr-discovery")

        let discoveredDto, discoveryEvents =
            match issued with
            | Ok decision -> decision.Session, startEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected discovery to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let result =
            UploadSessionActor.decideCommand discoveryEvents discoveredDto (claim "op-claim" subwindowHint authoritativeMetadata) (metadata "corr-claim")

        match result with
        | Ok decision ->
            Assert.That(decision.Session.ClaimedReuseRanges, Has.Length.EqualTo(1))

            Assert.That(
                decision.Session.ClaimedReuseRanges[0]
                    .OrdinalStart,
                Is.EqualTo(4)
            )

            Assert.That(
                decision.Session.ClaimedReuseRanges[0]
                    .OrdinalCount,
                Is.EqualTo(4)
            )

            Assert.That(
                decision.Session.ClaimedReuseRanges[0]
                    .PhysicalOffset,
                Is.EqualTo(4096L)
            )

            Assert.That(
                decision.Session.ClaimedReuseRanges[0]
                    .PhysicalLength,
                Is.EqualTo(4096L)
            )
        | Error error -> Assert.Fail($"Expected covered subwindow claim to succeed, got {error.Error}.")

    /// Verifies that claim Reuse Ranges Rejects Runs Below Minimum Reuse Length.
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

    /// Verifies that claim Reuse Ranges With Same Operation Id Is Stable Idempotent Replay.
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

    /// Verifies that claim Reuse Ranges Rejects Duplicate Hints In Same Command.
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

    /// Verifies that claim Reuse Ranges Rejects Already Claimed Hint With New Operation Id.
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

    /// Verifies that issue Dedupe Discovery Null Payload Returns Grace Error.
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

    /// Verifies that claim Reuse Ranges Null Payload Returns Grace Error.
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
