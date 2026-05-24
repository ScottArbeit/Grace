namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.ContentBlockMetadata
open Grace.Types.Types
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

    let operationId command =
        match command with
        | ContentBlockMetadataCommand.ReplaceWholeRecord replace -> replace.OperationId
        | ContentBlockMetadataCommand.MergePhysicalRanges merge -> merge.OperationId

    let private eventOperationId metadataEvent =
        match metadataEvent.Event with
        | ContentBlockMetadataEventType.WholeRecordReplaced (operationId, _) -> operationId
        | ContentBlockMetadataEventType.PhysicalRangesMerged (operationId, _) -> operationId

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
        if String.IsNullOrWhiteSpace placement.ObjectKey then
            Some(graceError correlationId "StoragePlacement.ObjectKey is required.")
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
                merge.Ranges
                |> Array.tryPick (validateRange correlationId)

    let private physicalEnd (range: ContentBlockMetadataRange) = range.PhysicalOffset + range.PhysicalLength

    let private totalPhysicalBytes ranges =
        ranges
        |> Array.map physicalEnd
        |> Array.append [| 0L |]
        |> Array.max

    let private activePhysicalBytes ranges =
        ranges
        |> Array.filter (fun range -> range.ActiveManifestCount > 0)
        |> Array.sumBy (fun range -> range.PhysicalLength)

    let private rangeKey (range: ContentBlockMetadataRange) = $"{range.OrdinalStart}:{range.OrdinalCount}"

    let private mergeRanges correlationId existingRanges incomingRanges =
        let merged = Dictionary<string, ContentBlockMetadataRange>()
        let mutable error = None

        let addRange preserveActiveCount range =
            let key = rangeKey range

            if merged.ContainsKey key then
                let existing = merged[key]

                if existing.PhysicalOffset <> range.PhysicalOffset
                   || existing.PhysicalLength <> range.PhysicalLength then
                    error <-
                        Some(
                            graceError
                                correlationId
                                $"Conflicting physical metadata range for OrdinalStart {range.OrdinalStart}, OrdinalCount {range.OrdinalCount}."
                        )
                else
                    let activeManifestCount =
                        if preserveActiveCount then
                            max existing.ActiveManifestCount range.ActiveManifestCount
                        else
                            range.ActiveManifestCount

                    merged[key] <- { existing with ActiveManifestCount = activeManifestCount }
            else
                merged[key] <- range

        existingRanges |> Array.iter (addRange true)

        incomingRanges |> Array.iter (addRange true)

        match error with
        | Some error -> Error error
        | None ->
            merged.Values
            |> Seq.sortBy (fun range -> range.OrdinalStart, range.OrdinalCount)
            |> Seq.toArray
            |> Ok

    let private createMergedMetadata
        correlationId
        (currentMetadata: ContentBlockMetadata option)
        (merge: MergeContentBlockPhysicalRanges)
        timestamp
        : Result<ContentBlockMetadata, GraceError>
        =
        match validateMergePhysicalRanges correlationId merge with
        | Some error -> Error error
        | None ->
            match currentMetadata with
            | Some existing when existing.StoragePoolId <> merge.StoragePoolId ->
                Error(
                    graceError correlationId $"ContentBlockMetadata StoragePoolId mismatch. Existing {existing.StoragePoolId}, requested {merge.StoragePoolId}."
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
            | _ ->
                let existingRanges =
                    currentMetadata
                    |> Option.map (fun metadata -> metadata.Ranges)
                    |> Option.defaultValue Array.empty

                match mergeRanges correlationId existingRanges merge.Ranges with
                | Error error -> Error error
                | Ok ranges ->
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
                            TotalPhysicalBytes = totalPhysicalBytes ranges
                            ActivePhysicalBytes = activePhysicalBytes ranges
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

            member this.Handle command eventMetadata =
                task {
                    this.correlationId <- eventMetadata.CorrelationId
                    RequestContext.Set(Constants.CurrentCommandProperty, commandName command)

                    match decideCommand state.State metadataDto command eventMetadata with
                    | Ok decision ->
                        if not decision.Events.IsEmpty then do! this.ApplyEvents decision.Events

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
