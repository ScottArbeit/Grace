namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared.Utilities
open Grace.Types.ManifestContributionWorkflow
open Grace.Types.Types
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

module ManifestContributionWorkflow =

    let primaryKey (repositoryId: RepositoryId) (manifestAddress: ManifestAddress) = $"{repositoryId:N}|{manifestAddress}"

    let commandName command =
        match command with
        | ManifestContributionWorkflowCommand.Start _ -> "Start"
        | ManifestContributionWorkflowCommand.RecordRangeSucceeded _ -> "RecordRangeSucceeded"
        | ManifestContributionWorkflowCommand.RecordRangeFailed _ -> "RecordRangeFailed"

    let operationId command =
        match command with
        | ManifestContributionWorkflowCommand.Start start -> start.OperationId
        | ManifestContributionWorkflowCommand.RecordRangeSucceeded (operationId, _) -> operationId
        | ManifestContributionWorkflowCommand.RecordRangeFailed (operationId, _, _) -> operationId

    let private eventOperationId workflowEvent =
        match workflowEvent.Event with
        | ManifestContributionWorkflowEventType.WorkflowStarted start -> start.OperationId
        | ManifestContributionWorkflowEventType.RangeSucceeded (operationId, _) -> operationId
        | ManifestContributionWorkflowEventType.RangeFailed (operationId, _, _) -> operationId

    let private eventCommandName workflowEvent =
        match workflowEvent.Event with
        | ManifestContributionWorkflowEventType.WorkflowStarted _ -> "Start"
        | ManifestContributionWorkflowEventType.RangeSucceeded _ -> "RecordRangeSucceeded"
        | ManifestContributionWorkflowEventType.RangeFailed _ -> "RecordRangeFailed"

    let private tryFindAppliedOperation (events: seq<ManifestContributionWorkflowEvent>) operationId =
        events
        |> Seq.tryFind (fun workflowEvent -> eventOperationId workflowEvent = operationId)

    let private applyEvents (events: ManifestContributionWorkflowEvent list) (workflow: ManifestContributionWorkflowDto) =
        events
        |> List.fold (fun current event -> ManifestContributionWorkflowDto.UpdateDto event current) workflow

    let pendingRanges (workflow: ManifestContributionWorkflowDto) =
        workflow.Ranges
        |> Array.filter (fun range -> not (workflow.CompletedRanges.ContainsKey range))

    let blocksUnsafeDeletion (workflow: ManifestContributionWorkflowDto) range =
        workflow.LifecycleState = ManifestContributionWorkflowLifecycleState.InProgress
        && pendingRanges workflow |> Array.exists ((=) range)

    let private isKnownRange (workflow: ManifestContributionWorkflowDto) range = workflow.Ranges |> Array.exists ((=) range)

    let private targetMismatch (workflow: ManifestContributionWorkflowDto) (repositoryId: RepositoryId) (manifestAddress: ManifestAddress) =
        (workflow.RepositoryId <> RepositoryId.Empty
         && workflow.RepositoryId <> repositoryId)
        || (not (String.IsNullOrWhiteSpace workflow.ManifestAddress)
            && workflow.ManifestAddress <> manifestAddress)

    let private expectedPrimaryKeyMismatch expectedPrimaryKey (repositoryId: RepositoryId) (manifestAddress: ManifestAddress) =
        match expectedPrimaryKey with
        | Some expectedPrimaryKey -> not (String.Equals(expectedPrimaryKey, primaryKey repositoryId manifestAddress, StringComparison.Ordinal))
        | None -> false

    let private activeCountDelta direction =
        match direction with
        | ManifestContributionDirection.Increment -> 1
        | ManifestContributionDirection.Decrement -> -1

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

    let private graceError correlationId message = GraceError.Create message correlationId

    let private validateRange correlationId range =
        if String.IsNullOrWhiteSpace range.StoragePoolId then
            Some(graceError correlationId "ManifestContributionWorkflow range requires a non-empty StoragePoolId.")
        elif String.IsNullOrWhiteSpace range.ContentBlockAddress then
            Some(graceError correlationId "ManifestContributionWorkflow range requires a non-empty ContentBlockAddress.")
        elif range.OrdinalStart < 0 then
            Some(graceError correlationId "ManifestContributionWorkflow range OrdinalStart must be zero or greater.")
        elif range.OrdinalCount <= 0 then
            Some(graceError correlationId "ManifestContributionWorkflow range OrdinalCount must be greater than zero.")
        else
            None

    let private validateStart
        expectedPrimaryKey
        (workflow: ManifestContributionWorkflowDto)
        (start: StartManifestContributionWorkflow)
        (metadata: EventMetadata)
        =
        if start.RepositoryId = RepositoryId.Empty then
            Some(graceError metadata.CorrelationId "ManifestContributionWorkflow requires a non-empty RepositoryId.")
        elif String.IsNullOrWhiteSpace start.ManifestAddress then
            Some(graceError metadata.CorrelationId "ManifestContributionWorkflow requires a non-empty ManifestAddress.")
        elif expectedPrimaryKeyMismatch expectedPrimaryKey start.RepositoryId start.ManifestAddress then
            Some(graceError metadata.CorrelationId "ManifestContributionWorkflow command target does not match the grain key.")
        elif targetMismatch workflow start.RepositoryId start.ManifestAddress then
            Some(graceError metadata.CorrelationId "ManifestContributionWorkflow command target does not match the initialized workflow.")
        elif
            workflow.LifecycleState
            <> ManifestContributionWorkflowLifecycleState.NotStarted
            && not (String.IsNullOrWhiteSpace workflow.ManifestAddress)
        then
            Some(graceError metadata.CorrelationId "ManifestContributionWorkflow has already been started.")
        elif Array.isEmpty start.Ranges then
            Some(graceError metadata.CorrelationId "ManifestContributionWorkflow requires at least one range.")
        else
            start.Ranges
            |> Array.tryPick (validateRange metadata.CorrelationId)

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
            match tryFindAppliedOperation events operationId with
            | Some workflowEvent when
                eventCommandName workflowEvent
                <> commandName command
                ->
                Error(graceError metadata.CorrelationId "ManifestContributionWorkflow operation id was already used for a different command.")
            | Some _ -> okDecision workflow operationId [] [] true "Manifest contribution workflow command replayed."
            | None ->
                match command with
                | ManifestContributionWorkflowCommand.Start start ->
                    match validateStart expectedPrimaryKey workflow start metadata with
                    | Some error -> Error error
                    | None ->
                        let workflowEvent = { Event = ManifestContributionWorkflowEventType.WorkflowStarted start; Metadata = metadata }
                        okDecision workflow operationId [ workflowEvent ] [] false "Manifest contribution workflow started."
                | ManifestContributionWorkflowCommand.RecordRangeSucceeded (_, range) ->
                    if workflow.LifecycleState = ManifestContributionWorkflowLifecycleState.NotStarted then
                        Error(graceError metadata.CorrelationId "ManifestContributionWorkflow must be started before recording range progress.")
                    elif not (isKnownRange workflow range) then
                        Error(graceError metadata.CorrelationId "ManifestContributionWorkflow range is not part of this workflow.")
                    elif workflow.CompletedRanges.ContainsKey range then
                        okDecision workflow operationId [] [] true "Manifest contribution workflow range was already completed."
                    else
                        let workflowEvent = { Event = ManifestContributionWorkflowEventType.RangeSucceeded(operationId, range); Metadata = metadata }

                        let intents =
                            [
                                ManifestContributionWorkflowIntent.AdjustRangeActiveManifestCount(range, activeCountDelta workflow.Direction)
                            ]

                        okDecision workflow operationId [ workflowEvent ] intents false "Manifest contribution workflow range completed."
                | ManifestContributionWorkflowCommand.RecordRangeFailed (_, range, message) ->
                    if workflow.LifecycleState = ManifestContributionWorkflowLifecycleState.NotStarted then
                        Error(graceError metadata.CorrelationId "ManifestContributionWorkflow must be started before recording range progress.")
                    elif not (isKnownRange workflow range) then
                        Error(graceError metadata.CorrelationId "ManifestContributionWorkflow range is not part of this workflow.")
                    elif workflow.CompletedRanges.ContainsKey range then
                        okDecision workflow operationId [] [] true "Manifest contribution workflow range was already completed."
                    else
                        let workflowEvent = { Event = ManifestContributionWorkflowEventType.RangeFailed(operationId, range, message); Metadata = metadata }

                        okDecision workflow operationId [ workflowEvent ] [] false "Manifest contribution workflow range failure recorded."

    let decideCommand events workflow command metadata = decideCommandForKey None events workflow command metadata

    type ManifestContributionWorkflowActor
        (
            [<PersistentState(StateName.ManifestContributionWorkflow, Grace.Shared.Constants.GraceActorStorage)>] state: IPersistentState<List<ManifestContributionWorkflowEvent>>
        ) =
        inherit Grain()

        let log = loggerFactory.CreateLogger("ManifestContributionWorkflow.Actor")
        let mutable workflow = ManifestContributionWorkflowDto.Default
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()
            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            workflow <-
                state.State
                |> Seq.fold (fun dto event -> ManifestContributionWorkflowDto.UpdateDto event dto) ManifestContributionWorkflowDto.Default

            Task.CompletedTask

        member private this.ApplyEvents(events: ManifestContributionWorkflowEvent list) =
            task {
                state.State.AddRange(events)
                do! state.WriteStateAsync()
                workflow <- applyEvents events workflow
            }

        interface IHasRepositoryId with
            member this.GetRepositoryId correlationId =
                this.correlationId <- correlationId
                workflow.RepositoryId |> returnTask

        interface IManifestContributionWorkflowActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId

                (workflow.RepositoryId <> RepositoryId.Empty
                 && not (String.IsNullOrWhiteSpace workflow.ManifestAddress))
                |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId
                workflow |> returnTask

            member this.GetPendingRanges correlationId =
                this.correlationId <- correlationId
                pendingRanges workflow |> returnTask

            member this.BlocksUnsafeDeletion range correlationId =
                this.correlationId <- correlationId
                blocksUnsafeDeletion workflow range |> returnTask

            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                (state.State :> IReadOnlyList<ManifestContributionWorkflowEvent>)
                |> returnTask

            member this.Handle command metadata =
                task {
                    this.correlationId <- metadata.CorrelationId
                    RequestContext.Set(Grace.Shared.Constants.CurrentCommandProperty, commandName command)

                    match decideCommandForKey (Some(this.GetPrimaryKeyString())) state.State workflow command metadata with
                    | Ok decision ->
                        if not decision.Events.IsEmpty then do! this.ApplyEvents decision.Events

                        let returnValue =
                            (GraceReturnValue.Create decision metadata.CorrelationId)
                                .enhance(nameof RepositoryId, decision.Workflow.RepositoryId)
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
