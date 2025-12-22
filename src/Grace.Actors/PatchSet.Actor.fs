namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Events
open Grace.Types.PatchSet
open Grace.Types.Types
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

module PatchSet =

    type PatchSetActor([<PersistentState(StateName.PatchSet, Constants.GraceActorStorage)>] state: IPersistentState<List<PatchSetEvent>>) =
        inherit Grain()

        static let actorName = ActorName.PatchSet

        let log = loggerFactory.CreateLogger("PatchSet.Actor")

        let mutable patchSetDto = PatchSetDto.Default

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()
            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            patchSetDto <- state.State |> Seq.fold (fun dto ev -> PatchSetDto.UpdateDto ev dto) patchSetDto

            Task.CompletedTask

        member private this.ApplyEvent(patchSetEvent: PatchSetEvent) =
            task {
                let correlationId = patchSetEvent.Metadata.CorrelationId

                try
                    state.State.Add(patchSetEvent)
                    do! state.WriteStateAsync()

                    patchSetDto <- PatchSetDto.UpdateDto patchSetEvent patchSetDto

                    let graceEvent = GraceEvent.PatchSetEvent patchSetEvent
                    do! publishGraceEvent graceEvent patchSetEvent.Metadata

                    let returnValue =
                        (GraceReturnValue.Create patchSetDto correlationId)
                            .enhance(nameof RepositoryId, patchSetDto.RepositoryId)
                            .enhance(nameof PatchSetId, patchSetDto.PatchSetId)
                            .enhance(nameof PatchSetEventType, getDiscriminatedUnionFullName patchSetEvent.Event)

                    return Ok returnValue
                with ex ->
                    let graceError =
                        (GraceError.CreateWithException ex "Failed while applying patch set event." correlationId)
                            .enhance(nameof RepositoryId, patchSetDto.RepositoryId)
                            .enhance(nameof PatchSetId, patchSetDto.PatchSetId)
                            .enhance(nameof PatchSetEventType, getDiscriminatedUnionFullName patchSetEvent.Event)

                    return Error graceError
            }

        interface IPatchSetActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId
                not <| patchSetDto.PatchSetId.Equals(PatchSetId.Empty) |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId
                patchSetDto |> returnTask

            member this.GetEvents correlationId =
                this.correlationId <- correlationId
                state.State :> IReadOnlyList<PatchSetEvent> |> returnTask

            member this.Handle command metadata =
                let isValid (command: PatchSetCommand) (metadata: EventMetadata) =
                    task {
                        if state.State.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId) then
                            return Error(GraceError.Create "Duplicate correlation id." metadata.CorrelationId)
                        else
                            match command with
                            | PatchSetCommand.Create _ ->
                                if patchSetDto.PatchSetId <> PatchSetId.Empty then
                                    return Error(GraceError.Create "PatchSet already exists." metadata.CorrelationId)
                                else
                                    return Ok command
                    }

                task {
                    match! isValid command metadata with
                    | Error error -> return Error error
                    | Ok validCommand ->
                        match validCommand with
                        | PatchSetCommand.Create(patchSetId, repositoryId, baseDirectoryId, headDirectoryId, updatedPaths, files, createdBy) ->
                            let dto =
                                { PatchSetDto.Default with
                                    PatchSetId = patchSetId
                                    RepositoryId = repositoryId
                                    BaseDirectoryId = baseDirectoryId
                                    HeadDirectoryId = headDirectoryId
                                    UpdatedPaths = updatedPaths
                                    Files = files
                                    CreatedAt = metadata.Timestamp
                                    CreatedBy = createdBy }

                            let patchSetEvent = { Event = PatchSetEventType.Created dto; Metadata = metadata }
                            return! this.ApplyEvent patchSetEvent
                }
