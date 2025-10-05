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
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.Events
open Grace.Types.Reference
open Grace.Types.Reminder
open Grace.Types.Types
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

module Reference =

    type ReferenceActor
        ([<PersistentState(StateName.Reference, Constants.GraceActorStorage)>] state: IPersistentState<List<ReferenceEvent>>, log: ILogger<ReferenceActor>) =
        inherit Grain()

        static let actorName = ActorName.Reference

        let mutable currentCommand = String.Empty

        let mutable referenceDto = ReferenceDto.Default

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            referenceDto <-
                state.State
                |> Seq.fold (fun referenceDto event -> ReferenceDto.UpdateDto event referenceDto) referenceDto

            Task.CompletedTask

        interface IGraceReminderWithGuidKey with
            /// Schedules a Grace reminder.
            member this.ScheduleReminderAsync reminderType delay state correlationId =
                task {
                    let reminderDto =
                        ReminderDto.Create
                            actorName
                            $"{this.IdentityString}"
                            referenceDto.OwnerId
                            referenceDto.OrganizationId
                            referenceDto.RepositoryId
                            reminderType
                            (getFutureInstant delay)
                            state
                            correlationId

                    do! createReminder reminderDto
                }
                :> Task

            /// Receives a Grace reminder.
            member this.ReceiveReminderAsync(reminder: ReminderDto) : Task<Result<unit, GraceError>> =
                task {
                    this.correlationId <- reminder.CorrelationId

                    match reminder.ReminderType with
                    | ReminderTypes.PhysicalDeletion ->
                        // Get values from state.
                        let physicalDeletionReminderState = reminder.State :?> PhysicalDeletionReminderState

                        this.correlationId <- physicalDeletionReminderState.CorrelationId

                        // Mark the branch as needing to update its latest references.
                        let branchActorProxy =
                            Branch.CreateActorProxy physicalDeletionReminderState.BranchId physicalDeletionReminderState.RepositoryId this.correlationId

                        do! branchActorProxy.MarkForRecompute physicalDeletionReminderState.CorrelationId

                        // Delete saved state for this actor.
                        do! state.ClearStateAsync()

                        log.LogInformation(
                            "{CurrentInstant}: CorrelationId: {correlationId}; Deleted physical state for reference; RepositoryId: {RepositoryId}; BranchId: {BranchId}; ReferenceId: {ReferenceId}; DirectoryVersionId: {DirectoryVersionId}; deleteReason: {deleteReason}.",
                            getCurrentInstantExtended (),
                            physicalDeletionReminderState.CorrelationId,
                            physicalDeletionReminderState.RepositoryId,
                            physicalDeletionReminderState.BranchId,
                            referenceDto.ReferenceId,
                            physicalDeletionReminderState.DirectoryVersionId,
                            physicalDeletionReminderState.DeleteReason
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
                    referenceDto <- referenceDto |> ReferenceDto.UpdateDto referenceEvent

                    // Publish the event to the rest of the world.
                    let graceEvent = GraceEvent.ReferenceEvent referenceEvent

                    let streamProvider = this.GetStreamProvider GraceEventStreamProvider
                    let stream = streamProvider.GetStream<GraceEvent>(StreamId.Create(GraceEventStreamTopic, referenceDto.ReferenceId))
                    do! stream.OnNextAsync(graceEvent)

                    // If this is a Save or Checkpoint reference, schedule a physical deletion based on the default delays from the repository.
                    match referenceEvent.Event with
                    | Created(referenceId, ownerId, organizationId, repositoryId, branchId, directoryId, sha256Hash, referenceType, referenceText, links) ->
                        do!
                            match referenceDto.ReferenceType with
                            | ReferenceType.Save ->
                                task {
                                    let repositoryActorProxy = Repository.CreateActorProxy referenceDto.OrganizationId referenceDto.RepositoryId correlationId
                                    let! repositoryDto = repositoryActorProxy.Get correlationId

                                    let reminderState: PhysicalDeletionReminderState =
                                        { RepositoryId = referenceDto.RepositoryId
                                          BranchId = referenceDto.BranchId
                                          DirectoryVersionId = referenceDto.DirectoryId
                                          Sha256Hash = referenceDto.Sha256Hash
                                          DeleteReason = $"Save: automatic deletion after {repositoryDto.SaveDays} days."
                                          CorrelationId = correlationId }

                                    do!
                                        (this :> IGraceReminderWithGuidKey).ScheduleReminderAsync
                                            ReminderTypes.PhysicalDeletion
                                            (Duration.FromDays(float repositoryDto.SaveDays))
                                            reminderState
                                            correlationId
                                }
                            | ReferenceType.Checkpoint ->
                                task {
                                    let repositoryActorProxy = Repository.CreateActorProxy referenceDto.OrganizationId referenceDto.RepositoryId correlationId
                                    let! repositoryDto = repositoryActorProxy.Get correlationId

                                    let reminderState: PhysicalDeletionReminderState =
                                        { RepositoryId = referenceDto.RepositoryId
                                          BranchId = referenceDto.BranchId
                                          DirectoryVersionId = referenceDto.DirectoryId
                                          Sha256Hash = referenceDto.Sha256Hash
                                          DeleteReason = $"Checkpoint: automatic deletion after {repositoryDto.CheckpointDays} days."
                                          CorrelationId = correlationId }

                                    do!
                                        (this :> IGraceReminderWithGuidKey).ScheduleReminderAsync
                                            ReminderTypes.PhysicalDeletion
                                            (Duration.FromDays(float repositoryDto.CheckpointDays))
                                            reminderState
                                            correlationId
                                }
                            | _ -> () |> returnTask
                            :> Task
                    | _ -> ()

                    let graceReturnValue =
                        (GraceReturnValue.Create referenceDto correlationId)
                            .enhance(nameof RepositoryId, referenceDto.RepositoryId)
                            .enhance(nameof BranchId, referenceDto.BranchId)
                            .enhance(nameof ReferenceId, referenceDto.ReferenceId)
                            .enhance(nameof DirectoryVersionId, referenceDto.DirectoryId)
                            .enhance(nameof ReferenceType, getDiscriminatedUnionCaseName referenceDto.ReferenceType)
                            .enhance (nameof ReferenceEventType, getDiscriminatedUnionFullName referenceEvent.Event)

                    return Ok graceReturnValue
                with ex ->
                    log.LogError(
                        ex,
                        "{CurrentInstant}: CorrelationId: {correlationId}; Failed to apply event {eventType} for reference {referenceId} in repository {repositoryId} on branch {branchId} with directory version {directoryVersionId}.",
                        getCurrentInstantExtended (),
                        correlationId,
                        getDiscriminatedUnionCaseName referenceEvent.Event,
                        referenceDto.ReferenceId,
                        referenceDto.RepositoryId,
                        referenceDto.BranchId,
                        referenceDto.DirectoryId
                    )

                    let graceError =
                        (GraceError.CreateWithException ex (getErrorMessage ReferenceError.FailedWhileApplyingEvent) correlationId)
                            .enhance(nameof RepositoryId, referenceDto.RepositoryId)
                            .enhance(nameof BranchId, referenceDto.BranchId)
                            .enhance(nameof ReferenceId, referenceDto.ReferenceId)
                            .enhance(nameof DirectoryVersionId, referenceDto.DirectoryId)
                            .enhance(nameof ReferenceType, getDiscriminatedUnionCaseName referenceDto.ReferenceType)
                            .enhance (nameof ReferenceEventType, getDiscriminatedUnionFullName referenceEvent.Event)

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
                            return Error(GraceError.Create (getErrorMessage ReferenceError.DuplicateCorrelationId) metadata.CorrelationId)
                        else
                            match command with
                            | Create(referenceId, ownerId, organizationId, repositoryId, branchId, directoryId, sha256Hash, referenceType, referenceText, links) ->
                                match referenceDto.UpdatedAt with
                                | Some _ -> return Error(GraceError.Create (getErrorMessage ReferenceError.ReferenceAlreadyExists) metadata.CorrelationId)
                                | None -> return Ok command
                            | _ ->
                                match referenceDto.UpdatedAt with
                                | Some _ -> return Ok command
                                | None -> return Error(GraceError.Create (getErrorMessage ReferenceError.ReferenceIdDoesNotExist) metadata.CorrelationId)
                    }

                let processCommand (command: ReferenceCommand) (metadata: EventMetadata) =
                    task {
                        let! referenceEventType =
                            task {
                                match command with
                                | Create(referenceId,
                                         ownerId,
                                         organizationId,
                                         repositoryId,
                                         branchId,
                                         directoryId,
                                         sha256Hash,
                                         referenceType,
                                         referenceText,
                                         links) ->
                                    return
                                        Created(
                                            referenceId,
                                            ownerId,
                                            organizationId,
                                            repositoryId,
                                            branchId,
                                            directoryId,
                                            sha256Hash,
                                            referenceType,
                                            referenceText,
                                            links
                                        )
                                | AddLink link -> return LinkAdded link
                                | RemoveLink link -> return LinkRemoved link
                                | DeleteLogical(force, deleteReason) ->
                                    let repositoryActorProxy =
                                        Repository.CreateActorProxy referenceDto.OrganizationId referenceDto.RepositoryId this.correlationId

                                    let! repositoryDto = repositoryActorProxy.Get this.correlationId

                                    let reminderState: PhysicalDeletionReminderState =
                                        { RepositoryId = referenceDto.RepositoryId
                                          BranchId = referenceDto.BranchId
                                          DirectoryVersionId = referenceDto.DirectoryId
                                          Sha256Hash = referenceDto.Sha256Hash
                                          DeleteReason = deleteReason
                                          CorrelationId = metadata.CorrelationId }

                                    do!
                                        (this :> IGraceReminderWithGuidKey).ScheduleReminderAsync
                                            ReminderTypes.PhysicalDeletion
                                            (Duration.FromDays(float repositoryDto.LogicalDeleteDays))
                                            reminderState
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
