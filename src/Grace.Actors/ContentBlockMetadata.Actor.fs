namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.ContentBlockMetadata
open Grace.Types.Common
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

module ContentBlockMetadataActorKey =

    let Create (storagePoolId: StoragePoolId) (contentBlockAddress: ContentBlockAddress) = $"{storagePoolId}|{contentBlockAddress}"

module ContentBlockMetadata =

    let commandName command =
        match command with
        | ContentBlockMetadataCommand.ReplaceWholeRecord _ -> "ReplaceWholeRecord"
        | ContentBlockMetadataCommand.MergePhysicalRanges _ -> "MergePhysicalRanges"
        | ContentBlockMetadataCommand.CompactPhysicalRanges _ -> "CompactPhysicalRanges"
        | ContentBlockMetadataCommand.SetCompactionChurnState _ -> "SetCompactionChurnState"

    let operationId command =
        match command with
        | ContentBlockMetadataCommand.ReplaceWholeRecord replace -> replace.OperationId
        | ContentBlockMetadataCommand.MergePhysicalRanges merge -> merge.OperationId
        | ContentBlockMetadataCommand.CompactPhysicalRanges compact when isNull (box compact) -> String.Empty
        | ContentBlockMetadataCommand.CompactPhysicalRanges compact -> compact.OperationId
        | ContentBlockMetadataCommand.SetCompactionChurnState setChurnState when isNull (box setChurnState) -> String.Empty
        | ContentBlockMetadataCommand.SetCompactionChurnState setChurnState -> setChurnState.OperationId

    let private eventOperationId metadataEvent =
        match metadataEvent.Event with
        | ContentBlockMetadataEventType.WholeRecordReplaced (operationId, _) -> operationId
        | ContentBlockMetadataEventType.PhysicalRangesMerged (operationId, _) -> operationId
        | ContentBlockMetadataEventType.PhysicalRangesCompacted (operationId, _) -> operationId
        | ContentBlockMetadataEventType.CompactionChurnStateSet (operationId, _) -> operationId

    let private hasAppliedOperationId (events: seq<ContentBlockMetadataEvent>) operationId =
        events
        |> Seq.exists (fun metadataEvent -> eventOperationId metadataEvent = operationId)

    let applyEvents (events: ContentBlockMetadataEvent list) (current: ContentBlockMetadataDto) =
        events
        |> List.fold (fun dto event -> ContentBlockMetadataDto.UpdateDto event dto) current

    let private graceError correlationId message = GraceError.Create message correlationId

    let private validateRange correlationId (range: ContentBlockMetadataRange) =
        if range.OrdinalStart < 0 then
            Some(graceError correlationId "ContentBlockMetadataRange.OrdinalStart must be zero or greater.")
        elif range.OrdinalCount <= 0 then
            Some(graceError correlationId "ContentBlockMetadataRange.OrdinalCount must be greater than zero.")
        elif range.ActiveManifestCount < 0 then
            Some(graceError correlationId "ContentBlockMetadataRange.ActiveManifestCount must be zero or greater.")
        elif range.PhysicalOffset < 0L then
            Some(graceError correlationId "ContentBlockMetadataRange.PhysicalOffset must be zero or greater.")
        elif range.PhysicalLength <= 0L then
            Some(graceError correlationId "ContentBlockMetadataRange.PhysicalLength must be greater than zero.")
        elif range.PhysicalOffset > Int64.MaxValue - range.PhysicalLength then
            Some(graceError correlationId "ContentBlockMetadataRange.PhysicalOffset plus PhysicalLength must not exceed Int64.MaxValue.")
        else
            None

    let private validateMetadata correlationId (metadata: ContentBlockMetadata) =
        if String.IsNullOrWhiteSpace metadata.StoragePoolId then
            Some(graceError correlationId "StoragePoolId is required.")
        elif String.IsNullOrWhiteSpace metadata.ContentBlockAddress then
            Some(graceError correlationId "ContentBlockAddress is required.")
        elif metadata.BlockFormatVersion <= 0s then
            Some(graceError correlationId "BlockFormatVersion must be greater than zero.")
        elif metadata.TotalPhysicalBytes < 0L then
            Some(graceError correlationId "TotalPhysicalBytes must be zero or greater.")
        elif metadata.ActivePhysicalBytes < 0L then
            Some(graceError correlationId "ActivePhysicalBytes must be zero or greater.")
        elif metadata.ActivePhysicalBytes > metadata.TotalPhysicalBytes then
            Some(graceError correlationId "ActivePhysicalBytes cannot exceed TotalPhysicalBytes.")
        else
            metadata.Ranges
            |> Array.tryPick (validateRange correlationId)

    let private validateStoragePlacement correlationId (placement: ContentBlockStoragePlacement) =
        if isNull (box placement) then
            Some(graceError correlationId "StoragePlacement is required.")
        elif String.IsNullOrWhiteSpace placement.StorageAccountName then
            Some(graceError correlationId "StoragePlacement.StorageAccountName is required.")
        elif String.IsNullOrWhiteSpace placement.StorageContainerName then
            Some(graceError correlationId "StoragePlacement.StorageContainerName is required.")
        elif String.IsNullOrWhiteSpace placement.ObjectKey then
            Some(graceError correlationId "StoragePlacement.ObjectKey is required.")
        else
            None

    let private validateExistingStoragePlacement correlationId (placement: ContentBlockStoragePlacement) =
        if isNull (box placement) then
            Some(graceError correlationId "Existing StoragePlacement is required.")
        elif String.IsNullOrWhiteSpace placement.StorageAccountName then
            Some(graceError correlationId "Existing StoragePlacement.StorageAccountName is required.")
        elif String.IsNullOrWhiteSpace placement.StorageContainerName then
            Some(graceError correlationId "Existing StoragePlacement.StorageContainerName is required.")
        elif String.IsNullOrWhiteSpace placement.ObjectKey then
            Some(graceError correlationId "Existing StoragePlacement.ObjectKey is required.")
        else
            None

    let private validateMergePhysicalRanges correlationId (merge: MergeContentBlockPhysicalRanges) =
        if String.IsNullOrWhiteSpace merge.StoragePoolId then
            Some(graceError correlationId "StoragePoolId is required.")
        elif String.IsNullOrWhiteSpace merge.ContentBlockAddress then
            Some(graceError correlationId "ContentBlockAddress is required.")
        elif merge.BlockFormatVersion <= 0s then
            Some(graceError correlationId "BlockFormatVersion must be greater than zero.")
        elif isNull merge.Ranges || merge.Ranges.Length = 0 then
            Some(graceError correlationId "At least one ContentBlockMetadataRange is required.")
        else
            match validateStoragePlacement correlationId merge.StoragePlacement with
            | Some error -> Some error
            | None ->
                match
                    merge.Ranges
                    |> Array.tryPick (validateRange correlationId)
                    with
                | Some error -> Some error
                | None when isNull merge.ExpectedRanges -> None
                | None ->
                    merge.ExpectedRanges
                    |> Array.tryPick (validateRange correlationId)

    let private validateCompactPhysicalRanges correlationId (compact: CompactContentBlockPhysicalRanges) =
        if isNull (box compact) then
            Some(graceError correlationId "CompactPhysicalRanges payload is required.")
        elif isNull (box compact.CandidateContext) then
            Some(graceError correlationId "Compaction CandidateContext is required.")
        elif isNull compact.Ranges || compact.Ranges.Length = 0 then
            Some(graceError correlationId "At least one compacted ContentBlockMetadataRange is required.")
        else
            match validateStoragePlacement correlationId compact.StoragePlacement with
            | Some error -> Some error
            | None ->
                compact.Ranges
                |> Array.tryPick (fun range ->
                    match validateRange correlationId range with
                    | Some error -> Some error
                    | None when range.ActiveManifestCount <= 0 -> Some(graceError correlationId "Compacted ContentBlockMetadataRange must be active.")
                    | None -> None)

    let private physicalEnd (range: ContentBlockMetadataRange) = range.PhysicalOffset + range.PhysicalLength

    let private totalPhysicalBytes ranges =
        ranges
        |> Array.map physicalEnd
        |> Array.append [| 0L |]
        |> Array.max

    let private activePhysicalBytes correlationId ranges =
        ranges
        |> Array.filter (fun range -> range.ActiveManifestCount > 0)
        |> Array.fold
            (fun total range ->
                match total with
                | Error error -> Error error
                | Ok current when range.PhysicalLength > Int64.MaxValue - current ->
                    Error(graceError correlationId "ActivePhysicalBytes cannot exceed Int64.MaxValue.")
                | Ok current -> Ok(current + range.PhysicalLength))
            (Ok 0L)

    let private rangeKey (range: ContentBlockMetadataRange) = $"{range.OrdinalStart}:{range.OrdinalCount}:{range.PhysicalOffset}:{range.PhysicalLength}"

    let private mergeRanges correlationId isFinalizeContribution existingRanges incomingRanges =
        let merged = Dictionary<string, ContentBlockMetadataRange>()
        let incoming = Dictionary<string, ContentBlockMetadataRange>()
        let mutable error = None

        let mergeActiveCount existing range =
            if range.ActiveManifestCount > Int32.MaxValue - existing.ActiveManifestCount then
                error <- Some(graceError correlationId "ContentBlockMetadataRange.ActiveManifestCount cannot exceed Int32.MaxValue.")
                existing
            else
                { existing with
                    ActiveManifestCount =
                        existing.ActiveManifestCount
                        + range.ActiveManifestCount
                }

        let addExistingRange range =
            let key = rangeKey range

            if error.IsSome then
                ()
            elif merged.ContainsKey key then
                let existing = merged[key]

                merged[key] <- mergeActiveCount existing range
            else
                merged[key] <- range

        let addIncomingRange range =
            let key = rangeKey range

            if error.IsNone then
                match incoming.TryGetValue key with
                | true, existing -> incoming[key] <- { existing with ActiveManifestCount = max existing.ActiveManifestCount range.ActiveManifestCount }
                | false, _ -> incoming[key] <- range

        existingRanges |> Array.iter addExistingRange
        incomingRanges |> Array.iter addIncomingRange

        let isExactReactivation (existing: ContentBlockMetadataRange) (range: ContentBlockMetadataRange) =
            existing.ActiveManifestCount = 0
            && range.ActiveManifestCount > 0

        incoming.Values
        |> Seq.iter (fun range ->
            let key = rangeKey range

            match merged.TryGetValue key with
            | true, existing ->
                if isFinalizeContribution
                   || isExactReactivation existing range then
                    merged[key] <- mergeActiveCount existing range
                else
                    merged[key] <- existing
            | false, _ -> merged[key] <- range)

        match error with
        | Some error -> Error error
        | None ->
            merged.Values
            |> Seq.sortBy (fun range -> range.OrdinalStart, range.OrdinalCount, range.PhysicalOffset, range.PhysicalLength)
            |> Seq.toArray
            |> Ok

    let private activeLogicalRangeKey (range: ContentBlockMetadataRange) = range.OrdinalStart, range.OrdinalCount, range.ActiveManifestCount

    let private activeLogicalRanges ranges =
        ranges
        |> Array.filter (fun range -> range.ActiveManifestCount > 0)
        |> Array.map activeLogicalRangeKey
        |> Array.sort

    let private compactionCandidateContext timestamp expectedMetadataVersion (churnState: ContentBlockCompactionChurnState) =
        {
            Now = timestamp
            ExpectedMetadataVersion = expectedMetadataVersion
            HasActiveUpload = churnState.HasActiveUpload
            HasActiveFinalization = churnState.HasActiveFinalization
            HasActiveRangeClaim = churnState.HasActiveRangeClaim
            HasActiveCompaction = churnState.HasActiveCompaction
        }

    let private createCompactedMetadata
        correlationId
        (current: ContentBlockMetadataDto)
        (compact: CompactContentBlockPhysicalRanges)
        timestamp
        : Result<ContentBlockMetadata, GraceError>
        =
        match current.Metadata with
        | None -> Error(graceError correlationId "ContentBlockMetadata does not exist; compaction requires current metadata.")
        | Some existing ->
            match validateCompactPhysicalRanges correlationId compact with
            | Some error -> Error error
            | None ->
                let candidateContext = compactionCandidateContext timestamp compact.ExpectedMetadataVersion current.CompactionChurnState

                match Grace.Types.ContentBlockMetadata.selectCompactionCandidate candidateContext existing with
                | ContentBlockCompactionSelection.Selected ->
                    let existingActiveRanges = activeLogicalRanges existing.Ranges
                    let compactedActiveRanges = activeLogicalRanges compact.Ranges

                    if existingActiveRanges <> compactedActiveRanges then
                        Error(graceError correlationId "Compacted ContentBlockMetadata ranges must preserve active logical reconstruction ranges.")
                    else
                        match activePhysicalBytes correlationId compact.Ranges with
                        | Error error -> Error error
                        | Ok activePhysicalBytes ->
                            let totalPhysicalBytes = totalPhysicalBytes compact.Ranges

                            if activePhysicalBytes > totalPhysicalBytes then
                                Error(graceError correlationId "ActivePhysicalBytes cannot exceed TotalPhysicalBytes.")
                            elif totalPhysicalBytes >= existing.TotalPhysicalBytes then
                                Error(graceError correlationId "Compacted ContentBlockMetadata must reduce TotalPhysicalBytes.")
                            else
                                Ok
                                    { existing with
                                        StoragePlacement = compact.StoragePlacement
                                        Ranges =
                                            compact.Ranges
                                            |> Array.sortBy (fun range -> range.OrdinalStart, range.OrdinalCount, range.PhysicalOffset, range.PhysicalLength)
                                        TotalPhysicalBytes = totalPhysicalBytes
                                        ActivePhysicalBytes = activePhysicalBytes
                                        MetadataVersion = existing.MetadataVersion + 1L
                                        UpdatedAt = timestamp
                                    }
                | ContentBlockCompactionSelection.RetainInsufficientReclaimableBytes ->
                    Error(graceError correlationId "ContentBlockMetadata compaction requires reclaimable bytes >= max(64 MiB, 10% of TotalPhysicalBytes).")
                | ContentBlockCompactionSelection.RetainTooYoung ->
                    Error(graceError correlationId "ContentBlockMetadata compaction requires reclaimable ranges to be at least 24 hours old.")
                | ContentBlockCompactionSelection.RetainChurn ->
                    Error(
                        graceError
                            correlationId
                            "ContentBlockMetadata compaction rejected while upload, finalization, range-claim, or compaction churn is active."
                    )
                | ContentBlockCompactionSelection.RetainStaleMetadata ->
                    Error(
                        graceError
                            correlationId
                            $"Stale ContentBlockMetadata compaction rejected. Expected MetadataVersion {compact.ExpectedMetadataVersion}, current MetadataVersion {existing.MetadataVersion}."
                    )

    let private expectedRangeExists (existing: ContentBlockMetadata) (expectedRange: ContentBlockMetadataRange) =
        let expectedKey = rangeKey expectedRange

        let ranges =
            if isNull (box existing) || isNull existing.Ranges then
                Array.empty
            else
                existing.Ranges

        ranges
        |> Array.exists (fun existingRange -> rangeKey existingRange = expectedKey)

    let private validateMergePreconditions correlationId (currentMetadata: ContentBlockMetadata option) (merge: MergeContentBlockPhysicalRanges) =
        match currentMetadata, merge.RequireMissingMetadata, merge.ExpectedMetadataVersion with
        | Some existing, true, _ ->
            Error(graceError correlationId $"ContentBlockMetadata already exists; merge expected missing metadata for {merge.ContentBlockAddress}.")
        | None, _, Some expectedVersion -> Error(graceError correlationId $"ContentBlockMetadata does not exist; expected MetadataVersion {expectedVersion}.")
        | Some existing, false, Some expectedVersion when existing.MetadataVersion <> expectedVersion ->
            Error(
                graceError
                    correlationId
                    $"Stale ContentBlockMetadata merge rejected. Expected MetadataVersion {expectedVersion}, current MetadataVersion {existing.MetadataVersion}."
            )
        | _ ->
            let expectedRanges = if isNull merge.ExpectedRanges then Array.empty else merge.ExpectedRanges

            match currentMetadata, expectedRanges with
            | None, [||] -> Ok()
            | None, _ -> Error(graceError correlationId $"ContentBlockMetadata does not exist; expected range evidence for {merge.ContentBlockAddress}.")
            | Some existing, expectedRanges ->
                let missingExpectedRange =
                    expectedRanges
                    |> Array.tryFind (fun expectedRange -> not (expectedRangeExists existing expectedRange))

                match missingExpectedRange with
                | Some _ ->
                    Error(graceError correlationId $"ContentBlockMetadata expected range evidence is absent or changed for {merge.ContentBlockAddress}.")
                | None -> Ok()

    let internal createMergedMetadata
        correlationId
        (currentMetadata: ContentBlockMetadata option)
        (merge: MergeContentBlockPhysicalRanges)
        timestamp
        : Result<ContentBlockMetadata, GraceError>
        =
        match validateMergePhysicalRanges correlationId merge with
        | Some error -> Error error
        | None ->
            match validateMergePreconditions correlationId currentMetadata merge with
            | Error error -> Error error
            | Ok () ->
                match currentMetadata with
                | Some existing when existing.StoragePoolId <> merge.StoragePoolId ->
                    Error(
                        graceError
                            correlationId
                            $"ContentBlockMetadata StoragePoolId mismatch. Existing {existing.StoragePoolId}, requested {merge.StoragePoolId}."
                    )
                | Some existing when
                    existing.ContentBlockAddress
                    <> merge.ContentBlockAddress
                    ->
                    Error(
                        graceError
                            correlationId
                            $"ContentBlockMetadata ContentBlockAddress mismatch. Existing {existing.ContentBlockAddress}, requested {merge.ContentBlockAddress}."
                    )
                | Some existing when
                    existing.BlockFormatVersion
                    <> merge.BlockFormatVersion
                    ->
                    Error(
                        graceError
                            correlationId
                            $"ContentBlockMetadata BlockFormatVersion mismatch. Existing {existing.BlockFormatVersion}, requested {merge.BlockFormatVersion}."
                    )
                | Some existing when
                    validateExistingStoragePlacement correlationId existing.StoragePlacement
                    |> Option.isSome
                    ->
                    Error(
                        validateExistingStoragePlacement correlationId existing.StoragePlacement
                        |> Option.get
                    )
                | Some existing when
                    existing.StoragePlacement.StorageAccountName
                    <> merge.StoragePlacement.StorageAccountName
                    ->
                    Error(
                        graceError
                            correlationId
                            $"ContentBlockMetadata StoragePlacement.StorageAccountName mismatch. Existing {existing.StoragePlacement.StorageAccountName}, requested {merge.StoragePlacement.StorageAccountName}."
                    )
                | Some existing when
                    existing.StoragePlacement.StorageContainerName
                    <> merge.StoragePlacement.StorageContainerName
                    ->
                    Error(
                        graceError
                            correlationId
                            $"ContentBlockMetadata StoragePlacement.StorageContainerName mismatch. Existing {existing.StoragePlacement.StorageContainerName}, requested {merge.StoragePlacement.StorageContainerName}."
                    )
                | Some existing when
                    existing.StoragePlacement.ObjectKey
                    <> merge.StoragePlacement.ObjectKey
                    ->
                    Error(
                        graceError
                            correlationId
                            $"ContentBlockMetadata StoragePlacement.ObjectKey mismatch. Existing {existing.StoragePlacement.ObjectKey}, requested {merge.StoragePlacement.ObjectKey}."
                    )
                | _ ->
                    let existingRanges =
                        currentMetadata
                        |> Option.map (fun metadata -> metadata.Ranges)
                        |> Option.defaultValue Array.empty

                    match mergeRanges correlationId merge.IsFinalizeContribution existingRanges merge.Ranges with
                    | Error error -> Error error
                    | Ok ranges ->
                        match activePhysicalBytes correlationId ranges with
                        | Error error -> Error error
                        | Ok activePhysicalBytes ->
                            let totalPhysicalBytes = totalPhysicalBytes ranges

                            if activePhysicalBytes > totalPhysicalBytes then
                                Error(graceError correlationId "ActivePhysicalBytes cannot exceed TotalPhysicalBytes.")
                            else
                                let metadataVersion =
                                    currentMetadata
                                    |> Option.map (fun metadata -> metadata.MetadataVersion + 1L)
                                    |> Option.defaultValue 1L

                                let metadata: ContentBlockMetadata =
                                    {
                                        Class = nameof ContentBlockMetadata
                                        StoragePoolId = merge.StoragePoolId
                                        ContentBlockAddress = merge.ContentBlockAddress
                                        BlockFormatVersion = merge.BlockFormatVersion
                                        StoragePlacement = merge.StoragePlacement
                                        Ranges = ranges
                                        TotalPhysicalBytes = totalPhysicalBytes
                                        ActivePhysicalBytes = activePhysicalBytes
                                        MetadataVersion = metadataVersion
                                        UpdatedAt = timestamp
                                    }

                                Ok metadata

    let private stampMetadata (metadata: ContentBlockMetadata) nextVersion timestamp = { metadata with MetadataVersion = nextVersion; UpdatedAt = timestamp }

    let private okDecision metadata operationId events wasReplay message =
        Ok { Metadata = metadata; OperationId = operationId; Events = events; WasIdempotentReplay = wasReplay; Message = message }

    let decideCommand
        (events: seq<ContentBlockMetadataEvent>)
        (current: ContentBlockMetadataDto)
        (command: ContentBlockMetadataCommand)
        (eventMetadata: EventMetadata)
        =
        let operationId = operationId command

        if String.IsNullOrWhiteSpace operationId then
            Error(graceError eventMetadata.CorrelationId "ContentBlockMetadata command requires a non-empty operation id.")
        elif hasAppliedOperationId events operationId then
            match current.Metadata with
            | Some metadata -> okDecision metadata operationId [] true "ContentBlockMetadata command replayed."
            | None -> Error(graceError eventMetadata.CorrelationId "ContentBlockMetadata operation was replayed but no metadata state is available.")
        else
            match command with
            | ContentBlockMetadataCommand.ReplaceWholeRecord replace ->
                match validateMetadata eventMetadata.CorrelationId replace.Metadata with
                | Some error -> Error error
                | None ->
                    match validateStoragePlacement eventMetadata.CorrelationId replace.Metadata.StoragePlacement with
                    | Some error -> Error error
                    | None ->
                        match current.Metadata, replace.ExpectedMetadataVersion with
                        | None, Some expectedVersion ->
                            Error(graceError eventMetadata.CorrelationId $"ContentBlockMetadata does not exist; expected MetadataVersion {expectedVersion}.")
                        | Some _, None ->
                            Error(graceError eventMetadata.CorrelationId "ExpectedMetadataVersion is required when replacing existing ContentBlockMetadata.")
                        | Some existing, Some expectedVersion when existing.MetadataVersion <> expectedVersion ->
                            Error(
                                graceError
                                    eventMetadata.CorrelationId
                                    $"Stale ContentBlockMetadata update rejected. Expected MetadataVersion {expectedVersion}, current MetadataVersion {existing.MetadataVersion}."
                            )
                        | currentState, _ ->
                            let nextVersion =
                                currentState
                                |> Option.map (fun metadata -> metadata.MetadataVersion + 1L)
                                |> Option.defaultValue 1L

                            let metadata = stampMetadata replace.Metadata nextVersion eventMetadata.Timestamp

                            let events =
                                [
                                    { Event = ContentBlockMetadataEventType.WholeRecordReplaced(operationId, metadata); Metadata = eventMetadata }
                                ]

                            okDecision metadata operationId events false "ContentBlockMetadata whole record replaced."
            | ContentBlockMetadataCommand.MergePhysicalRanges merge ->
                match createMergedMetadata eventMetadata.CorrelationId current.Metadata merge eventMetadata.Timestamp with
                | Error error -> Error error
                | Ok metadata ->
                    let events =
                        [
                            { Event = ContentBlockMetadataEventType.PhysicalRangesMerged(operationId, metadata); Metadata = eventMetadata }
                        ]

                    okDecision metadata operationId events false "ContentBlockMetadata physical ranges merged."
            | ContentBlockMetadataCommand.CompactPhysicalRanges compact ->
                match createCompactedMetadata eventMetadata.CorrelationId current compact eventMetadata.Timestamp with
                | Error error -> Error error
                | Ok metadata ->
                    let events =
                        [
                            { Event = ContentBlockMetadataEventType.PhysicalRangesCompacted(operationId, metadata); Metadata = eventMetadata }
                        ]

                    okDecision metadata operationId events false "ContentBlockMetadata physical ranges compacted."
            | ContentBlockMetadataCommand.SetCompactionChurnState setChurnState ->
                if isNull (box setChurnState) then
                    Error(graceError eventMetadata.CorrelationId "Compaction ChurnState payload is required.")
                elif isNull (box setChurnState.ChurnState) then
                    Error(graceError eventMetadata.CorrelationId "Compaction ChurnState is required.")
                else
                    match current.Metadata with
                    | None ->
                        Error(graceError eventMetadata.CorrelationId "ContentBlockMetadata does not exist; compaction churn state requires current metadata.")
                    | Some metadata ->
                        let events =
                            [
                                {
                                    Event = ContentBlockMetadataEventType.CompactionChurnStateSet(operationId, setChurnState.ChurnState)
                                    Metadata = eventMetadata
                                }
                            ]

                        okDecision metadata operationId events false "ContentBlockMetadata compaction churn state set."

    type ContentBlockMetadataActor
        (
            [<PersistentState(StateName.ContentBlockMetadata, Constants.GraceActorStorage)>] state: IPersistentState<List<ContentBlockMetadataEvent>>
        ) =
        inherit Grain()

        let log = loggerFactory.CreateLogger("ContentBlockMetadata.Actor")
        let mutable metadataDto = ContentBlockMetadataDto.Empty
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            metadataDto <-
                state.State
                |> Seq.fold (fun dto event -> ContentBlockMetadataDto.UpdateDto event dto) ContentBlockMetadataDto.Empty

            Task.CompletedTask

        member private this.ApplyEvents(events: ContentBlockMetadataEvent list) =
            task {
                for metadataEvent in events do
                    state.State.Add(metadataEvent)

                do! state.WriteStateAsync()

                metadataDto <- applyEvents events metadataDto
            }

        member private this.HandleCommand (command: ContentBlockMetadataCommand) (eventMetadata: EventMetadata) =
            task {
                this.correlationId <- eventMetadata.CorrelationId
                RequestContext.Set(Constants.CurrentCommandProperty, commandName command)

                match decideCommand state.State metadataDto command eventMetadata with
                | Ok decision ->
                    if not decision.Events.IsEmpty then do! this.ApplyEvents decision.Events

                    let dedupeIndexActor = DedupeIndexActor.CreateActorProxy eventMetadata.CorrelationId

                    do! dedupeIndexActor.WriteAfterAuthoritativeMetadata decision.Metadata eventMetadata.CorrelationId :> Task

                    let returnValue =
                        (GraceReturnValue.Create decision eventMetadata.CorrelationId)
                            .enhance(nameof StoragePoolId, decision.Metadata.StoragePoolId)
                            .enhance(nameof ContentBlockAddress, decision.Metadata.ContentBlockAddress)
                            .enhance (nameof MetadataVersion, decision.Metadata.MetadataVersion)

                    return Ok returnValue
                | Error error ->
                    log.LogWarning(
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Rejected ContentBlockMetadata command {Command}. Error: {Error}",
                        getCurrentInstantExtended (),
                        getMachineName,
                        eventMetadata.CorrelationId,
                        commandName command,
                        error.Error
                    )

                    return Error error
            }

        interface IContentBlockMetadataActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId

                metadataDto.Metadata.IsSome |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId

                metadataDto.Metadata |> returnTask

            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                (state.State :> IReadOnlyList<ContentBlockMetadataEvent>)
                |> returnTask

            member this.GetRangePresence query correlationId =
                this.correlationId <- correlationId

                match metadataDto.Metadata with
                | Some metadata -> Grace.Types.ContentBlockMetadata.rangePresence metadata query
                | None -> ContentBlockRangePresence.Absent
                |> returnTask

            member this.Handle command eventMetadata = this.HandleCommand command eventMetadata

            member this.MergePhysicalRanges merge eventMetadata = this.HandleCommand (ContentBlockMetadataCommand.MergePhysicalRanges merge) eventMetadata
