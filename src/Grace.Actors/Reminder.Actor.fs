namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open Grace.Actors
open Grace.Actors.Constants
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Context
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Types
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Interfaces

module Reminder =

    type ReminderActor(host: ActorHost) =
        inherit Actor(host)

        static let actorName = ActorName.Reminder
        static let dtoStateName = StateName.Reminder

        let mutable actorStartTime = Instant.MinValue
        let mutable logScope: IDisposable = null
        let mutable stateManager: IActorStateManager = null
        let log = loggerFactory.CreateLogger("Reminder.Actor")

        let mutable reminderDto = ReminderDto.Default

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync() =
            let activateStartTime = getCurrentInstant ()
            stateManager <- this.StateManager

            let correlationId =
                match memoryCache.GetCorrelationIdEntry this.Id with
                | Some correlationId -> correlationId
                | None -> String.Empty

            task {
                try
                    let! retrievedDto = Storage.RetrieveState<ReminderDto> stateManager dtoStateName correlationId

                    match retrievedDto with
                    | Some retrievedDto -> reminderDto <- retrievedDto
                    | None -> reminderDto <- ReminderDto.Default

                    logActorActivation log activateStartTime correlationId actorName this.Id (getActorActivationMessage retrievedDto)
                with ex ->
                    let exc = ExceptionResponse.Create ex
                    log.LogError("{CurrentInstant} Error activating {ActorType} {ActorId}.", getCurrentInstantExtended (), this.GetType().Name, host.Id)
                    log.LogError("{CurrentInstant} {ExceptionDetails}", getCurrentInstantExtended (), exc.ToString())
                    logActorActivation log activateStartTime correlationId actorName this.Id "Exception occurred during activation."
            }
            :> Task

        override this.OnPreActorMethodAsync(context) =
            actorStartTime <- getCurrentInstant ()
            logScope <- log.BeginScope("Actor {actorName}", actorName)
            log.LogTrace("{CurrentInstant}: Started {ActorName}.{MethodName} Id: {Id}.", getCurrentInstantExtended (), actorName, context.MethodName, this.Id)
            Task.CompletedTask

        override this.OnPostActorMethodAsync(context) =
            let duration_ms = getPaddedDuration_ms actorStartTime

            log.LogInformation(
                "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {CorrelationId}; Finished {ActorName}.{MethodName}. Actor {ActorName}||{ActorId}.",
                getCurrentInstantExtended (),
                getMachineName,
                duration_ms,
                this.correlationId,
                actorName,
                context.MethodName,
                reminderDto.ActorName,
                reminderDto.ActorId
            )

            Task.CompletedTask

        interface IReminderActor with

            member this.Create (reminder: ReminderDto) (correlationId: CorrelationId) =
                task {
                    try
                        reminderDto <- reminder
                        this.correlationId <- correlationId
                        do! Storage.SaveState stateManager dtoStateName reminderDto correlationId

                        log.LogTrace(
                            "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Created reminder {ReminderId}. Actor {ActorName}||{ActorId}.",
                            getCurrentInstantExtended (),
                            getMachineName,
                            correlationId,
                            reminderDto.ReminderId,
                            reminderDto.ActorName,
                            reminderDto.ActorId
                        )

                        return ()
                    with ex ->
                        log.LogError(
                            "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Error creating reminder {ReminderId}. Actor {ActorName}||{ActorId}. {ExceptionDetails}",
                            getCurrentInstantExtended (),
                            getMachineName,
                            correlationId,
                            reminderDto.ReminderId,
                            reminderDto.ActorName,
                            reminderDto.ActorId,
                            ExceptionResponse.Create ex
                        )

                        return ()
                }
                :> Task

            member this.Delete(correlationId: CorrelationId) =
                task {
                    try
                        this.correlationId <- correlationId
                        let! deleted = Storage.DeleteState stateManager dtoStateName

                        if deleted then
                            log.LogInformation(
                                "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Deleted reminder {ReminderId}. Actor {ActorName}||{ActorId}.",
                                getCurrentInstantExtended (),
                                getMachineName,
                                correlationId,
                                reminderDto.ReminderId,
                                reminderDto.ActorName,
                                reminderDto.ActorId
                            )
                        else
                            log.LogWarning(
                                "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; State for Reminder {ReminderId} was not deleted. It may not have been found. Actor {ActorName}||{ActorId}.",
                                getCurrentInstantExtended (),
                                getMachineName,
                                correlationId,
                                reminderDto.ReminderId,
                                reminderDto.ActorName,
                                reminderDto.ActorId
                            )

                        reminderDto <- ReminderDto.Default
                        return ()
                    with ex ->
                        log.LogError(
                            "{CurrentInstant}:  Node: {HostName}; CorrelationId: {CorrelationId}; Error deleting reminder {ReminderId}. Actor {ActorName}||{ActorId}. {ExceptionDetails}",
                            getCurrentInstantExtended (),
                            getMachineName,
                            correlationId,
                            reminderDto.ReminderId,
                            reminderDto.ActorName,
                            reminderDto.ActorId,
                            ExceptionResponse.Create ex
                        )

                        return ()
                }
                :> Task

            member this.Exists(correlationId: CorrelationId) : Task<bool> =
                this.correlationId <- correlationId

                if reminderDto.ReminderTime = Instant.MinValue then
                    false |> returnTask
                else
                    true |> returnTask

            member this.Get(correlationId: CorrelationId) =
                this.correlationId <- correlationId
                reminderDto |> returnTask

            member this.Remind(correlationId: CorrelationId) : Task<Result<unit, GraceError>> =
                task {
                    try
                        this.correlationId <- correlationId

                        match reminderDto.ActorName with
                        | ActorName.Owner ->
                            let ownerActorProxy = Owner.CreateActorProxy (Guid.Parse(reminderDto.ActorId)) correlationId
                            return! ownerActorProxy.ReceiveReminderAsync reminderDto
                        | ActorName.Organization ->
                            let organizationActorProxy = Organization.CreateActorProxy (Guid.Parse(reminderDto.ActorId)) correlationId
                            return! organizationActorProxy.ReceiveReminderAsync reminderDto
                        | ActorName.Repository ->
                            let repositoryActorProxy = Repository.CreateActorProxy (Guid.Parse(reminderDto.ActorId)) correlationId
                            return! repositoryActorProxy.ReceiveReminderAsync reminderDto
                        | ActorName.Branch ->
                            let branchActorProxy = Branch.CreateActorProxy (Guid.Parse(reminderDto.ActorId)) correlationId
                            return! branchActorProxy.ReceiveReminderAsync reminderDto
                        | ActorName.DirectoryVersion ->
                            let directoryVersionActorProxy = DirectoryVersion.CreateActorProxy (Guid.Parse(reminderDto.ActorId)) correlationId
                            return! directoryVersionActorProxy.ReceiveReminderAsync reminderDto
                        | ActorName.Diff ->
                            let directoryIds = reminderDto.ActorId.Split("*")
                            let diffActorProxy = Diff.CreateActorProxy (DirectoryVersionId directoryIds[0]) (DirectoryVersionId directoryIds[1]) correlationId
                            return! diffActorProxy.ReceiveReminderAsync reminderDto
                        | ActorName.Reference ->
                            let referenceActorProxy = Reference.CreateActorProxy (Guid.Parse(reminderDto.ActorId)) correlationId
                            return! referenceActorProxy.ReceiveReminderAsync reminderDto
                        | _ -> return Ok()
                    with ex ->
                        log.LogError(
                            "{CurrentInstant}:  Node: {HostName}; CorrelationId: {CorrelationId}; Error reminding actor {ActorName}||{ActorId}. {ExceptionDetails}",
                            getCurrentInstantExtended (),
                            getMachineName,
                            correlationId,
                            reminderDto.ActorName,
                            reminderDto.ActorId,
                            ExceptionResponse.Create ex
                        )

                        return
                            Error(
                                (GraceError.Create "Failed to execute reminder." correlationId)
                                    .enhance("reminder", (serialize reminderDto))
                                    .enhance ("exception", $"{ExceptionResponse.Create ex}")
                            )
                }
