namespace Grace.Actors

open Orleans
open Orleans.Runtime
open Grace.Actors
open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Shared
open Grace.Shared.Constants
open Grace.Types.Reminder
open Grace.Types.Types
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Concurrent
open System.Threading.Tasks

module Reminder =

    /// Orleans implementation of the ReminderActor.
    type ReminderActor([<PersistentState(StateName.Reminder, Constants.GraceActorStorage)>] reminderState: IPersistentState<ReminderWrapper>) =
        inherit Grain()

        static let actorName = ActorName.Reminder

        let log = loggerFactory.CreateLogger("Reminder.Actor")

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage reminderState.RecordExists)

            Task.CompletedTask

        interface IReminderActor with
            member this.Create (reminder: ReminderDto) (correlationId: CorrelationId) =
                task {
                    try
                        reminderState.State.Reminder <- reminder
                        do! reminderState.WriteStateAsync()

                        log.LogTrace(
                            "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Created reminder {ReminderId}. Actor {ActorName}||{ActorId}.",
                            getCurrentInstantExtended (),
                            getMachineName,
                            reminder.CorrelationId,
                            reminder.ReminderId,
                            reminder.ActorName,
                            reminder.ActorId
                        )

                        return ()
                    with ex ->
                        log.LogError(
                            ex,
                            "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Error creating reminder {ReminderId}. Actor {ActorName}||{ActorId}.",
                            getCurrentInstantExtended (),
                            getMachineName,
                            correlationId,
                            reminder.ReminderId,
                            reminder.ActorName,
                            reminder.ActorId
                        )

                        return ()
                }
                :> Task

            member this.Delete(correlationId: CorrelationId) =
                task {
                    let reminderDto = reminderState.State.Reminder

                    try
                        this.correlationId <- correlationId
                        do! reminderState.ClearStateAsync()

                        if not reminderState.RecordExists then
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

                if reminderState.State.Reminder.ReminderTime = Instant.MinValue then
                    false |> returnTask
                else
                    true |> returnTask

            member this.Get(correlationId: CorrelationId) =
                this.correlationId <- correlationId
                reminderState.State.Reminder |> returnTask

            member this.Remind(correlationId: CorrelationId) : Task<Result<unit, GraceError>> =
                task {
                    let reminderDto = reminderState.State.Reminder

                    try
                        this.correlationId <- correlationId

                        // Parse the Guid from the ActorId. Example: "referenceactor/da3926330c394275813d95e390a5c374"
                        let actorId =
                            if reminderDto.ActorName = ActorName.Diff then
                                // Diff actors have a different ActorId format: "directoryVersionId1*directoryVersionId2"
                                Guid.Empty
                            else
                                Guid.ParseExact(reminderDto.ActorId.Split("/").[1], "N")

                        match reminderDto.ActorName with
                        | ActorName.Owner ->
                            let ownerActorProxy = Owner.CreateActorProxy actorId correlationId
                            return! ownerActorProxy.ReceiveReminderAsync reminderDto
                        | ActorName.Organization ->
                            let organizationActorProxy = Organization.CreateActorProxy actorId correlationId
                            return! organizationActorProxy.ReceiveReminderAsync reminderDto
                        | ActorName.Repository ->
                            let repositoryActorProxy = Repository.CreateActorProxy actorId reminderDto.RepositoryId correlationId
                            return! repositoryActorProxy.ReceiveReminderAsync reminderDto
                        | ActorName.Branch ->
                            let branchActorProxy = Branch.CreateActorProxy actorId reminderDto.RepositoryId correlationId
                            return! branchActorProxy.ReceiveReminderAsync reminderDto
                        | ActorName.DirectoryVersion ->
                            let directoryVersionActorProxy = DirectoryVersion.CreateActorProxy actorId reminderDto.RepositoryId correlationId

                            return! directoryVersionActorProxy.ReceiveReminderAsync reminderDto
                        | ActorName.Reference ->
                            let referenceActorProxy = Reference.CreateActorProxy actorId reminderDto.RepositoryId correlationId
                            return! referenceActorProxy.ReceiveReminderAsync reminderDto
                        | ActorName.Diff ->
                            let directoryIds = reminderDto.ActorId.Split("/").[1].Split("*")

                            let diffActorProxy =
                                Diff.CreateActorProxy
                                    (Guid.ParseExact(directoryIds[0], "N"))
                                    (Guid.ParseExact(directoryIds[1], "N"))
                                    reminderDto.OrganizationId
                                    reminderDto.RepositoryId
                                    correlationId

                            return! diffActorProxy.ReceiveReminderAsync reminderDto
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
