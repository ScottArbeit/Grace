namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open Grace.Actors.Constants
open Grace.Shared.Types
open Grace.Shared
open NodaTime
open System.Collections.Generic
open System.Threading.Tasks

module FileAppearance =

    let GetActorId (fileVersion: FileVersion) = ActorId($"{fileVersion.RepositoryId}*{fileVersion.RelativePath}*{fileVersion.Sha256Hash}")

    type Appearance = { Root: DirectoryId; Parent: DirectoryId; Created: Instant }

    type AppearancesList = SortedSet<Appearance>

    type FileAppearanceDto() =
        member val public Appearances: AppearancesList = AppearancesList() with get, set

    type IFileAppearanceActor =
        inherit IActor
        abstract member Add: appearance: Appearance -> Task
        abstract member Remove: appearance: Appearance -> Task
        abstract member Contains: appearance: Appearance -> Task<bool>
        abstract member Appearances: unit -> Task<AppearancesList>

    type FileAppearanceActor(host: ActorHost) =
        inherit Actor(host)

        let dtoStateName = StateName.FileAppearance
        let mutable dto = FileAppearanceDto()

        override this.OnActivateAsync() =
            let stateManager = this.StateManager

            task {
                let! fileAppearanceDtoFromStorage =
                    task {
                        match! (Storage.RetrieveState<FileAppearanceDto> stateManager dtoStateName) with
                        | Some dto -> return dto
                        | None -> return FileAppearanceDto()
                    }

                dto <- fileAppearanceDtoFromStorage
            }
            :> Task

        interface IFileAppearanceActor with

            member this.Add(appearance) =
                let stateManager = this.StateManager

                task {
                    let wasAdded = dto.Appearances.Add(appearance)

                    if wasAdded then
                        let! _ = stateManager.SetStateAsync<FileAppearanceDto>(dtoStateName, dto)
                        ()
                }
                :> Task

            member this.Remove(appearance) =
                let stateManager = this.StateManager

                task {
                    let wasRemoved = dto.Appearances.Remove(appearance)

                    if wasRemoved then
                        if dto.Appearances.Count = 0 then
                            // TODO: Delete the file from storage
                            do! stateManager.RemoveStateAsync(dtoStateName)
                        else
                            do! stateManager.SetStateAsync<FileAppearanceDto>(dtoStateName, dto)

                        ()
                }
                :> Task

            member this.Contains(appearance) = Task.FromResult(dto.Appearances.Contains(appearance))

            member this.Appearances() = Task.FromResult(dto.Appearances)
