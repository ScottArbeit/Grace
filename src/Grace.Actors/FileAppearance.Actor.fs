namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Generic
open System.Threading.Tasks

module FileAppearance =

    let GetActorId (fileVersion: FileVersion) = ActorId($"{fileVersion.RepositoryId}*{fileVersion.RelativePath}*{fileVersion.Sha256Hash}")
    let actorName = ActorName.FileAppearance
    let log = loggerFactory.CreateLogger("FileAppearance.Actor")

    type Appearance = { Root: DirectoryVersionId; Parent: DirectoryVersionId; Created: Instant }

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

        let mutable stateManager = Unchecked.defaultof<IActorStateManager>
        let mutable correlationId = String.Empty

        let dtoStateName = StateName.FileAppearance
        let mutable dto = FileAppearanceDto()

        override this.OnActivateAsync() =
            stateManager <- this.StateManager

            task {
                let! fileAppearanceDtoFromStorage =
                    task {
                        match! (Storage.RetrieveState<FileAppearanceDto> stateManager dtoStateName correlationId) with
                        | Some dto -> return dto
                        | None -> return FileAppearanceDto()
                    }

                dto <- fileAppearanceDtoFromStorage
            }
            :> Task

        interface IGraceReminder with
            /// Schedules a Grace reminder.
            member this.ScheduleReminderAsync reminderType delay state correlationId =
                task {
                    let reminder = ReminderDto.Create actorName $"{this.Id}" reminderType (getFutureInstant delay) state correlationId
                    do! createReminder reminder
                }
                :> Task

            /// Receives a Grace reminder.
            member this.ReceiveReminderAsync(reminder: ReminderDto) : Task<Result<unit, GraceError>> =
                logToConsole $"Received a reminder: {reminder}."
                Ok() |> returnTask

        interface IFileAppearanceActor with

            member this.Add(appearance) =
                task {
                    let wasAdded = dto.Appearances.Add(appearance)

                    if wasAdded then
                        let! _ = stateManager.SetStateAsync<FileAppearanceDto>(dtoStateName, dto)
                        ()
                }
                :> Task

            member this.Remove(appearance) =
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
