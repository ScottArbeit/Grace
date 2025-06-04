namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Actors.Timing
open Grace.Actors.Types
open Grace.Shared
open Grace.Shared.Commands.Reference
open Grace.Shared.Constants
open Grace.Shared.Dto.Reference
open Grace.Shared.Events.Reference
open Grace.Types.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors.Reference
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

module Reference =

    type PhysicalDeletionReminderState = (RepositoryId * BranchId * DirectoryVersionId * Sha256Hash * DeleteReason * CorrelationId)

    type ReferenceActor
        ([<PersistentState(StateName.Reference, Constants.GraceActorStorage)>] state: IPersistentState<List<ReferenceEvent>>, log: ILogger<ReferenceActor>) =
        inherit Grain()

        static let actorName = ActorName.Reference

        let mutable currentCommand = String.Empty

        let mutable referenceDto = ReferenceDto.Default

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

        override this.OnActivateAsync(ct) =
            logActorActivation log this.IdentityString (getActorActivationMessage state.RecordExists)

            referenceDto <-
                state.State
                |> Seq.fold (fun referenceDto event -> updateDto event referenceDto) referenceDto

            Task.CompletedTask

        interface IGraceReminderWithGuidKey with
            /// Schedules a Grace reminder.
            member this.ScheduleReminderAsync reminderType delay state correlationId =
                task {
                    let reminder =
                        ReminderDto.Create
                            actorName
                            $"{this.IdentityString}"
                            referenceDto.RepositoryId
                            reminderType
                            (getFutureInstant delay)
                            state
                            correlationId

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
                        let! branchActorProxy = Branch.CreateActorProxy branchId repositoryId correlationId
                        do! branchActorProxy.MarkForRecompute correlationId

                        // Delete saved state for this actor.
                        do! state.ClearStateAsync()

                        log.LogInformation(
                            "{CurrentInstant}: CorrelationId: {correlationId}; Deleted physical state for reference; RepositoryId: {RepositoryId}; BranchId: {BranchId}; ReferenceId: {ReferenceId}; DirectoryVersionId: {DirectoryVersionId}; deleteReason: {deleteReason}.",
                            getCurrentInstantExtended (),
                            correlationId,
                            repositoryId,
                            branchId,
                            referenceDto.ReferenceId,
                            directoryVersionId,
                            deleteReason
                        )

                        this.DeactivateOnIdle()
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
                    state.State.Add(referenceEvent)
                    do! state.WriteStateAsync()

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
                                    let! repositoryActorProxy = Repository.CreateActorProxy referenceDto.RepositoryId correlationId
                                    let! repositoryDto = repositoryActorProxy.Get correlationId

                                    let reminderState: PhysicalDeletionReminderState =
                                        (referenceDto.RepositoryId,
                                         referenceDto.BranchId,
                                         referenceDto.DirectoryId,
                                         referenceDto.Sha256Hash,
                                         $"Save: automatic deletion after {repositoryDto.SaveDays} days.",
                                         correlationId)

                                    do!
                                        (this :> IGraceReminderWithGuidKey).ScheduleReminderAsync
                                            ReminderTypes.PhysicalDeletion
                                            (Duration.FromDays(float repositoryDto.SaveDays))
                                            (serialize reminderState)
                                            correlationId
                                }
                            | ReferenceType.Checkpoint ->
                                task {
                                    let! repositoryActorProxy = Repository.CreateActorProxy referenceDto.RepositoryId correlationId
                                    let! repositoryDto = repositoryActorProxy.Get correlationId

                                    let reminderState: PhysicalDeletionReminderState =
                                        (referenceDto.RepositoryId,
                                         referenceDto.BranchId,
                                         referenceDto.DirectoryId,
                                         referenceDto.Sha256Hash,
                                         $"Checkpoint: automatic deletion after {repositoryDto.CheckpointDays} days.",
                                         correlationId)

                                    do!
                                        (this :> IGraceReminderWithGuidKey).ScheduleReminderAsync
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

        interface IHasRepositoryId with
            member this.GetRepositoryId correlationId = referenceDto.RepositoryId |> returnTask

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
                        if state.State.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId) then
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
                                    let! repositoryActorProxy = Repository.CreateActorProxy referenceDto.RepositoryId this.correlationId
                                    let! repositoryDto = repositoryActorProxy.Get this.correlationId

                                    let reminderState: PhysicalDeletionReminderState =
                                        (referenceDto.RepositoryId,
                                         referenceDto.BranchId,
                                         referenceDto.DirectoryId,
                                         referenceDto.Sha256Hash,
                                         deleteReason,
                                         metadata.CorrelationId)

                                    do!
                                        (this :> IGraceReminderWithGuidKey).ScheduleReminderAsync
                                            ReminderTypes.PhysicalDeletion
                                            (Duration.FromDays(float repositoryDto.LogicalDeleteDays))
                                            (serialize reminderState)
                                            metadata.CorrelationId

                                    return LogicalDeleted(force, deleteReason)
                                | DeletePhysical ->
                                    // Delete the actor state and mark the actor as deactivated.
                                    do! state.ClearStateAsync()
                                    this.DeactivateOnIdle()
                                    return PhysicalDeleted
                                | Undelete -> return Undeleted
                            }

                        let referenceEvent = { Event = referenceEventType; Metadata = metadata }
                        let! returnValue = this.ApplyEvent referenceEvent

                        return returnValue
                    }

                task {
                    currentCommand <- $"{getDiscriminatedUnionCaseName command} {getDiscriminatedUnionCaseName referenceDto.ReferenceType}"
                    this.correlationId <- metadata.CorrelationId

                    match! isValid command metadata with
                    | Ok command -> return! processCommand command metadata
                    | Error error -> return Error error
                }
