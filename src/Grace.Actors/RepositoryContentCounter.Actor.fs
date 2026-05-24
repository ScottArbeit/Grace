namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.RepositoryContentCounter
open Grace.Types.Types
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

module RepositoryContentCounter =

    let primaryKey (repositoryId: RepositoryId) (manifestAddress: ManifestAddress) = $"{repositoryId:N}|{manifestAddress}"

    let commandName command =
        match command with
        | RepositoryContentCounterCommand.AddReference _ -> "AddReference"
        | RepositoryContentCounterCommand.RemoveReference _ -> "RemoveReference"

    let operationId command =
        match command with
        | RepositoryContentCounterCommand.AddReference (operationId, _, _) -> operationId
        | RepositoryContentCounterCommand.RemoveReference (operationId, _, _) -> operationId

    let private commandTarget command =
        match command with
        | RepositoryContentCounterCommand.AddReference (_, repositoryId, manifestAddress)
        | RepositoryContentCounterCommand.RemoveReference (_, repositoryId, manifestAddress) -> repositoryId, manifestAddress

    let private eventOperationId counterEvent =
        match counterEvent.Event with
        | RepositoryContentCounterEventType.ReferenceAdded (operationId, _, _) -> operationId
        | RepositoryContentCounterEventType.ReferenceRemoved operationId -> operationId

    let private hasAppliedOperationId (events: seq<RepositoryContentCounterEvent>) operationId =
        events
        |> Seq.exists (fun counterEvent -> eventOperationId counterEvent = operationId)

    let private applyEvents (events: RepositoryContentCounterEvent list) (counter: RepositoryContentCounterDto) =
        events
        |> List.fold (fun current event -> RepositoryContentCounterDto.UpdateDto event current) counter

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

    let private graceError correlationId message = GraceError.Create message correlationId

    let private targetMismatch (counter: RepositoryContentCounterDto) repositoryId manifestAddress =
        (counter.RepositoryId <> RepositoryId.Empty
         && counter.RepositoryId <> repositoryId)
        || (not (String.IsNullOrWhiteSpace counter.ManifestAddress)
            && counter.ManifestAddress <> manifestAddress)

    let private expectedPrimaryKeyMismatch expectedPrimaryKey repositoryId manifestAddress =
        match expectedPrimaryKey with
        | Some expectedPrimaryKey -> not (String.Equals(expectedPrimaryKey, primaryKey repositoryId manifestAddress, StringComparison.Ordinal))
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
        let repositoryId, manifestAddress = commandTarget command

        if String.IsNullOrWhiteSpace operationId then
            Error(graceError metadata.CorrelationId "RepositoryContentCounter command requires a non-empty operation id.")
        elif repositoryId = RepositoryId.Empty then
            Error(graceError metadata.CorrelationId "RepositoryContentCounter command requires a non-empty RepositoryId.")
        elif String.IsNullOrWhiteSpace manifestAddress then
            Error(graceError metadata.CorrelationId "RepositoryContentCounter command requires a non-empty ManifestAddress.")
        elif expectedPrimaryKeyMismatch expectedPrimaryKey repositoryId manifestAddress then
            Error(graceError metadata.CorrelationId "RepositoryContentCounter command target does not match the grain key.")
        elif targetMismatch counter repositoryId manifestAddress then
            Error(graceError metadata.CorrelationId "RepositoryContentCounter command target does not match the initialized counter.")
        elif hasAppliedOperationId events operationId then
            okDecision counter operationId [] [] true "Repository content counter command replayed."
        else
            match command with
            | RepositoryContentCounterCommand.AddReference _ ->
                let counterEvent = { Event = RepositoryContentCounterEventType.ReferenceAdded(operationId, repositoryId, manifestAddress); Metadata = metadata }

                let intents =
                    if counter.ReferenceCount = 0L then
                        [
                            RepositoryContentCounterIntent.IncrementManifestReferenceCount(repositoryId, manifestAddress)
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
                                RepositoryContentCounterIntent.DecrementManifestReferenceCount(repositoryId, manifestAddress)
                            ]
                        else
                            []

                    okDecision counter operationId [ counterEvent ] intents false "Repository content reference removed."

    let decideCommand events counter command metadata = decideCommandForKey None events counter command metadata

    type RepositoryContentCounterActor
        (
            [<PersistentState(StateName.RepositoryContentCounter, Grace.Shared.Constants.GraceActorStorage)>] state: IPersistentState<List<RepositoryContentCounterEvent>>
        ) =
        inherit Grain()

        let log = loggerFactory.CreateLogger("RepositoryContentCounter.Actor")
        let mutable counter = RepositoryContentCounterDto.Default
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()
            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            counter <-
                state.State
                |> Seq.fold (fun dto event -> RepositoryContentCounterDto.UpdateDto event dto) RepositoryContentCounterDto.Default

            Task.CompletedTask

        member private this.ApplyEvents(events: RepositoryContentCounterEvent list) =
            task {
                state.State.AddRange(events)
                do! state.WriteStateAsync()
                counter <- applyEvents events counter
            }

        interface IRepositoryContentCounterActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId

                (counter.RepositoryId <> RepositoryId.Empty
                 && not (String.IsNullOrWhiteSpace counter.ManifestAddress))
                |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId
                counter |> returnTask

            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                (state.State :> IReadOnlyList<RepositoryContentCounterEvent>)
                |> returnTask

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
