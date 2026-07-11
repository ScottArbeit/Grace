namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.ContentBlockMetadata
open Grace.Types.Reminder
open Grace.Types.Common
open Grace.Types.UploadSession
open Grace.Types.BillingAccount
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

/// Groups Orleans actor helpers for upload session keys, proxies, state, or workflow transitions.
module UploadSession =

    let private actorName = ActorName.UploadSession

    /// Maps a UploadSession command case to the operation name used in idempotency and diagnostics.
    let commandName command =
        match command with
        | UploadSessionCommand.Start _ -> "Start"
        | UploadSessionCommand.IssueDedupeDiscovery _ -> "IssueDedupeDiscovery"
        | UploadSessionCommand.RegisterBlockUploadIntent _ -> "RegisterBlockUploadIntent"
        | UploadSessionCommand.ConfirmBlockUploaded _ -> "ConfirmBlockUploaded"
        | UploadSessionCommand.ClaimReuseRanges _ -> "ClaimReuseRanges"
        | UploadSessionCommand.FinalizeManifest _ -> "FinalizeManifest"
        | UploadSessionCommand.Abandon _ -> "Abandon"
        | UploadSessionCommand.Expire _ -> "Expire"
        | UploadSessionCommand.DeletePhysicalState _ -> "DeletePhysicalState"

    /// Extracts the client operation id that lets command retries match previously emitted events.
    let operationId command =
        match command with
        | UploadSessionCommand.Start start -> start.OperationId
        | UploadSessionCommand.IssueDedupeDiscovery discovery when isNull (box discovery) -> String.Empty
        | UploadSessionCommand.IssueDedupeDiscovery discovery -> discovery.OperationId
        | UploadSessionCommand.RegisterBlockUploadIntent intent -> intent.OperationId
        | UploadSessionCommand.ConfirmBlockUploaded confirmation -> confirmation.OperationId
        | UploadSessionCommand.ClaimReuseRanges claim when isNull (box claim) -> String.Empty
        | UploadSessionCommand.ClaimReuseRanges claim -> claim.OperationId
        | UploadSessionCommand.FinalizeManifest finalize when isNull (box finalize) -> String.Empty
        | UploadSessionCommand.FinalizeManifest finalize -> finalize.OperationId
        | UploadSessionCommand.Abandon operationId -> operationId
        | UploadSessionCommand.Expire operationId -> operationId
        | UploadSessionCommand.DeletePhysicalState operationId -> operationId

    /// Builds cleanup operation id data needed by the UploadSession actor.
    let createCleanupOperationId operationId = $"{operationId}:cleanup"

    /// Builds cleanup reminder state data needed by the UploadSession actor.
    let createCleanupReminderState uploadSessionId repositoryId operationId correlationId =
        {
            UploadSessionId = uploadSessionId
            RepositoryId = repositoryId
            OperationId = createCleanupOperationId operationId
            DeleteReason = "UploadSession coordination state retention window elapsed."
            CorrelationId = correlationId
        }

    /// Coordinates event operation id logic for the UploadSession actor.
    let private eventOperationId uploadSessionEvent =
        match uploadSessionEvent.Event with
        | UploadSessionEventType.Started start -> start.OperationId
        | UploadSessionEventType.Abandoned operationId -> operationId
        | UploadSessionEventType.Expired operationId -> operationId
        | UploadSessionEventType.Finalized (operationId, _) -> operationId
        | UploadSessionEventType.CleanupReminderScheduled _ -> String.Empty
        | UploadSessionEventType.PhysicalStateDeleted operationId -> operationId
        | UploadSessionEventType.BlockUploadIntentRegistered (operationId, _) -> operationId
        | UploadSessionEventType.BlockUploadConfirmed (operationId, _) -> operationId
        | UploadSessionEventType.DedupeDiscoveryIssued (operationId, _) -> operationId
        | UploadSessionEventType.ReuseRangesClaimed (operationId, _) -> operationId

    /// Checks whether the operation id has already produced a persisted event.
    let private hasAppliedOperationId (events: seq<UploadSessionEvent>) operationId =
        events
        |> Seq.exists (fun uploadSessionEvent -> eventOperationId uploadSessionEvent = operationId)

    /// Checks whether the block-upload confirmation operation id was already persisted.
    let private hasAppliedBlockUploadConfirmedOperationId (events: seq<UploadSessionEvent>) operationId =
        events
        |> Seq.exists (fun uploadSessionEvent ->
            match uploadSessionEvent.Event with
            | UploadSessionEventType.BlockUploadConfirmed (eventOperationId, _) -> eventOperationId = operationId
            | _ -> false)

    /// Applies events changes to the UploadSession actor state.
    let private applyEvents (events: UploadSessionEvent list) (session: UploadSessionDto) =
        events
        |> List.fold (fun current event -> UploadSessionDto.UpdateDto event current) session

    /// Coordinates compact events for physical state cleanup logic for the UploadSession actor.
    let compactEventsForPhysicalStateCleanup (events: seq<UploadSessionEvent>) =
        events
        |> Seq.filter (fun uploadSessionEvent ->
            match uploadSessionEvent.Event with
            | UploadSessionEventType.Started _
            | UploadSessionEventType.Abandoned _
            | UploadSessionEventType.Expired _
            | UploadSessionEventType.Finalized _
            | UploadSessionEventType.CleanupReminderScheduled _
            | UploadSessionEventType.PhysicalStateDeleted _ -> true
            | UploadSessionEventType.BlockUploadIntentRegistered _
            | UploadSessionEventType.BlockUploadConfirmed _
            | UploadSessionEventType.DedupeDiscoveryIssued _
            | UploadSessionEventType.ReuseRangesClaimed _ -> false)
        |> Seq.toList

    /// Coordinates ok decision logic for the UploadSession actor.
    let private okDecision (session: UploadSessionDto) operationId (events: UploadSessionEvent list) wasReplay message =
        Ok { Session = applyEvents events session; OperationId = operationId; Events = events; WasIdempotentReplay = wasReplay; Message = message }

    /// Coordinates grace error logic for the UploadSession actor.
    let private graceError correlationId message = GraceError.Create message correlationId

    /// Coordinates cleanup events logic for the UploadSession actor.
    let private cleanupEvents (session: UploadSessionDto) operationId (metadata: EventMetadata) =
        let cleanupOperationId = createCleanupOperationId operationId
        let reminderTime = metadata.Timestamp.Plus(DefaultPhysicalDeletionReminderDuration)

        [
            { Event = UploadSessionEventType.CleanupReminderScheduled(cleanupOperationId, reminderTime); Metadata = metadata }
        ]

    /// Coordinates should schedule finalize cleanup reminder logic for the UploadSession actor.
    let internal shouldScheduleFinalizeCleanupReminder (session: UploadSessionDto) (finalize: FinalizeManifest) =
        if
            isNull (box finalize)
            || session.LifecycleState = UploadSessionLifecycleState.StateDeleted
        then
            false
        else
            let expectedCleanupOperationId = createCleanupOperationId finalize.OperationId

            match session.CleanupReminderOperationId with
            | None -> true
            | Some cleanupOperationId when
                session.LifecycleState = UploadSessionLifecycleState.RetentionPending
                && cleanupOperationId = expectedCleanupOperationId
                ->
                true
            | Some _ -> false

    /// Coordinates split finalize events logic for the UploadSession actor.
    let private splitFinalizeEvents (events: UploadSessionEvent list) =
        let finalizedEvents = ResizeArray<UploadSessionEvent>()
        let retentionEvents = ResizeArray<UploadSessionEvent>()

        for uploadSessionEvent in events do
            match uploadSessionEvent.Event with
            | UploadSessionEventType.Finalized _ -> finalizedEvents.Add(uploadSessionEvent)
            | _ -> retentionEvents.Add(uploadSessionEvent)

        finalizedEvents |> Seq.toList, retentionEvents |> Seq.toList

    /// Coordinates terminal mutation error logic for the UploadSession actor.
    let private terminalMutationError (session: UploadSessionDto) command correlationId =
        match session.LifecycleState with
        | UploadSessionLifecycleState.Finalized -> Some(graceError correlationId $"UploadSession is finalized and cannot be changed by {commandName command}.")
        | UploadSessionLifecycleState.RetentionPending ->
            Some(graceError correlationId $"UploadSession is waiting for cleanup and cannot be changed by {commandName command}.")
        | UploadSessionLifecycleState.StateDeleted ->
            Some(graceError correlationId $"UploadSession physical state has been deleted and cannot be changed by {commandName command}.")
        | _ -> None

    /// Coordinates require started session logic for the UploadSession actor.
    let private requireStartedSession (session: UploadSessionDto) command correlationId =
        match terminalMutationError session command correlationId with
        | Some error -> Some error
        | None ->
            match session.LifecycleState with
            | UploadSessionLifecycleState.Started
            | UploadSessionLifecycleState.Discovering
            | UploadSessionLifecycleState.UploadingBlocks
            | UploadSessionLifecycleState.ClaimingRanges -> None
            | UploadSessionLifecycleState.NotStarted -> Some(graceError correlationId $"UploadSession must be started before {commandName command}.")
            | _ -> Some(graceError correlationId $"UploadSession cannot {commandName command} from {session.LifecycleState}.")

    /// Validates intent before the operation continues.
    let private validateIntent correlationId (intent: RegisterBlockUploadIntent) =
        if String.IsNullOrWhiteSpace intent.ContentBlockAddress then
            Some(graceError correlationId "ContentBlockAddress is required.")
        elif intent.LogicalOffset < 0L then
            Some(graceError correlationId "LogicalOffset must be zero or greater.")
        elif intent.LogicalLength <= 0L then
            Some(graceError correlationId "LogicalLength must be greater than zero.")
        elif intent.ExpectedPayloadLength <= 0L then
            Some(graceError correlationId "ExpectedPayloadLength must be greater than zero.")
        else
            None

    /// Validates storage placement before the operation continues.
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

    /// Coordinates normalize hints logic for the UploadSession actor.
    let private normalizeHints (hints: ContentBlockReuseRangeHint array) = if isNull hints then Array.empty else hints

    /// Validates reuse hint before the operation continues.
    let private validateReuseHint correlationId (hint: ContentBlockReuseRangeHint) =
        if isNull (box hint) then
            Some(graceError correlationId "Reuse range hint is required.")
        elif String.IsNullOrWhiteSpace hint.StoragePoolId then
            Some(graceError correlationId "Reuse range hint StoragePoolId is required.")
        elif String.IsNullOrWhiteSpace hint.ContentBlockAddress then
            Some(graceError correlationId "Reuse range hint ContentBlockAddress is required.")
        elif hint.OrdinalStart < 0 then
            Some(graceError correlationId "Reuse range hint OrdinalStart must be zero or greater.")
        elif hint.OrdinalCount <= 0 then
            Some(graceError correlationId "Reuse range hint OrdinalCount must be greater than zero.")
        elif hint.MetadataVersion <= 0L then
            Some(graceError correlationId "Reuse range hint MetadataVersion must be greater than zero.")
        else
            None

    /// Coordinates same hint logic for the UploadSession actor.
    let private sameHint (left: ContentBlockReuseRangeHint) (right: ContentBlockReuseRangeHint) =
        left.StoragePoolId = right.StoragePoolId
        && left.ContentBlockAddress = right.ContentBlockAddress
        && left.OrdinalStart = right.OrdinalStart
        && left.OrdinalCount = right.OrdinalCount
        && left.MetadataVersion = right.MetadataVersion

    /// Coordinates claimed range key logic for the UploadSession actor.
    let private claimedRangeKey (range: ClaimedReuseRange) =
        $"{range.StoragePoolId}|{range.ContentBlockAddress}|{range.OrdinalStart}|{range.OrdinalCount}|{range.MetadataVersion}"

    /// Checks whether issue dedupe discovery is true for the UploadSession actor state.
    let private issueDedupeDiscovery (session: UploadSessionDto) (discovery: IssueDedupeDiscovery) (metadata: EventMetadata) =
        if discovery.MinimumReuseRunLength <= 0 then
            Error(graceError metadata.CorrelationId "MinimumReuseRunLength must be greater than zero.")
        elif discovery.ExpiresAt <= metadata.Timestamp then
            Error(graceError metadata.CorrelationId "Dedupe discovery ExpiresAt must be later than the issue timestamp.")
        else
            let hints = normalizeHints discovery.Hints

            match
                hints
                |> Array.tryPick (validateReuseHint metadata.CorrelationId)
                with
            | Some error -> Error error
            | None ->
                let snapshot: DedupeDiscoverySnapshot =
                    {
                        OperationId = discovery.OperationId
                        ExpiresAt = discovery.ExpiresAt
                        MinimumReuseRunLength = discovery.MinimumReuseRunLength
                        Hints = hints
                    }

                let events =
                    [
                        { Event = UploadSessionEventType.DedupeDiscoveryIssued(discovery.OperationId, snapshot); Metadata = metadata }
                    ]

                okDecision session discovery.OperationId events false "Dedupe discovery issued."

    /// Coordinates claim reuse range logic for the UploadSession actor.
    let private claimReuseRange correlationId timestamp (discovery: DedupeDiscoverySnapshot) (claimRange: ClaimReuseRange) =
        if isNull (box claimRange) then
            Error(graceError correlationId "Reuse range claim is required.")
        else
            match validateReuseHint correlationId claimRange.Hint with
            | Some error -> Error error
            | None ->
                let hint = claimRange.Hint

                if hint.OrdinalCount < discovery.MinimumReuseRunLength then
                    Error(graceError correlationId $"Reuse range is shorter than the minimum reuse run length {discovery.MinimumReuseRunLength}.")
                elif discovery.Hints
                     |> Array.exists (sameHint hint)
                     |> not then
                    Error(graceError correlationId "Reuse range hint was not issued by the active discovery.")
                elif isNull (box claimRange.Metadata) then
                    Error(graceError correlationId "Authoritative ContentBlockMetadata is required.")
                elif claimRange.Metadata.StoragePoolId
                     <> hint.StoragePoolId then
                    Error(graceError correlationId "Authoritative ContentBlockMetadata StoragePoolId does not match the reuse range hint.")
                elif claimRange.Metadata.ContentBlockAddress
                     <> hint.ContentBlockAddress then
                    Error(graceError correlationId "Authoritative ContentBlockMetadata ContentBlockAddress does not match the reuse range hint.")
                elif claimRange.Metadata.MetadataVersion
                     <> hint.MetadataVersion then
                    Error(
                        graceError
                            correlationId
                            $"stale reuse range hint rejected. Hinted MetadataVersion {hint.MetadataVersion}, authoritative MetadataVersion {claimRange.Metadata.MetadataVersion}."
                    )
                elif isNull claimRange.Metadata.Ranges then
                    Error(graceError correlationId "Authoritative ContentBlockMetadata range is absent; reuse range cannot be claimed.")
                else
                    match
                        Grace.Types.ContentBlockMetadata.tryFindRange claimRange.Metadata { OrdinalStart = hint.OrdinalStart; OrdinalCount = hint.OrdinalCount }
                        with
                    | None -> Error(graceError correlationId "Authoritative ContentBlockMetadata range is absent; reuse range cannot be claimed.")
                    | Some physicalRange ->
                        Ok
                            {
                                StoragePoolId = hint.StoragePoolId
                                ContentBlockAddress = hint.ContentBlockAddress
                                OrdinalStart = hint.OrdinalStart
                                OrdinalCount = hint.OrdinalCount
                                PhysicalOffset = physicalRange.PhysicalOffset
                                PhysicalLength = physicalRange.PhysicalLength
                                MetadataVersion = claimRange.Metadata.MetadataVersion
                                ClaimedAt = timestamp
                            }

    /// Coordinates claim reuse ranges logic for the UploadSession actor.
    let private claimReuseRanges (session: UploadSessionDto) (claim: ClaimReuseRanges) (metadata: EventMetadata) =
        match session.DedupeDiscovery with
        | None -> Error(graceError metadata.CorrelationId "Dedupe discovery must be issued before reuse ranges can be claimed.")
        | Some discovery when
            discovery.OperationId
            <> claim.DiscoveryOperationId
            ->
            Error(graceError metadata.CorrelationId "ClaimReuseRanges DiscoveryOperationId does not match the active discovery.")
        | Some discovery when metadata.Timestamp >= discovery.ExpiresAt ->
            Error(graceError metadata.CorrelationId "Dedupe discovery has expired; reuse ranges cannot be claimed.")
        | Some discovery ->
            if isNull claim.Ranges || claim.Ranges.Length = 0 then
                Error(graceError metadata.CorrelationId "At least one reuse range claim is required.")
            else
                let claimedRanges = ResizeArray<ClaimedReuseRange>()

                let claimedRangeKeys =
                    session.ClaimedReuseRanges
                    |> Array.map claimedRangeKey
                    |> HashSet<string>

                let mutable error = None
                let mutable index = 0

                while error.IsNone && index < claim.Ranges.Length do
                    match claimReuseRange metadata.CorrelationId metadata.Timestamp discovery claim.Ranges[index] with
                    | Ok claimedRange when claimedRangeKeys.Add(claimedRangeKey claimedRange) ->
                        claimedRanges.Add(claimedRange)
                        index <- index + 1
                    | Ok _ -> error <- Some(graceError metadata.CorrelationId "Reuse range hint has already been claimed.")
                    | Error claimError -> error <- Some claimError

                match error with
                | Some claimError -> Error claimError
                | None ->
                    let events =
                        [
                            { Event = UploadSessionEventType.ReuseRangesClaimed(claim.OperationId, claimedRanges.ToArray()); Metadata = metadata }
                        ]

                    okDecision session claim.OperationId events false "Reuse ranges claimed."

    /// Coordinates find block intents logic for the UploadSession actor.
    let private findBlockIntents (session: UploadSessionDto) contentBlockAddress =
        session.BlockUploadIntents
        |> Array.filter (fun intent -> intent.ContentBlockAddress = contentBlockAddress)

    /// Coordinates content block error logic for the UploadSession actor.
    let private contentBlockError error = $"ContentBlock payload is invalid: {error}."

    /// Coordinates physical ranges from decoded block logic for the UploadSession actor.
    let private physicalRangesFromDecodedBlock (decodedBlock: ContentBlockFormat.DecodedContentBlock) =
        decodedBlock.Chunks
        |> Array.mapi (fun index chunk ->
            { OrdinalStart = index; OrdinalCount = 1; ActiveManifestCount = 0; PhysicalOffset = chunk.PhysicalOffset; PhysicalLength = int64 chunk.Length })

    /// Coordinates decoded logical length logic for the UploadSession actor.
    let private decodedLogicalLength (decodedBlock: ContentBlockFormat.DecodedContentBlock) =
        decodedBlock.Chunks
        |> Array.sumBy (fun chunk -> int64 chunk.Length)

    /// Coordinates finalized manifest content block addresses logic for the UploadSession actor.
    let private finalizedManifestContentBlockAddresses (manifest: FileManifest) =
        let addresses = HashSet<ContentBlockAddress>()
        let orderedAddresses = ResizeArray<ContentBlockAddress>()

        if
            not (isNull (box manifest))
            && not (isNull manifest.Blocks)
        then
            for block in manifest.Blocks do
                if
                    not (isNull (box block))
                    && addresses.Add(block.Address)
                then
                    orderedAddresses.Add(block.Address)

        orderedAddresses.ToArray()

    /// Coordinates manifest block addresses logic for the UploadSession actor.
    let private manifestBlockAddresses (manifest: FileManifest) =
        finalizedManifestContentBlockAddresses manifest
        |> HashSet<ContentBlockAddress>

    /// Coordinates finalized manifest contribution ranges logic for the UploadSession actor.
    let finalizedManifestContributionRanges (ranges: ContentBlockMetadataRange array) =
        ranges
        |> Array.map (fun range -> { range with ActiveManifestCount = 1 })

    /// Coordinates block was uploaded logic for the UploadSession actor.
    let private blockWasUploaded (session: UploadSessionDto) (block: ContentBlock) =
        session.ConfirmedBlockUploads
        |> Array.exists (fun confirmedBlock -> confirmedBlock.ContentBlockAddress = block.Address)
        && session.BlockUploadIntents
           |> Array.exists (fun intent ->
               intent.ContentBlockAddress = block.Address
               && intent.LogicalOffset = block.Offset
               && intent.LogicalLength = block.Size)

    /// Coordinates manifest blocks requiring claimed metadata logic for the UploadSession actor.
    let private manifestBlocksRequiringClaimedMetadata (session: UploadSessionDto) (manifest: FileManifest) =
        if isNull (box manifest) || isNull manifest.Blocks then
            Array.empty
        else
            manifest.Blocks
            |> Seq.filter (fun block ->
                not (isNull (box block))
                && not (blockWasUploaded session block))
            |> Seq.toArray

    /// Validates authoritative storage placement before the operation continues.
    let private validateAuthoritativeStoragePlacement correlationId (placement: ContentBlockStoragePlacement) =
        match validateStoragePlacement correlationId placement with
        | Some error -> Error error
        | None -> Ok()

    /// Coordinates claimed metadata content block key logic for the UploadSession actor.
    let private claimedMetadataContentBlockKey storagePoolId contentBlockAddress = $"{storagePoolId}|{contentBlockAddress}"

    /// Coordinates claimed metadata by content block address logic for the UploadSession actor.
    let private claimedMetadataByContentBlockAddress (claimedMetadata: ContentBlockMetadata array) =
        let metadata = Dictionary<string, ResizeArray<ContentBlockMetadata>>()

        if not (isNull claimedMetadata) then
            for item in claimedMetadata do
                if not (isNull (box item))
                   && not (String.IsNullOrWhiteSpace item.StoragePoolId)
                   && not (String.IsNullOrWhiteSpace item.ContentBlockAddress)
                   && item.MetadataVersion > 0L then
                    let key = claimedMetadataContentBlockKey item.StoragePoolId item.ContentBlockAddress

                    match metadata.TryGetValue key with
                    | true, candidates -> candidates.Add item
                    | false, _ ->
                        let candidates = ResizeArray<ContentBlockMetadata>()
                        candidates.Add item
                        metadata[key] <- candidates

        metadata

    /// Coordinates matching claimed metadata range logic for the UploadSession actor.
    let private matchingClaimedMetadataRange (metadata: ContentBlockMetadata) (claimedRange: ClaimedReuseRange) =
        let query = { OrdinalStart = claimedRange.OrdinalStart; OrdinalCount = claimedRange.OrdinalCount }

        Grace.Types.ContentBlockMetadata.findRanges metadata query
        |> Array.tryFind (fun range ->
            range.PhysicalOffset = claimedRange.PhysicalOffset
            && range.PhysicalLength = claimedRange.PhysicalLength)
        |> Option.orElseWith (fun () ->
            Grace.Types.ContentBlockMetadata.findRanges metadata query
            |> Array.tryFind (fun range ->
                range.PhysicalLength = claimedRange.PhysicalLength
                && range.ActiveManifestCount > 0))

    /// Attempts to select active contiguous range chain and returns no value when the required invariant is not met.
    let private trySelectActiveContiguousRangeChain ordinalStart ordinalCount physicalOffset physicalLength (ranges: ContentBlockMetadataRange array) =
        if ordinalCount <= 0
           || physicalLength <= 0L
           || isNull ranges then
            None
        else
            let finalOrdinal = ordinalStart + ordinalCount

            /// Calculates the exclusive logical ordinal end for a metadata range.
            let rangeEnd (range: ContentBlockMetadataRange) = range.OrdinalStart + range.OrdinalCount

            /// Attempts to project covering range and returns no value when the required invariant is not met.
            let tryProjectCoveringRange () =
                ranges
                |> Array.filter (fun range ->
                    range.ActiveManifestCount > 0
                    && range.OrdinalStart >= 0
                    && range.OrdinalStart <= ordinalStart
                    && range.OrdinalCount > 0
                    && range.OrdinalCount
                       <= Int32.MaxValue - range.OrdinalStart
                    && rangeEnd range >= finalOrdinal
                    && range.PhysicalOffset >= 0L
                    && range.PhysicalLength > 0L
                    && range.PhysicalLength % int64 range.OrdinalCount = 0L)
                |> Array.sortBy (fun range -> range.OrdinalStart, range.OrdinalCount, range.PhysicalOffset, range.PhysicalLength)
                |> Array.tryPick (fun range ->
                    let bytesPerOrdinal = range.PhysicalLength / int64 range.OrdinalCount
                    let projectedPhysicalLength = int64 ordinalCount * bytesPerOrdinal

                    if projectedPhysicalLength <> physicalLength then
                        None
                    else
                        let projectedPhysicalOffset =
                            range.PhysicalOffset
                            + (int64 (ordinalStart - range.OrdinalStart)
                               * bytesPerOrdinal)

                        match physicalOffset with
                        | Some expectedPhysicalOffset when projectedPhysicalOffset <> expectedPhysicalOffset -> None
                        | _ ->
                            Some [| { range with
                                        OrdinalStart = ordinalStart
                                        OrdinalCount = ordinalCount
                                        PhysicalOffset = projectedPhysicalOffset
                                        PhysicalLength = projectedPhysicalLength
                                    } |])

            let candidates =
                ranges
                |> Array.filter (fun range ->
                    range.OrdinalStart >= ordinalStart
                    && range.OrdinalStart < finalOrdinal
                    && range.OrdinalCount > 0
                    && range.PhysicalLength > 0L
                    && range.PhysicalLength <= physicalLength)
                |> Array.sortBy (fun range -> range.OrdinalStart, range.PhysicalOffset, range.PhysicalLength)

            /// Selects select chain data for the UploadSession actor workflow.
            let rec selectChain nextOrdinal selectedLength expectedPhysicalOffset selected =
                if nextOrdinal = finalOrdinal
                   && selectedLength = physicalLength then
                    selected |> List.rev |> List.toArray |> Some
                elif nextOrdinal >= finalOrdinal
                     || selectedLength >= physicalLength then
                    None
                else
                    candidates
                    |> Array.filter (fun range ->
                        range.OrdinalStart = nextOrdinal
                        && selectedLength + range.PhysicalLength
                           <= physicalLength
                        && match expectedPhysicalOffset with
                           | Some physicalOffset -> range.PhysicalOffset = physicalOffset
                           | None -> true)
                    |> Array.tryPick (fun range ->
                        selectChain
                            (range.OrdinalStart + range.OrdinalCount)
                            (selectedLength + range.PhysicalLength)
                            (Some(range.PhysicalOffset + range.PhysicalLength))
                            (range :: selected))

            selectChain ordinalStart 0L physicalOffset []
            |> Option.orElseWith tryProjectCoveringRange

    type private ClaimedMetadataRangeEvidence = { ExpectedRanges: ContentBlockMetadataRange array; PhysicalRanges: ContentBlockMetadataRange array }

    /// Coordinates claimed metadata range evidence logic for the UploadSession actor.
    let private claimedMetadataRangeEvidence (metadata: ContentBlockMetadata) (claimedRange: ClaimedReuseRange) =
        let query = { OrdinalStart = claimedRange.OrdinalStart; OrdinalCount = claimedRange.OrdinalCount }

        let expectedRanges =
            Grace.Types.ContentBlockMetadata.findRangeEvidence metadata query
            |> Array.sortBy (fun range -> range.OrdinalStart, range.PhysicalOffset, range.PhysicalLength)

        if expectedRanges.Length = 0 then
            None
        else
            match
                trySelectActiveContiguousRangeChain
                    claimedRange.OrdinalStart
                    claimedRange.OrdinalCount
                    (Some claimedRange.PhysicalOffset)
                    claimedRange.PhysicalLength
                    expectedRanges
                with
            | Some activeRanges -> Some { ExpectedRanges = expectedRanges; PhysicalRanges = activeRanges }
            | None ->
                let activeExpectedRanges =
                    expectedRanges
                    |> Array.filter (fun range -> range.ActiveManifestCount > 0)

                match
                    trySelectActiveContiguousRangeChain
                        claimedRange.OrdinalStart
                        claimedRange.OrdinalCount
                        None
                        claimedRange.PhysicalLength
                        activeExpectedRanges
                    with
                | Some activeRanges -> Some { ExpectedRanges = expectedRanges; PhysicalRanges = activeRanges }
                | None -> None

    let private validateClaimedRangeMetadata
        correlationId
        storagePoolId
        (claimedMetadata: Dictionary<string, ResizeArray<ContentBlockMetadata>>)
        (claimedRange: ClaimedReuseRange)
        =
        if claimedRange.StoragePoolId <> storagePoolId then
            Error(graceError correlationId "Claimed reuse range StoragePoolId must match the upload session StoragePoolId before finalization.")
        else
            let key = claimedMetadataContentBlockKey claimedRange.StoragePoolId claimedRange.ContentBlockAddress

            match claimedMetadata.TryGetValue key with
            | false, _ ->
                Error(
                    graceError
                        correlationId
                        $"Authoritative ContentBlockMetadata is required before finalizing claimed reuse range {claimedRange.ContentBlockAddress}."
                )
            | true, metadata ->
                metadata
                |> Seq.sortByDescending (fun candidate -> candidate.MetadataVersion)
                |> Seq.tryPick (fun candidate ->
                    if candidate.StoragePoolId
                       <> claimedRange.StoragePoolId then
                        Some(Error(graceError correlationId "Authoritative ContentBlockMetadata StoragePoolId does not match the claimed reuse range."))
                    elif candidate.ContentBlockAddress
                         <> claimedRange.ContentBlockAddress then
                        Some(Error(graceError correlationId "Authoritative ContentBlockMetadata ContentBlockAddress does not match the claimed reuse range."))
                    elif candidate.MetadataVersion < claimedRange.MetadataVersion then
                        None
                    elif candidate.BlockFormatVersion <= 0s then
                        Some(
                            Error(graceError correlationId "Authoritative ContentBlockMetadata BlockFormatVersion is required before finalizing claimed reuse.")
                        )
                    else
                        match validateAuthoritativeStoragePlacement correlationId candidate.StoragePlacement with
                        | Error error -> Some(Error error)
                        | Ok () ->
                            match matchingClaimedMetadataRange candidate claimedRange with
                            | Some _ -> Some(Ok())
                            | None -> None)
                |> Option.defaultWith (fun () ->
                    Error(
                        graceError
                            correlationId
                            $"Authoritative ContentBlockMetadata range is absent or changed for claimed reuse range {claimedRange.ContentBlockAddress}."
                    ))

    /// Coordinates compare claimed range newest first logic for the UploadSession actor.
    let private compareClaimedRangeNewestFirst (left: ClaimedReuseRange) (right: ClaimedReuseRange) =
        let versionComparison = compare right.MetadataVersion left.MetadataVersion

        if versionComparison <> 0 then
            versionComparison
        else
            compare right.ClaimedAt left.ClaimedAt

    /// Attempts to select claimed range cover and returns no value when the required invariant is not met.
    let private trySelectClaimedRangeCover blockLength (claimedRanges: ClaimedReuseRange array) =
        if blockLength <= 0L || isNull claimedRanges then
            None
        else
            let candidates =
                claimedRanges
                |> Array.filter (fun claimedRange ->
                    not (isNull (box claimedRange))
                    && claimedRange.OrdinalStart >= 0
                    && claimedRange.OrdinalCount > 0
                    && claimedRange.PhysicalOffset >= 0L
                    && claimedRange.PhysicalLength > 0L
                    && claimedRange.PhysicalLength <= blockLength)

            /// Selects select cover data for the UploadSession actor workflow.
            let rec selectCover nextOrdinal selectedLength selected =
                if selectedLength = blockLength then
                    selected |> List.rev |> List.toArray |> Some
                elif selectedLength > blockLength then
                    None
                else
                    candidates
                    |> Array.filter (fun claimedRange -> claimedRange.OrdinalStart = nextOrdinal)
                    |> Array.sortWith compareClaimedRangeNewestFirst
                    |> Array.tryPick (fun claimedRange ->
                        selectCover
                            (claimedRange.OrdinalStart
                             + claimedRange.OrdinalCount)
                            (selectedLength + claimedRange.PhysicalLength)
                            (claimedRange :: selected))

            selectCover 0 0L []

    let private trySelectClaimedRangeMetadataCover
        correlationId
        storagePoolId
        (claimedMetadata: Dictionary<string, ResizeArray<ContentBlockMetadata>>)
        (claimedRanges: ClaimedReuseRange array)
        contentBlockAddress
        physicalLength
        =
        let candidates =
            claimedRanges
            |> Array.filter (fun claimedRange ->
                not (isNull (box claimedRange))
                && claimedRange.ContentBlockAddress = contentBlockAddress)
            |> Array.sortWith compareClaimedRangeNewestFirst

        let validatedClaims = ResizeArray<ClaimedReuseRange>()
        let mutable firstError = None
        let mutable index = 0

        while index < candidates.Length do
            let claimedRange = candidates[index]

            match validateClaimedRangeMetadata correlationId storagePoolId claimedMetadata claimedRange with
            | Ok () -> validatedClaims.Add claimedRange
            | Error validationError -> if firstError.IsNone then firstError <- Some validationError

            index <- index + 1

        match trySelectClaimedRangeCover physicalLength (validatedClaims.ToArray()) with
        | Some claimedRanges ->
            let distinctVersions =
                claimedRanges
                |> Array.map (fun claimedRange -> claimedRange.MetadataVersion)
                |> Array.distinct

            if distinctVersions.Length = 1 then
                Ok claimedRanges
            else
                Error(
                    graceError
                        correlationId
                        $"Claimed reuse range cover for manifest block {contentBlockAddress} must come from one authoritative metadata version."
                )
        | None ->
            match firstError with
            | Some validationError -> Error validationError
            | None ->
                Error(
                    graceError
                        correlationId
                        $"A claimed reuse range set covering manifest block {contentBlockAddress} length {physicalLength} is required before finalization."
                )

    /// Validates claimed metadata for finalize before the operation continues.
    let private validateClaimedMetadataForFinalize correlationId storagePoolId (session: UploadSessionDto) (manifest: FileManifest) claimedMetadata =
        let manifestBlocks = manifestBlocksRequiringClaimedMetadata session manifest
        let metadata = claimedMetadataByContentBlockAddress claimedMetadata

        let claimedRanges =
            if isNull session.ClaimedReuseRanges then
                Array.empty
            else
                session.ClaimedReuseRanges

        let mutable error = None

        for block in manifestBlocks do
            if error.IsNone then
                match trySelectClaimedRangeMetadataCover correlationId storagePoolId metadata claimedRanges block.Address block.Size with
                | Ok _ -> ()
                | Error validationError -> error <- Some validationError

        match error with
        | Some validationError -> Error validationError
        | None -> Ok()

    let private tryFindClaimedMetadataCoverEvidence
        (claimedMetadata: Dictionary<string, ResizeArray<ContentBlockMetadata>>)
        (claimedRanges: ClaimedReuseRange array)
        =
        if isNull claimedRanges || claimedRanges.Length = 0 then
            None
        else
            let firstClaimedRange = claimedRanges[0]
            let key = claimedMetadataContentBlockKey firstClaimedRange.StoragePoolId firstClaimedRange.ContentBlockAddress

            match claimedMetadata.TryGetValue key with
            | false, _ -> None
            | true, candidates ->
                candidates
                |> Seq.sortByDescending (fun candidate -> candidate.MetadataVersion)
                |> Seq.tryFind (fun candidate ->
                    candidate.StoragePoolId = firstClaimedRange.StoragePoolId
                    && candidate.ContentBlockAddress = firstClaimedRange.ContentBlockAddress
                    && claimedRanges
                       |> Array.forall (fun claimedRange ->
                           candidate.MetadataVersion
                           >= claimedRange.MetadataVersion
                           && matchingClaimedMetadataRange candidate claimedRange
                              |> Option.isSome))

    /// Attempts to find claimed manifest block and returns no value when the required invariant is not met.
    let private tryFindClaimedManifestBlock (session: UploadSessionDto) (manifest: FileManifest) contentBlockAddress =
        manifestBlocksRequiringClaimedMetadata session manifest
        |> Array.tryFind (fun block -> block.Address = contentBlockAddress)

    let internal createRevalidatedClaimedMetadataMergeCommand
        correlationId
        storagePoolId
        finalizeOperationId
        (session: UploadSessionDto)
        (manifest: FileManifest)
        claimedMetadata
        (authoritativeMetadata: ContentBlockMetadata)
        =
        match tryFindClaimedManifestBlock session manifest authoritativeMetadata.ContentBlockAddress with
        | None -> Ok None
        | Some block ->
            let metadata = claimedMetadataByContentBlockAddress claimedMetadata

            let claimedRanges =
                if isNull session.ClaimedReuseRanges then
                    Array.empty
                else
                    session.ClaimedReuseRanges

            match trySelectClaimedRangeMetadataCover correlationId storagePoolId metadata claimedRanges block.Address block.Size with
            | Error validationError -> Error validationError
            | Ok claimedRanges ->
                let firstClaimedRange = claimedRanges[0]

                if authoritativeMetadata.StoragePoolId
                   <> firstClaimedRange.StoragePoolId then
                    Error(graceError correlationId "Authoritative ContentBlockMetadata StoragePoolId does not match the claimed reuse range.")
                elif authoritativeMetadata.ContentBlockAddress
                     <> firstClaimedRange.ContentBlockAddress then
                    Error(graceError correlationId "Authoritative ContentBlockMetadata ContentBlockAddress does not match the claimed reuse range.")
                elif authoritativeMetadata.BlockFormatVersion <= 0s then
                    Error(graceError correlationId "Authoritative ContentBlockMetadata BlockFormatVersion is required before finalizing claimed reuse.")
                else
                    validateAuthoritativeStoragePlacement correlationId authoritativeMetadata.StoragePlacement
                    |> Result.bind (fun () ->
                        let physicalRanges = ResizeArray<ContentBlockMetadataRange>()
                        let expectedRanges = ResizeArray<ContentBlockMetadataRange>()
                        let mutable rangeError = None
                        let mutable rangeIndex = 0

                        while rangeError.IsNone
                              && rangeIndex < claimedRanges.Length do
                            let claimedRange = claimedRanges[rangeIndex]

                            match claimedMetadataRangeEvidence authoritativeMetadata claimedRange with
                            | Some evidence ->
                                for physicalRange in evidence.PhysicalRanges do
                                    physicalRanges.Add physicalRange

                                for expectedRange in evidence.ExpectedRanges do
                                    expectedRanges.Add expectedRange
                            | None ->
                                rangeError <-
                                    Some(
                                        graceError
                                            correlationId
                                            $"Authoritative ContentBlockMetadata range is absent or changed for claimed reuse range {claimedRange.ContentBlockAddress}."
                                    )

                            rangeIndex <- rangeIndex + 1

                        match rangeError with
                        | Some error -> Error error
                        | None ->
                            Ok(
                                Some(
                                    ContentBlockMetadataCommand.MergePhysicalRanges
                                        {
                                            OperationId =
                                                $"{finalizeOperationId}:repository:{session.RepositoryId:N}:upload-session:{session.UploadSessionId:N}:content-block-metadata:{firstClaimedRange.ContentBlockAddress}"
                                            StoragePoolId = storagePoolId
                                            ContentBlockAddress = firstClaimedRange.ContentBlockAddress
                                            BlockFormatVersion = authoritativeMetadata.BlockFormatVersion
                                            StoragePlacement = authoritativeMetadata.StoragePlacement
                                            Ranges = finalizedManifestContributionRanges (physicalRanges.ToArray())
                                            ExpectedMetadataVersion = None
                                            RequireMissingMetadata = false
                                            ExpectedRanges = expectedRanges.ToArray()
                                            IsFinalizeContribution = true
                                        }
                                )
                            ))

    /// Builds content block metadata merge commands for finalized uploads data needed by the UploadSession actor.
    let createContentBlockMetadataMergeCommandsForFinalizedUploads storagePoolId finalizeOperationId (session: UploadSessionDto) (manifest: FileManifest) =
        let manifestAddresses = manifestBlockAddresses manifest
        let commands = ResizeArray<ContentBlockMetadataCommand>()
        let seen = HashSet<ContentBlockAddress>()

        for confirmedBlock in session.ConfirmedBlockUploads do
            if manifestAddresses.Contains confirmedBlock.ContentBlockAddress
               && seen.Add confirmedBlock.ContentBlockAddress then
                commands.Add(
                    ContentBlockMetadataCommand.MergePhysicalRanges
                        {
                            OperationId =
                                $"{finalizeOperationId}:repository:{session.RepositoryId:N}:upload-session:{session.UploadSessionId:N}:content-block-metadata:{confirmedBlock.ContentBlockAddress}"
                            StoragePoolId = storagePoolId
                            ContentBlockAddress = confirmedBlock.ContentBlockAddress
                            BlockFormatVersion = 1s
                            StoragePlacement = confirmedBlock.StoragePlacement
                            Ranges = finalizedManifestContributionRanges confirmedBlock.Ranges
                            ExpectedMetadataVersion = None
                            RequireMissingMetadata = false
                            ExpectedRanges = Array.empty
                            IsFinalizeContribution = true
                        }
                )

        commands.ToArray()

    /// Coordinates with finalize merge precondition logic for the UploadSession actor.
    let withFinalizeMergePrecondition (merge: MergeContentBlockPhysicalRanges) =
        { merge with
            ExpectedMetadataVersion = None
            RequireMissingMetadata = false
            ExpectedRanges = if isNull merge.ExpectedRanges then Array.empty else merge.ExpectedRanges
        }

    /// Attempts to select active current ranges for uploaded merge and returns no value when the required invariant is not met.
    let private trySelectActiveCurrentRangesForUploadedMerge (authoritativeMetadata: ContentBlockMetadata) (uploadedRanges: ContentBlockMetadataRange array) =
        if isNull uploadedRanges || uploadedRanges.Length = 0 then
            None
        else
            let sortedUploadedRanges =
                uploadedRanges
                |> Array.sortBy (fun range -> range.OrdinalStart, range.PhysicalOffset, range.PhysicalLength)

            /// Selects select ranges data for the UploadSession actor workflow.
            let rec selectRanges index expectedPhysicalOffset selected =
                if index = sortedUploadedRanges.Length then
                    selected |> List.rev |> List.toArray |> Some
                else
                    let uploadedRange = sortedUploadedRanges[index]
                    let query = { OrdinalStart = uploadedRange.OrdinalStart; OrdinalCount = uploadedRange.OrdinalCount }

                    Grace.Types.ContentBlockMetadata.findRanges authoritativeMetadata query
                    |> Array.filter (fun range ->
                        range.ActiveManifestCount > 0
                        && range.PhysicalLength = uploadedRange.PhysicalLength
                        && match expectedPhysicalOffset with
                           | Some physicalOffset -> range.PhysicalOffset = physicalOffset
                           | None -> true)
                    |> Array.sortBy (fun range -> range.PhysicalOffset, range.PhysicalLength)
                    |> Array.tryPick (fun activeRange ->
                        selectRanges
                            (index + 1)
                            (Some(
                                activeRange.PhysicalOffset
                                + activeRange.PhysicalLength
                            ))
                            (activeRange :: selected))

            selectRanges 0 None []

    /// Coordinates rebase uploaded merge on current metadata logic for the UploadSession actor.
    let internal rebaseUploadedMergeOnCurrentMetadata (currentMetadata: ContentBlockMetadata option) (merge: MergeContentBlockPhysicalRanges) =
        match currentMetadata with
        | None -> merge
        | Some authoritativeMetadata ->
            match trySelectActiveCurrentRangesForUploadedMerge authoritativeMetadata merge.Ranges with
            | None -> merge
            | Some activeCurrentRanges ->
                { merge with
                    BlockFormatVersion = authoritativeMetadata.BlockFormatVersion
                    StoragePlacement = authoritativeMetadata.StoragePlacement
                    Ranges = finalizedManifestContributionRanges activeCurrentRanges
                    ExpectedRanges = Array.empty
                }

    let tryCreateContentBlockMetadataMergeCommandsForFinalizedBlocks
        correlationId
        storagePoolId
        finalizeOperationId
        (session: UploadSessionDto)
        (manifest: FileManifest)
        claimedMetadata
        =
        let commands = ResizeArray<ContentBlockMetadataCommand>()
        let seen = HashSet<ContentBlockAddress>()

        for uploadCommand in createContentBlockMetadataMergeCommandsForFinalizedUploads storagePoolId finalizeOperationId session manifest do
            match uploadCommand with
            | ContentBlockMetadataCommand.MergePhysicalRanges merge -> if seen.Add merge.ContentBlockAddress then commands.Add uploadCommand
            | _ -> commands.Add uploadCommand

        let metadata = claimedMetadataByContentBlockAddress claimedMetadata

        let claimedRanges =
            if isNull session.ClaimedReuseRanges then
                Array.empty
            else
                session.ClaimedReuseRanges

        let manifestBlocks =
            if isNull (box manifest) || isNull manifest.Blocks then
                Array.empty
            else
                manifest.Blocks
                |> Seq.filter (fun block -> not (isNull (box block)))
                |> Seq.toArray

        let mutable error = None

        for block in manifestBlocks do
            if seen.Add block.Address && error.IsNone then
                match trySelectClaimedRangeMetadataCover correlationId storagePoolId metadata claimedRanges block.Address block.Size with
                | Error validationError -> error <- Some validationError
                | Ok claimedRanges ->
                    let firstClaimedRange = claimedRanges[0]

                    match tryFindClaimedMetadataCoverEvidence metadata claimedRanges with
                    | None ->
                        error <-
                            Some(
                                graceError
                                    correlationId
                                    $"Authoritative ContentBlockMetadata range cover is split or changed for claimed reuse range {firstClaimedRange.ContentBlockAddress}."
                            )
                    | Some authoritativeMetadata ->
                        let physicalRanges = ResizeArray<ContentBlockMetadataRange>()
                        let expectedRanges = ResizeArray<ContentBlockMetadataRange>()
                        let mutable rangeError = None
                        let mutable rangeIndex = 0

                        while rangeError.IsNone
                              && rangeIndex < claimedRanges.Length do
                            let claimedRange = claimedRanges[rangeIndex]

                            match claimedMetadataRangeEvidence authoritativeMetadata claimedRange with
                            | Some evidence ->
                                for physicalRange in evidence.PhysicalRanges do
                                    physicalRanges.Add physicalRange

                                for expectedRange in evidence.ExpectedRanges do
                                    expectedRanges.Add expectedRange
                            | None ->
                                rangeError <-
                                    Some(
                                        graceError
                                            correlationId
                                            $"Authoritative ContentBlockMetadata range is absent or changed for claimed reuse range {claimedRange.ContentBlockAddress}."
                                    )

                            rangeIndex <- rangeIndex + 1

                        match rangeError with
                        | Some validationError -> error <- Some validationError
                        | None ->
                            commands.Add(
                                ContentBlockMetadataCommand.MergePhysicalRanges
                                    {
                                        OperationId =
                                            $"{finalizeOperationId}:repository:{session.RepositoryId:N}:upload-session:{session.UploadSessionId:N}:content-block-metadata:{firstClaimedRange.ContentBlockAddress}"
                                        StoragePoolId = storagePoolId
                                        ContentBlockAddress = firstClaimedRange.ContentBlockAddress
                                        BlockFormatVersion = authoritativeMetadata.BlockFormatVersion
                                        StoragePlacement = authoritativeMetadata.StoragePlacement
                                        Ranges = finalizedManifestContributionRanges (physicalRanges.ToArray())
                                        ExpectedMetadataVersion = None
                                        RequireMissingMetadata = false
                                        ExpectedRanges = expectedRanges.ToArray()
                                        IsFinalizeContribution = true
                                    }
                            )

        match error with
        | Some validationError -> Error validationError
        | None -> Ok(commands.ToArray())

    /// Builds content block metadata merge commands for finalized blocks data needed by the UploadSession actor.
    let createContentBlockMetadataMergeCommandsForFinalizedBlocks storagePoolId finalizeOperationId session manifest claimedMetadata =
        match tryCreateContentBlockMetadataMergeCommandsForFinalizedBlocks String.Empty storagePoolId finalizeOperationId session manifest claimedMetadata with
        | Ok commands -> commands
        | Error _ -> Array.empty

    /// Coordinates manifest validation error message logic for the UploadSession actor.
    let private manifestValidationErrorMessage error =
        match error with
        | ManifestValidation.NullManifest -> "FileManifest is required."
        | ManifestValidation.NullBlockPayloadSequence -> "FinalizeManifest BlockPayloads are required."
        | ManifestValidation.NullBlockPayload index -> $"FinalizeManifest BlockPayloads[{index}] is required."
        | ManifestValidation.NullBlockPayloadBytes address -> $"FinalizeManifest BlockPayloads for ContentBlockAddress {address} must include payload bytes."
        | ManifestValidation.EmptyManifest -> "FileManifest must include at least one ContentBlock."
        | ManifestValidation.InvalidManifestSize -> "FileManifest Size must be greater than zero."
        | ManifestValidation.InvalidChunkingSuiteId chunkingSuiteId -> $"FileManifest ChunkingSuiteId is invalid: {chunkingSuiteId}."
        | ManifestValidation.InvalidFileContentHash fileContentHash -> $"FileManifest FileContentHash is invalid: {fileContentHash}."
        | ManifestValidation.InvalidManifestAddress manifestAddress -> $"FileManifest ManifestAddress is invalid: {manifestAddress}."
        | ManifestValidation.NullContentBlock index -> $"FileManifest Blocks[{index}] is required."
        | ManifestValidation.InvalidContentBlockAddress (index, address) -> $"FileManifest Blocks[{index}] ContentBlockAddress is invalid: {address}."
        | ManifestValidation.ChunkingSuiteMismatch (expected, actual) -> $"FileManifest ChunkingSuiteId mismatch. Expected {expected}, actual {actual}."
        | ManifestValidation.ManifestAddressMismatch (expected, actual) -> $"FileManifest ManifestAddress mismatch. Expected {expected}, actual {actual}."
        | ManifestValidation.BlockRangeOutOfOrder index -> $"FileManifest Blocks[{index}] must be contiguous and ordered by logical offset."
        | ManifestValidation.BlockRangeNotPositive index -> $"FileManifest Blocks[{index}] Size must be greater than zero."
        | ManifestValidation.MissingContentBlockPayload (index, address) -> $"FileManifest Blocks[{index}] ContentBlockAddress {address} is missing a payload."
        | ManifestValidation.ContentBlockPayloadInvalid (address, payloadError) -> $"ContentBlock payload for {address} is invalid: {payloadError}."
        | ManifestValidation.ContentBlockPayloadSizeMismatch (index, expected, actual) ->
            $"FileManifest Blocks[{index}] payload size mismatch. Expected {expected}, actual {actual}."
        | ManifestValidation.FileContentHashMismatch (expected, actual) -> $"FileManifest FileContentHash mismatch. Expected {expected}, actual {actual}."
        | ManifestValidation.ManifestSizeMismatch (expected, actual) -> $"FileManifest Size mismatch. Expected {expected}, actual {actual}."

    /// Coordinates block was claimed logic for the UploadSession actor.
    let private blockWasClaimed (session: UploadSessionDto) (block: ContentBlock) =
        let claimedRanges =
            if isNull session.ClaimedReuseRanges then
                Array.empty
            else
                session.ClaimedReuseRanges

        claimedRanges
        |> Array.filter (fun claimedRange ->
            not (isNull (box claimedRange))
            && claimedRange.ContentBlockAddress = block.Address)
        |> trySelectClaimedRangeCover block.Size
        |> Option.isSome

    /// Validates manifest block presence before the operation continues.
    let private validateManifestBlockPresence correlationId (session: UploadSessionDto) (manifest: FileManifest) =
        if isNull (box manifest) then
            Error(graceError correlationId "FileManifest is required.")
        elif isNull manifest.Blocks then
            Error(graceError correlationId "FileManifest must include at least one ContentBlock.")
        else
            let mutable error = None
            let mutable index = 0

            while index < manifest.Blocks.Count
                  && Option.isNone error do
                let block = manifest.Blocks[index]

                if isNull (box block) then
                    error <- Some(graceError correlationId $"FileManifest Blocks[{index}] is required.")
                elif blockWasUploaded session block
                     || blockWasClaimed session block then
                    index <- index + 1
                else
                    error <-
                        Some(
                            graceError
                                correlationId
                                $"FileManifest Blocks[{index}] ContentBlockAddress {block.Address} was not uploaded or claimed by this UploadSession."
                        )

            match error with
            | Some error -> Error error
            | None -> Ok()

    /// Validates finalize session fields before the operation continues.
    let private validateFinalizeSessionFields correlationId (session: UploadSessionDto) (manifest: FileManifest) =
        if isNull (box manifest) then
            Error(graceError correlationId "FileManifest is required.")
        elif manifest.Size <> session.ExpectedSize then
            Error(graceError correlationId $"FileManifest Size must match UploadSession ExpectedSize. Expected {session.ExpectedSize}, actual {manifest.Size}.")
        elif manifest.FileContentHash
             <> session.FileContentHash then
            Error(
                graceError
                    correlationId
                    $"FileManifest FileContentHash must match UploadSession FileContentHash. Expected {session.FileContentHash}, actual {manifest.FileContentHash}."
            )
        elif manifest.ChunkingSuiteId
             <> session.ChunkingSuiteId then
            Error(
                graceError
                    correlationId
                    $"FileManifest ChunkingSuiteId must match UploadSession ChunkingSuiteId. Expected {session.ChunkingSuiteId}, actual {manifest.ChunkingSuiteId}."
            )
        elif String.IsNullOrWhiteSpace manifest.StoragePoolId then
            Error(graceError correlationId "FileManifest StoragePoolId must be recorded before finalization.")
        elif manifest.StoragePoolId <> session.StoragePoolId then
            Error(
                graceError
                    correlationId
                    $"FileManifest StoragePoolId must match UploadSession StoragePoolId. Expected {session.StoragePoolId}, actual {manifest.StoragePoolId}."
            )
        else
            Ok()

    /// Validates finalize replay manifest identity before the operation continues.
    let private validateFinalizeReplayManifestIdentity correlationId (session: UploadSessionDto) (manifest: FileManifest) finalizedManifestAddress =
        validateFinalizeSessionFields correlationId session manifest
        |> Result.bind (fun () ->
            if isNull manifest.Blocks
               || manifest.Blocks.Count = 0 then
                Error(graceError correlationId "FinalizeManifest replay manifest must include accepted ContentBlocks.")
            else
                let missingBlockIndex =
                    manifest.Blocks
                    |> Seq.mapi (fun index block -> index, block)
                    |> Seq.tryFind (fun (_, block) -> isNull (box block))

                match missingBlockIndex with
                | Some (index, _) -> Error(graceError correlationId $"FinalizeManifest replay manifest Blocks[{index}] is required.")
                | None ->
                    let expectedManifestAddress = ContentAddress.computeManifestAddressForManifest manifest

                    if manifest.ManifestAddress
                       <> expectedManifestAddress then
                        Error(
                            graceError
                                correlationId
                                $"FinalizeManifest replay ManifestAddress must match manifest content. Expected {expectedManifestAddress}, actual {manifest.ManifestAddress}."
                        )
                    elif manifest.ManifestAddress
                         <> finalizedManifestAddress then
                        Error(
                            graceError
                                correlationId
                                $"FinalizeManifest replay ManifestAddress must match durable finalized manifest address. Expected {finalizedManifestAddress}, actual {manifest.ManifestAddress}."
                        )
                    else
                        Ok())

    /// Coordinates finalize block payload logic for the UploadSession actor.
    let private finalizeBlockPayload (payload: FinalizeManifestBlockPayload) =
        if isNull (box payload) then
            Unchecked.defaultof<ManifestValidation.ManifestBlockPayload>
        else
            ManifestValidation.createBlockPayload payload.Address payload.Payload

    /// Coordinates finalize manifest logic for the UploadSession actor.
    let private finalizeManifest (session: UploadSessionDto) (finalize: FinalizeManifest) (metadata: EventMetadata) =
        if isNull (box finalize) then
            Error(graceError metadata.CorrelationId "FinalizeManifest payload is required.")
        else
            validateFinalizeSessionFields metadata.CorrelationId session finalize.Manifest
            |> Result.bind (fun () -> validateManifestBlockPresence metadata.CorrelationId session finalize.Manifest)
            |> Result.bind (fun () ->
                validateClaimedMetadataForFinalize metadata.CorrelationId session.StoragePoolId session finalize.Manifest finalize.ClaimedMetadata)
            |> Result.bind (fun () ->
                let blockPayloads =
                    if isNull finalize.BlockPayloads then
                        Unchecked.defaultof<ManifestValidation.ManifestBlockPayload array>
                    else
                        finalize.BlockPayloads
                        |> Array.map finalizeBlockPayload

                match ManifestValidation.validate session.ChunkingSuiteId finalize.Manifest blockPayloads with
                | Ok _ ->
                    let events =
                        [
                            { Event = UploadSessionEventType.Finalized(finalize.OperationId, finalize.Manifest.ManifestAddress); Metadata = metadata }
                        ]
                        @ cleanupEvents session finalize.OperationId metadata

                    okDecision session finalize.OperationId events false "Upload session manifest finalized."
                | Error error -> Error(graceError metadata.CorrelationId (manifestValidationErrorMessage error)))

    /// Validates finalize replay manifest against durable state before the operation continues.
    let private validateFinalizeReplayManifestAgainstDurableState (session: UploadSessionDto) (finalize: FinalizeManifest) (metadata: EventMetadata) =
        if isNull (box finalize) then
            Error(graceError metadata.CorrelationId "FinalizeManifest payload is required for replay.")
        else
            match session.FinalizedManifestAddress with
            | None -> Error(graceError metadata.CorrelationId "Durable finalized manifest address is required for FinalizeManifest replay.")
            | Some finalizedManifestAddress -> validateFinalizeReplayManifestIdentity metadata.CorrelationId session finalize.Manifest finalizedManifestAddress

    /// Coordinates confirm block upload logic for the UploadSession actor.
    let private confirmBlockUpload (session: UploadSessionDto) (confirmation: ConfirmBlockUploaded) (metadata: EventMetadata) =
        if String.IsNullOrWhiteSpace confirmation.ContentBlockAddress then
            Error(graceError metadata.CorrelationId "ContentBlockAddress is required.")
        elif isNull confirmation.Payload then
            Error(graceError metadata.CorrelationId "ContentBlock payload is required.")
        elif confirmation.Payload.LongLength = 0L then
            Error(graceError metadata.CorrelationId "ContentBlock payload must not be empty.")
        else
            match validateStoragePlacement metadata.CorrelationId confirmation.StoragePlacement with
            | Some error -> Error error
            | None ->
                match findBlockIntents session confirmation.ContentBlockAddress with
                | intents when intents.Length = 0 ->
                    Error(graceError metadata.CorrelationId $"Block upload intent does not exist for ContentBlockAddress {confirmation.ContentBlockAddress}.")
                | intents when
                    intents
                    |> Array.exists (fun intent -> intent.ExpectedPayloadLength = confirmation.Payload.LongLength)
                    |> not
                    ->
                    let expectedLengths =
                        intents
                        |> Array.map (fun intent -> intent.ExpectedPayloadLength.ToString())
                        |> String.concat ", "

                    Error(
                        graceError
                            metadata.CorrelationId
                            $"ContentBlock payload length mismatch. Expected one of [{expectedLengths}], actual {confirmation.Payload.LongLength}."
                    )
                | _ ->
                    match ContentBlockFormat.decode confirmation.Payload with
                    | Error error -> Error(graceError metadata.CorrelationId (contentBlockError error))
                    | Ok decodedBlock ->
                        match ContentBlockFormat.validateAddress confirmation.ContentBlockAddress decodedBlock with
                        | Error error -> Error(graceError metadata.CorrelationId (contentBlockError error))
                        | Ok () ->
                            let logicalLength = decodedLogicalLength decodedBlock

                            let matchingLogicalLength =
                                findBlockIntents session confirmation.ContentBlockAddress
                                |> Array.filter (fun intent -> intent.ExpectedPayloadLength = confirmation.Payload.LongLength)

                            if matchingLogicalLength
                               |> Array.exists (fun intent -> intent.LogicalLength = logicalLength)
                               |> not then
                                let expectedLengths =
                                    matchingLogicalLength
                                    |> Array.map (fun intent -> intent.LogicalLength.ToString())
                                    |> String.concat ", "

                                Error(
                                    graceError
                                        metadata.CorrelationId
                                        $"ContentBlock logical length mismatch. Expected one of [{expectedLengths}], actual {logicalLength}."
                                )
                            else
                                let confirmedBlock =
                                    {
                                        ContentBlockAddress = confirmation.ContentBlockAddress
                                        PayloadLength = confirmation.Payload.LongLength
                                        StoragePlacement = confirmation.StoragePlacement
                                        Ranges = physicalRangesFromDecodedBlock decodedBlock
                                        ConfirmedAt = metadata.Timestamp
                                    }

                                let events =
                                    [
                                        { Event = UploadSessionEventType.BlockUploadConfirmed(confirmation.OperationId, confirmedBlock); Metadata = metadata }
                                    ]

                                okDecision session confirmation.OperationId events false "ContentBlock upload confirmed."

    /// Validates a UploadSession command and derives the events needed for a state transition.
    let decideCommand (events: seq<UploadSessionEvent>) (session: UploadSessionDto) (command: UploadSessionCommand) (metadata: EventMetadata) =
        let operationId = operationId command

        if String.IsNullOrWhiteSpace operationId then
            Error(graceError metadata.CorrelationId "UploadSession command requires a non-empty operation id.")
        elif
            match command with
            | UploadSessionCommand.ConfirmBlockUploaded _ -> hasAppliedBlockUploadConfirmedOperationId events operationId
            | _ -> hasAppliedOperationId events operationId
        then
            okDecision session operationId [] true "Upload session command replayed."
        elif
            match command with
            | UploadSessionCommand.ConfirmBlockUploaded _ -> hasAppliedOperationId events operationId
            | _ -> false
        then
            Error(
                graceError
                    metadata.CorrelationId
                    $"UploadSession OperationId {operationId} was already applied to a non-confirm event and cannot confirm a ContentBlock upload."
            )
        else
            match command with
            | UploadSessionCommand.Start start ->
                if session.LifecycleState
                   <> UploadSessionLifecycleState.NotStarted then
                    Error(graceError metadata.CorrelationId "UploadSession has already been started.")
                elif start.UploadSessionId = UploadSessionId.Empty then
                    Error(graceError metadata.CorrelationId "UploadSessionId must be a non-empty Guid.")
                elif start.RepositoryId = RepositoryId.Empty then
                    Error(graceError metadata.CorrelationId "RepositoryId must be a non-empty Guid.")
                elif String.IsNullOrWhiteSpace start.StoragePoolId then
                    Error(graceError metadata.CorrelationId "StoragePoolId must be recorded when the upload session starts.")
                elif start.ExpectedSize <= 0L then
                    Error(graceError metadata.CorrelationId "ExpectedSize must be greater than zero.")
                else
                    let events =
                        [
                            { Event = UploadSessionEventType.Started start; Metadata = metadata }
                        ]

                    okDecision session operationId events false "Upload session started."
            | UploadSessionCommand.Abandon operationId ->
                match terminalMutationError session command metadata.CorrelationId with
                | Some error -> Error error
                | None ->
                    match session.LifecycleState with
                    | UploadSessionLifecycleState.Started
                    | UploadSessionLifecycleState.Discovering
                    | UploadSessionLifecycleState.UploadingBlocks
                    | UploadSessionLifecycleState.ClaimingRanges ->
                        let events =
                            [
                                { Event = UploadSessionEventType.Abandoned operationId; Metadata = metadata }
                            ]
                            @ cleanupEvents session operationId metadata

                        okDecision session operationId events false "Upload session abandoned."
                    | UploadSessionLifecycleState.NotStarted -> Error(graceError metadata.CorrelationId "UploadSession must be started before Abandon.")
                    | _ -> Error(graceError metadata.CorrelationId $"UploadSession cannot Abandon from {session.LifecycleState}.")
            | UploadSessionCommand.Expire operationId ->
                match terminalMutationError session command metadata.CorrelationId with
                | Some error -> Error error
                | None ->
                    match session.LifecycleState with
                    | UploadSessionLifecycleState.Started
                    | UploadSessionLifecycleState.Discovering
                    | UploadSessionLifecycleState.UploadingBlocks
                    | UploadSessionLifecycleState.ClaimingRanges ->
                        let events =
                            [
                                { Event = UploadSessionEventType.Expired operationId; Metadata = metadata }
                            ]
                            @ cleanupEvents session operationId metadata

                        okDecision session operationId events false "Upload session expired."
                    | UploadSessionLifecycleState.NotStarted -> Error(graceError metadata.CorrelationId "UploadSession must be started before Expire.")
                    | _ -> Error(graceError metadata.CorrelationId $"UploadSession cannot Expire from {session.LifecycleState}.")
            | UploadSessionCommand.DeletePhysicalState operationId ->
                match session.LifecycleState with
                | UploadSessionLifecycleState.NotStarted
                | UploadSessionLifecycleState.StateDeleted -> okDecision session operationId [] true "Upload session physical state was already deleted."
                | UploadSessionLifecycleState.RetentionPending ->
                    let events =
                        [
                            { Event = UploadSessionEventType.PhysicalStateDeleted operationId; Metadata = metadata }
                        ]

                    okDecision session operationId events false "Upload session physical state deleted."
                | _ -> Error(graceError metadata.CorrelationId $"UploadSession cannot DeletePhysicalState from {session.LifecycleState}.")
            | UploadSessionCommand.RegisterBlockUploadIntent intent ->
                match requireStartedSession session command metadata.CorrelationId with
                | Some error -> Error error
                | None ->
                    match validateIntent metadata.CorrelationId intent with
                    | Some error -> Error error
                    | None ->
                        let blockUploadIntent =
                            {
                                ContentBlockAddress = intent.ContentBlockAddress
                                LogicalOffset = intent.LogicalOffset
                                LogicalLength = intent.LogicalLength
                                ExpectedPayloadLength = intent.ExpectedPayloadLength
                                RegisteredAt = metadata.Timestamp
                            }

                        let events =
                            [
                                { Event = UploadSessionEventType.BlockUploadIntentRegistered(intent.OperationId, blockUploadIntent); Metadata = metadata }
                            ]

                        okDecision session intent.OperationId events false "Block upload intent registered."
            | UploadSessionCommand.ConfirmBlockUploaded confirmation ->
                match requireStartedSession session command metadata.CorrelationId with
                | Some error -> Error error
                | None -> confirmBlockUpload session confirmation metadata
            | UploadSessionCommand.IssueDedupeDiscovery discovery ->
                match requireStartedSession session command metadata.CorrelationId with
                | Some error -> Error error
                | None -> issueDedupeDiscovery session discovery metadata
            | UploadSessionCommand.ClaimReuseRanges claim ->
                match requireStartedSession session command metadata.CorrelationId with
                | Some error -> Error error
                | None -> claimReuseRanges session claim metadata
            | UploadSessionCommand.FinalizeManifest finalize ->
                match terminalMutationError session command metadata.CorrelationId with
                | Some error -> Error error
                | None ->
                    match session.LifecycleState with
                    | UploadSessionLifecycleState.Started
                    | UploadSessionLifecycleState.Discovering
                    | UploadSessionLifecycleState.UploadingBlocks
                    | UploadSessionLifecycleState.ClaimingRanges -> finalizeManifest session finalize metadata
                    | UploadSessionLifecycleState.NotStarted ->
                        Error(graceError metadata.CorrelationId "UploadSession must be started before FinalizeManifest.")
                    | _ -> Error(graceError metadata.CorrelationId $"UploadSession cannot FinalizeManifest from {session.LifecycleState}.")

    /// Implements the Orleans grain for session actor.
    type UploadSessionActor([<PersistentState(StateName.UploadSession, Constants.GraceActorStorage)>] state: IPersistentState<List<UploadSessionEvent>>) =
        inherit Grain()

        let log = loggerFactory.CreateLogger("UploadSession.Actor")
        let mutable uploadSessionDto = UploadSessionDto.Default
        /// Stores the correlation id used by this actor while reporting timings and errors.
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            uploadSessionDto <-
                state.State
                |> Seq.fold (fun dto event -> UploadSessionDto.UpdateDto event dto) UploadSessionDto.Default

            Task.CompletedTask

        /// Replays persisted UploadSession events into an in-memory state snapshot.
        member private this.ApplyEvents(events: UploadSessionEvent list) =
            task {
                for uploadSessionEvent in events do
                    state.State.Add(uploadSessionEvent)

                do! state.WriteStateAsync()

                uploadSessionDto <- applyEvents events uploadSessionDto
            }

        /// Coordinates compact physical state events logic for the UploadSession actor.
        member private this.CompactPhysicalStateEvents() =
            task {
                let retainedEvents =
                    state.State
                    |> compactEventsForPhysicalStateCleanup
                    |> List.toArray

                state.State.Clear()

                let mutable index = 0

                while index < retainedEvents.Length do
                    state.State.Add(retainedEvents[index])
                    index <- index + 1

                do! state.WriteStateAsync()

                uploadSessionDto <-
                    state.State
                    |> Seq.fold (fun dto event -> UploadSessionDto.UpdateDto event dto) UploadSessionDto.Default
            }

        /// Coordinates prevalidate finalized content block metadata logic for the UploadSession actor.
        member private this.PrevalidateFinalizedContentBlockMetadata decision (finalize: FinalizeManifest) (metadata: EventMetadata) =
            task {
                let storagePoolId = decision.Session.StoragePoolId

                let metadataCommandsResult =
                    tryCreateContentBlockMetadataMergeCommandsForFinalizedBlocks
                        metadata.CorrelationId
                        storagePoolId
                        finalize.OperationId
                        decision.Session
                        finalize.Manifest
                        finalize.ClaimedMetadata

                let mutable metadataIndex = 0
                let mutable metadataError = None
                let prevalidatedMerges = ResizeArray<IContentBlockMetadataActor * MergeContentBlockPhysicalRanges>()

                match metadataCommandsResult with
                | Error error -> return Error error
                | Ok metadataCommands ->
                    while metadataIndex < metadataCommands.Length
                          && metadataError.IsNone do
                        match metadataCommands[metadataIndex] with
                        | ContentBlockMetadataCommand.MergePhysicalRanges merge ->
                            let actorKey = ContentBlockMetadataActorKey.Create merge.StoragePoolId merge.ContentBlockAddress

                            let metadataActor = orleansClient.CreateActorProxyWithCorrelationId<IContentBlockMetadataActor>(actorKey, metadata.CorrelationId)

                            let claimedBlock = tryFindClaimedManifestBlock decision.Session finalize.Manifest merge.ContentBlockAddress

                            let! currentMetadata = metadataActor.Get metadata.CorrelationId

                            let prevalidationResult =
                                match claimedBlock with
                                | Some _ ->
                                    match currentMetadata with
                                    | None ->
                                        Error(
                                            graceError
                                                metadata.CorrelationId
                                                $"Authoritative ContentBlockMetadata is absent for claimed reuse range {merge.ContentBlockAddress}; claimed reuse cannot be finalized."
                                        )
                                    | Some authoritativeMetadata ->
                                        match
                                            createRevalidatedClaimedMetadataMergeCommand
                                                metadata.CorrelationId
                                                storagePoolId
                                                finalize.OperationId
                                                decision.Session
                                                finalize.Manifest
                                                finalize.ClaimedMetadata
                                                authoritativeMetadata
                                            with
                                        | Error error -> Error error
                                        | Ok None ->
                                            Error(
                                                graceError
                                                    metadata.CorrelationId
                                                    $"Authoritative ContentBlockMetadata could not be revalidated for claimed reuse range {merge.ContentBlockAddress}."
                                            )
                                        | Ok (Some revalidatedCommand) ->
                                            match revalidatedCommand with
                                            | ContentBlockMetadataCommand.MergePhysicalRanges revalidatedMerge ->
                                                let preconditionedMerge = withFinalizeMergePrecondition revalidatedMerge

                                                match
                                                    ContentBlockMetadata.createMergedMetadata
                                                        metadata.CorrelationId
                                                        currentMetadata
                                                        preconditionedMerge
                                                        metadata.Timestamp
                                                    with
                                                | Ok _ ->
                                                    prevalidatedMerges.Add(metadataActor, preconditionedMerge)
                                                    Ok()
                                                | Error error -> Error error
                                            | _ ->
                                                Error(
                                                    graceError
                                                        metadata.CorrelationId
                                                        $"Authoritative ContentBlockMetadata revalidation produced an unsupported command for claimed reuse range {merge.ContentBlockAddress}."
                                                )
                                | None ->
                                    let preconditionedMerge =
                                        merge
                                        |> rebaseUploadedMergeOnCurrentMetadata currentMetadata
                                        |> withFinalizeMergePrecondition

                                    match
                                        ContentBlockMetadata.createMergedMetadata metadata.CorrelationId currentMetadata preconditionedMerge metadata.Timestamp
                                        with
                                    | Ok _ ->
                                        prevalidatedMerges.Add(metadataActor, preconditionedMerge)
                                        Ok()
                                    | Error error -> Error error

                            match prevalidationResult with
                            | Ok () -> ()
                            | Error error -> metadataError <- Some error
                        | _ -> ()

                        metadataIndex <- metadataIndex + 1

                    match metadataError with
                    | Some error -> return Error error
                    | None -> return Ok(prevalidatedMerges.ToArray())
            }

        /// Coordinates revalidate prevalidated content block metadata logic for the UploadSession actor.
        member private this.RevalidatePrevalidatedContentBlockMetadata
            (prevalidatedMerges: (IContentBlockMetadataActor * MergeContentBlockPhysicalRanges) array)
            (metadata: EventMetadata)
            =
            task {
                let mutable metadataIndex = 0
                let mutable metadataError = None

                while metadataIndex < prevalidatedMerges.Length
                      && metadataError.IsNone do
                    /// Coordinates metadata actor logic for the UploadSession actor.
                    let metadataActor, merge = prevalidatedMerges[metadataIndex]
                    let! currentMetadata = metadataActor.Get metadata.CorrelationId

                    match ContentBlockMetadata.createMergedMetadata metadata.CorrelationId currentMetadata merge metadata.Timestamp with
                    | Ok _ -> ()
                    | Error error -> metadataError <- Some error

                    metadataIndex <- metadataIndex + 1

                match metadataError with
                | Some error -> return Error error
                | None -> return Ok()
            }

        /// Coordinates merge prevalidated content block metadata logic for the UploadSession actor.
        member private this.MergePrevalidatedContentBlockMetadata
            (prevalidatedMerges: (IContentBlockMetadataActor * MergeContentBlockPhysicalRanges) array)
            (metadata: EventMetadata)
            =
            task {
                let mutable metadataIndex = 0
                let mutable metadataError = None
                let mergedMetadata = ResizeArray<ContentBlockMetadata>()

                while metadataIndex < prevalidatedMerges.Length
                      && metadataError.IsNone do
                    /// Coordinates metadata actor logic for the UploadSession actor.
                    let metadataActor, merge = prevalidatedMerges[metadataIndex]
                    let! metadataResult = metadataActor.MergePhysicalRanges merge metadata

                    match metadataResult with
                    | Ok returnValue -> mergedMetadata.Add(returnValue.ReturnValue.Metadata)
                    | Error error ->
                        let expectedRanges = if isNull merge.ExpectedRanges then Array.empty else merge.ExpectedRanges

                        if merge.IsFinalizeContribution
                           && expectedRanges.Length = 0 then
                            let! currentMetadata = metadataActor.Get metadata.CorrelationId

                            match currentMetadata with
                            | Some _ ->
                                let rebasedMerge =
                                    merge
                                    |> rebaseUploadedMergeOnCurrentMetadata currentMetadata
                                    |> withFinalizeMergePrecondition

                                let! retryResult = metadataActor.MergePhysicalRanges rebasedMerge metadata

                                match retryResult with
                                | Ok returnValue -> mergedMetadata.Add(returnValue.ReturnValue.Metadata)
                                | Error retryError -> metadataError <- Some retryError
                            | None -> metadataError <- Some error
                        else
                            metadataError <- Some error

                    metadataIndex <- metadataIndex + 1

                match metadataError with
                | Some error -> return Error error
                | None -> return Ok(mergedMetadata.ToArray())
            }

        /// Coordinates merge finalized content block metadata logic for the UploadSession actor.
        member private this.MergeFinalizedContentBlockMetadata decision (finalize: FinalizeManifest) (metadata: EventMetadata) =
            task {
                let! prevalidationResult = this.PrevalidateFinalizedContentBlockMetadata decision finalize metadata

                match prevalidationResult with
                | Error error -> return Error error
                | Ok prevalidatedMerges ->
                    let! revalidationResult = this.RevalidatePrevalidatedContentBlockMetadata prevalidatedMerges metadata

                    match revalidationResult with
                    | Error error -> return Error error
                    | Ok () -> return! this.MergePrevalidatedContentBlockMetadata prevalidatedMerges metadata
            }

        /// Loads load authoritative finalized manifest metadata data required by the UploadSession actor workflow.
        member private this.LoadAuthoritativeFinalizedManifestMetadata decision (finalize: FinalizeManifest) (metadata: EventMetadata) =
            task {
                let authoritativeMetadata = ResizeArray<ContentBlockMetadata>()

                if not (isNull (box finalize)) then
                    let manifestAddresses = finalizedManifestContentBlockAddresses finalize.Manifest
                    let mutable addressIndex = 0
                    let mutable metadataError = None

                    while addressIndex < manifestAddresses.Length
                          && metadataError.IsNone do
                        let contentBlockAddress = manifestAddresses[addressIndex]
                        let actorKey = ContentBlockMetadataActorKey.Create decision.Session.StoragePoolId contentBlockAddress

                        let metadataActor = orleansClient.CreateActorProxyWithCorrelationId<IContentBlockMetadataActor>(actorKey, metadata.CorrelationId)

                        let! currentMetadata = metadataActor.Get metadata.CorrelationId

                        match currentMetadata with
                        | Some current when
                            current.StoragePoolId = decision.Session.StoragePoolId
                            && current.ContentBlockAddress = contentBlockAddress
                            ->
                            authoritativeMetadata.Add current
                        | Some _ ->
                            metadataError <-
                                Some(
                                    graceError
                                        metadata.CorrelationId
                                        $"Authoritative ContentBlockMetadata actor returned mismatched metadata for finalize replay repair {contentBlockAddress}."
                                )
                        | None ->
                            metadataError <-
                                Some(
                                    graceError
                                        metadata.CorrelationId
                                        $"Authoritative ContentBlockMetadata is required before repairing finalize replay Dedupe metadata for ContentBlockAddress {contentBlockAddress}."
                                )

                        addressIndex <- addressIndex + 1

                    match metadataError with
                    | Some error -> return Error error
                    | None -> return Ok(authoritativeMetadata.ToArray())
                else
                    return Ok(Array.empty)
            }

        /// Adds register finalized manifest in dedupe data to the UploadSession actor workflow or state.
        member private this.RegisterFinalizedManifestInDedupe
            decision
            (finalize: FinalizeManifest)
            (mergedMetadata: ContentBlockMetadata array)
            (metadata: EventMetadata)
            =
            task {
                if not (isNull (box finalize)) then
                    let dedupeIndexActor = DedupeIndexActor.CreateActorProxy metadata.CorrelationId

                    do!
                        dedupeIndexActor.RegisterFinalizedManifest
                            {
                                StoragePoolId = decision.Session.StoragePoolId
                                Session = decision.Session
                                Manifest = finalize.Manifest
                                BlockPayloads = finalize.BlockPayloads
                            }
                            metadata.CorrelationId
                        :> Task

                    let metadataToRefresh = if isNull mergedMetadata then Array.empty else mergedMetadata

                    let mutable metadataIndex = 0

                    while metadataIndex < metadataToRefresh.Length do
                        do! dedupeIndexActor.WriteAfterAuthoritativeMetadata metadataToRefresh[metadataIndex] metadata.CorrelationId :> Task
                        metadataIndex <- metadataIndex + 1
            }

        /// Schedules schedule finalize cleanup reminder work for the UploadSession actor.
        member private this.ScheduleFinalizeCleanupReminder decision (finalize: FinalizeManifest) (metadata: EventMetadata) =
            task {
                let reminderState =
                    createCleanupReminderState decision.Session.UploadSessionId decision.Session.RepositoryId finalize.OperationId metadata.CorrelationId

                do!
                    (this :> IGraceReminderWithGuidKey)
                        .ScheduleReminderAsync
                        ReminderTypes.PhysicalDeletion
                        DefaultPhysicalDeletionReminderDuration
                        (ReminderState.UploadSessionPhysicalDeletion reminderState)
                        metadata.CorrelationId
            }

        /// Validates finalize cleanup reminder before the operation continues.
        member private this.EnsureFinalizeCleanupReminder decision (finalize: FinalizeManifest) (metadata: EventMetadata) =
            task {
                if shouldScheduleFinalizeCleanupReminder decision.Session finalize then
                    do! this.ScheduleFinalizeCleanupReminder decision finalize metadata

                    if decision.Session.CleanupReminderOperationId.IsNone then
                        let retentionEvents = cleanupEvents decision.Session finalize.OperationId metadata

                        if not retentionEvents.IsEmpty then do! this.ApplyEvents retentionEvents
            }

        interface IGraceReminderWithGuidKey with
            /// Schedules schedule reminder async work for the UploadSession actor.
            member this.ScheduleReminderAsync reminderType delay reminderState correlationId =
                task {
                    let reminder =
                        ReminderDto.Create
                            actorName
                            $"{this.IdentityString}"
                            uploadSessionDto.OwnerId
                            uploadSessionDto.OrganizationId
                            uploadSessionDto.RepositoryId
                            reminderType
                            (getFutureInstant delay)
                            reminderState
                            correlationId

                    do! createReminder reminder
                }
                :> Task

            /// Handles receive reminder async callbacks for the UploadSession actor.
            member this.ReceiveReminderAsync(reminder: ReminderDto) : Task<Result<unit, GraceError>> =
                task {
                    this.correlationId <- reminder.CorrelationId

                    match reminder.ReminderType, reminder.State with
                    | ReminderTypes.PhysicalDeletion, ReminderState.UploadSessionPhysicalDeletion reminderState ->
                        this.correlationId <- reminderState.CorrelationId

                        let metadata = EventMetadata.New reminderState.CorrelationId "system"
                        let command = UploadSessionCommand.DeletePhysicalState reminderState.OperationId

                        match decideCommand state.State uploadSessionDto command metadata with
                        | Ok decision ->
                            match! deleteUploadSessionStagingPayloads uploadSessionDto metadata.CorrelationId with
                            | Error error -> return Error error
                            | Ok _ ->
                                if not decision.Events.IsEmpty then do! this.ApplyEvents decision.Events

                                do! this.CompactPhysicalStateEvents()
                                this.DeactivateOnIdle()
                                return Ok()
                        | Error error -> return Error error
                    | reminderType, reminderState ->
                        return
                            Error(
                                GraceError.Create
                                    $"{actorName} does not process reminder type {getDiscriminatedUnionCaseName reminderType} with state {getDiscriminatedUnionCaseName reminderState}."
                                    this.correlationId
                            )
                }

        interface IUploadSessionActor with
            /// Reports whether this UploadSession actor has persisted state.
            member this.Exists correlationId =
                this.correlationId <- correlationId

                (uploadSessionDto.UploadSessionId
                 <> UploadSessionId.Empty)
                |> returnTask

            /// Returns the current UploadSession actor state snapshot.
            member this.Get correlationId =
                this.correlationId <- correlationId
                uploadSessionDto |> returnTask

            /// Returns the persisted UploadSession event stream for replay or audit.
            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                (state.State :> IReadOnlyList<UploadSessionEvent>)
                |> returnTask

            /// Routes a public actor command to the domain operation that validates and persists it.
            member this.Handle command metadata =
                task {
                    this.correlationId <- metadata.CorrelationId
                    RequestContext.Set(Constants.CurrentCommandProperty, commandName command)

                    match decideCommand state.State uploadSessionDto command metadata with
                    | Ok decision ->
                        match command with
                        | UploadSessionCommand.FinalizeManifest finalize ->
                            if decision.WasIdempotentReplay then
                                match validateFinalizeReplayManifestAgainstDurableState decision.Session finalize metadata with
                                | Error error -> return Error error
                                | Ok () ->
                                    let! replayMetadataResult =
                                        if decision.Session.LifecycleState = UploadSessionLifecycleState.StateDeleted then
                                            this.LoadAuthoritativeFinalizedManifestMetadata decision finalize metadata
                                        else
                                            this.MergeFinalizedContentBlockMetadata decision finalize metadata

                                    match replayMetadataResult with
                                    | Error error -> return Error error
                                    | Ok replayMetadata ->
                                        do! this.EnsureFinalizeCleanupReminder decision finalize metadata
                                        do! this.RegisterFinalizedManifestInDedupe decision finalize replayMetadata metadata

                                        let returnValue =
                                            (GraceReturnValue.Create decision metadata.CorrelationId)
                                                .enhance(nameof RepositoryId, decision.Session.RepositoryId)
                                                .enhance(nameof UploadSessionId, decision.Session.UploadSessionId)
                                                .enhance(nameof UploadSessionOperationId, decision.OperationId)
                                                .enhance (nameof UploadSessionLifecycleState, decision.Session.LifecycleState)

                                        return Ok returnValue
                            else
                                let! prevalidationResult = this.PrevalidateFinalizedContentBlockMetadata decision finalize metadata

                                match prevalidationResult with
                                | Error error -> return Error error
                                | Ok prevalidatedMerges ->
                                    let! revalidationResult = this.RevalidatePrevalidatedContentBlockMetadata prevalidatedMerges metadata

                                    match revalidationResult with
                                    | Error error -> return Error error
                                    | Ok () ->
                                        /// Coordinates finalization events logic for the UploadSession actor.
                                        let finalizationEvents, retentionEvents = splitFinalizeEvents decision.Events

                                        if not finalizationEvents.IsEmpty then do! this.ApplyEvents finalizationEvents

                                        do! this.ScheduleFinalizeCleanupReminder decision finalize metadata

                                        if not retentionEvents.IsEmpty then do! this.ApplyEvents retentionEvents

                                        let! metadataMergeResult = this.MergePrevalidatedContentBlockMetadata prevalidatedMerges metadata

                                        match metadataMergeResult with
                                        | Error error -> return Error error
                                        | Ok mergedMetadata ->
                                            do! this.RegisterFinalizedManifestInDedupe decision finalize mergedMetadata metadata

                                            let returnValue =
                                                (GraceReturnValue.Create decision metadata.CorrelationId)
                                                    .enhance(nameof RepositoryId, decision.Session.RepositoryId)
                                                    .enhance(nameof UploadSessionId, decision.Session.UploadSessionId)
                                                    .enhance(nameof UploadSessionOperationId, decision.OperationId)
                                                    .enhance (nameof UploadSessionLifecycleState, decision.Session.LifecycleState)

                                            return Ok returnValue
                        | _ ->
                            if not decision.Events.IsEmpty then do! this.ApplyEvents decision.Events

                            match command with
                            | UploadSessionCommand.Abandon operationId
                            | UploadSessionCommand.Expire operationId ->
                                let reservationId = $"upload-session-{decision.Session.UploadSessionId:N}-reserve"
                                let billingAccount = BillingAccount.CreateActorProxy decision.Session.OwnerId metadata.CorrelationId
                                let! account = billingAccount.Get metadata.CorrelationId

                                if account.Reservations.ContainsKey reservationId then
                                    let releaseId = $"{reservationId}-release-{operationId}"

                                    match!
                                        billingAccount.Handle
                                            (BillingAccountCommand.Release(releaseId, reservationId, decision.Session.RepositoryId, BranchId.Empty))
                                            metadata
                                        with
                                    | Error error -> raise (InvalidOperationException(error.Error))
                                    | Ok _ -> ()

                                if not decision.WasIdempotentReplay then
                                    let reminderState =
                                        createCleanupReminderState
                                            decision.Session.UploadSessionId
                                            decision.Session.RepositoryId
                                            operationId
                                            metadata.CorrelationId

                                    do!
                                        (this :> IGraceReminderWithGuidKey)
                                            .ScheduleReminderAsync
                                            ReminderTypes.PhysicalDeletion
                                            DefaultPhysicalDeletionReminderDuration
                                            (ReminderState.UploadSessionPhysicalDeletion reminderState)
                                            metadata.CorrelationId
                            | UploadSessionCommand.DeletePhysicalState _ ->
                                do! this.CompactPhysicalStateEvents()
                                this.DeactivateOnIdle()
                            | _ -> ()

                            let returnValue =
                                (GraceReturnValue.Create decision metadata.CorrelationId)
                                    .enhance(nameof RepositoryId, decision.Session.RepositoryId)
                                    .enhance(nameof UploadSessionId, decision.Session.UploadSessionId)
                                    .enhance(nameof UploadSessionOperationId, decision.OperationId)
                                    .enhance (nameof UploadSessionLifecycleState, decision.Session.LifecycleState)

                            return Ok returnValue
                    | Error error ->
                        log.LogWarning(
                            "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Rejected UploadSession command {Command}. Error: {Error}",
                            getCurrentInstantExtended (),
                            getMachineName,
                            metadata.CorrelationId,
                            commandName command,
                            error.Error
                        )

                        return Error error
                }
