namespace Grace.Server.Tests

open Grace.Actors
open Grace.Types.ContentBlockMetadata
open Grace.Types.Types
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

    let metadata correlationId = { Timestamp = timestamp; CorrelationId = correlationId; Principal = "tester"; Properties = Dictionary<string, string>() }

    let storagePoolId = StoragePoolId "pool-main"
    let contentBlockAddress = ContentBlockAddress "block-blake3-0001"

    let activeRange = { OrdinalStart = 0; OrdinalCount = 8; ActiveManifestCount = 2; PhysicalOffset = 0L; PhysicalLength = 1024L }

    let reclaimableRange = { OrdinalStart = 8; OrdinalCount = 4; ActiveManifestCount = 0; PhysicalOffset = 1024L; PhysicalLength = 512L }

    let record ranges =
        {
            Class = nameof ContentBlockMetadata
            StoragePoolId = storagePoolId
            ContentBlockAddress = contentBlockAddress
            BlockFormatVersion = 1s
            StoragePlacement = { ObjectKey = "cas/content-blocks/block-blake3-0001"; ETag = Some "etag-1" }
            Ranges = ranges
            TotalPhysicalBytes = 1536L
            ActivePhysicalBytes = 1024L
            MetadataVersion = 0L
            UpdatedAt = timestamp
        }

    let replace operationId expectedVersion ranges =
        ContentBlockMetadataCommand.ReplaceWholeRecord { OperationId = operationId; ExpectedMetadataVersion = expectedVersion; Metadata = record ranges }

    let merge operationId ranges =
        ContentBlockMetadataCommand.MergePhysicalRanges
            {
                OperationId = operationId
                StoragePoolId = storagePoolId
                ContentBlockAddress = contentBlockAddress
                BlockFormatVersion = 1s
                StoragePlacement = { ObjectKey = "cas/content-blocks/block-blake3-0001"; ETag = Some "etag-1" }
                Ranges = ranges
            }

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
    member _.RangePresenceDistinguishesActiveReclaimableAndAbsentRanges() =
        let metadata =
            record [| activeRange
                      reclaimableRange |]

        Assert.That(ContentBlockMetadataTypes.rangePresence metadata { OrdinalStart = 0; OrdinalCount = 8 }, Is.EqualTo(ContentBlockRangePresence.Active))

        Assert.That(ContentBlockMetadataTypes.rangePresence metadata { OrdinalStart = 8; OrdinalCount = 4 }, Is.EqualTo(ContentBlockRangePresence.Reclaimable))

        Assert.That(ContentBlockMetadataTypes.rangePresence metadata { OrdinalStart = 12; OrdinalCount = 4 }, Is.EqualTo(ContentBlockRangePresence.Absent))

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
    member _.MetadataActorUsesOneCompositeStringKeyAndDoesNotIntroduceChunkActorState() =
        let key = ContentBlockMetadataActorKey.Create storagePoolId contentBlockAddress

        Assert.That(key, Is.EqualTo("pool-main|block-blake3-0001"))

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
