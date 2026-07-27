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
open System.Threading
open System.Threading.Tasks

/// Provides short-lived replay results without making Redis part of repository manifest membership.
type IRepositoryCounterRecentResult =

    /// Returns a cached result or `None` when the operation is unknown, expired, malformed, or unavailable.
    abstract member TryGetAsync:
        repositoryId: RepositoryId *
        storagePoolId: StoragePoolId *
        manifestAddress: ManifestAddress *
        operationId: RepositoryContentCounterOperationId *
        cancellationToken: CancellationToken ->
            Task<RepositoryContentCounterCompletedChange option>

    /// Attempts to cache one completed result for ten minutes and reports whether Redis accepted the write.
    abstract member TrySetAsync:
        repositoryId: RepositoryId *
        storagePoolId: StoragePoolId *
        manifestAddress: ManifestAddress *
        change: RepositoryContentCounterCompletedChange *
        cancellationToken: CancellationToken ->
            Task<bool>

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

    /// Attempts to find the bounded completed change for one operation.
    let private tryFindCompletedChange (counter: RepositoryContentCounterDto) operationId =
        counter.LastCompletedChange
        |> Option.filter (fun change -> change.OperationId = operationId)

    /// Returns whether the completed change matches the requested counter direction.
    let private changeMatchesCommand change command =
        match change.Operation, command with
        | RepositoryContentCounterChangeOperation.Added, RepositoryContentCounterCommand.AddReference _
        | RepositoryContentCounterChangeOperation.Removed, RepositoryContentCounterCommand.RemoveReference _ -> true
        | _ -> false

    /// Recreates only the zero-transition intent carried by an original completed result.
    let private intentsForCompletedChange repositoryId storagePoolId manifestAddress change =
        match change.Operation, change.PreviousCount, change.CurrentCount with
        | RepositoryContentCounterChangeOperation.Added, 0L, 1L ->
            [
                RepositoryContentCounterIntent.IncrementManifestReferenceCount(repositoryId, storagePoolId, manifestAddress, change.Revision)
            ]
        | RepositoryContentCounterChangeOperation.Removed, 1L, 0L ->
            [
                RepositoryContentCounterIntent.DecrementManifestReferenceCount(repositoryId, storagePoolId, manifestAddress, change.Revision)
            ]
        | _ -> []

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

    /// Validates a counter command before either Redis replay or durable mutation.
    let private validateCommandTarget
        (expectedPrimaryKey: string option)
        (counter: RepositoryContentCounterDto)
        (command: RepositoryContentCounterCommand)
        (metadata: EventMetadata)
        =
        let operationId = operationId command
        let repositoryId, storagePoolId, manifestAddress = commandTarget command

        if String.IsNullOrWhiteSpace operationId then
            Some(graceError metadata.CorrelationId "RepositoryContentCounter command requires a non-empty operation id.")
        elif repositoryId = RepositoryId.Empty then
            Some(graceError metadata.CorrelationId "RepositoryContentCounter command requires a non-empty RepositoryId.")
        elif String.IsNullOrWhiteSpace storagePoolId then
            Some(graceError metadata.CorrelationId "RepositoryContentCounter command requires a non-empty StoragePoolId.")
        elif String.IsNullOrWhiteSpace manifestAddress then
            Some(graceError metadata.CorrelationId "RepositoryContentCounter command requires a non-empty ManifestAddress.")
        elif expectedPrimaryKeyMismatch expectedPrimaryKey repositoryId storagePoolId manifestAddress then
            Some(graceError metadata.CorrelationId "RepositoryContentCounter command target does not match the grain key.")
        elif targetMismatch counter repositoryId storagePoolId manifestAddress then
            Some(graceError metadata.CorrelationId "RepositoryContentCounter command target does not match the initialized counter.")
        else
            None

    let decideCommandForKey
        (expectedPrimaryKey: string option)
        (_events: seq<RepositoryContentCounterEvent>)
        (counter: RepositoryContentCounterDto)
        (command: RepositoryContentCounterCommand)
        (metadata: EventMetadata)
        : Result<RepositoryContentCounterDecision, GraceError>
        =
        let operationId = operationId command
        /// Coordinates repository id logic for the RepositoryContentCounter actor.
        let repositoryId, storagePoolId, manifestAddress = commandTarget command

        match validateCommandTarget expectedPrimaryKey counter command metadata with
        | Some error -> Error error
        | None ->
            match tryFindCompletedChange counter operationId with
            | Some change when not (changeMatchesCommand change command) ->
                Error(graceError metadata.CorrelationId "RepositoryContentCounter operation id was already used for a different command.")
            | Some change ->
                okDecision
                    counter
                    operationId
                    []
                    (intentsForCompletedChange repositoryId storagePoolId manifestAddress change)
                    true
                    "Repository content counter command replayed."
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
                                RepositoryContentCounterIntent.IncrementManifestReferenceCount(
                                    repositoryId,
                                    storagePoolId,
                                    manifestAddress,
                                    counter.Revision + 1L
                                )
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
                                    RepositoryContentCounterIntent.DecrementManifestReferenceCount(
                                        repositoryId,
                                        storagePoolId,
                                        manifestAddress,
                                        counter.Revision + 1L
                                    )
                                ]
                            else
                                []

                        okDecision counter operationId [ counterEvent ] intents false "Repository content reference removed."

    /// Validates a RepositoryContentCounter command and derives the events needed for a state transition.
    let decideCommand events counter command metadata = decideCommandForKey None events counter command metadata

    /// Resolves Redis replay, bounded persistence, and safe removal gating for one counter command.
    let handleWithRecentResult
        (recentResult: IRepositoryCounterRecentResult)
        (persistSnapshot: RepositoryContentCounterDto -> Task)
        expectedPrimaryKey
        counter
        command
        metadata
        cancellationToken
        =
        task {
            match validateCommandTarget expectedPrimaryKey counter command metadata with
            | Some error -> return Error error
            | None ->
                let repositoryId, storagePoolId, manifestAddress = commandTarget command
                let operationId = operationId command

                match! recentResult.TryGetAsync(repositoryId, storagePoolId, manifestAddress, operationId, cancellationToken) with
                | Some cachedChange when cachedChange.OperationId <> operationId ->
                    return
                        Error(graceError metadata.CorrelationId "RepositoryContentCounter recent result operation id does not match the requested operation.")
                | Some cachedChange when not (changeMatchesCommand cachedChange command) ->
                    return Error(graceError metadata.CorrelationId "RepositoryContentCounter operation id was already used for a different command.")
                | Some cachedChange ->
                    return
                        okDecision
                            counter
                            operationId
                            []
                            (intentsForCompletedChange repositoryId storagePoolId manifestAddress cachedChange)
                            true
                            "Repository content counter command replayed from recent result."
                | None ->
                    match decideCommandForKey expectedPrimaryKey Seq.empty counter command metadata with
                    | Error error -> return Error error
                    | Ok localDecision ->
                        let isRemoval =
                            match command with
                            | RepositoryContentCounterCommand.RemoveReference _ -> true
                            | RepositoryContentCounterCommand.AddReference _ -> false

                        let! previousResultCached =
                            match counter.LastCompletedChange with
                            | Some previousChange when previousChange.OperationId <> operationId ->
                                recentResult.TrySetAsync(repositoryId, storagePoolId, manifestAddress, previousChange, cancellationToken)
                            | Some _
                            | None -> Task.FromResult true

                        if isRemoval && not previousResultCached then
                            return
                                Error(
                                    graceError
                                        metadata.CorrelationId
                                        "RepositoryContentCounter removal paused because Redis could not preserve the previous completed result."
                                )
                        else
                            if not localDecision.Events.IsEmpty then
                                do! persistSnapshot localDecision.Counter

                            match localDecision.Counter.LastCompletedChange with
                            | None -> return Error(graceError metadata.CorrelationId "RepositoryContentCounter completed without a bounded result.")
                            | Some completedChange ->
                                let! completedResultCached =
                                    recentResult.TrySetAsync(repositoryId, storagePoolId, manifestAddress, completedChange, cancellationToken)

                                if isRemoval && not completedResultCached then
                                    return
                                        Error(
                                            graceError
                                                metadata.CorrelationId
                                                "RepositoryContentCounter removal was retained safely because Redis did not confirm the completed result."
                                        )
                                else
                                    return Ok localDecision
        }

    /// Implements the Orleans grain for repository content counter actor.
    type RepositoryContentCounterActor
        (
            [<PersistentState(StateName.RepositoryContentCounter, Grace.Shared.Constants.GraceActorStorage)>] state: IPersistentState<RepositoryContentCounterDto>,
            recentResult: IRepositoryCounterRecentResult
        ) =
        inherit Grain()

        let log = loggerFactory.CreateLogger("RepositoryContentCounter.Actor")
        let mutable counter = RepositoryContentCounterDto.Default
        /// Stores the correlation id used by this actor while reporting timings and errors.
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()
            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            counter <- if state.RecordExists then state.State else RepositoryContentCounterDto.Default

            Task.CompletedTask

        /// Overwrites the bounded RepositoryContentCounter snapshot after one completed transition.
        member private this.ApplySnapshot(snapshot: RepositoryContentCounterDto) : Task =
            (task {
                state.State <- snapshot
                do! state.WriteStateAsync()
                counter <- snapshot
            }
            :> Task)

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

            /// Returns no lifetime events because the actor persists only its bounded current snapshot.
            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                (Array.empty<RepositoryContentCounterEvent> :> IReadOnlyList<RepositoryContentCounterEvent>)
                |> returnTask

            /// Routes a public actor command to the domain operation that validates and persists it.
            member this.Handle command metadata =
                task {
                    this.correlationId <- metadata.CorrelationId
                    RequestContext.Set(Grace.Shared.Constants.CurrentCommandProperty, commandName command)

                    match!
                        handleWithRecentResult
                            recentResult
                            this.ApplySnapshot
                            (Some(this.GetPrimaryKeyString()))
                            counter
                            command
                            metadata
                            CancellationToken.None
                        with
                    | Ok decision ->
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
