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
open Grace.Actors.Timing
open Grace.Actors.Types
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
open Events.Reference
open Grace.Shared.Validation.Errors.Reference
open Grace.Shared.Constants
open Commands.Reference

module Reference =

    type PhysicalDeletionReminderState = (RepositoryId * BranchId * DirectoryVersionId * Sha256Hash * DeleteReason * CorrelationId)
    let log = loggerFactory.CreateLogger("Reference.Actor")

    type ReferenceActor(host: ActorHost) =
        inherit Actor(host)

        static let actorName = ActorName.Reference
        static let eventsStateName = StateName.Reference

        let mutable actorStartTime = Instant.MinValue
        let mutable logScope: IDisposable = null
        let mutable currentCommand = String.Empty
        let mutable stateManager = Unchecked.defaultof<IActorStateManager>

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
            stateManager <- this.StateManager

            task {
                let correlationId =
                    match memoryCache.GetCorrelationIdEntry this.Id with
                    | Some correlationId -> correlationId
                    | None -> String.Empty

                addTiming AfterGettingCorrelationIdFromMemoryCache eventsStateName correlationId

                try
                    let! retrievedEvents = Storage.RetrieveState<List<ReferenceEvent>> stateManager eventsStateName correlationId

                    match retrievedEvents with
                    | Some retrievedEvents ->
                        referenceEvents.AddRange(retrievedEvents)

                        // Apply all events to the state.
                        referenceDto <-
                            retrievedEvents
                            |> Seq.fold (fun referenceDto referenceEvent -> referenceDto |> updateDto referenceEvent) ReferenceDto.Default
                    | None -> ()

                    logActorActivation log activateStartTime correlationId actorName this.Id (getActorActivationMessage retrievedEvents)
                with ex ->
                    let exc = ExceptionResponse.Create ex
                    log.LogError("{CurrentInstant} Error activating {ActorType} {ActorId}.", getCurrentInstantExtended (), this.GetType().Name, host.Id)
                    log.LogError("{CurrentInstant} {ExceptionDetails}", getCurrentInstantExtended (), exc.ToString())
                    logActorActivation log activateStartTime correlationId actorName this.Id "Exception occurred during activation."
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
                    "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {ActorName}.{MethodName}; RepositoryId: {RepositoryId}; BranchId: {BranchId}; ReferenceId: {ReferenceId}.",
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
                    "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {ActorName}.{MethodName}; Command: {Command}; RepositoryId: {RepositoryId}; BranchId: {BranchId}; ReferenceId: {ReferenceId}.",
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
                task {
                    this.correlationId <- reminder.CorrelationId

                    match reminder.ReminderType with
                    | ReminderTypes.PhysicalDeletion ->
                        // Get values from state.
                        let (repositoryId, branchId, directoryVersionId, sha256Hash, deleteReason, correlationId) =
                            deserialize<PhysicalDeletionReminderState> reminder.State

                        this.correlationId <- correlationId

                        // Mark the branch as needing to update its latest references.
                        let branchActorProxy = Branch.CreateActorProxy branchId correlationId
                        do! branchActorProxy.MarkForRecompute correlationId

                        // Delete saved state for this actor.
                        let! deleted = Storage.DeleteState stateManager eventsStateName

                        if deleted then
                            log.LogInformation(
                                "{CurrentInstant}: CorrelationId: {correlationId}; Deleted physical state for reference; RepositoryId: {RepositoryId}; BranchId: {BranchId}; ReferenceId: {ReferenceId}; DirectoryVersionId: {DirectoryVersionId}; deleteReason: {deleteReason}.",
                                getCurrentInstantExtended (),
                                correlationId,
                                repositoryId,
                                branchId,
                                this.Id,
                                directoryVersionId,
                                deleteReason
                            )
                        else
                            log.LogWarning(
                                "{CurrentInstant}: CorrelationId: {correlationId}; Physical state for reference could not be deleted because it was not found; RepositoryId: {RepositoryId}; BranchId: {BranchId}; ReferenceId: {ReferenceId}; DirectoryVersionId: {DirectoryVersionId}; deleteReason: {deleteReason}.",
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
                        return Ok()
                    | _ ->
                        return
                            Error(
                                (GraceError.Create
                                    $"{actorName} does not process reminder type {getDiscriminatedUnionCaseName reminder.ReminderType}."
                                    this.correlationId)
                                    .enhance ("IsRetryable", "false")
                            )
                }

        member private this.ApplyEvent(referenceEvent: ReferenceEvent) =
            task {
                let correlationId = referenceEvent.Metadata.CorrelationId

                try
                    // Add the event to the referenceEvents list, and save it to actor state.
                    referenceEvents.Add(referenceEvent)
                    do! Storage.SaveState stateManager eventsStateName referenceEvents this.correlationId

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

                                    let reminderState: PhysicalDeletionReminderState =
                                        (referenceDto.RepositoryId,
                                         referenceDto.BranchId,
                                         referenceDto.DirectoryId,
                                         referenceDto.Sha256Hash,
                                         $"Save: automatic deletion after {repositoryDto.SaveDays} days.",
                                         correlationId)

                                    do!
                                        (this :> IGraceReminder).ScheduleReminderAsync
                                            ReminderTypes.PhysicalDeletion
                                            (Duration.FromDays(float repositoryDto.SaveDays))
                                            (serialize reminderState)
                                            correlationId
                                }
                            | ReferenceType.Checkpoint ->
                                task {
                                    let repositoryActorProxy = Repository.CreateActorProxy referenceDto.RepositoryId correlationId
                                    let! repositoryDto = repositoryActorProxy.Get correlationId

                                    let reminderState: PhysicalDeletionReminderState =
                                        (referenceDto.RepositoryId,
                                         referenceDto.BranchId,
                                         referenceDto.DirectoryId,
                                         referenceDto.Sha256Hash,
                                         $"Checkpoint: automatic deletion after {repositoryDto.CheckpointDays} days.",
                                         correlationId)

                                    do!
                                        (this :> IGraceReminder).ScheduleReminderAsync
                                            ReminderTypes.PhysicalDeletion
                                            (Duration.FromDays(float repositoryDto.CheckpointDays))
                                            (serialize reminderState)
                                            correlationId
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
                            .enhance(nameof (ReferenceType), $"{getDiscriminatedUnionCaseName referenceDto.ReferenceType}")
                            .enhance (nameof (ReferenceEventType), $"{getDiscriminatedUnionFullName referenceEvent.Event}")

                    return Ok graceReturnValue
                with ex ->
                    let exceptionResponse = ExceptionResponse.Create ex

                    let graceError =
                        (GraceError.Create (ReferenceError.getErrorMessage FailedWhileApplyingEvent) correlationId)
                            .enhance("Exception details", exceptionResponse.``exception`` + exceptionResponse.innerException)
                            .enhance(nameof (RepositoryId), $"{referenceDto.RepositoryId}")
                            .enhance(nameof (BranchId), $"{referenceDto.BranchId}")
                            .enhance(nameof (ReferenceId), $"{referenceDto.ReferenceId}")
                            .enhance(nameof (DirectoryVersionId), $"{referenceDto.DirectoryId}")
                            .enhance(nameof (ReferenceType), $"{getDiscriminatedUnionCaseName referenceDto.ReferenceType}")
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

                                    let reminderState: PhysicalDeletionReminderState =
                                        (referenceDto.RepositoryId,
                                         referenceDto.BranchId,
                                         referenceDto.DirectoryId,
                                         referenceDto.Sha256Hash,
                                         deleteReason,
                                         metadata.CorrelationId)

                                    do!
                                        (this :> IGraceReminder).ScheduleReminderAsync
                                            ReminderTypes.PhysicalDeletion
                                            (Duration.FromDays(float repositoryDto.LogicalDeleteDays))
                                            (serialize reminderState)
                                            metadata.CorrelationId

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
