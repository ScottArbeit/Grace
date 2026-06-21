namespace Grace.Types

open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Common
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

module ContentBlockMetadata =

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

        static member Empty =
            { StorageAccountName = String.Empty; StorageContainerName = StorageContainerName String.Empty; ObjectKey = String.Empty; ETag = None }

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

    [<CLIMutable; GenerateSerializer>]
    type ContentBlockRangeQuery =
        {
            [<Id(0u)>]
            OrdinalStart: int
            [<Id(1u)>]
            OrdinalCount: int
        }

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ContentBlockRangePresence =
        | Active
        | Reclaimable
        | Absent

        static member GetKnownTypes() = GetKnownTypes<ContentBlockRangePresence>()

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ContentBlockRangeGcSafety =
        | RetainActiveRange
        | RetainPendingContributionWorkflow
        | RetainActiveReuseClaim
        | Reclaimable
        | Absent

        static member GetKnownTypes() = GetKnownTypes<ContentBlockRangeGcSafety>()

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

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ContentBlockCompactionSelection =
        | Selected
        | RetainInsufficientReclaimableBytes
        | RetainTooYoung
        | RetainChurn
        | RetainStaleMetadata

        static member GetKnownTypes() = GetKnownTypes<ContentBlockCompactionSelection>()

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

        static member NoChurn = { HasActiveUpload = false; HasActiveFinalization = false; HasActiveRangeClaim = false; HasActiveCompaction = false }

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

    [<GenerateSerializer>]
    type SetContentBlockCompactionChurnState =
        {
            [<Id(0u)>]
            OperationId: string
            [<Id(1u)>]
            ChurnState: ContentBlockCompactionChurnState
        }

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ContentBlockMetadataCommand =
        | [<Id(0u)>] ReplaceWholeRecord of replace: ReplaceContentBlockMetadata
        | [<Id(1u)>] MergePhysicalRanges of merge: MergeContentBlockPhysicalRanges
        | [<Id(2u)>] CompactPhysicalRanges of compact: CompactContentBlockPhysicalRanges
        | [<Id(3u)>] SetCompactionChurnState of setChurnState: SetContentBlockCompactionChurnState

        static member GetKnownTypes() = GetKnownTypes<ContentBlockMetadataCommand>()

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ContentBlockMetadataEventType =
        | WholeRecordReplaced of operationId: string * metadata: ContentBlockMetadata
        | PhysicalRangesMerged of operationId: string * metadata: ContentBlockMetadata
        | PhysicalRangesCompacted of operationId: string * metadata: ContentBlockMetadata
        | CompactionChurnStateSet of operationId: string * churnState: ContentBlockCompactionChurnState

        static member GetKnownTypes() = GetKnownTypes<ContentBlockMetadataEventType>()

    [<GenerateSerializer>]
    type ContentBlockMetadataEvent = { Event: ContentBlockMetadataEventType; Metadata: EventMetadata }

    [<GenerateSerializer>]
    type ContentBlockMetadataDto =
        {
            Metadata: ContentBlockMetadata option
            CompactionChurnState: ContentBlockCompactionChurnState
            LastOperationId: string option
        }

        static member Empty = { Metadata = None; CompactionChurnState = ContentBlockCompactionChurnState.NoChurn; LastOperationId = None }

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

    [<GenerateSerializer>]
    type ContentBlockMetadataDecision =
        {
            Metadata: ContentBlockMetadata
            OperationId: string
            Events: ContentBlockMetadataEvent list
            WasIdempotentReplay: bool
            Message: string
        }

    let findRanges (metadata: ContentBlockMetadata) query =
        if query.OrdinalCount <= 0 then
            Array.empty
        else
            metadata.Ranges
            |> Array.filter (fun range ->
                range.OrdinalStart = query.OrdinalStart
                && range.OrdinalCount = query.OrdinalCount)

    let tryFindRange metadata query =
        findRanges metadata query
        |> Array.sortByDescending (fun range -> range.ActiveManifestCount)
        |> Array.tryHead

    let rangePresence metadata query =
        let ranges = findRanges metadata query

        if ranges
           |> Array.exists (fun range -> range.ActiveManifestCount > 0) then
            ContentBlockRangePresence.Active
        elif ranges.Length > 0 then
            ContentBlockRangePresence.Reclaimable
        else
            ContentBlockRangePresence.Absent

    let rangeGcSafety context =
        match context.Presence with
        | ContentBlockRangePresence.Absent -> ContentBlockRangeGcSafety.Absent
        | ContentBlockRangePresence.Active -> ContentBlockRangeGcSafety.RetainActiveRange
        | ContentBlockRangePresence.Reclaimable when context.HasPendingContributionWorkflow -> ContentBlockRangeGcSafety.RetainPendingContributionWorkflow
        | ContentBlockRangePresence.Reclaimable when context.HasActiveReuseClaim -> ContentBlockRangeGcSafety.RetainActiveReuseClaim
        | ContentBlockRangePresence.Reclaimable -> ContentBlockRangeGcSafety.Reclaimable

    let private minimumCompactionReclaimableBytes = 64L * 1024L * 1024L

    let private minimumCompactionAge = Duration.FromHours(24.0)

    let private addSaturatingPhysicalBytes total length =
        if length <= 0L then total
        elif total > Int64.MaxValue - length then Int64.MaxValue
        else total + length

    let private reclaimablePhysicalBytes (metadata: ContentBlockMetadata) =
        metadata.Ranges
        |> Array.filter (fun range -> range.ActiveManifestCount = 0)
        |> Array.fold (fun total range -> addSaturatingPhysicalBytes total range.PhysicalLength) 0L

    let private requiredReclaimableBytes (metadata: ContentBlockMetadata) =
        let tenPercent =
            metadata.TotalPhysicalBytes / 10L
            + if metadata.TotalPhysicalBytes % 10L = 0L then 0L else 1L

        max minimumCompactionReclaimableBytes tenPercent

    let private hasCompactionChurn (context: ContentBlockCompactionCandidateContext) =
        context.HasActiveUpload
        || context.HasActiveFinalization
        || context.HasActiveRangeClaim
        || context.HasActiveCompaction

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
