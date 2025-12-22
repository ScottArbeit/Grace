namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Events
open Grace.Types.Stack
open Grace.Types.Types
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

module Stack =

    type StackActor([<PersistentState(StateName.Stack, Constants.GraceActorStorage)>] state: IPersistentState<List<StackEvent>>) =
        inherit Grain()

        static let actorName = ActorName.Stack

        let log = loggerFactory.CreateLogger("Stack.Actor")

        let mutable stackDto = StackDto.Default

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()
            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            stackDto <- state.State |> Seq.fold (fun dto ev -> StackDto.UpdateDto ev dto) stackDto

            Task.CompletedTask

        member private this.ApplyEvent(stackEvent: StackEvent) =
            task {
                let correlationId = stackEvent.Metadata.CorrelationId

                try
                    state.State.Add(stackEvent)
                    do! state.WriteStateAsync()

                    stackDto <- StackDto.UpdateDto stackEvent stackDto

                    let graceEvent = GraceEvent.StackEvent stackEvent
                    do! publishGraceEvent graceEvent stackEvent.Metadata

                    let returnValue =
                        (GraceReturnValue.Create stackDto correlationId)
                            .enhance(nameof RepositoryId, stackDto.RepositoryId)
                            .enhance(nameof StackId, stackDto.StackId)
                            .enhance(nameof StackEventType, getDiscriminatedUnionFullName stackEvent.Event)

                    return Ok returnValue
                with ex ->
                    let graceError =
                        (GraceError.CreateWithException ex "Failed while applying stack event." correlationId)
                            .enhance(nameof RepositoryId, stackDto.RepositoryId)
                            .enhance(nameof StackId, stackDto.StackId)
                            .enhance(nameof StackEventType, getDiscriminatedUnionFullName stackEvent.Event)

                    return Error graceError
            }

        interface IStackActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId
                not <| stackDto.StackId.Equals(StackId.Empty) |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId
                stackDto |> returnTask

            member this.GetEvents correlationId =
                this.correlationId <- correlationId
                state.State :> IReadOnlyList<StackEvent> |> returnTask

            member this.Handle command metadata =
                let isValid (command: StackCommand) (metadata: EventMetadata) =
                    task {
                        if state.State.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId) then
                            return Error(GraceError.Create "Duplicate correlation id." metadata.CorrelationId)
                        else
                            match command with
                            | StackCommand.Create _ ->
                                if stackDto.StackId <> StackId.Empty then
                                    return Error(GraceError.Create "Stack already exists." metadata.CorrelationId)
                                else
                                    return Ok command
                            | _ ->
                                if stackDto.StackId = StackId.Empty then
                                    return Error(GraceError.Create "Stack does not exist." metadata.CorrelationId)
                                else
                                    return Ok command
                    }

                let applyEvent eventType =
                    let stackEvent = { Event = eventType; Metadata = metadata }
                    this.ApplyEvent stackEvent

                task {
                    match! isValid command metadata with
                    | Error error -> return Error error
                    | Ok validCommand ->
                        match validCommand with
                        | StackCommand.Create(stackId, repositoryId, baseBranchId, layers) ->
                            return! applyEvent (StackEventType.Created(stackId, repositoryId, baseBranchId, layers))
                        | StackCommand.AddLayer layer ->
                            return! applyEvent (StackEventType.LayerAdded layer)
                        | StackCommand.RemoveLayer changeId ->
                            return! applyEvent (StackEventType.LayerRemoved changeId)
                        | StackCommand.ReorderLayers layers ->
                            return! applyEvent (StackEventType.LayersReordered layers)
                        | StackCommand.UpdateBaseBranch baseBranchId ->
                            return! applyEvent (StackEventType.BaseBranchUpdated baseBranchId)
                }
