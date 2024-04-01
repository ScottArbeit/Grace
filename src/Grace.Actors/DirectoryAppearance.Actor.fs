namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open Grace.Actors.Constants
open Grace.Shared.Types
open Grace.Shared
open NodaTime
open System.Collections.Generic
open System.Threading.Tasks
open Interfaces

module DirectoryAppearance =

    let GetActorId (directoryId: DirectoryId) = ActorId($"{directoryId}")

    type Appearance =
        { Root: DirectoryId
          Parent: DirectoryId
          Created: Instant }

    type AppearancesList = SortedSet<Appearance>

    type DirectoryAppearanceDto() =
        member val public Appearances: AppearancesList = AppearancesList() with get, set

    type IDirectoryAppearanceActor =
        inherit IActor
        abstract member Add: appearance: Appearance -> Task
        abstract member Remove: appearance: Appearance -> correlationId: string -> Task
        abstract member Contains: appearance: Appearance -> Task<bool>
        abstract member Appearances: unit -> Task<AppearancesList>

    type DirectoryAppearanceActor(host: ActorHost) =
        inherit Actor(host)

        let actorName = ActorName.DirectoryAppearance
        let log = host.LoggerFactory.CreateLogger(actorName)

        let dtoStateName = "directoryAppearancesDtoState"
        let mutable dto = DirectoryAppearanceDto()

        override this.OnActivateAsync() =
            let stateManager = this.StateManager

            task {
                let! directoryAppearanceDtoFromStorage =
                    task {
                        match! (Storage.RetrieveState<DirectoryAppearanceDto> stateManager dtoStateName) with
                        | Some dto -> return dto
                        | None -> return DirectoryAppearanceDto()
                    }

                dto <- directoryAppearanceDtoFromStorage
            }
            :> Task

        interface IDirectoryAppearanceActor with

            member this.Add(appearance) =
                let stateManager = this.StateManager

                task {
                    let wasAdded = dto.Appearances.Add(appearance)

                    if wasAdded then
                        do! Storage.SaveState stateManager dtoStateName dto
                }
                :> Task

            member this.Remove appearance correlationId =
                let stateManager = this.StateManager

                task {
                    let wasRemoved = dto.Appearances.Remove(appearance)

                    if wasRemoved then
                        if dto.Appearances.Count = 0 then
                            let! deleteSucceeded = Storage.DeleteState stateManager dtoStateName

                            if deleteSucceeded then
                                let directoryVersionActorProxy =
                                    Services.actorProxyFactory.CreateActorProxy<IDirectoryVersionActor>(
                                        this.Id,
                                        Constants.ActorName.DirectoryVersion
                                    )

                                let! result = directoryVersionActorProxy.Delete(correlationId)

                                match result with
                                | Ok returnValue -> ()
                                | Error error -> ()

                                ()
                        else
                            do! Storage.SaveState stateManager dtoStateName dto

                        ()
                }
                :> Task

            member this.Contains(appearance) =
                Task.FromResult(dto.Appearances.Contains(appearance))

            member this.Appearances() = Task.FromResult(dto.Appearances)
