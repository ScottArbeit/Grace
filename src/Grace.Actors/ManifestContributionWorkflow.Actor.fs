namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared.Utilities
open Grace.Types.ContentBlockMetadata
open Grace.Types.ManifestContributionWorkflow
open Grace.Types.Common
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

/// Groups Orleans actor helpers for manifest contribution workflow keys, proxies, state, or workflow transitions.
module ManifestContributionWorkflow =

    /// Coordinates primary key logic for the ManifestContributionWorkflow actor.
    let primaryKey (repositoryId: RepositoryId) (storagePoolId: StoragePoolId) (manifestAddress: ManifestAddress) =
        $"{repositoryId:N}|{storagePoolId}|{manifestAddress}"

    /// Maps a ManifestContributionWorkflow command case to the operation name used in idempotency and diagnostics.
    let commandName command =
        match command with
        | ManifestContributionWorkflowCommand.Start _ -> "Start"
        | ManifestContributionWorkflowCommand.RecordRangeSucceeded _ -> "RecordRangeSucceeded"
        | ManifestContributionWorkflowCommand.RecordRangeFailed _ -> "RecordRangeFailed"

    /// Extracts the client operation id that lets command retries match previously emitted events.
    let operationId command =
        match command with
        | ManifestContributionWorkflowCommand.Start (operationId, _, _, _, _, _, _) -> operationId
        | ManifestContributionWorkflowCommand.RecordRangeSucceeded (operationId, _, _, _, _) -> operationId
        | ManifestContributionWorkflowCommand.RecordRangeFailed (operationId, _, _, _, _, _) -> operationId

    /// Coordinates start command payload logic for the ManifestContributionWorkflow actor.
    let private startCommandPayload operationId repositoryId storagePoolId manifestAddress direction ranges counterRevision =
        {
            OperationId = operationId
            RepositoryId = repositoryId
            StoragePoolId = storagePoolId
            ManifestAddress = manifestAddress
            Direction = direction
            Ranges = ranges
            CounterRevision = counterRevision
        }

    /// Coordinates progress command payload logic for the ManifestContributionWorkflow actor.
    let private progressCommandPayload operationId repositoryId storagePoolId manifestAddress range =
        { OperationId = operationId; RepositoryId = repositoryId; StoragePoolId = storagePoolId; ManifestAddress = manifestAddress; Range = range }

    /// Coordinates failure command payload logic for the ManifestContributionWorkflow actor.
    let private failureCommandPayload operationId repositoryId storagePoolId manifestAddress range message =
        {
            OperationId = operationId
            RepositoryId = repositoryId
            StoragePoolId = storagePoolId
            ManifestAddress = manifestAddress
            Range = range
            Message = message
        }

    /// Coordinates event operation id logic for the ManifestContributionWorkflow actor.
    let private eventOperationId workflowEvent =
        match workflowEvent.Event with
        | ManifestContributionWorkflowEventType.WorkflowStarted start -> start.OperationId
        | ManifestContributionWorkflowEventType.RangeSucceeded progress -> progress.OperationId
        | ManifestContributionWorkflowEventType.RangeFailed failure -> failure.OperationId

    /// Coordinates event command name logic for the ManifestContributionWorkflow actor.
    let private eventCommandName workflowEvent =
        match workflowEvent.Event with
        | ManifestContributionWorkflowEventType.WorkflowStarted _ -> "Start"
        | ManifestContributionWorkflowEventType.RangeSucceeded _ -> "RecordRangeSucceeded"
        | ManifestContributionWorkflowEventType.RangeFailed _ -> "RecordRangeFailed"

    /// Coordinates ranges equal logic for the ManifestContributionWorkflow actor.
    let private rangesEqual left right =
        Array.length left = Array.length right
        && Seq.forall2 (=) left right

    /// Coordinates start matches logic for the ManifestContributionWorkflow actor.
    let private startMatches (existing: StartManifestContributionWorkflow) (candidate: StartManifestContributionWorkflow) =
        existing.RepositoryId = candidate.RepositoryId
        && existing.StoragePoolId = candidate.StoragePoolId
        && existing.ManifestAddress = candidate.ManifestAddress
        && existing.Direction = candidate.Direction
        && rangesEqual existing.Ranges candidate.Ranges
        && existing.CounterRevision = candidate.CounterRevision

    /// Coordinates event matches command logic for the ManifestContributionWorkflow actor.
    let private eventMatchesCommand workflowEvent command =
        match workflowEvent.Event, command with
        | ManifestContributionWorkflowEventType.WorkflowStarted existing,
          ManifestContributionWorkflowCommand.Start (operationId, repositoryId, storagePoolId, manifestAddress, direction, ranges, counterRevision) ->
            startMatches existing (startCommandPayload operationId repositoryId storagePoolId manifestAddress direction ranges counterRevision)
        | ManifestContributionWorkflowEventType.RangeSucceeded existing,
          ManifestContributionWorkflowCommand.RecordRangeSucceeded (operationId, repositoryId, storagePoolId, manifestAddress, range) ->
            existing = progressCommandPayload operationId repositoryId storagePoolId manifestAddress range
        | ManifestContributionWorkflowEventType.RangeFailed existing,
          ManifestContributionWorkflowCommand.RecordRangeFailed (operationId, repositoryId, storagePoolId, manifestAddress, range, message) ->
            existing = failureCommandPayload operationId repositoryId storagePoolId manifestAddress range message
        | _ -> false

    /// Attempts to find applied operation and returns no value when the required invariant is not met.
    let private tryFindAppliedOperation (events: seq<ManifestContributionWorkflowEvent>) operationId =
        events
        |> Seq.tryFind (fun workflowEvent -> eventOperationId workflowEvent = operationId)

    /// Matches an operation against bounded current progress when no lifetime event stream is retained.
    let private tryMatchCurrentOperation (workflow: ManifestContributionWorkflowDto) command =
        let commandOperationId = operationId command

        match command with
        | ManifestContributionWorkflowCommand.Start (_, repositoryId, storagePoolId, manifestAddress, direction, ranges, counterRevision) when
            workflow.StartOperationId = Some commandOperationId
            ->
            Some(
                workflow.RepositoryId = repositoryId
                && workflow.StoragePoolId = storagePoolId
                && workflow.ManifestAddress = manifestAddress
                && workflow.Direction = direction
                && rangesEqual workflow.Ranges ranges
                && workflow.CounterRevision = counterRevision
            )
        | ManifestContributionWorkflowCommand.RecordRangeSucceeded (_, repositoryId, storagePoolId, manifestAddress, range) ->
            match workflow.CompletedRanges
                  |> Array.tryFind (fun completed -> completed.Range = range)
                with
            | Some completed when completed.OperationId = commandOperationId ->
                Some(
                    workflow.RepositoryId = repositoryId
                    && workflow.StoragePoolId = storagePoolId
                    && workflow.ManifestAddress = manifestAddress
                )
            | _ when workflow.LastOperationId = Some commandOperationId -> Some false
            | _ -> None
        | ManifestContributionWorkflowCommand.RecordRangeFailed (_, repositoryId, storagePoolId, manifestAddress, range, message) ->
            match workflow.FailedRanges
                  |> Array.tryFind (fun failure -> failure.Range = range)
                with
            | Some failure when failure.OperationId = commandOperationId ->
                Some(
                    workflow.RepositoryId = repositoryId
                    && workflow.StoragePoolId = storagePoolId
                    && workflow.ManifestAddress = manifestAddress
                    && failure.Message = message
                )
            | _ when workflow.LastOperationId = Some commandOperationId -> Some false
            | _ -> None
        | _ -> None

    /// Applies events changes to the ManifestContributionWorkflow actor state.
    let private applyEvents (events: ManifestContributionWorkflowEvent list) (workflow: ManifestContributionWorkflowDto) =
        events
        |> List.fold (fun current event -> ManifestContributionWorkflowDto.UpdateDto event current) workflow

    /// Coordinates pending ranges logic for the ManifestContributionWorkflow actor.
    let pendingRanges (workflow: ManifestContributionWorkflowDto) =
        workflow.Ranges
        |> Array.filter (fun range ->
            workflow.CompletedRanges
            |> Array.exists (fun completed -> completed.Range = range)
            |> not)

    /// Checks whether blocks unsafe deletion is true for the ManifestContributionWorkflow actor state.
    let blocksUnsafeDeletion (workflow: ManifestContributionWorkflowDto) range =
        workflow.LifecycleState = ManifestContributionWorkflowLifecycleState.InProgress
        && pendingRanges workflow |> Array.exists ((=) range)

    /// Checks whether the contribution range belongs to the active workflow.
    let private isKnownRange (workflow: ManifestContributionWorkflowDto) range = workflow.Ranges |> Array.exists ((=) range)

    /// Coordinates target mismatch logic for the ManifestContributionWorkflow actor.
    let private targetMismatch (workflow: ManifestContributionWorkflowDto) (repositoryId: RepositoryId) storagePoolId (manifestAddress: ManifestAddress) =
        (workflow.RepositoryId <> RepositoryId.Empty
         && workflow.RepositoryId <> repositoryId)
        || (not (String.IsNullOrWhiteSpace workflow.StoragePoolId)
            && workflow.StoragePoolId <> storagePoolId)
        || (not (String.IsNullOrWhiteSpace workflow.ManifestAddress)
            && workflow.ManifestAddress <> manifestAddress)

    /// Coordinates expected primary key mismatch logic for the ManifestContributionWorkflow actor.
    let private expectedPrimaryKeyMismatch expectedPrimaryKey (repositoryId: RepositoryId) storagePoolId (manifestAddress: ManifestAddress) =
        match expectedPrimaryKey with
        | Some expectedPrimaryKey -> not (String.Equals(expectedPrimaryKey, primaryKey repositoryId storagePoolId manifestAddress, StringComparison.Ordinal))
        | None -> false

    /// Coordinates active count delta logic for the ManifestContributionWorkflow actor.
    let private activeCountDelta direction =
        match direction with
        | ManifestContributionDirection.Increment -> 1
        | ManifestContributionDirection.Decrement -> -1

    /// Coordinates ok decision logic for the ManifestContributionWorkflow actor.
    let private okDecision workflow operationId events intents wasReplay message =
        Ok
            {
                Workflow = applyEvents events workflow
                OperationId = operationId
                Events = events
                Intents = intents
                WasIdempotentReplay = wasReplay
                Message = message
            }

    /// Coordinates grace error logic for the ManifestContributionWorkflow actor.
    let private graceError correlationId message = GraceError.Create message correlationId

    /// Validates range before the operation continues.
    let private validateRange correlationId (range: ManifestContributionWorkflowRange) =
        if String.IsNullOrWhiteSpace range.StoragePoolId then
            Some(graceError correlationId "ManifestContributionWorkflow range requires a non-empty StoragePoolId.")
        elif String.IsNullOrWhiteSpace range.ContentBlockAddress then
            Some(graceError correlationId "ManifestContributionWorkflow range requires a non-empty ContentBlockAddress.")
        else
            None

    /// Validates start range before the operation continues.
    let private validateStartRange correlationId storagePoolId range =
        match validateRange correlationId range with
        | Some error -> Some error
        | None when range.StoragePoolId <> storagePoolId ->
            Some(
                graceError
                    correlationId
                    $"ManifestContributionWorkflow range StoragePoolId must match workflow StoragePoolId. Expected {storagePoolId}, actual {range.StoragePoolId}."
            )
        | None -> None

    /// Checks whether the workflow start request repeats a contribution range.
    let private hasDuplicateRanges (ranges: ManifestContributionWorkflowRange array) =
        let seen = HashSet<ManifestContributionWorkflowRange>()

        ranges
        |> Array.exists (fun range -> not (seen.Add range))

    let private validateStart
        expectedPrimaryKey
        (workflow: ManifestContributionWorkflowDto)
        (start: StartManifestContributionWorkflow)
        (metadata: EventMetadata)
        =
        if start.RepositoryId = RepositoryId.Empty then
            Some(graceError metadata.CorrelationId "ManifestContributionWorkflow requires a non-empty RepositoryId.")
        elif String.IsNullOrWhiteSpace start.StoragePoolId then
            Some(graceError metadata.CorrelationId "ManifestContributionWorkflow requires a non-empty StoragePoolId.")
        elif String.IsNullOrWhiteSpace start.ManifestAddress then
            Some(graceError metadata.CorrelationId "ManifestContributionWorkflow requires a non-empty ManifestAddress.")
        elif expectedPrimaryKeyMismatch expectedPrimaryKey start.RepositoryId start.StoragePoolId start.ManifestAddress then
            Some(graceError metadata.CorrelationId "ManifestContributionWorkflow command target does not match the grain key.")
        elif targetMismatch workflow start.RepositoryId start.StoragePoolId start.ManifestAddress then
            Some(graceError metadata.CorrelationId "ManifestContributionWorkflow command target does not match the initialized workflow.")
        elif workflow.LifecycleState = ManifestContributionWorkflowLifecycleState.InProgress then
            Some(graceError metadata.CorrelationId "ManifestContributionWorkflow has already been started.")
        elif start.CounterRevision <= 0L then
            Some(graceError metadata.CorrelationId "ManifestContributionWorkflow counter revision must be greater than zero.")
        elif Array.isEmpty start.Ranges then
            Some(graceError metadata.CorrelationId "ManifestContributionWorkflow requires at least one range.")
        elif hasDuplicateRanges start.Ranges then
            Some(graceError metadata.CorrelationId "ManifestContributionWorkflow ranges must be unique.")
        else
            start.Ranges
            |> Array.tryPick (validateStartRange metadata.CorrelationId start.StoragePoolId)

    let private validateProgressTarget
        expectedPrimaryKey
        (workflow: ManifestContributionWorkflowDto)
        repositoryId
        storagePoolId
        manifestAddress
        (metadata: EventMetadata)
        =
        if repositoryId = RepositoryId.Empty then
            Some(graceError metadata.CorrelationId "ManifestContributionWorkflow requires a non-empty RepositoryId.")
        elif String.IsNullOrWhiteSpace storagePoolId then
            Some(graceError metadata.CorrelationId "ManifestContributionWorkflow requires a non-empty StoragePoolId.")
        elif String.IsNullOrWhiteSpace manifestAddress then
            Some(graceError metadata.CorrelationId "ManifestContributionWorkflow requires a non-empty ManifestAddress.")
        elif expectedPrimaryKeyMismatch expectedPrimaryKey repositoryId storagePoolId manifestAddress then
            Some(graceError metadata.CorrelationId "ManifestContributionWorkflow command target does not match the grain key.")
        elif targetMismatch workflow repositoryId storagePoolId manifestAddress then
            Some(graceError metadata.CorrelationId "ManifestContributionWorkflow command target does not match the initialized workflow.")
        else
            None

    let decideCommandForKey
        (expectedPrimaryKey: string option)
        (events: seq<ManifestContributionWorkflowEvent>)
        (workflow: ManifestContributionWorkflowDto)
        (command: ManifestContributionWorkflowCommand)
        (metadata: EventMetadata)
        : Result<ManifestContributionWorkflowDecision, GraceError>
        =
        let operationId = operationId command

        if String.IsNullOrWhiteSpace operationId then
            Error(graceError metadata.CorrelationId "ManifestContributionWorkflow command requires a non-empty operation id.")
        else
            match tryMatchCurrentOperation workflow command with
            | Some true -> okDecision workflow operationId [] [] true "Manifest contribution workflow command replayed."
            | Some false -> Error(graceError metadata.CorrelationId "ManifestContributionWorkflow operation id was already used with a different payload.")
            | None ->
                match tryFindAppliedOperation events operationId with
                | Some workflowEvent when
                    eventCommandName workflowEvent
                    <> commandName command
                    ->
                    Error(graceError metadata.CorrelationId "ManifestContributionWorkflow operation id was already used for a different command.")
                | Some workflowEvent when not (eventMatchesCommand workflowEvent command) ->
                    Error(graceError metadata.CorrelationId "ManifestContributionWorkflow operation id was already used with a different payload.")
                | Some _ -> okDecision workflow operationId [] [] true "Manifest contribution workflow command replayed."
                | None ->
                    match command with
                    | ManifestContributionWorkflowCommand.Start (operationId, repositoryId, storagePoolId, manifestAddress, direction, ranges, counterRevision) ->
                        let start = startCommandPayload operationId repositoryId storagePoolId manifestAddress direction ranges counterRevision

                        if counterRevision < workflow.CounterRevision then
                            okDecision workflow operationId [] [] true "Manifest contribution workflow start was superseded."
                        elif counterRevision = workflow.CounterRevision
                             && workflow.StartOperationId.IsSome then
                            Error(graceError metadata.CorrelationId "ManifestContributionWorkflow counter revision was already used by a different operation.")
                        else
                            match validateStart expectedPrimaryKey workflow start metadata with
                            | Some error -> Error error
                            | None ->
                                let workflowEvent = { Event = ManifestContributionWorkflowEventType.WorkflowStarted start; Metadata = metadata }
                                okDecision workflow operationId [ workflowEvent ] [] false "Manifest contribution workflow started."
                    | ManifestContributionWorkflowCommand.RecordRangeSucceeded (operationId, repositoryId, storagePoolId, manifestAddress, range) ->
                        let progress = progressCommandPayload operationId repositoryId storagePoolId manifestAddress range

                        match validateProgressTarget expectedPrimaryKey workflow progress.RepositoryId progress.StoragePoolId progress.ManifestAddress metadata
                            with
                        | Some error -> Error error
                        | None ->
                            if workflow.LifecycleState = ManifestContributionWorkflowLifecycleState.NotStarted then
                                Error(graceError metadata.CorrelationId "ManifestContributionWorkflow must be started before recording range progress.")
                            elif not (isKnownRange workflow progress.Range) then
                                Error(graceError metadata.CorrelationId "ManifestContributionWorkflow range is not part of this workflow.")
                            elif workflow.CompletedRanges
                                 |> Array.exists (fun completed -> completed.Range = progress.Range) then
                                okDecision workflow operationId [] [] true "Manifest contribution workflow range was already completed."
                            else
                                let workflowEvent = { Event = ManifestContributionWorkflowEventType.RangeSucceeded progress; Metadata = metadata }

                                let intents =
                                    [
                                        ManifestContributionWorkflowIntent.AdjustRangeActiveManifestCount(progress.Range, activeCountDelta workflow.Direction)
                                    ]

                                okDecision workflow operationId [ workflowEvent ] intents false "Manifest contribution workflow range completed."
                    | ManifestContributionWorkflowCommand.RecordRangeFailed (operationId, repositoryId, storagePoolId, manifestAddress, range, message) ->
                        let failure = failureCommandPayload operationId repositoryId storagePoolId manifestAddress range message

                        match validateProgressTarget expectedPrimaryKey workflow failure.RepositoryId failure.StoragePoolId failure.ManifestAddress metadata
                            with
                        | Some error -> Error error
                        | None ->
                            if workflow.LifecycleState = ManifestContributionWorkflowLifecycleState.NotStarted then
                                Error(graceError metadata.CorrelationId "ManifestContributionWorkflow must be started before recording range progress.")
                            elif not (isKnownRange workflow failure.Range) then
                                Error(graceError metadata.CorrelationId "ManifestContributionWorkflow range is not part of this workflow.")
                            elif workflow.CompletedRanges
                                 |> Array.exists (fun completed -> completed.Range = failure.Range) then
                                okDecision workflow operationId [] [] true "Manifest contribution workflow range was already completed."
                            else
                                let workflowEvent = { Event = ManifestContributionWorkflowEventType.RangeFailed failure; Metadata = metadata }

                                okDecision workflow operationId [ workflowEvent ] [] false "Manifest contribution workflow range failure recorded."

    /// Validates a ManifestContributionWorkflow command and derives the events needed for a state transition.
    let decideCommand events workflow command metadata = decideCommandForKey None events workflow command metadata

    /// Implements the Orleans grain for manifest contribution workflow actor.
    type ManifestContributionWorkflowActor
        (
            [<PersistentState(StateName.ManifestContributionWorkflow, Grace.Shared.Constants.GraceActorStorage)>] state: IPersistentState<ManifestContributionWorkflowDto>
        ) =
        inherit Grain()

        let log = loggerFactory.CreateLogger("ManifestContributionWorkflow.Actor")
        let mutable workflow = ManifestContributionWorkflowDto.Default
        /// Stores the correlation id used by this actor while reporting timings and errors.
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()
            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            workflow <-
                if state.RecordExists then
                    state.State
                else
                    ManifestContributionWorkflowDto.Default

            Task.CompletedTask

        /// Overwrites the bounded current workflow snapshot after one progress transition.
        member private this.ApplySnapshot(snapshot: ManifestContributionWorkflowDto) =
            task {
                state.State <- snapshot
                do! state.WriteStateAsync()
                workflow <- snapshot
            }

        /// Executes one deterministic ContentBlock delta and records bounded workflow progress.
        member private this.ApplyRange
            (
                startOperationId: ManifestContributionWorkflowOperationId,
                rangeIndex: int,
                range: ManifestContributionWorkflowRange,
                metadata: EventMetadata
            ) =
            let grainFactory = this.GrainFactory

            task {
                let operationPrefix = $"{startOperationId}:range:{rangeIndex}"

                let contentBlockActor =
                    grainFactory.GetGrain<IContentBlockMetadataActor>(ContentBlockMetadataActorKey.Create range.StoragePoolId range.ContentBlockAddress)

                let! currentMetadata = contentBlockActor.Get metadata.CorrelationId

                match currentMetadata with
                | None ->
                    return
                        Error(
                            GraceError.Create
                                $"ContentBlockMetadata does not exist for workflow range {range.StoragePoolId}/{range.ContentBlockAddress}."
                                metadata.CorrelationId
                        )
                | Some currentMetadata ->
                    let delta = activeCountDelta workflow.Direction

                    let adjust =
                        {
                            OperationId = $"{operationPrefix}:active-count"
                            ExpectedMetadataVersion = currentMetadata.MetadataVersion
                            StoragePoolId = range.StoragePoolId
                            ContentBlockAddress = range.ContentBlockAddress
                            Delta = delta
                        }

                    match! contentBlockActor.AdjustActiveManifestCount adjust metadata with
                    | Error error -> return Error error
                    | Ok _ ->
                        match! (this :> IManifestContributionWorkflowActor)
                                   .Handle
                                   (ManifestContributionWorkflowCommand.RecordRangeSucceeded(
                                       $"{operationPrefix}:completed",
                                       workflow.RepositoryId,
                                       workflow.StoragePoolId,
                                       workflow.ManifestAddress,
                                       range
                                   ))
                                   metadata
                            with
                        | Error error -> return Error error
                        | Ok _ -> return Ok()
            }

        /// Resumes every pending range from the bounded workflow snapshot.
        member private this.ApplyPendingRanges(startOperationId: ManifestContributionWorkflowOperationId, metadata: EventMetadata) =
            task {
                let ranges = workflow.Ranges
                let mutable rangeIndex = 0
                let mutable error: GraceError option = None

                while rangeIndex < ranges.Length && error.IsNone do
                    let range = ranges[rangeIndex]

                    if workflow.CompletedRanges
                       |> Array.exists (fun completed -> completed.Range = range)
                       |> not then
                        match! this.ApplyRange(startOperationId, rangeIndex, range, metadata) with
                        | Ok _ -> ()
                        | Error rangeError ->
                            let failureOperationId = $"{startOperationId}:range:{rangeIndex}:failed"

                            let! failureResult =
                                (this :> IManifestContributionWorkflowActor)
                                    .Handle
                                    (ManifestContributionWorkflowCommand.RecordRangeFailed(
                                        failureOperationId,
                                        workflow.RepositoryId,
                                        workflow.StoragePoolId,
                                        workflow.ManifestAddress,
                                        range,
                                        rangeError.Error
                                    ))
                                    metadata

                            match failureResult with
                            | Ok _ -> error <- Some rangeError
                            | Error failureError -> error <- Some failureError

                    rangeIndex <- rangeIndex + 1

                return
                    match error with
                    | Some rangeError -> Error rangeError
                    | None -> Ok()
            }

        interface IHasRepositoryId with
            /// Returns the repository id recorded in this ManifestContributionWorkflow actor state.
            member this.GetRepositoryId correlationId =
                this.correlationId <- correlationId
                workflow.RepositoryId |> returnTask

        interface IManifestContributionWorkflowActor with
            /// Reports whether this ManifestContributionWorkflow actor has persisted state.
            member this.Exists correlationId =
                this.correlationId <- correlationId

                (workflow.RepositoryId <> RepositoryId.Empty
                 && not (String.IsNullOrWhiteSpace workflow.StoragePoolId)
                 && not (String.IsNullOrWhiteSpace workflow.ManifestAddress))
                |> returnTask

            /// Returns the current ManifestContributionWorkflow actor state snapshot.
            member this.Get correlationId =
                this.correlationId <- correlationId
                workflow |> returnTask

            /// Returns pending ranges data from the ManifestContributionWorkflow actor state or related storage.
            member this.GetPendingRanges correlationId =
                this.correlationId <- correlationId
                pendingRanges workflow |> returnTask

            /// Checks whether blocks unsafe deletion is true for the ManifestContributionWorkflow actor state.
            member this.BlocksUnsafeDeletion range correlationId =
                this.correlationId <- correlationId
                blocksUnsafeDeletion workflow range |> returnTask

            /// Returns the persisted ManifestContributionWorkflow event stream for replay or audit.
            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                (Array.empty<ManifestContributionWorkflowEvent> :> IReadOnlyList<ManifestContributionWorkflowEvent>)
                |> returnTask

            /// Coordinates start logic for the ManifestContributionWorkflow actor.
            member this.Start operationId repositoryId storagePoolId manifestAddress direction ranges counterRevision metadata =
                task {
                    match! (this :> IManifestContributionWorkflowActor)
                               .Handle
                               (ManifestContributionWorkflowCommand.Start(
                                   operationId,
                                   repositoryId,
                                   storagePoolId,
                                   manifestAddress,
                                   direction,
                                   ranges,
                                   counterRevision
                               ))
                               metadata
                        with
                    | Error error -> return Error error
                    | Ok startResult when startResult.ReturnValue.Workflow.StartOperationId = Some operationId ->
                        match! this.ApplyPendingRanges(operationId, metadata) with
                        | Error error -> return Error error
                        | Ok _ -> return Ok startResult
                    | Ok startResult -> return Ok startResult
                }

            /// Routes a public actor command to the domain operation that validates and persists it.
            member this.Handle command metadata =
                task {
                    this.correlationId <- metadata.CorrelationId
                    RequestContext.Set(Grace.Shared.Constants.CurrentCommandProperty, commandName command)

                    match decideCommandForKey (Some(this.GetPrimaryKeyString())) Seq.empty workflow command metadata with
                    | Ok decision ->
                        if not decision.Events.IsEmpty then do! this.ApplySnapshot decision.Workflow

                        let returnValue =
                            (GraceReturnValue.Create decision metadata.CorrelationId)
                                .enhance(nameof RepositoryId, decision.Workflow.RepositoryId)
                                .enhance(nameof StoragePoolId, decision.Workflow.StoragePoolId)
                                .enhance(nameof ManifestAddress, decision.Workflow.ManifestAddress)
                                .enhance (nameof ManifestContributionWorkflowLifecycleState, decision.Workflow.LifecycleState)

                        return Ok returnValue
                    | Error error ->
                        log.LogWarning(
                            "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Rejected ManifestContributionWorkflow command {Command}. Error: {Error}",
                            getCurrentInstantExtended (),
                            getMachineName,
                            metadata.CorrelationId,
                            commandName command,
                            error.Error
                        )

                        return Error error
                }
