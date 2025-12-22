namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Events
open Grace.Types.Train
open Grace.Types.Types
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

module Train =

    type TrainActor([<PersistentState(StateName.Train, Constants.GraceActorStorage)>] state: IPersistentState<List<TrainEvent>>) =
        inherit Grain()

        static let actorName = ActorName.Train

        let log = loggerFactory.CreateLogger("Train.Actor")

        let mutable trainDto = TrainDto.Default

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()
            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            trainDto <- state.State |> Seq.fold (fun dto ev -> TrainDto.UpdateDto ev dto) trainDto

            Task.CompletedTask

        member private this.ApplyEvent(trainEvent: TrainEvent) =
            task {
                let correlationId = trainEvent.Metadata.CorrelationId

                try
                    state.State.Add(trainEvent)
                    do! state.WriteStateAsync()

                    trainDto <- TrainDto.UpdateDto trainEvent trainDto

                    let graceEvent = GraceEvent.TrainEvent trainEvent
                    do! publishGraceEvent graceEvent trainEvent.Metadata

                    let returnValue =
                        (GraceReturnValue.Create trainDto correlationId)
                            .enhance(nameof RepositoryId, trainDto.RepositoryId)
                            .enhance(nameof PromotionTrainId, trainDto.PromotionTrainId)
                            .enhance(nameof TrainEventType, getDiscriminatedUnionFullName trainEvent.Event)

                    return Ok returnValue
                with ex ->
                    let graceError =
                        (GraceError.CreateWithException ex "Failed while applying train event." correlationId)
                            .enhance(nameof RepositoryId, trainDto.RepositoryId)
                            .enhance(nameof PromotionTrainId, trainDto.PromotionTrainId)
                            .enhance(nameof TrainEventType, getDiscriminatedUnionFullName trainEvent.Event)

                    return Error graceError
            }

        interface ITrainActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId
                not <| trainDto.PromotionTrainId.Equals(PromotionTrainId.Empty) |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId
                trainDto |> returnTask

            member this.GetEvents correlationId =
                this.correlationId <- correlationId
                state.State :> IReadOnlyList<TrainEvent> |> returnTask

            member this.Handle command metadata =
                let isValid (command: TrainCommand) (metadata: EventMetadata) =
                    task {
                        if state.State.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId) then
                            return Error(GraceError.Create "Duplicate correlation id." metadata.CorrelationId)
                        else
                            match command with
                            | TrainCommand.Create _ ->
                                if trainDto.PromotionTrainId <> PromotionTrainId.Empty then
                                    return Error(GraceError.Create "Train already exists." metadata.CorrelationId)
                                else
                                    return Ok command
                            | _ ->
                                if trainDto.PromotionTrainId = PromotionTrainId.Empty then
                                    return Error(GraceError.Create "Train does not exist." metadata.CorrelationId)
                                else
                                    return Ok command
                    }

                let applyEvent eventType =
                    let trainEvent = { Event = eventType; Metadata = metadata }
                    this.ApplyEvent trainEvent

                task {
                    match! isValid command metadata with
                    | Error error -> return Error error
                    | Ok validCommand ->
                        match validCommand with
                        | TrainCommand.Create(trainId, repositoryId, targetBranchId, baseReferenceId, scheduledAt) ->
                            return! applyEvent (TrainEventType.Created(trainId, repositoryId, targetBranchId, baseReferenceId, scheduledAt))
                        | TrainCommand.Enqueue changeId ->
                            return! applyEvent (TrainEventType.Enqueued changeId)
                        | TrainCommand.Dequeue changeId ->
                            return! applyEvent (TrainEventType.Dequeued changeId)
                        | TrainCommand.Remove changeId ->
                            return! applyEvent (TrainEventType.Removed changeId)
                        | TrainCommand.SetBaseReference referenceId ->
                            return! applyEvent (TrainEventType.BaseReferenceSet referenceId)
                        | TrainCommand.Schedule scheduledAt ->
                            return! applyEvent (TrainEventType.Scheduled scheduledAt)
                }
