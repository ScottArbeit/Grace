namespace Grace.Types

open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Types
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

module ContentBlockMetadata =

    [<CLIMutable; GenerateSerializer>]
    type ContentBlockStoragePlacement =
        {
            ObjectKey: string
            ETag: string option
        }

        static member Empty = { ObjectKey = String.Empty; ETag = None }

    [<CLIMutable; GenerateSerializer>]
    type ContentBlockMetadataRange = { OrdinalStart: int; OrdinalCount: int; ActiveManifestCount: int; PhysicalOffset: int64; PhysicalLength: int64 }

    [<CLIMutable; GenerateSerializer>]
    type ContentBlockRangeQuery = { OrdinalStart: int; OrdinalCount: int }

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
    type ContentBlockRangeGcSafetyContext = { Presence: ContentBlockRangePresence; HasPendingContributionWorkflow: bool; HasActiveReuseClaim: bool }

    [<CLIMutable; GenerateSerializer>]
    type ContentBlockMetadata =
        {
            Class: string
            StoragePoolId: StoragePoolId
            ContentBlockAddress: ContentBlockAddress
            BlockFormatVersion: int16
            StoragePlacement: ContentBlockStoragePlacement
            Ranges: ContentBlockMetadataRange array
            TotalPhysicalBytes: int64
            ActivePhysicalBytes: int64
            MetadataVersion: MetadataVersion
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
    type ReplaceContentBlockMetadata = { OperationId: string; ExpectedMetadataVersion: MetadataVersion option; Metadata: ContentBlockMetadata }

    [<GenerateSerializer>]
    type MergeContentBlockPhysicalRanges =
        {
            OperationId: string
            StoragePoolId: StoragePoolId
            ContentBlockAddress: ContentBlockAddress
            BlockFormatVersion: int16
            StoragePlacement: ContentBlockStoragePlacement
            Ranges: ContentBlockMetadataRange array
        }

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ContentBlockMetadataCommand =
        | ReplaceWholeRecord of replace: ReplaceContentBlockMetadata
        | MergePhysicalRanges of merge: MergeContentBlockPhysicalRanges

        static member GetKnownTypes() = GetKnownTypes<ContentBlockMetadataCommand>()

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ContentBlockMetadataEventType =
        | WholeRecordReplaced of operationId: string * metadata: ContentBlockMetadata
        | PhysicalRangesMerged of operationId: string * metadata: ContentBlockMetadata

        static member GetKnownTypes() = GetKnownTypes<ContentBlockMetadataEventType>()

    [<GenerateSerializer>]
    type ContentBlockMetadataEvent = { Event: ContentBlockMetadataEventType; Metadata: EventMetadata }

    [<GenerateSerializer>]
    type ContentBlockMetadataDto =
        {
            Metadata: ContentBlockMetadata option
            LastOperationId: string option
        }

        static member Empty = { Metadata = None; LastOperationId = None }

        static member UpdateDto event _current =
            match event.Event with
            | ContentBlockMetadataEventType.WholeRecordReplaced (operationId, metadata) -> { Metadata = Some metadata; LastOperationId = Some operationId }
            | ContentBlockMetadataEventType.PhysicalRangesMerged (operationId, metadata) -> { Metadata = Some metadata; LastOperationId = Some operationId }

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
