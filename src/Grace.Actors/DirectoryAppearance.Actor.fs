namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Shared.Types
open Grace.Shared
open NodaTime
open System
open System.Collections.Generic
open System.Threading.Tasks
open Interfaces
open Grace.Shared.Utilities

module DirectoryAppearance =

    let GetActorId (directoryId: DirectoryVersionId) = ActorId($"{directoryId}")

    type Appearance = { Root: DirectoryVersionId; Parent: DirectoryVersionId; Created: Instant }

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
        let log = host.LoggerFactory.CreateLogger("DirectoryAppearance.Actor")

        let mutable stateManager = Unchecked.defaultof<IActorStateManager>

        let dtoStateName = StateName.DirectoryAppearance
        let mutable dto = DirectoryAppearanceDto()

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync() =
            stateManager <- this.StateManager

            task {
                let! directoryAppearanceDtoFromStorage =
                    task {
                        match! (Storage.RetrieveState<DirectoryAppearanceDto> stateManager dtoStateName this.correlationId) with
                        | Some dto -> return dto
                        | None -> return DirectoryAppearanceDto()
                    }

                dto <- directoryAppearanceDtoFromStorage
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

        interface IDirectoryAppearanceActor with

            member this.Add(appearance) =
                task {
                    let wasAdded = dto.Appearances.Add(appearance)

                    if wasAdded then
                        do! Storage.SaveState stateManager dtoStateName dto this.correlationId
                }
                :> Task

            member this.Remove appearance correlationId =
                task {
                    let wasRemoved = dto.Appearances.Remove(appearance)

                    if wasRemoved then
                        if dto.Appearances.Count = 0 then
                            let! deleteSucceeded = Storage.DeleteState stateManager dtoStateName

                            if deleteSucceeded then
                                let directoryVersionGuid = Guid.Parse($"{this.Id}")
                                let directoryVersionActorProxy = DirectoryVersion.CreateActorProxy directoryVersionGuid correlationId

                                let! result = directoryVersionActorProxy.Delete(correlationId)

                                match result with
                                | Ok returnValue -> ()
                                | Error error -> ()

                                ()
                        else
                            do! Storage.SaveState stateManager dtoStateName dto this.correlationId

                        ()
                }
                :> Task

            member this.Contains(appearance) = Task.FromResult(dto.Appearances.Contains(appearance))

            member this.Appearances() = Task.FromResult(dto.Appearances)
