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

    let operationId command =
        match command with
        | ContentBlockMetadataCommand.ReplaceWholeRecord replace -> replace.OperationId

    let private eventOperationId metadataEvent =
        match metadataEvent.Event with
        | ContentBlockMetadataEventType.WholeRecordReplaced (operationId, _) -> operationId

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
