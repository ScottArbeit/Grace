namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Change
open Grace.Types.Events
open Grace.Types.Types
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

module Change =

    type ChangeActor([<PersistentState(StateName.Change, Constants.GraceActorStorage)>] state: IPersistentState<List<ChangeEvent>>) =
        inherit Grain()

        static let actorName = ActorName.Change

        let log = loggerFactory.CreateLogger("Change.Actor")

        let mutable changeDto = ChangeDto.Default

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()
            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            changeDto <- state.State |> Seq.fold (fun dto ev -> ChangeDto.UpdateDto ev dto) changeDto

            Task.CompletedTask

        member private this.ApplyEvent(changeEvent: ChangeEvent) =
            task {
                let correlationId = changeEvent.Metadata.CorrelationId

                try
                    state.State.Add(changeEvent)
                    do! state.WriteStateAsync()

                    changeDto <- ChangeDto.UpdateDto changeEvent changeDto

                    let graceEvent = GraceEvent.ChangeEvent changeEvent
                    do! publishGraceEvent graceEvent changeEvent.Metadata

                    let returnValue =
                        (GraceReturnValue.Create changeDto correlationId)
                            .enhance(nameof RepositoryId, changeDto.RepositoryId)
                            .enhance(nameof ChangeId, changeDto.ChangeId)
                            .enhance("Status", getDiscriminatedUnionCaseName changeDto.Status)
                            .enhance(nameof ChangeEventType, getDiscriminatedUnionFullName changeEvent.Event)

                    return Ok returnValue
                with ex ->
                    let graceError =
                        (GraceError.CreateWithException ex "Failed while applying change event." correlationId)
                            .enhance(nameof RepositoryId, changeDto.RepositoryId)
                            .enhance(nameof ChangeId, changeDto.ChangeId)
                            .enhance(nameof ChangeEventType, getDiscriminatedUnionFullName changeEvent.Event)

                    return Error graceError
            }

        interface IChangeActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId
                not <| changeDto.ChangeId.Equals(ChangeId.Empty) |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId
                changeDto |> returnTask

            member this.GetEvents correlationId =
                this.correlationId <- correlationId
                state.State :> IReadOnlyList<ChangeEvent> |> returnTask

            member this.Handle command metadata =
                let isValid (command: ChangeCommand) (metadata: EventMetadata) =
                    task {
                        if state.State.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId) then
                            return Error(GraceError.Create "Duplicate correlation id." metadata.CorrelationId)
                        else
                            match command with
                            | ChangeCommand.Create _ ->
                                if changeDto.ChangeId <> ChangeId.Empty then
                                    return Error(GraceError.Create "Change already exists." metadata.CorrelationId)
                                else
                                    return Ok command
                            | _ ->
                                if changeDto.ChangeId = ChangeId.Empty then
                                    return Error(GraceError.Create "Change does not exist." metadata.CorrelationId)
                                else
                                    return Ok command
                    }

                let applyEvents (eventTypes: ChangeEventType list) =
                    task {
                        let mutable lastResult = Ok(GraceReturnValue.Create changeDto metadata.CorrelationId)
                        let mutable failure: GraceError option = None

                        for eventType in eventTypes do
                            if failure.IsNone then
                                let changeEvent = { Event = eventType; Metadata = metadata }

                                match! this.ApplyEvent changeEvent with
                                | Ok graceReturnValue -> lastResult <- Ok graceReturnValue
                                | Error error -> failure <- Some error

                        match failure with
                        | Some error -> return Error error
                        | None -> return lastResult
                    }

                task {
                    match! isValid command metadata with
                    | Error error -> return Error error
                    | Ok validCommand ->
                        match validCommand with
                        | ChangeCommand.Create(changeId, repositoryId, title, description, initialRevision, updatedPaths) ->
                            let created = ChangeEventType.Created(changeId, repositoryId, title, description)

                            let events =
                                match initialRevision, updatedPaths with
                                | Some revision, Some paths -> [ created; ChangeEventType.RevisionAdded(revision, paths) ]
                                | _ -> [ created ]

                            return! applyEvents events
                        | ChangeCommand.AddRevision(revision, updatedPaths) ->
                            return! applyEvents [ ChangeEventType.RevisionAdded(revision, updatedPaths) ]
                        | ChangeCommand.SetCurrentRevision(referenceId, updatedPaths) ->
                            return! applyEvents [ ChangeEventType.CurrentRevisionSet(referenceId, updatedPaths) ]
                        | ChangeCommand.UpdateMetadata(title, description) ->
                            return! applyEvents [ ChangeEventType.MetadataUpdated(title, description) ]
                        | ChangeCommand.SetStatus status ->
                            return! applyEvents [ ChangeEventType.StatusSet status ]
                }
