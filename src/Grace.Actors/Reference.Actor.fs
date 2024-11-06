namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open Grace.Actors.Commands
open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Dto.Reference
open Grace.Shared.Types
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Generic
open System.Threading.Tasks
open Types
open Events.Reference
open Grace.Shared.Validation.Errors.Reference
open Grace.Shared.Constants
open Commands.Reference

module Reference =

    type PhysicalDeletionReminderState = (RepositoryId * BranchId * DirectoryVersionId * Sha256Hash * DeleteReason * CorrelationId)

    type ReferenceActor(host: ActorHost) =
        inherit Actor(host)

        let actorName = ActorName.Reference
        let mutable actorStartTime = Instant.MinValue
        let log = loggerFactory.CreateLogger("Reference.Actor")
        let mutable logScope: IDisposable = null
        let mutable currentCommand = String.Empty

        let eventsStateName = StateName.Reference
        let mutable referenceDto = ReferenceDto.Default
        let referenceEvents = List<ReferenceEvent>()

        /// Indicates that the actor is in an undefined state, and should be reset.
        let mutable isDisposed = false

        let updateDto referenceEvent currentReferenceDto =
            let newReferenceDto =
                match referenceEvent.Event with
                | Created createdDto ->
                    { currentReferenceDto with
                        ReferenceId = createdDto.ReferenceId
                        RepositoryId = createdDto.RepositoryId
                        BranchId = createdDto.BranchId
                        DirectoryId = createdDto.DirectoryId
                        Sha256Hash = createdDto.Sha256Hash
                        ReferenceType = createdDto.ReferenceType
                        ReferenceText = createdDto.ReferenceText
                        Links = createdDto.Links
                        CreatedAt = referenceEvent.Metadata.Timestamp }
                | LinkAdded link ->
                    { currentReferenceDto with
                        Links =
                            currentReferenceDto.Links
                            |> Array.append (Array.singleton link)
                            |> Array.distinct }
                | LinkRemoved link -> { currentReferenceDto with Links = currentReferenceDto.Links |> Array.except (Array.singleton link) }
                | LogicalDeleted(force, deleteReason) -> { currentReferenceDto with DeletedAt = Some(getCurrentInstant ()); DeleteReason = deleteReason }
                | PhysicalDeleted -> currentReferenceDto // Do nothing because it's about to be deleted anyway.
                | Undeleted -> { currentReferenceDto with DeletedAt = None; DeleteReason = String.Empty }

            { newReferenceDto with UpdatedAt = Some referenceEvent.Metadata.Timestamp }

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync() =
            let activateStartTime = getCurrentInstant ()
            let stateManager = this.StateManager

            task {
                let mutable message = String.Empty
                let! retrievedEvents = Storage.RetrieveState<List<ReferenceEvent>> stateManager eventsStateName

                let correlationId =
                    match memoryCache.GetCorrelationIdEntry this.Id with
                    | Some correlationId -> correlationId
                    | None -> String.Empty

                match retrievedEvents with
                | Some retrievedEvents ->
                    referenceEvents.AddRange(retrievedEvents)

                    // Apply all events to the state.
                    referenceDto <-
                        retrievedEvents
                        |> Seq.fold (fun referenceDto referenceEvent -> referenceDto |> updateDto referenceEvent) ReferenceDto.Default

                    message <- "Retrieved from database"
                | None -> message <- "Not found in database"

                let duration_ms = getPaddedDuration_ms activateStartTime

                log.LogInformation(
                    "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Activated {ActorType} {ActorId}. {message}.",
                    getCurrentInstantExtended (),
                    getMachineName,
                    duration_ms,
                    correlationId,
                    actorName,
                    host.Id,
                    message
                )
            }
            :> Task

        override this.OnPreActorMethodAsync(context) =
            this.correlationId <- String.Empty
            actorStartTime <- getCurrentInstant ()
            logScope <- log.BeginScope("Actor {actorName}", actorName)
            currentCommand <- String.Empty

            log.LogTrace(
                "{CurrentInstant}: Started {ActorName}.{MethodName} ReferenceId: {Id}.",
                getCurrentInstantExtended (),
                actorName,
                context.MethodName,
                this.Id
            )

            // This checks if the actor is still active, but in an undefined state, which will _almost_ never happen.
            // isDisposed is set when the actor is deleted, or if an error occurs where we're not sure of the state and want to reload from the database.
            if isDisposed then
                this.OnActivateAsync().Wait()
                isDisposed <- false

            Task.CompletedTask

        override this.OnPostActorMethodAsync(context) =
            let duration_ms = getPaddedDuration_ms actorStartTime

            if String.IsNullOrEmpty(currentCommand) then
                log.LogInformation(
                    "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {ActorName}.{MethodName}; RepositoryId: {RepositoryId}; BranchId: {BranchId}; ReferenceId: {ReferenceId}.",
                    getCurrentInstantExtended (),
                    getMachineName,
                    duration_ms,
                    this.correlationId,
                    actorName,
                    context.MethodName,
                    referenceDto.RepositoryId,
                    referenceDto.BranchId,
                    this.Id
                )
            else
                log.LogInformation(
                    "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {ActorName}.{MethodName}; Command: {Command}; RepositoryId: {RepositoryId}; BranchId: {BranchId}; ReferenceId: {ReferenceId}.",
                    getCurrentInstantExtended (),
                    getMachineName,
                    duration_ms,
                    this.correlationId,
                    actorName,
                    context.MethodName,
                    currentCommand,
                    referenceDto.RepositoryId,
                    referenceDto.BranchId,
                    this.Id
                )

            logScope.Dispose()
            Task.CompletedTask

        member private this.ApplyEventOld referenceEvent =
            let stateManager = this.StateManager

            task {
                try
                    //if referenceEvents.Count = 0 then do! this.OnFirstWrite()

                    // Add the event to the branchEvents list, and save it to actor state.
                    referenceEvents.Add(referenceEvent)
                    do! Storage.SaveState stateManager eventsStateName referenceEvents

                    // Update the referenceDto with the event.
                    referenceDto <- referenceDto |> updateDto referenceEvent

                    // Publish the event to the rest of the world.
                    let graceEvent = Events.GraceEvent.ReferenceEvent referenceEvent
                    let message = serialize graceEvent
                    do! daprClient.PublishEventAsync(GracePubSubService, GraceEventStreamTopic, graceEvent)

                    let returnValue = GraceReturnValue.Create "Reference command succeeded." referenceEvent.Metadata.CorrelationId

                    returnValue
                        .enhance(nameof (RepositoryId), $"{referenceDto.RepositoryId}")
                        .enhance(nameof (BranchId), $"{referenceDto.BranchId}")
                        .enhance(nameof (ReferenceId), $"{referenceDto.ReferenceId}")
                        .enhance(nameof (ReferenceType), $"{discriminatedUnionCaseName referenceDto.ReferenceType}")
                        .enhance (nameof (ReferenceEventType), $"{getDiscriminatedUnionFullName referenceEvent.Event}")
                    |> ignore

                    return Ok returnValue
                with ex ->
                    let exceptionResponse = createExceptionResponse ex

                    let graceError = GraceError.Create (ReferenceError.getErrorMessage FailedWhileApplyingEvent) referenceEvent.Metadata.CorrelationId

                    graceError
                        .enhance("Exception details", exceptionResponse.``exception`` + exceptionResponse.innerException)
                        .enhance(nameof (RepositoryId), $"{referenceDto.RepositoryId}")
                        .enhance(nameof (BranchId), $"{referenceDto.BranchId}")
                        .enhance(nameof (ReferenceId), $"{referenceDto.ReferenceId}")
                        .enhance(nameof (ReferenceType), $"{discriminatedUnionCaseName referenceDto.ReferenceType}")
                        .enhance (nameof (ReferenceEventType), $"{getDiscriminatedUnionFullName referenceEvent.Event}")
                    |> ignore

                    return Error graceError
            }

        member public this.SchedulePhysicalDeletion(deleteReason, delay, correlationId) =
            let (tuple: PhysicalDeletionReminderState) =
                (referenceDto.RepositoryId, referenceDto.BranchId, referenceDto.DirectoryId, referenceDto.Sha256Hash, deleteReason, correlationId)

            this.RegisterReminderAsync(ReminderType.PhysicalDeletion, toByteArray tuple, delay, TimeSpan.FromMilliseconds(-1))

        member private this.ApplyEvent(referenceEvent: ReferenceEvent) =
            let stateManager = this.StateManager

            task {
                let correlationId = referenceEvent.Metadata.CorrelationId

                try
                    //if referenceEvents.Count = 0 then do! this.OnFirstWrite()

                    // Add the event to the referenceEvents list, and save it to actor state.
                    referenceEvents.Add(referenceEvent)
                    do! Storage.SaveState stateManager eventsStateName referenceEvents

                    // Update the referenceDto with the event.
                    referenceDto <- referenceDto |> updateDto referenceEvent

                    // Publish the event to the rest of the world.
                    let graceEvent = Events.GraceEvent.ReferenceEvent referenceEvent
                    do! daprClient.PublishEventAsync(GracePubSubService, GraceEventStreamTopic, graceEvent)

                    // If this is a Save or Checkpoint reference, schedule a physical deletion based on the default delays from the repository.
                    match referenceEvent.Event with
                    | Created referenceDto ->
                        do!
                            match referenceDto.ReferenceType with
                            | ReferenceType.Save ->
                                task {
                                    let repositoryActorProxy = Repository.CreateActorProxy referenceDto.RepositoryId correlationId

                                    let! repositoryDto = repositoryActorProxy.Get correlationId

                                    let! deletionReminder =
                                        this.SchedulePhysicalDeletion(
                                            $"Deletion for saves of {repositoryDto.SaveDays} days.",
                                            TimeSpan.FromDays(float repositoryDto.SaveDays),
                                            correlationId
                                        )

                                    ()
                                }
                            | ReferenceType.Checkpoint ->
                                task {
                                    let repositoryActorProxy = Repository.CreateActorProxy referenceDto.RepositoryId correlationId

                                    let! repositoryDto = repositoryActorProxy.Get correlationId

                                    let! deletionReminder =
                                        this.SchedulePhysicalDeletion(
                                            $"Deletion for checkpoints of {repositoryDto.CheckpointDays} days.",
                                            TimeSpan.FromDays(float repositoryDto.CheckpointDays),
                                            correlationId
                                        )

                                    ()
                                }
                            | _ -> () |> returnTask
                            :> Task
                    | _ -> ()

                    let graceReturnValue =
                        (GraceReturnValue.Create "Reference command succeeded." correlationId)
                            .enhance(nameof (RepositoryId), $"{referenceDto.RepositoryId}")
                            .enhance(nameof (BranchId), $"{referenceDto.BranchId}")
                            .enhance(nameof (ReferenceId), $"{referenceDto.ReferenceId}")
                            .enhance(nameof (DirectoryVersionId), $"{referenceDto.DirectoryId}")
                            .enhance(nameof (ReferenceType), $"{discriminatedUnionCaseName referenceDto.ReferenceType}")
                            .enhance (nameof (ReferenceEventType), $"{getDiscriminatedUnionFullName referenceEvent.Event}")

                    return Ok graceReturnValue
                with ex ->
                    let exceptionResponse = createExceptionResponse ex

                    let graceError =
                        (GraceError.Create (ReferenceError.getErrorMessage FailedWhileApplyingEvent) correlationId)
                            .enhance("Exception details", exceptionResponse.``exception`` + exceptionResponse.innerException)
                            .enhance(nameof (RepositoryId), $"{referenceDto.RepositoryId}")
                            .enhance(nameof (BranchId), $"{referenceDto.BranchId}")
                            .enhance(nameof (ReferenceId), $"{referenceDto.ReferenceId}")
                            .enhance(nameof (DirectoryVersionId), $"{referenceDto.DirectoryId}")
                            .enhance(nameof (ReferenceType), $"{discriminatedUnionCaseName referenceDto.ReferenceType}")
                            .enhance (nameof (ReferenceEventType), $"{getDiscriminatedUnionFullName referenceEvent.Event}")

                    return Error graceError
            }

        interface IReferenceActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId

                not <| referenceDto.ReferenceId.Equals(ReferenceDto.Default.ReferenceId)
                |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId
                referenceDto |> returnTask

            member this.GetReferenceType correlationId =
                this.correlationId <- correlationId
                referenceDto.ReferenceType |> returnTask

            member this.IsDeleted correlationId =
                this.correlationId <- correlationId
                referenceDto.DeletedAt.IsSome |> returnTask

            member this.Handle command metadata =
                let isValid (command: ReferenceCommand) (metadata: EventMetadata) =
                    task {
                        if referenceEvents.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId) then
                            return Error(GraceError.Create (ReferenceError.getErrorMessage DuplicateCorrelationId) metadata.CorrelationId)
                        else
                            match command with
                            | Create dto ->
                                match referenceDto.UpdatedAt with
                                | Some _ -> return Error(GraceError.Create (ReferenceError.getErrorMessage ReferenceAlreadyExists) metadata.CorrelationId)
                                | None -> return Ok command
                            | _ ->
                                match referenceDto.UpdatedAt with
                                | Some _ -> return Ok command
                                | None -> return Error(GraceError.Create (ReferenceError.getErrorMessage ReferenceIdDoesNotExist) metadata.CorrelationId)
                    }

                let processCommand (command: ReferenceCommand) (metadata: EventMetadata) =
                    task {
                        let! referenceEventType =
                            task {
                                match command with
                                | Create dto -> return Created dto
                                | AddLink link -> return LinkAdded link
                                | RemoveLink link -> return LinkRemoved link
                                | DeleteLogical(force, deleteReason) ->
                                    let repositoryActorProxy = Repository.CreateActorProxy referenceDto.RepositoryId this.correlationId
                                    let! repositoryDto = repositoryActorProxy.Get this.correlationId

                                    let! deletionReminder =
                                        this.SchedulePhysicalDeletion(
                                            deleteReason,
                                            TimeSpan.FromDays(float repositoryDto.LogicalDeleteDays),
                                            this.correlationId
                                        )

                                    return LogicalDeleted(force, deleteReason)
                                | DeletePhysical ->
                                    isDisposed <- true
                                    return PhysicalDeleted
                                | Undelete -> return Undeleted
                            }

                        let referenceEvent = { Event = referenceEventType; Metadata = metadata }
                        let! returnValue = this.ApplyEvent referenceEvent

                        return returnValue
                    }

                task {
                    currentCommand <- getDiscriminatedUnionCaseName command
                    this.correlationId <- metadata.CorrelationId

                    match! isValid command metadata with
                    | Ok command -> return! processCommand command metadata
                    | Error error -> return Error error
                }

        interface IRemindable with
            override this.ReceiveReminderAsync(reminderName, state, dueTime, period) =
                let stateManager = this.StateManager

                match reminderName with
                | ReminderType.Maintenance ->
                    task {
                        // Do some maintenance
                        ()
                    }
                    :> Task
                | ReminderType.PhysicalDeletion ->
                    task {
                        // Get values from state.
                        let (repositoryId, branchId, directoryVersionId, sha256Hash, deleteReason, correlationId) =
                            fromByteArray<PhysicalDeletionReminderState> state

                        this.correlationId <- correlationId

                        // Mark the branch as needing to update its latest references.
                        let branchActorProxy = Branch.CreateActorProxy branchId correlationId
                        do! branchActorProxy.MarkForRecompute correlationId

                        // Delete saved state for this actor.
                        let! deletedEventsState = Storage.DeleteState stateManager eventsStateName

                        log.LogInformation(
                            "{currentInstant}: CorrelationId: {correlationId}; Deleted physical state for reference; RepositoryId: {RepositoryId}; BranchId: {BranchId}; ReferenceId: {ReferenceId}; DirectoryVersionId: {DirectoryVersionId}; deleteReason: {deleteReason}.",
                            getCurrentInstantExtended (),
                            correlationId,
                            repositoryId,
                            branchId,
                            this.Id,
                            directoryVersionId,
                            deleteReason
                        )

                        // Set all values to default.
                        referenceDto <- ReferenceDto.Default
                        referenceEvents.Clear()

                        // Mark the actor as disposed, in case someone tries to use it before Dapr GC's it.
                        isDisposed <- true
                    }
                    :> Task
                | _ -> failwith "Unknown reminder type."
