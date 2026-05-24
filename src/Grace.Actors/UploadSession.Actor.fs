namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.ContentBlockMetadata
open Grace.Types.Reminder
open Grace.Types.Types
open Grace.Types.UploadSession
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

module UploadSession =

    let private actorName = ActorName.UploadSession

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

    let createCleanupOperationId operationId = $"{operationId}:cleanup"

    let createCleanupReminderState uploadSessionId repositoryId operationId correlationId =
        {
            UploadSessionId = uploadSessionId
            RepositoryId = repositoryId
            OperationId = createCleanupOperationId operationId
            DeleteReason = "UploadSession coordination state retention window elapsed."
            CorrelationId = correlationId
        }

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

    let private hasAppliedOperationId (events: seq<UploadSessionEvent>) operationId =
        events
        |> Seq.exists (fun uploadSessionEvent -> eventOperationId uploadSessionEvent = operationId)

    let private applyEvents (events: UploadSessionEvent list) (session: UploadSessionDto) =
        events
        |> List.fold (fun current event -> UploadSessionDto.UpdateDto event current) session

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

    let private okDecision (session: UploadSessionDto) operationId (events: UploadSessionEvent list) wasReplay message =
        Ok { Session = applyEvents events session; OperationId = operationId; Events = events; WasIdempotentReplay = wasReplay; Message = message }

    let private graceError correlationId message = GraceError.Create message correlationId

    let private cleanupEvents (session: UploadSessionDto) operationId (metadata: EventMetadata) =
        let cleanupOperationId = createCleanupOperationId operationId
        let reminderTime = metadata.Timestamp.Plus(DefaultPhysicalDeletionReminderDuration)

        [
            { Event = UploadSessionEventType.CleanupReminderScheduled(cleanupOperationId, reminderTime); Metadata = metadata }
        ]

    let private terminalMutationError (session: UploadSessionDto) command correlationId =
        match session.LifecycleState with
        | UploadSessionLifecycleState.Finalized -> Some(graceError correlationId $"UploadSession is finalized and cannot be changed by {commandName command}.")
        | UploadSessionLifecycleState.RetentionPending ->
            Some(graceError correlationId $"UploadSession is waiting for cleanup and cannot be changed by {commandName command}.")
        | UploadSessionLifecycleState.StateDeleted ->
            Some(graceError correlationId $"UploadSession physical state has been deleted and cannot be changed by {commandName command}.")
        | _ -> None

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

    let private validateStoragePlacement correlationId (placement: ContentBlockStoragePlacement) =
        if isNull (box placement) then
            Some(graceError correlationId "StoragePlacement is required.")
        elif String.IsNullOrWhiteSpace placement.ObjectKey then
            Some(graceError correlationId "StoragePlacement.ObjectKey is required.")
        else
            None

    let private normalizeHints (hints: ContentBlockReuseRangeHint array) = if isNull hints then Array.empty else hints

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

    let private sameHint (left: ContentBlockReuseRangeHint) (right: ContentBlockReuseRangeHint) =
        left.StoragePoolId = right.StoragePoolId
        && left.ContentBlockAddress = right.ContentBlockAddress
        && left.OrdinalStart = right.OrdinalStart
        && left.OrdinalCount = right.OrdinalCount
        && left.MetadataVersion = right.MetadataVersion

    let private claimedRangeKey (range: ClaimedReuseRange) =
        $"{range.StoragePoolId}|{range.ContentBlockAddress}|{range.OrdinalStart}|{range.OrdinalCount}|{range.MetadataVersion}"

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

    let private findBlockIntents (session: UploadSessionDto) contentBlockAddress =
        session.BlockUploadIntents
        |> Array.filter (fun intent -> intent.ContentBlockAddress = contentBlockAddress)

    let private contentBlockError error = $"ContentBlock payload is invalid: {error}."

    let private physicalRangesFromDecodedBlock (decodedBlock: ContentBlockFormat.DecodedContentBlock) =
        decodedBlock.Chunks
        |> Array.mapi (fun index chunk ->
            { OrdinalStart = index; OrdinalCount = 1; ActiveManifestCount = 0; PhysicalOffset = chunk.PhysicalOffset; PhysicalLength = int64 chunk.Length })

    let private decodedLogicalLength (decodedBlock: ContentBlockFormat.DecodedContentBlock) =
        decodedBlock.Chunks
        |> Array.sumBy (fun chunk -> int64 chunk.Length)

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

    let private blockWasUploaded (session: UploadSessionDto) (block: ContentBlock) =
        session.ConfirmedBlockUploads
        |> Array.exists (fun confirmedBlock -> confirmedBlock.ContentBlockAddress = block.Address)
        && session.BlockUploadIntents
           |> Array.exists (fun intent ->
               intent.ContentBlockAddress = block.Address
               && intent.LogicalOffset = block.Offset
               && intent.LogicalLength = block.Size)

    let private blockWasClaimed (session: UploadSessionDto) (block: ContentBlock) =
        session.ClaimedReuseRanges
        |> Array.exists (fun claimedRange ->
            claimedRange.ContentBlockAddress = block.Address
            && claimedRange.PhysicalLength = block.Size)

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
        else
            Ok()

    let private finalizeBlockPayload (payload: FinalizeManifestBlockPayload) =
        if isNull (box payload) then
            Unchecked.defaultof<ManifestValidation.ManifestBlockPayload>
        else
            ManifestValidation.createBlockPayload payload.Address payload.Payload

    let private finalizeManifest (session: UploadSessionDto) (finalize: FinalizeManifest) (metadata: EventMetadata) =
        if isNull (box finalize) then
            Error(graceError metadata.CorrelationId "FinalizeManifest payload is required.")
        else
            validateFinalizeSessionFields metadata.CorrelationId session finalize.Manifest
            |> Result.bind (fun () -> validateManifestBlockPresence metadata.CorrelationId session finalize.Manifest)
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

    let decideCommand (events: seq<UploadSessionEvent>) (session: UploadSessionDto) (command: UploadSessionCommand) (metadata: EventMetadata) =
        let operationId = operationId command

        if String.IsNullOrWhiteSpace operationId then
            Error(graceError metadata.CorrelationId "UploadSession command requires a non-empty operation id.")
        elif hasAppliedOperationId events operationId then
            okDecision session operationId [] true "Upload session command replayed."
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

    type UploadSessionActor([<PersistentState(StateName.UploadSession, Constants.GraceActorStorage)>] state: IPersistentState<List<UploadSessionEvent>>) =
        inherit Grain()

        let log = loggerFactory.CreateLogger("UploadSession.Actor")
        let mutable uploadSessionDto = UploadSessionDto.Default
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            uploadSessionDto <-
                state.State
                |> Seq.fold (fun dto event -> UploadSessionDto.UpdateDto event dto) UploadSessionDto.Default

            Task.CompletedTask

        member private this.ApplyEvents(events: UploadSessionEvent list) =
            task {
                for uploadSessionEvent in events do
                    state.State.Add(uploadSessionEvent)

                do! state.WriteStateAsync()

                uploadSessionDto <- applyEvents events uploadSessionDto
            }

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

        interface IGraceReminderWithGuidKey with
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
            member this.Exists correlationId =
                this.correlationId <- correlationId

                (uploadSessionDto.UploadSessionId
                 <> UploadSessionId.Empty)
                |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId
                uploadSessionDto |> returnTask

            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                (state.State :> IReadOnlyList<UploadSessionEvent>)
                |> returnTask

            member this.Handle command metadata =
                task {
                    this.correlationId <- metadata.CorrelationId
                    RequestContext.Set(Constants.CurrentCommandProperty, commandName command)

                    match decideCommand state.State uploadSessionDto command metadata with
                    | Ok decision ->
                        if not decision.Events.IsEmpty then do! this.ApplyEvents decision.Events

                        match command with
                        | UploadSessionCommand.Abandon operationId when not decision.WasIdempotentReplay ->
                            let reminderState =
                                createCleanupReminderState decision.Session.UploadSessionId decision.Session.RepositoryId operationId metadata.CorrelationId

                            do!
                                (this :> IGraceReminderWithGuidKey)
                                    .ScheduleReminderAsync
                                    ReminderTypes.PhysicalDeletion
                                    DefaultPhysicalDeletionReminderDuration
                                    (ReminderState.UploadSessionPhysicalDeletion reminderState)
                                    metadata.CorrelationId
                        | UploadSessionCommand.Expire operationId when not decision.WasIdempotentReplay ->
                            let reminderState =
                                createCleanupReminderState decision.Session.UploadSessionId decision.Session.RepositoryId operationId metadata.CorrelationId

                            do!
                                (this :> IGraceReminderWithGuidKey)
                                    .ScheduleReminderAsync
                                    ReminderTypes.PhysicalDeletion
                                    DefaultPhysicalDeletionReminderDuration
                                    (ReminderState.UploadSessionPhysicalDeletion reminderState)
                                    metadata.CorrelationId
                        | UploadSessionCommand.FinalizeManifest finalize when not decision.WasIdempotentReplay ->
                            DedupeIndex.registerFinalizedManifest
                                { Session = decision.Session; Manifest = finalize.Manifest; BlockPayloads = finalize.BlockPayloads }
                            |> ignore

                            let reminderState =
                                createCleanupReminderState
                                    decision.Session.UploadSessionId
                                    decision.Session.RepositoryId
                                    finalize.OperationId
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
