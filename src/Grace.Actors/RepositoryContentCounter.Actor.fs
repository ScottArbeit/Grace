namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.RepositoryContentCounter
open Grace.Types.Common
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

/// Groups Orleans actor helpers for repository content counter keys, proxies, state, or workflow transitions.
module RepositoryContentCounter =

    /// Coordinates primary key logic for the RepositoryContentCounter actor.
    let primaryKey (repositoryId: RepositoryId) (storagePoolId: StoragePoolId) (manifestAddress: ManifestAddress) =
        $"{repositoryId:N}|{storagePoolId}|{manifestAddress}"

    /// Maps a RepositoryContentCounter command case to the operation name used in idempotency and diagnostics.
    let commandName command =
        match command with
        | RepositoryContentCounterCommand.AddReference _ -> "AddReference"
        | RepositoryContentCounterCommand.RemoveReference _ -> "RemoveReference"

    /// Extracts the client operation id that lets command retries match previously emitted events.
    let operationId command =
        match command with
        | RepositoryContentCounterCommand.AddReference (operationId, _, _, _) -> operationId
        | RepositoryContentCounterCommand.RemoveReference (operationId, _, _, _) -> operationId

    /// Coordinates command target logic for the RepositoryContentCounter actor.
    let private commandTarget command =
        match command with
        | RepositoryContentCounterCommand.AddReference (_, repositoryId, storagePoolId, manifestAddress)
        | RepositoryContentCounterCommand.RemoveReference (_, repositoryId, storagePoolId, manifestAddress) -> repositoryId, storagePoolId, manifestAddress

    /// Coordinates event operation id logic for the RepositoryContentCounter actor.
    let private eventOperationId counterEvent =
        match counterEvent.Event with
        | RepositoryContentCounterEventType.ReferenceAdded (operationId, _, _, _) -> operationId
        | RepositoryContentCounterEventType.ReferenceRemoved operationId -> operationId

    /// Coordinates event command name logic for the RepositoryContentCounter actor.
    let private eventCommandName counterEvent =
        match counterEvent.Event with
        | RepositoryContentCounterEventType.ReferenceAdded _ -> "AddReference"
        | RepositoryContentCounterEventType.ReferenceRemoved _ -> "RemoveReference"

    /// Attempts to find applied operation and returns no value when the required invariant is not met.
    let private tryFindAppliedOperation (events: seq<RepositoryContentCounterEvent>) operationId =
        events
        |> Seq.tryFind (fun counterEvent -> eventOperationId counterEvent = operationId)

    /// Applies events changes to the RepositoryContentCounter actor state.
    let private applyEvents (events: RepositoryContentCounterEvent list) (counter: RepositoryContentCounterDto) =
        events
        |> List.fold (fun current event -> RepositoryContentCounterDto.UpdateDto event current) counter

    /// Coordinates ok decision logic for the RepositoryContentCounter actor.
    let private okDecision counter operationId events intents wasReplay message =
        Ok
            {
                Counter = applyEvents events counter
                OperationId = operationId
                Events = events
                Intents = intents
                WasIdempotentReplay = wasReplay
                Message = message
            }

    /// Coordinates grace error logic for the RepositoryContentCounter actor.
    let private graceError correlationId message = GraceError.Create message correlationId

    /// Coordinates target mismatch logic for the RepositoryContentCounter actor.
    let private targetMismatch (counter: RepositoryContentCounterDto) repositoryId storagePoolId manifestAddress =
        (counter.RepositoryId <> RepositoryId.Empty
         && counter.RepositoryId <> repositoryId)
        || (not (String.IsNullOrWhiteSpace counter.StoragePoolId)
            && counter.StoragePoolId <> storagePoolId)
        || (not (String.IsNullOrWhiteSpace counter.ManifestAddress)
            && counter.ManifestAddress <> manifestAddress)

    /// Coordinates expected primary key mismatch logic for the RepositoryContentCounter actor.
    let private expectedPrimaryKeyMismatch expectedPrimaryKey repositoryId storagePoolId manifestAddress =
        match expectedPrimaryKey with
        | Some expectedPrimaryKey -> not (String.Equals(expectedPrimaryKey, primaryKey repositoryId storagePoolId manifestAddress, StringComparison.Ordinal))
        | None -> false

    let decideCommandForKey
        (expectedPrimaryKey: string option)
        (events: seq<RepositoryContentCounterEvent>)
        (counter: RepositoryContentCounterDto)
        (command: RepositoryContentCounterCommand)
        (metadata: EventMetadata)
        : Result<RepositoryContentCounterDecision, GraceError>
        =
        let operationId = operationId command
        /// Coordinates repository id logic for the RepositoryContentCounter actor.
        let repositoryId, storagePoolId, manifestAddress = commandTarget command

        if String.IsNullOrWhiteSpace operationId then
            Error(graceError metadata.CorrelationId "RepositoryContentCounter command requires a non-empty operation id.")
        elif repositoryId = RepositoryId.Empty then
            Error(graceError metadata.CorrelationId "RepositoryContentCounter command requires a non-empty RepositoryId.")
        elif String.IsNullOrWhiteSpace storagePoolId then
            Error(graceError metadata.CorrelationId "RepositoryContentCounter command requires a non-empty StoragePoolId.")
        elif String.IsNullOrWhiteSpace manifestAddress then
            Error(graceError metadata.CorrelationId "RepositoryContentCounter command requires a non-empty ManifestAddress.")
        elif expectedPrimaryKeyMismatch expectedPrimaryKey repositoryId storagePoolId manifestAddress then
            Error(graceError metadata.CorrelationId "RepositoryContentCounter command target does not match the grain key.")
        elif targetMismatch counter repositoryId storagePoolId manifestAddress then
            Error(graceError metadata.CorrelationId "RepositoryContentCounter command target does not match the initialized counter.")
        else
            match tryFindAppliedOperation events operationId with
            | Some counterEvent when
                eventCommandName counterEvent
                <> commandName command
                ->
                Error(graceError metadata.CorrelationId "RepositoryContentCounter operation id was already used for a different command.")
            | Some _ -> okDecision counter operationId [] [] true "Repository content counter command replayed."
            | None ->
                match command with
                | RepositoryContentCounterCommand.AddReference _ ->
                    let counterEvent =
                        {
                            Event = RepositoryContentCounterEventType.ReferenceAdded(operationId, repositoryId, storagePoolId, manifestAddress)
                            Metadata = metadata
                        }

                    let intents =
                        if counter.ReferenceCount = 0L then
                            [
                                RepositoryContentCounterIntent.IncrementManifestReferenceCount(repositoryId, storagePoolId, manifestAddress)
                            ]
                        else
                            []

                    okDecision counter operationId [ counterEvent ] intents false "Repository content reference added."
                | RepositoryContentCounterCommand.RemoveReference _ ->
                    if counter.ReferenceCount = 0L then
                        Error(graceError metadata.CorrelationId "RepositoryContentCounter cannot remove a reference when the local count is already zero.")
                    else
                        let counterEvent = { Event = RepositoryContentCounterEventType.ReferenceRemoved operationId; Metadata = metadata }

                        let intents =
                            if counter.ReferenceCount = 1L then
                                [
                                    RepositoryContentCounterIntent.DecrementManifestReferenceCount(repositoryId, storagePoolId, manifestAddress)
                                ]
                            else
                                []

                        okDecision counter operationId [ counterEvent ] intents false "Repository content reference removed."

    /// Validates a RepositoryContentCounter command and derives the events needed for a state transition.
    let decideCommand events counter command metadata = decideCommandForKey None events counter command metadata

    /// Implements the Orleans grain for repository content counter actor.
    type RepositoryContentCounterActor
        (
            [<PersistentState(StateName.RepositoryContentCounter, Grace.Shared.Constants.GraceActorStorage)>] state: IPersistentState<List<RepositoryContentCounterEvent>>
        ) =
        inherit Grain()

        let log = loggerFactory.CreateLogger("RepositoryContentCounter.Actor")
        let mutable counter = RepositoryContentCounterDto.Default
        /// Stores the correlation id used by this actor while reporting timings and errors.
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()
            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            counter <-
                state.State
                |> Seq.fold (fun dto event -> RepositoryContentCounterDto.UpdateDto event dto) RepositoryContentCounterDto.Default

            Task.CompletedTask

        /// Replays persisted RepositoryContentCounter events into an in-memory state snapshot.
        member private this.ApplyEvents(events: RepositoryContentCounterEvent list) =
            task {
                state.State.AddRange(events)
                do! state.WriteStateAsync()
                counter <- applyEvents events counter
            }

        interface IRepositoryContentCounterActor with
            /// Reports whether this RepositoryContentCounter actor has persisted state.
            member this.Exists correlationId =
                this.correlationId <- correlationId

                (counter.RepositoryId <> RepositoryId.Empty
                 && not (String.IsNullOrWhiteSpace counter.StoragePoolId)
                 && not (String.IsNullOrWhiteSpace counter.ManifestAddress))
                |> returnTask

            /// Returns the current RepositoryContentCounter actor state snapshot.
            member this.Get correlationId =
                this.correlationId <- correlationId
                counter |> returnTask

            /// Returns the persisted RepositoryContentCounter event stream for replay or audit.
            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                (state.State :> IReadOnlyList<RepositoryContentCounterEvent>)
                |> returnTask

            /// Routes a public actor command to the domain operation that validates and persists it.
            member this.Handle command metadata =
                task {
                    this.correlationId <- metadata.CorrelationId
                    RequestContext.Set(Grace.Shared.Constants.CurrentCommandProperty, commandName command)

                    match decideCommandForKey (Some(this.GetPrimaryKeyString())) state.State counter command metadata with
                    | Ok decision ->
                        if not decision.Events.IsEmpty then do! this.ApplyEvents decision.Events

                        let returnValue =
                            (GraceReturnValue.Create decision metadata.CorrelationId)
                                .enhance(nameof RepositoryId, decision.Counter.RepositoryId)
                                .enhance(nameof StoragePoolId, decision.Counter.StoragePoolId)
                                .enhance(nameof ManifestAddress, decision.Counter.ManifestAddress)
                                .enhance(nameof ReferenceCount, decision.Counter.ReferenceCount)
                                .enhance (nameof RepositoryContentCounterLifecycleState, decision.Counter.LifecycleState)

                        return Ok returnValue
                    | Error error ->
                        log.LogWarning(
                            "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Rejected RepositoryContentCounter command {Command}. Error: {Error}",
                            getCurrentInstantExtended (),
                            getMachineName,
                            metadata.CorrelationId,
                            commandName command,
                            error.Error
                        )

                        return Error error
                }
