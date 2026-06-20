namespace Grace.Server.Tests

open Grace.Actors
open Grace.Shared
open Grace.Types.ContentBlockMetadata
open Grace.Types.Common
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO

module ContentBlockMetadataActor = Grace.Actors.ContentBlockMetadata
module ContentBlockMetadataTypes = Grace.Types.ContentBlockMetadata

[<Parallelizable(ParallelScope.All)>]
type ContentBlockMetadataActorTests() =

    let timestamp = Instant.FromUtc(2026, 5, 24, 13, 0)

    let metadata correlationId =
        {
            Timestamp = timestamp
            CorrelationId = correlationId
            Principal = "tester"
            ClientType = Microsoft.FSharp.Core.Option.None
            Properties = Dictionary<string, string>()
        }

    let storagePoolId = StoragePoolId "pool-main"
    let contentBlockAddress = ContentBlockAddress "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
    let contentBlockObjectKey = StorageKeys.contentBlockObjectKey contentBlockAddress

    let placementFor objectKey eTag =
        { StorageAccountName = "cas-account"; StorageContainerName = StorageContainerName "cas-container"; ObjectKey = objectKey; ETag = eTag }

    let activeRange = { OrdinalStart = 0; OrdinalCount = 8; ActiveManifestCount = 2; PhysicalOffset = 0L; PhysicalLength = 1024L }

    let reclaimableRange = { OrdinalStart = 8; OrdinalCount = 4; ActiveManifestCount = 0; PhysicalOffset = 1024L; PhysicalLength = 512L }

    let record ranges =
        {
            Class = nameof ContentBlockMetadata
            StoragePoolId = storagePoolId
            ContentBlockAddress = contentBlockAddress
            BlockFormatVersion = 1s
            StoragePlacement = placementFor contentBlockObjectKey (Some "etag-1")
            Ranges = ranges
            TotalPhysicalBytes = 1536L
            ActivePhysicalBytes = 1024L
            MetadataVersion = 0L
            UpdatedAt = timestamp
        }

    let recordWithTotals ranges totalPhysicalBytes activePhysicalBytes updatedAt metadataVersion =
        { record ranges with
            TotalPhysicalBytes = totalPhysicalBytes
            ActivePhysicalBytes = activePhysicalBytes
            UpdatedAt = updatedAt
            MetadataVersion = metadataVersion
        }

    let replace operationId expectedVersion ranges =
        ContentBlockMetadataCommand.ReplaceWholeRecord { OperationId = operationId; ExpectedMetadataVersion = expectedVersion; Metadata = record ranges }

    let replaceWithMetadata operationId expectedVersion metadata =
        ContentBlockMetadataCommand.ReplaceWholeRecord { OperationId = operationId; ExpectedMetadataVersion = expectedVersion; Metadata = metadata }

    let mergeWithPlacement operationId placement ranges =
        ContentBlockMetadataCommand.MergePhysicalRanges
            {
                OperationId = operationId
                StoragePoolId = storagePoolId
                ContentBlockAddress = contentBlockAddress
                BlockFormatVersion = 1s
                StoragePlacement = placement
                Ranges = ranges
            }

    let mergeWithObjectKey operationId objectKey ranges = mergeWithPlacement operationId (placementFor objectKey (Some "etag-1")) ranges

    let merge operationId ranges = mergeWithObjectKey operationId contentBlockObjectKey ranges

    let compact operationId expectedMetadataVersion placement ranges context =
        ContentBlockMetadataCommand.CompactPhysicalRanges
            {
                OperationId = operationId
                ExpectedMetadataVersion = expectedMetadataVersion
                StoragePlacement = placement
                Ranges = ranges
                CandidateContext = context
            }

    let setChurnState operationId churnState = ContentBlockMetadataCommand.SetCompactionChurnState { OperationId = operationId; ChurnState = churnState }

    let applyAll events current =
        events
        |> List.fold (fun state event -> ContentBlockMetadataDto.UpdateDto event state) current

    [<Test>]
    member _.CreateWholeRecordStampsVersionAndReportsActivePresence() =
        let result =
            ContentBlockMetadataActor.decideCommand [] ContentBlockMetadataDto.Empty (replace "op-create" None [| activeRange |]) (metadata "corr-create")

        match result with
        | Ok decision ->
            Assert.That(decision.WasIdempotentReplay, Is.False)
            Assert.That(decision.Events.Length, Is.EqualTo(1))
            Assert.That(decision.Metadata.MetadataVersion, Is.EqualTo(1L))
            Assert.That(decision.Metadata.StoragePoolId, Is.EqualTo(storagePoolId))

            let presence = ContentBlockMetadataTypes.rangePresence decision.Metadata { OrdinalStart = 0; OrdinalCount = 8 }

            Assert.That(presence, Is.EqualTo(ContentBlockRangePresence.Active))
        | Error error -> Assert.Fail($"Expected create to succeed, got {error.Error}.")

    [<Test>]
    member _.StaleWholeRecordUpdateIsRejected() =
        let created =
            match ContentBlockMetadataActor.decideCommand [] ContentBlockMetadataDto.Empty (replace "op-create" None [| activeRange |]) (metadata "corr-create")
                with
            | Ok decision -> applyAll decision.Events ContentBlockMetadataDto.Empty
            | Error error ->
                Assert.Fail($"Expected create to succeed, got {error.Error}.")
                ContentBlockMetadataDto.Empty

        let staleUpdate =
            ContentBlockMetadataActor.decideCommand [] created (replace "op-stale" (Some 0L) [| activeRange; reclaimableRange |]) (metadata "corr-stale")

        match staleUpdate with
        | Ok _ -> Assert.Fail("Expected stale whole-record update to be rejected.")
        | Error error -> Assert.That(error.Error, Does.Contain("MetadataVersion"))

    [<Test>]
    member _.ReplaceWholeRecordRejectsNullStoragePlacementAsGraceError() =
        let invalidRecord = { record [| activeRange |] with StoragePlacement = Unchecked.defaultof<ContentBlockStoragePlacement> }

        let result =
            ContentBlockMetadataActor.decideCommand
                []
                ContentBlockMetadataDto.Empty
                (replaceWithMetadata "op-replace-null-placement" None invalidRecord)
                (metadata "corr-replace-null-placement")

        match result with
        | Ok _ -> Assert.Fail("Expected null storage placement to be rejected.")
        | Error error -> Assert.That(error.Error, Is.EqualTo("StoragePlacement is required."))

    [<Test>]
    member _.ReplaceWholeRecordRejectsBlankStoragePlacementAccountAsGraceError() =
        let invalidRecord =
            { record [| activeRange |] with StoragePlacement = { placementFor contentBlockObjectKey (Some "etag-1") with StorageAccountName = String.Empty } }

        let result =
            ContentBlockMetadataActor.decideCommand
                []
                ContentBlockMetadataDto.Empty
                (replaceWithMetadata "op-replace-blank-account" None invalidRecord)
                (metadata "corr-replace-blank-account")

        match result with
        | Ok _ -> Assert.Fail("Expected blank storage account to be rejected.")
        | Error error -> Assert.That(error.Error, Is.EqualTo("StoragePlacement.StorageAccountName is required."))

    [<Test>]
    member _.RangePresenceDistinguishesActiveReclaimableAndAbsentRanges() =
        let metadata =
            record [| activeRange
                      reclaimableRange |]

        Assert.That(ContentBlockMetadataTypes.rangePresence metadata { OrdinalStart = 0; OrdinalCount = 8 }, Is.EqualTo(ContentBlockRangePresence.Active))

        Assert.That(ContentBlockMetadataTypes.rangePresence metadata { OrdinalStart = 8; OrdinalCount = 4 }, Is.EqualTo(ContentBlockRangePresence.Reclaimable))

        Assert.That(ContentBlockMetadataTypes.rangePresence metadata { OrdinalStart = 12; OrdinalCount = 4 }, Is.EqualTo(ContentBlockRangePresence.Absent))

    [<Test>]
    member _.RangeGcSafetyRetainsActiveOrClaimedRangesAndReclaimsOnlyUnclaimedZeroActiveRanges() =
        let retainActive =
            ContentBlockMetadataTypes.rangeGcSafety
                { Presence = ContentBlockRangePresence.Active; HasPendingContributionWorkflow = false; HasActiveReuseClaim = false }

        let retainPendingWorkflow =
            ContentBlockMetadataTypes.rangeGcSafety
                { Presence = ContentBlockRangePresence.Reclaimable; HasPendingContributionWorkflow = true; HasActiveReuseClaim = false }

        let retainActiveClaim =
            ContentBlockMetadataTypes.rangeGcSafety
                { Presence = ContentBlockRangePresence.Reclaimable; HasPendingContributionWorkflow = false; HasActiveReuseClaim = true }

        let reclaimZeroActive =
            ContentBlockMetadataTypes.rangeGcSafety
                { Presence = ContentBlockRangePresence.Reclaimable; HasPendingContributionWorkflow = false; HasActiveReuseClaim = false }

        let absent =
            ContentBlockMetadataTypes.rangeGcSafety
                { Presence = ContentBlockRangePresence.Absent; HasPendingContributionWorkflow = false; HasActiveReuseClaim = false }

        Assert.That(retainActive, Is.EqualTo(ContentBlockRangeGcSafety.RetainActiveRange))
        Assert.That(retainPendingWorkflow, Is.EqualTo(ContentBlockRangeGcSafety.RetainPendingContributionWorkflow))
        Assert.That(retainActiveClaim, Is.EqualTo(ContentBlockRangeGcSafety.RetainActiveReuseClaim))
        Assert.That(reclaimZeroActive, Is.EqualTo(ContentBlockRangeGcSafety.Reclaimable))
        Assert.That(absent, Is.EqualTo(ContentBlockRangeGcSafety.Absent))

    [<Test>]
    member _.CompactionCandidateSelectionRequiresThresholdAgeNoChurnAndCurrentMetadataVersion() =
        let oldEnough = timestamp.Plus(Duration.FromHours(-25.0))
        let tooYoung = timestamp.Plus(Duration.FromHours(-23.0))
        let minimumReclaimableBytes = 64L * 1024L * 1024L
        let active = { activeRange with PhysicalLength = minimumReclaimableBytes; ActiveManifestCount = 1 }
        let reclaimable = { reclaimableRange with PhysicalOffset = minimumReclaimableBytes; PhysicalLength = minimumReclaimableBytes; ActiveManifestCount = 0 }

        let candidateMetadata = recordWithTotals [| active; reclaimable |] (minimumReclaimableBytes * 2L) minimumReclaimableBytes oldEnough 7L

        let baseContext =
            {
                Now = timestamp
                ExpectedMetadataVersion = 7L
                HasActiveUpload = false
                HasActiveFinalization = false
                HasActiveRangeClaim = false
                HasActiveCompaction = false
            }

        let candidate = ContentBlockMetadataTypes.selectCompactionCandidate baseContext candidateMetadata

        Assert.That(candidate, Is.EqualTo(ContentBlockCompactionSelection.Selected))

        let tooSmall =
            ContentBlockMetadataTypes.selectCompactionCandidate
                baseContext
                { candidateMetadata with
                    Ranges =
                        [|
                            active
                            { reclaimable with PhysicalLength = minimumReclaimableBytes - 1L }
                        |]
                    TotalPhysicalBytes = (minimumReclaimableBytes * 2L) - 1L
                    ActivePhysicalBytes = minimumReclaimableBytes
                }

        let tooNew = ContentBlockMetadataTypes.selectCompactionCandidate baseContext { candidateMetadata with UpdatedAt = tooYoung }

        let churn = ContentBlockMetadataTypes.selectCompactionCandidate { baseContext with HasActiveRangeClaim = true } candidateMetadata

        let stale = ContentBlockMetadataTypes.selectCompactionCandidate { baseContext with ExpectedMetadataVersion = 6L } candidateMetadata

        Assert.That(tooSmall, Is.EqualTo(ContentBlockCompactionSelection.RetainInsufficientReclaimableBytes))
        Assert.That(tooNew, Is.EqualTo(ContentBlockCompactionSelection.RetainTooYoung))
        Assert.That(churn, Is.EqualTo(ContentBlockCompactionSelection.RetainChurn))
        Assert.That(stale, Is.EqualTo(ContentBlockCompactionSelection.RetainStaleMetadata))

        let overflowReclaimable = { reclaimable with PhysicalLength = Int64.MaxValue }

        let overflowMetadata =
            recordWithTotals
                [|
                    overflowReclaimable
                    { reclaimable with PhysicalLength = 1L }
                |]
                Int64.MaxValue
                0L
                oldEnough
                7L

        let overflowSelection = ContentBlockMetadataTypes.selectCompactionCandidate baseContext overflowMetadata

        Assert.That(overflowSelection, Is.EqualTo(ContentBlockCompactionSelection.Selected))

    [<Test>]
    member _.CompactPhysicalRangesRemovesReclaimableBytesWithoutChangingLogicalReconstructionIdentity() =
        let oldEnough = timestamp.Plus(Duration.FromHours(-25.0))
        let minimumReclaimableBytes = 64L * 1024L * 1024L

        let active = { OrdinalStart = 0; OrdinalCount = 4; ActiveManifestCount = 3; PhysicalOffset = minimumReclaimableBytes; PhysicalLength = 8192L }

        let reclaimable =
            {
                OrdinalStart = 4
                OrdinalCount = 8
                ActiveManifestCount = 0
                PhysicalOffset = minimumReclaimableBytes + 8192L
                PhysicalLength = minimumReclaimableBytes
            }

        let currentMetadata =
            recordWithTotals
                [| active; reclaimable |]
                (minimumReclaimableBytes
                 + 8192L
                 + minimumReclaimableBytes)
                8192L
                oldEnough
                7L

        let currentDto = { ContentBlockMetadataDto.Empty with Metadata = Some currentMetadata }

        let context =
            {
                Now = timestamp
                ExpectedMetadataVersion = 7L
                HasActiveUpload = false
                HasActiveFinalization = false
                HasActiveRangeClaim = false
                HasActiveCompaction = false
            }

        let compactedRange = { active with PhysicalOffset = 0L }
        let compactedPlacement = placementFor $"{contentBlockObjectKey}.compacted" (Some "etag-compact")

        let result =
            ContentBlockMetadataActor.decideCommand
                []
                currentDto
                (compact "op-compact" 7L compactedPlacement [| compactedRange |] context)
                (metadata "corr-compact")

        match result with
        | Ok decision ->
            Assert.That(decision.Metadata.ContentBlockAddress, Is.EqualTo(contentBlockAddress))
            Assert.That(decision.Metadata.MetadataVersion, Is.EqualTo(8L))
            Assert.That(decision.Metadata.StoragePlacement, Is.EqualTo(compactedPlacement))
            Assert.That(decision.Metadata.TotalPhysicalBytes, Is.EqualTo(8192L))
            Assert.That(decision.Metadata.ActivePhysicalBytes, Is.EqualTo(8192L))
            Assert.That(decision.Metadata.Ranges.Length, Is.EqualTo(1))
            Assert.That(decision.Metadata.Ranges[0], Is.EqualTo(compactedRange))

            Assert.That(
                ContentBlockMetadataTypes.rangePresence decision.Metadata { OrdinalStart = 0; OrdinalCount = 4 },
                Is.EqualTo(ContentBlockRangePresence.Active)
            )

            Assert.That(
                ContentBlockMetadataTypes.rangePresence decision.Metadata { OrdinalStart = 4; OrdinalCount = 8 },
                Is.EqualTo(ContentBlockRangePresence.Absent)
            )
        | Error error -> Assert.Fail($"Expected compaction to succeed, got {error.Error}.")

    [<Test>]
    member _.CompactPhysicalRangesRejectsChangedLogicalRangesAndStaleMetadataVersion() =
        let oldEnough = timestamp.Plus(Duration.FromHours(-25.0))
        let minimumReclaimableBytes = 64L * 1024L * 1024L
        let active = { activeRange with PhysicalLength = 8192L; ActiveManifestCount = 1 }
        let reclaimable = { reclaimableRange with PhysicalOffset = 8192L; PhysicalLength = minimumReclaimableBytes; ActiveManifestCount = 0 }

        let currentMetadata = recordWithTotals [| active; reclaimable |] (8192L + minimumReclaimableBytes) 8192L oldEnough 7L

        let currentDto = { ContentBlockMetadataDto.Empty with Metadata = Some currentMetadata }

        let context =
            {
                Now = timestamp
                ExpectedMetadataVersion = 7L
                HasActiveUpload = false
                HasActiveFinalization = false
                HasActiveRangeClaim = false
                HasActiveCompaction = false
            }

        let placement = placementFor $"{contentBlockObjectKey}.compacted" (Some "etag-compact")
        let changedLogicalRange = { active with OrdinalCount = active.OrdinalCount + 1; PhysicalOffset = 0L }

        let changedLogicalResult =
            ContentBlockMetadataActor.decideCommand
                []
                currentDto
                (compact "op-compact-changed-logical" 7L placement [| changedLogicalRange |] context)
                (metadata "corr-compact-changed-logical")

        let staleResult =
            ContentBlockMetadataActor.decideCommand
                []
                currentDto
                (compact
                    "op-compact-stale"
                    6L
                    placement
                    [|
                        { active with PhysicalOffset = 0L }
                    |]
                    context)
                (metadata "corr-compact-stale")

        match changedLogicalResult with
        | Ok _ -> Assert.Fail("Expected changed logical reconstruction ranges to be rejected.")
        | Error error -> Assert.That(error.Error, Does.Contain("preserve active logical reconstruction ranges"))

        match staleResult with
        | Ok _ -> Assert.Fail("Expected stale compaction to be rejected.")
        | Error error -> Assert.That(error.Error, Does.Contain("Stale ContentBlockMetadata compaction rejected"))

    [<Test>]
    member _.CompactPhysicalRangesRejectsRewriteThatDoesNotReducePhysicalBytes() =
        let oldEnough = timestamp.Plus(Duration.FromHours(-25.0))
        let minimumReclaimableBytes = 64L * 1024L * 1024L
        let active = { activeRange with PhysicalLength = 8192L; ActiveManifestCount = 1 }
        let reclaimable = { reclaimableRange with PhysicalOffset = 8192L; PhysicalLength = minimumReclaimableBytes; ActiveManifestCount = 0 }

        let currentMetadata = recordWithTotals [| active; reclaimable |] (8192L + minimumReclaimableBytes) 8192L oldEnough 7L

        let currentDto = { ContentBlockMetadataDto.Empty with Metadata = Some currentMetadata }

        let context =
            {
                Now = timestamp
                ExpectedMetadataVersion = 7L
                HasActiveUpload = false
                HasActiveFinalization = false
                HasActiveRangeClaim = false
                HasActiveCompaction = false
            }

        let placement = placementFor $"{contentBlockObjectKey}.compacted" (Some "etag-compact")
        let nonCompactingRange = { active with PhysicalOffset = minimumReclaimableBytes; PhysicalLength = 8192L }

        let result =
            ContentBlockMetadataActor.decideCommand
                []
                currentDto
                (compact "op-compact-non-reducing" 7L placement [| nonCompactingRange |] context)
                (metadata "corr-compact-non-reducing")

        match result with
        | Ok _ -> Assert.Fail("Expected non-reducing compaction rewrite to be rejected.")
        | Error error -> Assert.That(error.Error, Is.EqualTo("Compacted ContentBlockMetadata must reduce TotalPhysicalBytes."))

    [<Test>]
    member _.CompactPhysicalRangesUsesActorTimestampForAgeGateAndRejectsMissingCandidateContext() =
        let tooYoung = timestamp.Plus(Duration.FromHours(-23.0))
        let callerSuppliedFuture = timestamp.Plus(Duration.FromDays(30.0))
        let minimumReclaimableBytes = 64L * 1024L * 1024L
        let active = { activeRange with PhysicalLength = 8192L; ActiveManifestCount = 1 }
        let reclaimable = { reclaimableRange with PhysicalOffset = 8192L; PhysicalLength = minimumReclaimableBytes; ActiveManifestCount = 0 }

        let currentMetadata = recordWithTotals [| active; reclaimable |] (8192L + minimumReclaimableBytes) 8192L tooYoung 7L

        let currentDto = { ContentBlockMetadataDto.Empty with Metadata = Some currentMetadata }

        let futureContext =
            {
                Now = callerSuppliedFuture
                ExpectedMetadataVersion = 7L
                HasActiveUpload = false
                HasActiveFinalization = false
                HasActiveRangeClaim = false
                HasActiveCompaction = false
            }

        let placement = placementFor $"{contentBlockObjectKey}.compacted" (Some "etag-compact")

        let futureBypassResult =
            ContentBlockMetadataActor.decideCommand
                []
                currentDto
                (compact
                    "op-compact-future-now"
                    7L
                    placement
                    [|
                        { active with PhysicalOffset = 0L }
                    |]
                    futureContext)
                (metadata "corr-compact-future-now")

        let missingContextResult =
            ContentBlockMetadataActor.decideCommand
                []
                currentDto
                (compact
                    "op-compact-missing-context"
                    7L
                    placement
                    [|
                        { active with PhysicalOffset = 0L }
                    |]
                    Unchecked.defaultof<ContentBlockCompactionCandidateContext>)
                (metadata "corr-compact-missing-context")

        let missingPayloadResult =
            ContentBlockMetadataActor.decideCommand
                []
                currentDto
                (ContentBlockMetadataCommand.CompactPhysicalRanges Unchecked.defaultof<CompactContentBlockPhysicalRanges>)
                (metadata "corr-compact-missing-payload")

        match futureBypassResult with
        | Ok _ -> Assert.Fail("Expected actor timestamp to enforce the 24-hour compaction age gate.")
        | Error error -> Assert.That(error.Error, Does.Contain("at least 24 hours old"))

        match missingContextResult with
        | Ok _ -> Assert.Fail("Expected missing CandidateContext to be rejected.")
        | Error error -> Assert.That(error.Error, Is.EqualTo("Compaction CandidateContext is required."))

        match missingPayloadResult with
        | Ok _ -> Assert.Fail("Expected missing CompactPhysicalRanges payload to be rejected.")
        | Error error -> Assert.That(error.Error, Does.Contain("requires a non-empty operation id"))

    [<Test>]
    member _.CompactPhysicalRangesIgnoresCallerSuppliedChurnFlags() =
        let oldEnough = timestamp.Plus(Duration.FromHours(-25.0))
        let minimumReclaimableBytes = 64L * 1024L * 1024L
        let active = { activeRange with PhysicalLength = 8192L; ActiveManifestCount = 1 }
        let reclaimable = { reclaimableRange with PhysicalOffset = 8192L; PhysicalLength = minimumReclaimableBytes; ActiveManifestCount = 0 }

        let currentMetadata = recordWithTotals [| active; reclaimable |] (8192L + minimumReclaimableBytes) 8192L oldEnough 7L

        let currentDto = { ContentBlockMetadataDto.Empty with Metadata = Some currentMetadata }

        let callerSuppliedChurn =
            {
                Now = timestamp
                ExpectedMetadataVersion = 0L
                HasActiveUpload = true
                HasActiveFinalization = true
                HasActiveRangeClaim = true
                HasActiveCompaction = true
            }

        let placement = placementFor $"{contentBlockObjectKey}.compacted" (Some "etag-compact")

        let result =
            ContentBlockMetadataActor.decideCommand
                []
                currentDto
                (compact
                    "op-compact-ignore-caller-churn"
                    7L
                    placement
                    [|
                        { active with PhysicalOffset = 0L }
                    |]
                    callerSuppliedChurn)
                (metadata "corr-compact-ignore-caller-churn")

        match result with
        | Ok decision ->
            Assert.That(decision.Metadata.MetadataVersion, Is.EqualTo(8L))
            Assert.That(decision.Metadata.StoragePlacement, Is.EqualTo(placement))
        | Error error -> Assert.Fail($"Expected caller-supplied churn flags to be ignored by actor compaction, got {error.Error}.")

    [<Test>]
    member _.CompactPhysicalRangesRejectsActorOwnedChurnState() =
        let oldEnough = timestamp.Plus(Duration.FromHours(-25.0))
        let minimumReclaimableBytes = 64L * 1024L * 1024L
        let active = { activeRange with PhysicalLength = 8192L; ActiveManifestCount = 1 }
        let reclaimable = { reclaimableRange with PhysicalOffset = 8192L; PhysicalLength = minimumReclaimableBytes; ActiveManifestCount = 0 }

        let currentMetadata = recordWithTotals [| active; reclaimable |] (8192L + minimumReclaimableBytes) 8192L oldEnough 7L

        let churnState = { ContentBlockCompactionChurnState.NoChurn with HasActiveRangeClaim = true }

        let noChurnDto = { ContentBlockMetadataDto.Empty with Metadata = Some currentMetadata }

        let churnDecision = ContentBlockMetadataActor.decideCommand [] noChurnDto (setChurnState "op-set-churn" churnState) (metadata "corr-set-churn")

        let currentDto =
            match churnDecision with
            | Ok decision ->
                Assert.That(decision.Events.Length, Is.EqualTo(1))
                applyAll decision.Events noChurnDto
            | Error error ->
                Assert.Fail($"Expected actor churn state event to be accepted, got {error.Error}.")
                noChurnDto

        Assert.That(currentDto.CompactionChurnState.HasActiveRangeClaim, Is.True)

        let callerSuppliedNoChurn =
            {
                Now = timestamp
                ExpectedMetadataVersion = 7L
                HasActiveUpload = false
                HasActiveFinalization = false
                HasActiveRangeClaim = false
                HasActiveCompaction = false
            }

        let placement = placementFor $"{contentBlockObjectKey}.compacted" (Some "etag-compact")

        let result =
            ContentBlockMetadataActor.decideCommand
                []
                currentDto
                (compact
                    "op-compact-actor-churn"
                    7L
                    placement
                    [|
                        { active with PhysicalOffset = 0L }
                    |]
                    callerSuppliedNoChurn)
                (metadata "corr-compact-actor-churn")

        match result with
        | Ok _ -> Assert.Fail("Expected actor-owned churn state to reject compaction.")
        | Error error -> Assert.That(error.Error, Does.Contain("churn is active"))

    [<Test>]
    member _.SetCompactionChurnStateRequiresMetadataAndReplaysIdempotently() =
        let churnState = { ContentBlockCompactionChurnState.NoChurn with HasActiveUpload = true }
        let command = setChurnState "op-set-churn-replay" churnState

        let missingMetadataResult = ContentBlockMetadataActor.decideCommand [] ContentBlockMetadataDto.Empty command (metadata "corr-set-churn-missing")

        match missingMetadataResult with
        | Ok _ -> Assert.Fail("Expected churn state command to require current metadata.")
        | Error error -> Assert.That(error.Error, Does.Contain("requires current metadata"))

        let currentMetadata = recordWithTotals [| activeRange |] activeRange.PhysicalLength activeRange.PhysicalLength timestamp 7L
        let currentDto = { ContentBlockMetadataDto.Empty with Metadata = Some currentMetadata }
        let first = ContentBlockMetadataActor.decideCommand [] currentDto command (metadata "corr-set-churn")

        match first with
        | Error error -> Assert.Fail($"Expected churn state command to succeed with metadata, got {error.Error}.")
        | Ok decision ->
            let updated = applyAll decision.Events currentDto
            let replay = ContentBlockMetadataActor.decideCommand decision.Events updated command (metadata "corr-set-churn-replay")

            Assert.That(updated.CompactionChurnState.HasActiveUpload, Is.True)

            match replay with
            | Ok replayDecision ->
                Assert.That(replayDecision.WasIdempotentReplay, Is.True)
                Assert.That(replayDecision.Metadata.MetadataVersion, Is.EqualTo(currentMetadata.MetadataVersion))
            | Error error -> Assert.Fail($"Expected churn state replay to succeed, got {error.Error}.")

    [<Test>]
    member _.RangePresenceAggregatesDuplicateOrdinalRangesAsActiveWhenAnyPhysicalCopyIsActive() =
        let activePhysicalCopy = { reclaimableRange with ActiveManifestCount = 1; PhysicalOffset = 2048L }

        let metadata =
            record [| reclaimableRange
                      activePhysicalCopy |]

        Assert.That(ContentBlockMetadataTypes.rangePresence metadata { OrdinalStart = 8; OrdinalCount = 4 }, Is.EqualTo(ContentBlockRangePresence.Active))

    [<Test>]
    member _.SameOperationIdIsIdempotentReplayWithoutVersionBump() =
        let command = replace "op-create" None [| activeRange |]
        let first = ContentBlockMetadataActor.decideCommand [] ContentBlockMetadataDto.Empty command (metadata "corr-create")

        match first with
        | Ok decision ->
            let current = applyAll decision.Events ContentBlockMetadataDto.Empty
            let replay = ContentBlockMetadataActor.decideCommand decision.Events current command (metadata "corr-replay")

            match replay with
            | Ok replayDecision ->
                Assert.That(replayDecision.WasIdempotentReplay, Is.True)
                Assert.That(replayDecision.Events, Is.Empty)
                Assert.That(replayDecision.Metadata.MetadataVersion, Is.EqualTo(1L))
            | Error error -> Assert.Fail($"Expected idempotent replay, got {error.Error}.")
        | Error error -> Assert.Fail($"Expected create to succeed, got {error.Error}.")

    [<Test>]
    member _.MergePhysicalRangesCreatesAuthoritativeMetadata() =
        let result = ContentBlockMetadataActor.decideCommand [] ContentBlockMetadataDto.Empty (merge "op-merge" [| reclaimableRange |]) (metadata "corr-merge")

        match result with
        | Ok decision ->
            Assert.That(decision.WasIdempotentReplay, Is.False)
            Assert.That(decision.Metadata.MetadataVersion, Is.EqualTo(1L))
            Assert.That(decision.Metadata.Ranges.Length, Is.EqualTo(1))
            Assert.That(decision.Metadata.TotalPhysicalBytes, Is.EqualTo(1536L))
            Assert.That(decision.Metadata.ActivePhysicalBytes, Is.EqualTo(0L))

            Assert.That(
                ContentBlockMetadataTypes.rangePresence decision.Metadata { OrdinalStart = 8; OrdinalCount = 4 },
                Is.EqualTo(ContentBlockRangePresence.Reclaimable)
            )
        | Error error -> Assert.Fail($"Expected physical range merge to succeed, got {error.Error}.")

    [<Test>]
    member _.MergePhysicalRangesRejectsNullStoragePlacementAsGraceError() =
        let result =
            ContentBlockMetadataActor.decideCommand
                []
                ContentBlockMetadataDto.Empty
                (mergeWithPlacement "op-merge-null-placement" Unchecked.defaultof<ContentBlockStoragePlacement> [| reclaimableRange |])
                (metadata "corr-merge-null-placement")

        match result with
        | Ok _ -> Assert.Fail("Expected null storage placement to be rejected.")
        | Error error -> Assert.That(error.Error, Is.EqualTo("StoragePlacement is required."))

    [<Test>]
    member _.MergePhysicalRangesRejectsExistingNullStoragePlacementAsGraceError() =
        let existing =
            { ContentBlockMetadataDto.Empty with
                Metadata = Some { record [| activeRange |] with StoragePlacement = Unchecked.defaultof<ContentBlockStoragePlacement> }
            }

        let result = ContentBlockMetadataActor.decideCommand [] existing (merge "op-merge" [| reclaimableRange |]) (metadata "corr-merge")

        match result with
        | Ok _ -> Assert.Fail("Expected null existing storage placement to be rejected.")
        | Error error -> Assert.That(error.Error, Is.EqualTo("Existing StoragePlacement is required."))

    [<Test>]
    member _.MergePhysicalRangesRejectsPhysicalEndOverflow() =
        let overflowingRange = { reclaimableRange with PhysicalOffset = Int64.MaxValue - 5L; PhysicalLength = 10L }

        let result =
            ContentBlockMetadataActor.decideCommand
                []
                ContentBlockMetadataDto.Empty
                (merge "op-merge-overflow" [| overflowingRange |])
                (metadata "corr-merge-overflow")

        match result with
        | Ok _ -> Assert.Fail("Expected physical range overflow to be rejected.")
        | Error error -> Assert.That(error.Error, Is.EqualTo("ContentBlockMetadataRange.PhysicalOffset plus PhysicalLength must not exceed Int64.MaxValue."))

    [<Test>]
    member _.MergePhysicalRangesRejectsActivePhysicalBytesOverflow() =
        let firstActiveRange = { OrdinalStart = 0; OrdinalCount = 1; ActiveManifestCount = 1; PhysicalOffset = 0L; PhysicalLength = (Int64.MaxValue / 2L) + 1L }

        let secondActiveRange = { firstActiveRange with OrdinalStart = 1; PhysicalOffset = 8L }

        let result =
            ContentBlockMetadataActor.decideCommand
                []
                ContentBlockMetadataDto.Empty
                (merge
                    "op-merge-active-overflow"
                    [|
                        firstActiveRange
                        secondActiveRange
                    |])
                (metadata "corr-merge-active-overflow")

        match result with
        | Ok _ -> Assert.Fail("Expected active physical byte overflow to be rejected.")
        | Error error -> Assert.That(error.Error, Is.EqualTo("ActivePhysicalBytes cannot exceed Int64.MaxValue."))

    [<Test>]
    member _.MergePhysicalRangesRejectsActivePhysicalBytesGreaterThanTotalPhysicalBytes() =
        let firstActiveRange = { OrdinalStart = 0; OrdinalCount = 1; ActiveManifestCount = 1; PhysicalOffset = 0L; PhysicalLength = 10L }

        let overlappingActiveRange = { firstActiveRange with OrdinalStart = 1; PhysicalOffset = 5L }

        let result =
            ContentBlockMetadataActor.decideCommand
                []
                ContentBlockMetadataDto.Empty
                (merge
                    "op-merge-active-exceeds-total"
                    [|
                        firstActiveRange
                        overlappingActiveRange
                    |])
                (metadata "corr-merge-active-exceeds-total")

        match result with
        | Ok _ -> Assert.Fail("Expected active bytes greater than total bytes to be rejected.")
        | Error error -> Assert.That(error.Error, Is.EqualTo("ActivePhysicalBytes cannot exceed TotalPhysicalBytes."))

    [<Test>]
    member _.MergePhysicalRangesAddsMissingRangesWithoutDuplicatingExistingOnReplay() =
        let created =
            match ContentBlockMetadataActor.decideCommand [] ContentBlockMetadataDto.Empty (merge "op-create" [| activeRange |]) (metadata "corr-create") with
            | Ok decision -> applyAll decision.Events ContentBlockMetadataDto.Empty, decision.Events
            | Error error ->
                Assert.Fail($"Expected create to succeed, got {error.Error}.")
                ContentBlockMetadataDto.Empty, []

        let createdDto, createdEvents = created

        let updated =
            ContentBlockMetadataActor.decideCommand createdEvents createdDto (merge "op-append" [| activeRange; reclaimableRange |]) (metadata "corr-append")

        match updated with
        | Ok decision ->
            Assert.That(decision.Metadata.MetadataVersion, Is.EqualTo(2L))
            Assert.That(decision.Metadata.Ranges.Length, Is.EqualTo(2))

            let replay =
                ContentBlockMetadataActor.decideCommand
                    (createdEvents @ decision.Events)
                    (applyAll decision.Events createdDto)
                    (merge "op-append" [| activeRange; reclaimableRange |])
                    (metadata "corr-replay")

            match replay with
            | Ok replayDecision ->
                Assert.That(replayDecision.WasIdempotentReplay, Is.True)
                Assert.That(replayDecision.Metadata.MetadataVersion, Is.EqualTo(2L))
                Assert.That(replayDecision.Metadata.Ranges.Length, Is.EqualTo(2))
            | Error error -> Assert.Fail($"Expected idempotent replay, got {error.Error}.")
        | Error error -> Assert.Fail($"Expected append merge to succeed, got {error.Error}.")

    [<Test>]
    member _.MergePhysicalRangesAddsDistinctRangeContributionToActiveCount() =
        let currentMetadata = record [| activeRange |]
        let currentDto = { ContentBlockMetadataDto.Empty with Metadata = Some currentMetadata }
        let contribution = { activeRange with ActiveManifestCount = 1 }

        let result =
            ContentBlockMetadataActor.decideCommand [] currentDto (merge "op-finalize-second-ref" [| contribution |]) (metadata "corr-finalize-second-ref")

        match result with
        | Ok decision ->
            Assert.That(decision.Metadata.Ranges, Has.Length.EqualTo(1))

            Assert.That(decision.Metadata.Ranges[0].ActiveManifestCount, Is.EqualTo(activeRange.ActiveManifestCount + 1))

            Assert.That(decision.Metadata.ActivePhysicalBytes, Is.EqualTo(activeRange.PhysicalLength))
        | Error error -> Assert.Fail($"Expected duplicate contribution merge to succeed, got {error.Error}.")

    [<Test>]
    member _.MergePhysicalRangesReactivatesExactReclaimableRangeContribution() =
        let currentMetadata = record [| reclaimableRange |]
        let currentDto = { ContentBlockMetadataDto.Empty with Metadata = Some currentMetadata }
        let contribution = { reclaimableRange with ActiveManifestCount = 1 }

        let result =
            ContentBlockMetadataActor.decideCommand [] currentDto (merge "op-finalize-reclaimable" [| contribution |]) (metadata "corr-finalize-reclaimable")

        match result with
        | Ok decision ->
            Assert.That(decision.Metadata.Ranges, Has.Length.EqualTo(1))
            Assert.That(decision.Metadata.Ranges[0].ActiveManifestCount, Is.EqualTo(1))
            Assert.That(decision.Metadata.ActivePhysicalBytes, Is.EqualTo(reclaimableRange.PhysicalLength))

            Assert.That(
                ContentBlockMetadataTypes.rangePresence decision.Metadata { OrdinalStart = 8; OrdinalCount = 4 },
                Is.EqualTo(ContentBlockRangePresence.Active)
            )
        | Error error -> Assert.Fail($"Expected reclaimable contribution merge to succeed, got {error.Error}.")

    [<Test>]
    member _.MergePhysicalRangesCoalescesDuplicateEvidenceWithinOneCommand() =
        let currentMetadata = record [| reclaimableRange |]
        let currentDto = { ContentBlockMetadataDto.Empty with Metadata = Some currentMetadata }
        let contribution = { reclaimableRange with ActiveManifestCount = 1 }

        let result =
            ContentBlockMetadataActor.decideCommand
                []
                currentDto
                (merge "op-finalize-duplicate-evidence" [| contribution; contribution |])
                (metadata "corr-finalize-duplicate-evidence")

        match result with
        | Ok decision ->
            Assert.That(decision.Metadata.Ranges, Has.Length.EqualTo(1))
            Assert.That(decision.Metadata.Ranges[0].ActiveManifestCount, Is.EqualTo(1))
        | Error error -> Assert.Fail($"Expected duplicate evidence merge to succeed, got {error.Error}.")

    [<Test>]
    member _.MergePhysicalRangesAllowsSameOrdinalAtDifferentPhysicalOffsets() =
        let relocatedRange = { activeRange with PhysicalOffset = 4096L; PhysicalLength = activeRange.PhysicalLength; ActiveManifestCount = 0 }

        let created =
            match ContentBlockMetadataActor.decideCommand [] ContentBlockMetadataDto.Empty (merge "op-create" [| activeRange |]) (metadata "corr-create") with
            | Ok decision -> applyAll decision.Events ContentBlockMetadataDto.Empty, decision.Events
            | Error error ->
                Assert.Fail($"Expected create to succeed, got {error.Error}.")
                ContentBlockMetadataDto.Empty, []

        let createdDto, createdEvents = created

        let updated = ContentBlockMetadataActor.decideCommand createdEvents createdDto (merge "op-relocated" [| relocatedRange |]) (metadata "corr-relocated")

        match updated with
        | Ok decision ->
            Assert.That(decision.Metadata.Ranges.Length, Is.EqualTo(2))
            Assert.That(decision.Metadata.Ranges[0].PhysicalOffset, Is.EqualTo(0L))
            Assert.That(decision.Metadata.Ranges[1].PhysicalOffset, Is.EqualTo(4096L))
            Assert.That(decision.Metadata.TotalPhysicalBytes, Is.EqualTo(5120L))
        | Error error -> Assert.Fail($"Expected relocated physical range to merge, got {error.Error}.")

    [<Test>]
    member _.MergePhysicalRangesRejectsStoragePlacementObjectKeyChanges() =
        let created =
            match ContentBlockMetadataActor.decideCommand [] ContentBlockMetadataDto.Empty (merge "op-create" [| activeRange |]) (metadata "corr-create") with
            | Ok decision -> applyAll decision.Events ContentBlockMetadataDto.Empty, decision.Events
            | Error error ->
                Assert.Fail($"Expected create to succeed, got {error.Error}.")
                ContentBlockMetadataDto.Empty, []

        let createdDto, createdEvents = created

        let changedPlacement =
            ContentBlockMetadataActor.decideCommand
                createdEvents
                createdDto
                (mergeWithObjectKey
                    "op-moved"
                    (StorageKeys.contentBlockObjectKey "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
                    [| reclaimableRange |])
                (metadata "corr-moved")

        match changedPlacement with
        | Ok _ -> Assert.Fail("Expected object key change to be rejected.")
        | Error error -> Assert.That(error.Error, Does.Contain("StoragePlacement.ObjectKey mismatch"))

    [<Test>]
    member _.MergePhysicalRangesRejectsStoragePlacementAccountChangesForSameObjectKey() =
        let created =
            match ContentBlockMetadataActor.decideCommand [] ContentBlockMetadataDto.Empty (merge "op-create" [| activeRange |]) (metadata "corr-create") with
            | Ok decision -> applyAll decision.Events ContentBlockMetadataDto.Empty, decision.Events
            | Error error ->
                Assert.Fail($"Expected create to succeed, got {error.Error}.")
                Unchecked.defaultof<_>

        let changedAccount =
            let current, events = created
            let placement = { placementFor contentBlockObjectKey (Some "etag-1") with StorageAccountName = "cas-account-other" }

            ContentBlockMetadataActor.decideCommand
                events
                current
                (mergeWithPlacement "op-merge-account" placement [| reclaimableRange |])
                (metadata "corr-merge-account")

        match changedAccount with
        | Ok _ -> Assert.Fail("Expected account change to be rejected for the same object key.")
        | Error error -> Assert.That(error.Error, Does.Contain("StoragePlacement.StorageAccountName mismatch"))

    [<Test>]
    member _.MergePhysicalRangesRejectsStoragePlacementContainerChangesForSameObjectKey() =
        let created =
            match ContentBlockMetadataActor.decideCommand [] ContentBlockMetadataDto.Empty (merge "op-create" [| activeRange |]) (metadata "corr-create") with
            | Ok decision -> applyAll decision.Events ContentBlockMetadataDto.Empty, decision.Events
            | Error error ->
                Assert.Fail($"Expected create to succeed, got {error.Error}.")
                Unchecked.defaultof<_>

        let changedContainer =
            let current, events = created
            let placement = { placementFor contentBlockObjectKey (Some "etag-1") with StorageContainerName = StorageContainerName "cas-container-other" }

            ContentBlockMetadataActor.decideCommand
                events
                current
                (mergeWithPlacement "op-merge-container" placement [| reclaimableRange |])
                (metadata "corr-merge-container")

        match changedContainer with
        | Ok _ -> Assert.Fail("Expected container change to be rejected for the same object key.")
        | Error error -> Assert.That(error.Error, Does.Contain("StoragePlacement.StorageContainerName mismatch"))

    [<Test>]
    member _.MetadataActorUsesOneCompositeStringKeyAndDoesNotIntroduceChunkActorState() =
        let key = ContentBlockMetadataActorKey.Create storagePoolId contentBlockAddress

        Assert.That(key, Is.EqualTo($"pool-main|{contentBlockAddress}"))

        let repoRoot =
            DirectoryInfo(
                TestContext.CurrentContext.TestDirectory
            )
                .Parent
                .Parent
                .Parent
                .Parent
                .FullName

        let actorsDirectory = Path.Combine(repoRoot, "Grace.Actors")
        let actorFiles = Directory.EnumerateFiles(actorsDirectory, "*.fs", SearchOption.AllDirectories)

        let contentChunkActorMentions =
            actorFiles
            |> Seq.collect (fun path -> File.ReadLines(path))
            |> Seq.filter (fun line -> line.Contains("ContentChunkActor", StringComparison.Ordinal))
            |> Seq.toArray

        Assert.That(contentChunkActorMentions, Is.Empty)
