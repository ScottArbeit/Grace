namespace Grace.Types

open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Common
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

/// Contains content block metadata helpers.
module ContentBlockMetadata =

    /// Represents content block storage placement.
    [<CLIMutable; GenerateSerializer>]
    type ContentBlockStoragePlacement =
        {
            [<Id(0u)>]
            StorageAccountName: StorageAccountName
            [<Id(1u)>]
            StorageContainerName: StorageContainerName
            [<Id(2u)>]
            ObjectKey: string
            [<Id(3u)>]
            ETag: string option
        }

        /// Represents the normalized empty instance used before persisted state or caller input contributes values.
        static member Empty =
            { StorageAccountName = String.Empty; StorageContainerName = StorageContainerName String.Empty; ObjectKey = String.Empty; ETag = None }

    /// Represents content block metadata range.
    [<CLIMutable; GenerateSerializer>]
    type ContentBlockMetadataRange =
        {
            [<Id(0u)>]
            OrdinalStart: int
            [<Id(1u)>]
            OrdinalCount: int
            [<Id(2u)>]
            ActiveManifestCount: int
            [<Id(3u)>]
            PhysicalOffset: int64
            [<Id(4u)>]
            PhysicalLength: int64
        }

    /// Represents content block range query.
    [<CLIMutable; GenerateSerializer>]
    type ContentBlockRangeQuery =
        {
            [<Id(0u)>]
            OrdinalStart: int
            [<Id(1u)>]
            OrdinalCount: int
        }

    /// Represents content block range presence.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ContentBlockRangePresence =
        | Active
        | Reclaimable
        | Absent

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ContentBlockRangePresence>()

    /// Represents content block range gc safety.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ContentBlockRangeGcSafety =
        | RetainActiveRange
        | RetainPendingContributionWorkflow
        | RetainActiveReuseClaim
        | Reclaimable
        | Absent

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ContentBlockRangeGcSafety>()

    /// Represents content block range gc safety context.
    [<CLIMutable; GenerateSerializer>]
    type ContentBlockRangeGcSafetyContext =
        {
            [<Id(0u)>]
            Presence: ContentBlockRangePresence
            [<Id(1u)>]
            HasPendingContributionWorkflow: bool
            [<Id(2u)>]
            HasActiveReuseClaim: bool
        }

    /// Represents content block compaction selection.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ContentBlockCompactionSelection =
        | Selected
        | RetainInsufficientReclaimableBytes
        | RetainTooYoung
        | RetainChurn
        | RetainStaleMetadata

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ContentBlockCompactionSelection>()

    /// Represents content block compaction candidate context.
    [<CLIMutable; GenerateSerializer>]
    type ContentBlockCompactionCandidateContext =
        {
            [<Id(0u)>]
            Now: Instant
            [<Id(1u)>]
            ExpectedMetadataVersion: MetadataVersion
            [<Id(2u)>]
            HasActiveUpload: bool
            [<Id(3u)>]
            HasActiveFinalization: bool
            [<Id(4u)>]
            HasActiveRangeClaim: bool
            [<Id(5u)>]
            HasActiveCompaction: bool
        }

    /// Represents content block compaction churn state.
    [<CLIMutable; GenerateSerializer>]
    type ContentBlockCompactionChurnState =
        {
            [<Id(0u)>]
            HasActiveUpload: bool
            [<Id(1u)>]
            HasActiveFinalization: bool
            [<Id(2u)>]
            HasActiveRangeClaim: bool
            [<Id(3u)>]
            HasActiveCompaction: bool
        }

        /// Represents a metadata summary with no compaction or range churn detected.
        static member NoChurn = { HasActiveUpload = false; HasActiveFinalization = false; HasActiveRangeClaim = false; HasActiveCompaction = false }

    /// Represents content block metadata.
    [<CLIMutable; GenerateSerializer>]
    type ContentBlockMetadata =
        {
            [<Id(0u)>]
            Class: string
            [<Id(1u)>]
            StoragePoolId: StoragePoolId
            [<Id(2u)>]
            ContentBlockAddress: ContentBlockAddress
            [<Id(3u)>]
            BlockFormatVersion: int16
            [<Id(4u)>]
            StoragePlacement: ContentBlockStoragePlacement
            [<Id(5u)>]
            Ranges: ContentBlockMetadataRange array
            [<Id(6u)>]
            TotalPhysicalBytes: int64
            [<Id(7u)>]
            ActivePhysicalBytes: int64
            [<Id(8u)>]
            MetadataVersion: MetadataVersion
            [<Id(9u)>]
            UpdatedAt: Instant
        }

        /// Represents the normalized empty instance used before persisted state or caller input contributes values.
        static member Empty =
            {
                Class = nameof ContentBlockMetadata
                StoragePoolId = StoragePoolId String.Empty
                ContentBlockAddress = ContentBlockAddress String.Empty
                BlockFormatVersion = 0s
                StoragePlacement = ContentBlockStoragePlacement.Empty
                Ranges = Array.empty
                TotalPhysicalBytes = 0L
                ActivePhysicalBytes = 0L
                MetadataVersion = 0L
                UpdatedAt = DefaultTimestamp
            }

    /// Represents replace content block metadata.
    [<GenerateSerializer>]
    type ReplaceContentBlockMetadata =
        {
            [<Id(0u)>]
            OperationId: string
            [<Id(1u)>]
            ExpectedMetadataVersion: MetadataVersion option
            [<Id(2u)>]
            Metadata: ContentBlockMetadata
        }

    /// Represents merge content block physical ranges.
    [<GenerateSerializer>]
    type MergeContentBlockPhysicalRanges =
        {
            [<Id(0u)>]
            OperationId: string
            [<Id(1u)>]
            StoragePoolId: StoragePoolId
            [<Id(2u)>]
            ContentBlockAddress: ContentBlockAddress
            [<Id(3u)>]
            BlockFormatVersion: int16
            [<Id(4u)>]
            StoragePlacement: ContentBlockStoragePlacement
            [<Id(5u)>]
            Ranges: ContentBlockMetadataRange array
            [<Id(6u)>]
            ExpectedMetadataVersion: MetadataVersion option
            [<Id(7u)>]
            RequireMissingMetadata: bool
            [<Id(8u)>]
            ExpectedRanges: ContentBlockMetadataRange array
            [<Id(9u)>]
            IsFinalizeContribution: bool
        }

    /// Represents compact content block physical ranges.
    [<GenerateSerializer>]
    type CompactContentBlockPhysicalRanges =
        {
            [<Id(0u)>]
            OperationId: string
            [<Id(1u)>]
            ExpectedMetadataVersion: MetadataVersion
            [<Id(2u)>]
            StoragePlacement: ContentBlockStoragePlacement
            [<Id(3u)>]
            Ranges: ContentBlockMetadataRange array
            [<Id(4u)>]
            CandidateContext: ContentBlockCompactionCandidateContext
        }

    /// Represents set content block compaction churn state.
    [<GenerateSerializer>]
    type SetContentBlockCompactionChurnState =
        {
            [<Id(0u)>]
            OperationId: string
            [<Id(1u)>]
            ChurnState: ContentBlockCompactionChurnState
        }

    /// Represents content block metadata command.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ContentBlockMetadataCommand =
        | [<Id(0u)>] ReplaceWholeRecord of replace: ReplaceContentBlockMetadata
        | [<Id(1u)>] MergePhysicalRanges of merge: MergeContentBlockPhysicalRanges
        | [<Id(2u)>] CompactPhysicalRanges of compact: CompactContentBlockPhysicalRanges
        | [<Id(3u)>] SetCompactionChurnState of setChurnState: SetContentBlockCompactionChurnState

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ContentBlockMetadataCommand>()

    /// Represents content block metadata event type.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ContentBlockMetadataEventType =
        | WholeRecordReplaced of operationId: string * metadata: ContentBlockMetadata
        | PhysicalRangesMerged of operationId: string * metadata: ContentBlockMetadata
        | PhysicalRangesCompacted of operationId: string * metadata: ContentBlockMetadata
        | CompactionChurnStateSet of operationId: string * churnState: ContentBlockCompactionChurnState

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ContentBlockMetadataEventType>()

    /// Represents the content block metadata event contract.
    [<GenerateSerializer>]
    type ContentBlockMetadataEvent = { Event: ContentBlockMetadataEventType; Metadata: EventMetadata }

    /// Represents content block metadata dto.
    [<GenerateSerializer>]
    type ContentBlockMetadataDto =
        {
            Metadata: ContentBlockMetadata option
            CompactionChurnState: ContentBlockCompactionChurnState
            LastOperationId: string option
        }

        /// Represents the normalized empty instance used before persisted state or caller input contributes values.
        static member Empty = { Metadata = None; CompactionChurnState = ContentBlockCompactionChurnState.NoChurn; LastOperationId = None }

        /// Creates the DTO shape used to carry partial updates without mutating the persisted aggregate directly.
        static member UpdateDto event _current =
            match event.Event with
            | ContentBlockMetadataEventType.WholeRecordReplaced (operationId, metadata) ->
                { _current with Metadata = Some metadata; LastOperationId = Some operationId }
            | ContentBlockMetadataEventType.PhysicalRangesMerged (operationId, metadata) ->
                { _current with Metadata = Some metadata; LastOperationId = Some operationId }
            | ContentBlockMetadataEventType.PhysicalRangesCompacted (operationId, metadata) ->
                { _current with Metadata = Some metadata; LastOperationId = Some operationId }
            | ContentBlockMetadataEventType.CompactionChurnStateSet (operationId, churnState) ->
                { _current with CompactionChurnState = churnState; LastOperationId = Some operationId }

    /// Represents content block metadata decision.
    [<GenerateSerializer>]
    type ContentBlockMetadataDecision =
        {
            Metadata: ContentBlockMetadata
            OperationId: string
            Events: ContentBlockMetadataEvent list
            WasIdempotentReplay: bool
            Message: string
        }

    /// Attempts to select contiguous range evidence.
    let private trySelectContiguousRangeEvidence (metadata: ContentBlockMetadata) query =
        if isNull (box metadata)
           || isNull metadata.Ranges
           || isNull (box query)
           || query.OrdinalStart < 0
           || query.OrdinalCount <= 0
           || query.OrdinalCount > Int32.MaxValue - query.OrdinalStart then
            None
        else
            let queryEnd = query.OrdinalStart + query.OrdinalCount

            /// Computes the exclusive end of a range with overflow protection.
            let rangeEnd (range: ContentBlockMetadataRange) = range.OrdinalStart + range.OrdinalCount

            let candidates =
                metadata.Ranges
                |> Array.choose (fun range ->
                    if range.ActiveManifestCount > 0
                       && range.OrdinalStart >= 0
                       && range.OrdinalCount > 0
                       && range.OrdinalCount
                          <= Int32.MaxValue - range.OrdinalStart
                       && range.PhysicalLength > 0L
                       && range.PhysicalLength
                          <= Int64.MaxValue - range.PhysicalOffset
                       && range.OrdinalStart >= query.OrdinalStart
                       && rangeEnd range <= queryEnd then
                        Some range
                    else
                        None)
                |> Array.sortBy (fun range -> range.OrdinalStart, range.PhysicalOffset, range.PhysicalLength)

            /// Attempts to build chain.
            let rec tryBuildChain nextOrdinal nextPhysicalOffset selected =
                if nextOrdinal = queryEnd then
                    Some(List.rev selected |> List.toArray)
                else
                    candidates
                    |> Array.filter (fun candidate ->
                        candidate.OrdinalStart = nextOrdinal
                        && candidate.PhysicalOffset = nextPhysicalOffset)
                    |> Array.tryPick (fun candidate ->
                        let candidateEnd =
                            if candidate.OrdinalCount > Int32.MaxValue - candidate.OrdinalStart then
                                Int32.MaxValue
                            else
                                candidate.OrdinalStart + candidate.OrdinalCount

                        let candidatePhysicalEnd =
                            if candidate.PhysicalLength > Int64.MaxValue - candidate.PhysicalOffset then
                                Int64.MaxValue
                            else
                                candidate.PhysicalOffset
                                + candidate.PhysicalLength

                        if candidateEnd > queryEnd then
                            None
                        else
                            tryBuildChain candidateEnd candidatePhysicalEnd (candidate :: selected))

            candidates
            |> Array.filter (fun candidate -> candidate.OrdinalStart = query.OrdinalStart)
            |> Array.tryPick (fun candidate ->
                let candidateEnd =
                    if candidate.OrdinalCount > Int32.MaxValue - candidate.OrdinalStart then
                        Int32.MaxValue
                    else
                        candidate.OrdinalStart + candidate.OrdinalCount

                let candidatePhysicalEnd =
                    if candidate.PhysicalLength > Int64.MaxValue - candidate.PhysicalOffset then
                        Int64.MaxValue
                    else
                        candidate.PhysicalOffset
                        + candidate.PhysicalLength

                if candidateEnd > queryEnd then
                    None
                else
                    tryBuildChain candidateEnd candidatePhysicalEnd [ candidate ])

    /// Attempts to synthesize contiguous range.
    let private trySynthesizeContiguousRange (metadata: ContentBlockMetadata) query =
        match trySelectContiguousRangeEvidence metadata query with
        | None -> None
        | Some selected ->
            let first = selected[0]

            Some
                {
                    OrdinalStart = query.OrdinalStart
                    OrdinalCount = query.OrdinalCount
                    ActiveManifestCount =
                        selected
                        |> Seq.map (fun range -> range.ActiveManifestCount)
                        |> Seq.min
                    PhysicalOffset = first.PhysicalOffset
                    PhysicalLength =
                        selected
                        |> Seq.sumBy (fun range -> range.PhysicalLength)
                }

    /// Attempts to synthesize uniform covering range.
    let private trySynthesizeUniformCoveringRange (metadata: ContentBlockMetadata) query =
        if query.OrdinalStart < 0
           || query.OrdinalCount <= 1
           || query.OrdinalCount > Int32.MaxValue - query.OrdinalStart then
            None
        else
            let queryEnd = query.OrdinalStart + query.OrdinalCount

            /// Computes the exclusive end of a range with overflow protection.
            let rangeEnd (range: ContentBlockMetadataRange) =
                if range.OrdinalCount > Int32.MaxValue - range.OrdinalStart then
                    Int32.MaxValue
                else
                    range.OrdinalStart + range.OrdinalCount

            metadata.Ranges
            |> Array.filter (fun range ->
                range.ActiveManifestCount > 0
                && range.OrdinalStart >= 0
                && range.OrdinalCount > 0
                && range.OrdinalCount
                   <= Int32.MaxValue - range.OrdinalStart
                && range.PhysicalOffset >= 0L
                && range.PhysicalLength > 0L
                && range.PhysicalLength
                   <= Int64.MaxValue - range.PhysicalOffset
                && range.OrdinalStart <= query.OrdinalStart
                && rangeEnd range >= queryEnd
                && range.PhysicalLength % int64 range.OrdinalCount = 0L)
            |> Array.sortBy (fun range -> range.OrdinalStart, range.OrdinalCount, range.PhysicalOffset, range.PhysicalLength)
            |> Array.tryHead
            |> Option.map (fun range ->
                let bytesPerOrdinal = range.PhysicalLength / int64 range.OrdinalCount
                let ordinalOffset = query.OrdinalStart - range.OrdinalStart

                { range with
                    OrdinalStart = query.OrdinalStart
                    OrdinalCount = query.OrdinalCount
                    PhysicalOffset =
                        range.PhysicalOffset
                        + (int64 ordinalOffset * bytesPerOrdinal)
                    PhysicalLength = int64 query.OrdinalCount * bytesPerOrdinal
                })

    /// Finds exact active metadata ranges for a query, synthesizing contiguous coverage when exact active ranges are absent.
    let findRanges (metadata: ContentBlockMetadata) query =
        if isNull (box metadata)
           || isNull metadata.Ranges
           || isNull (box query)
           || query.OrdinalCount <= 0 then
            Array.empty
        else
            let exactRanges =
                metadata.Ranges
                |> Array.filter (fun range ->
                    range.OrdinalStart = query.OrdinalStart
                    && range.OrdinalCount = query.OrdinalCount)

            let activeExactRanges =
                exactRanges
                |> Array.filter (fun range -> range.ActiveManifestCount > 0)

            if activeExactRanges.Length > 0 then
                activeExactRanges
            else
                match trySynthesizeContiguousRange metadata query with
                | Some range -> [| range |]
                | None ->
                    match trySynthesizeUniformCoveringRange metadata query with
                    | Some range -> [| range |]
                    | None -> if exactRanges.Length > 0 then exactRanges else Array.empty

    /// Returns the metadata ranges that prove whether a queried ordinal range is active, reclaimable, or absent.
    let findRangeEvidence (metadata: ContentBlockMetadata) query =
        if isNull (box metadata)
           || isNull metadata.Ranges
           || isNull (box query)
           || query.OrdinalStart < 0
           || query.OrdinalCount <= 0
           || query.OrdinalCount > Int32.MaxValue - query.OrdinalStart then
            Array.empty
        else
            let queryEnd = query.OrdinalStart + query.OrdinalCount

            /// Computes the exclusive end of a range with overflow protection.
            let rangeEnd (range: ContentBlockMetadataRange) =
                if range.OrdinalCount > Int32.MaxValue - range.OrdinalStart then
                    Int32.MaxValue
                else
                    range.OrdinalStart + range.OrdinalCount

            let exactRanges =
                metadata.Ranges
                |> Array.filter (fun range ->
                    range.OrdinalStart = query.OrdinalStart
                    && range.OrdinalCount = query.OrdinalCount)

            let activeExactRanges =
                exactRanges
                |> Array.filter (fun range -> range.ActiveManifestCount > 0)

            if activeExactRanges.Length > 0 then
                activeExactRanges
            else
                match trySelectContiguousRangeEvidence metadata query with
                | Some ranges -> ranges
                | None ->
                    let activeCoveringRanges =
                        metadata.Ranges
                        |> Array.filter (fun range ->
                            range.ActiveManifestCount > 0
                            && range.OrdinalStart >= 0
                            && range.OrdinalCount > 0
                            && range.OrdinalCount
                               <= Int32.MaxValue - range.OrdinalStart
                            && range.PhysicalLength > 0L
                            && range.PhysicalLength
                               <= Int64.MaxValue - range.PhysicalOffset
                            && range.OrdinalStart <= query.OrdinalStart
                            && rangeEnd range >= queryEnd)

                    if activeCoveringRanges.Length > 0 then activeCoveringRanges
                    elif exactRanges.Length > 0 then exactRanges
                    else Array.empty

    /// Attempts to find range.
    let tryFindRange metadata query =
        findRanges metadata query
        |> Array.sortByDescending (fun range -> range.ActiveManifestCount)
        |> Array.tryHead

    /// Classifies a queried ordinal range as active, reclaimable, or absent from block metadata.
    let rangePresence metadata query =
        let ranges = findRanges metadata query

        if ranges
           |> Array.exists (fun range -> range.ActiveManifestCount > 0) then
            ContentBlockRangePresence.Active
        elif ranges.Length > 0 then
            ContentBlockRangePresence.Reclaimable
        else
            ContentBlockRangePresence.Absent

    /// Converts range presence and in-flight workflow state into the garbage-collection safety decision.
    let rangeGcSafety context =
        match context.Presence with
        | ContentBlockRangePresence.Absent -> ContentBlockRangeGcSafety.Absent
        | ContentBlockRangePresence.Active -> ContentBlockRangeGcSafety.RetainActiveRange
        | ContentBlockRangePresence.Reclaimable when context.HasPendingContributionWorkflow -> ContentBlockRangeGcSafety.RetainPendingContributionWorkflow
        | ContentBlockRangePresence.Reclaimable when context.HasActiveReuseClaim -> ContentBlockRangeGcSafety.RetainActiveReuseClaim
        | ContentBlockRangePresence.Reclaimable -> ContentBlockRangeGcSafety.Reclaimable

    let private minimumCompactionReclaimableBytes = 64L * 1024L * 1024L

    let private minimumCompactionAge = Duration.FromHours(24.0)

    /// Adds physical byte counts while saturating at Int64.MaxValue instead of overflowing.
    let private addSaturatingPhysicalBytes total length =
        if length <= 0L then total
        elif total > Int64.MaxValue - length then Int64.MaxValue
        else total + length

    /// Totals physical bytes from inactive ranges that compaction may reclaim.
    let private reclaimablePhysicalBytes (metadata: ContentBlockMetadata) =
        metadata.Ranges
        |> Array.filter (fun range -> range.ActiveManifestCount = 0)
        |> Array.fold (fun total range -> addSaturatingPhysicalBytes total range.PhysicalLength) 0L

    /// Computes the reclaimable-byte threshold required before compaction is eligible.
    let private requiredReclaimableBytes (metadata: ContentBlockMetadata) =
        let tenPercent =
            metadata.TotalPhysicalBytes / 10L
            + if metadata.TotalPhysicalBytes % 10L = 0L then 0L else 1L

        max minimumCompactionReclaimableBytes tenPercent

    /// Detects active upload, finalization, range-claim, or compaction work that should defer compaction.
    let private hasCompactionChurn (context: ContentBlockCompactionCandidateContext) =
        context.HasActiveUpload
        || context.HasActiveFinalization
        || context.HasActiveRangeClaim
        || context.HasActiveCompaction

    /// Chooses the compaction eligibility result for a content block from metadata freshness, churn, age, and reclaimable bytes.
    let selectCompactionCandidate (context: ContentBlockCompactionCandidateContext) (metadata: ContentBlockMetadata) =
        if metadata.MetadataVersion
           <> context.ExpectedMetadataVersion then
            ContentBlockCompactionSelection.RetainStaleMetadata
        elif hasCompactionChurn context then
            ContentBlockCompactionSelection.RetainChurn
        elif context.Now - metadata.UpdatedAt < minimumCompactionAge then
            ContentBlockCompactionSelection.RetainTooYoung
        elif reclaimablePhysicalBytes metadata < requiredReclaimableBytes metadata then
            ContentBlockCompactionSelection.RetainInsufficientReclaimableBytes
        else
            ContentBlockCompactionSelection.Selected
