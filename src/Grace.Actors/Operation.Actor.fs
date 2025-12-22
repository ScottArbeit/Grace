namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Events
open Grace.Types.Operation
open Grace.Types.Types
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

module Operation =

    type OperationActor([<PersistentState(StateName.Operation, Constants.GraceActorStorage)>] state: IPersistentState<List<OperationEvent>>) =
        inherit Grain()

        static let actorName = ActorName.Operation

        let log = loggerFactory.CreateLogger("Operation.Actor")

        let mutable operationDto = OperationDto.Default

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()
            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            operationDto <- state.State |> Seq.fold (fun dto ev -> OperationDto.UpdateDto ev dto) operationDto

            Task.CompletedTask

        member private this.ApplyEvent(operationEvent: OperationEvent) =
            task {
                let correlationId = operationEvent.Metadata.CorrelationId

                try
                    state.State.Add(operationEvent)
                    do! state.WriteStateAsync()

                    operationDto <- OperationDto.UpdateDto operationEvent operationDto

                    let graceEvent = GraceEvent.OperationEvent operationEvent
                    do! publishGraceEvent graceEvent operationEvent.Metadata

                    let returnValue =
                        (GraceReturnValue.Create operationDto correlationId)
                            .enhance(nameof RepositoryId, operationDto.RepositoryId)
                            .enhance(nameof OperationId, operationDto.OperationId)
                            .enhance(nameof OperationStatus, getDiscriminatedUnionCaseName operationDto.Status)
                            .enhance(nameof OperationEventType, getDiscriminatedUnionFullName operationEvent.Event)

                    return Ok returnValue
                with ex ->
                    let graceError =
                        (GraceError.CreateWithException ex "Failed while applying operation event." correlationId)
                            .enhance(nameof RepositoryId, operationDto.RepositoryId)
                            .enhance(nameof OperationId, operationDto.OperationId)
                            .enhance(nameof OperationEventType, getDiscriminatedUnionFullName operationEvent.Event)

                    return Error graceError
            }

        interface IOperationActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId
                not <| operationDto.OperationId.Equals(OperationId.Empty) |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId
                operationDto |> returnTask

            member this.GetEvents correlationId =
                this.correlationId <- correlationId
                state.State :> IReadOnlyList<OperationEvent> |> returnTask

            member this.Handle command metadata =
                let isValid (command: OperationCommand) (metadata: EventMetadata) =
                    task {
                        if state.State.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId) then
                            return Error(GraceError.Create "Duplicate correlation id." metadata.CorrelationId)
                        else
                            match command with
                            | OperationCommand.Create _ ->
                                if operationDto.OperationId <> OperationId.Empty then
                                    return Error(GraceError.Create "Operation already exists." metadata.CorrelationId)
                                else
                                    return Ok command
                            | _ ->
                                if operationDto.OperationId = OperationId.Empty then
                                    return Error(GraceError.Create "Operation does not exist." metadata.CorrelationId)
                                else
                                    return Ok command
                    }

                let applyEvent eventType =
                    let operationEvent = { Event = eventType; Metadata = metadata }
                    this.ApplyEvent operationEvent

                task {
                    match! isValid command metadata with
                    | Error error -> return Error error
                    | Ok validCommand ->
                        match validCommand with
                        | OperationCommand.Create(operationId, repositoryId) ->
                            return! applyEvent (OperationEventType.Created(operationId, repositoryId))
                        | OperationCommand.Start -> return! applyEvent OperationEventType.Started
                        | OperationCommand.UpdateProgress(percent, message) ->
                            return! applyEvent (OperationEventType.ProgressUpdated(percent, message))
                        | OperationCommand.NeedsInput message ->
                            return! applyEvent (OperationEventType.NeedsUserInput message)
                        | OperationCommand.Complete result ->
                            return! applyEvent (OperationEventType.Completed result)
                        | OperationCommand.Fail error ->
                            return! applyEvent (OperationEventType.Failed error)
                        | OperationCommand.Cancel -> return! applyEvent OperationEventType.Cancelled
                }
