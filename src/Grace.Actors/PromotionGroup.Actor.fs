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
open Grace.Types.PromotionGroup
open Grace.Types.Reminder
open Grace.Types.Types
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

module PromotionGroup =

    type PromotionGroupActor([<PersistentState(StateName.PromotionGroup, Constants.GraceActorStorage)>] state: IPersistentState<List<PromotionGroupEvent>>) =
        inherit Grain()

        static let actorName = ActorName.PromotionGroup

        let log = loggerFactory.CreateLogger("PromotionGroup.Actor")

        let mutable currentCommand = String.Empty

        let mutable promotionGroupDto = PromotionGroupDto.Default

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            promotionGroupDto <-
                state.State
                |> Seq.fold (fun dto event -> PromotionGroupDto.UpdateDto event dto) promotionGroupDto

            Task.CompletedTask

        interface IGraceReminderWithGuidKey with
            /// Schedules a Grace reminder.
            member this.ScheduleReminderAsync reminderType delay reminderState correlationId =
                task {
                    let reminderDto =
                        ReminderDto.Create
                            actorName
                            $"{this.IdentityString}"
                            promotionGroupDto.OwnerId
                            promotionGroupDto.OrganizationId
                            promotionGroupDto.RepositoryId
                            reminderType
                            (getFutureInstant delay)
                            reminderState
                            correlationId

                    do! createReminder reminderDto
                }
                :> Task

            /// Receives a Grace reminder.
            member this.ReceiveReminderAsync(reminder: ReminderDto) : Task<Result<unit, GraceError>> =
                task {
                    this.correlationId <- reminder.CorrelationId

                    match reminder.ReminderType, reminder.State with
                    | ReminderTypes.ScheduledPromotionGroup, _ ->
                        // TODO: Implement scheduled promotion group execution
                        log.LogInformation(
                            "{CurrentInstant}: Node: {hostName}; CorrelationId: {correlationId}; Scheduled promotion group reminder received; PromotionGroupId: {PromotionGroupId}.",
                            getCurrentInstantExtended (),
                            getMachineName,
                            reminder.CorrelationId,
                            promotionGroupDto.PromotionGroupId
                        )

                        return Ok()
                    | reminderType, reminderState ->
                        return
                            Error(
                                (GraceError.Create
                                    $"{actorName} does not process reminder type {getDiscriminatedUnionCaseName reminderType} with state {getDiscriminatedUnionCaseName reminderState}."
                                    this.correlationId)
                                    .enhance ("IsRetryable", "false")
                            )
                }

        member private this.ApplyEvent(promotionGroupEvent: PromotionGroupEvent) =
            task {
                let correlationId = promotionGroupEvent.Metadata.CorrelationId

                try
                    // Add the event to the state list, and save it to actor state.
                    state.State.Add(promotionGroupEvent)
                    do! state.WriteStateAsync()

                    // Update the DTO with the event.
                    promotionGroupDto <-
                        promotionGroupDto
                        |> PromotionGroupDto.UpdateDto promotionGroupEvent

                    // Publish the event to the rest of the world.
                    let graceEvent = GraceEvent.PromotionGroupEvent promotionGroupEvent
                    do! publishGraceEvent graceEvent promotionGroupEvent.Metadata

                    let graceReturnValue =
                        (GraceReturnValue.Create "Promotion group command succeeded." correlationId)
                            .enhance(nameof RepositoryId, promotionGroupDto.RepositoryId)
                            .enhance(nameof PromotionGroupId, promotionGroupDto.PromotionGroupId)
                            .enhance("TargetBranchId", promotionGroupDto.TargetBranchId)
                            .enhance("Status", getDiscriminatedUnionCaseName promotionGroupDto.Status)
                            .enhance (nameof PromotionGroupEventType, getDiscriminatedUnionFullName promotionGroupEvent.Event)

                    return Ok graceReturnValue
                with
                | ex ->
                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {hostName}; CorrelationId: {correlationId}; Failed to apply event {eventType} for promotion group {promotionGroupId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId,
                        getDiscriminatedUnionCaseName promotionGroupEvent.Event,
                        promotionGroupDto.PromotionGroupId
                    )

                    let graceError =
                        (GraceError.CreateWithException ex (PromotionGroupError.getErrorMessage PromotionGroupError.FailedWhileApplyingEvent) correlationId)
                            .enhance(nameof RepositoryId, promotionGroupDto.RepositoryId)
                            .enhance (nameof PromotionGroupId, promotionGroupDto.PromotionGroupId)

                    return Error graceError
            }

        interface IHasRepositoryId with
            member this.GetRepositoryId correlationId = promotionGroupDto.RepositoryId |> returnTask

        interface IPromotionGroupActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId

                not
                <| promotionGroupDto.PromotionGroupId.Equals(PromotionGroupDto.Default.PromotionGroupId)
                |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId
                promotionGroupDto |> returnTask

            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                state.State :> IReadOnlyList<PromotionGroupEvent>
                |> returnTask

            member this.IsDeleted correlationId =
                this.correlationId <- correlationId
                promotionGroupDto.DeletedAt.IsSome |> returnTask

            member this.Handle command metadata =
                let isValid (command: PromotionGroupCommand) (metadata: EventMetadata) =
                    task {
                        if state.State.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId) then
                            return
                                Error(GraceError.Create (PromotionGroupError.getErrorMessage PromotionGroupError.DuplicateCorrelationId) metadata.CorrelationId)
                        else
                            match command with
                            | Create (promotionGroupId, ownerId, organizationId, repositoryId, targetBranchId, description, scheduledAt) ->
                                if promotionGroupDto.PromotionGroupId
                                   <> PromotionGroupId.Empty then
                                    return
                                        Error(
                                            GraceError.Create
                                                (PromotionGroupError.getErrorMessage PromotionGroupError.PromotionGroupAlreadyExists)
                                                metadata.CorrelationId
                                        )
                                else
                                    return Ok command
                            | AddPromotion promotionId ->
                                match promotionGroupDto.Status with
                                | Draft
                                | Ready -> return Ok command
                                | _ ->
                                    return
                                        Error(
                                            GraceError.Create
                                                (PromotionGroupError.getErrorMessage PromotionGroupError.PromotionGroupNotInEditableState)
                                                metadata.CorrelationId
                                        )
                            | RemovePromotion promotionId ->
                                match promotionGroupDto.Status with
                                | Draft
                                | Ready -> return Ok command
                                | _ ->
                                    return
                                        Error(
                                            GraceError.Create
                                                (PromotionGroupError.getErrorMessage PromotionGroupError.PromotionGroupNotInEditableState)
                                                metadata.CorrelationId
                                        )
                            | ReorderPromotions promotionIds ->
                                match promotionGroupDto.Status with
                                | Draft
                                | Ready -> return Ok command
                                | _ ->
                                    return
                                        Error(
                                            GraceError.Create
                                                (PromotionGroupError.getErrorMessage PromotionGroupError.PromotionGroupNotInEditableState)
                                                metadata.CorrelationId
                                        )
                            | Schedule scheduledAt ->
                                match promotionGroupDto.Status with
                                | Draft
                                | Ready -> return Ok command
                                | _ ->
                                    return
                                        Error(
                                            GraceError.Create
                                                (PromotionGroupError.getErrorMessage PromotionGroupError.PromotionGroupNotInEditableState)
                                                metadata.CorrelationId
                                        )
                            | MarkReady ->
                                match promotionGroupDto.Status with
                                | Draft -> return Ok command
                                | _ ->
                                    return
                                        Error(
                                            GraceError.Create
                                                (PromotionGroupError.getErrorMessage PromotionGroupError.PromotionGroupNotInDraftState)
                                                metadata.CorrelationId
                                        )
                            | Start ->
                                match promotionGroupDto.Status with
                                | Ready -> return Ok command
                                | _ ->
                                    return
                                        Error(
                                            GraceError.Create
                                                (PromotionGroupError.getErrorMessage PromotionGroupError.PromotionGroupNotInReadyState)
                                                metadata.CorrelationId
                                        )
                            | Complete success ->
                                match promotionGroupDto.Status with
                                | Running -> return Ok command
                                | _ ->
                                    return
                                        Error(
                                            GraceError.Create
                                                (PromotionGroupError.getErrorMessage PromotionGroupError.PromotionGroupNotRunning)
                                                metadata.CorrelationId
                                        )
                            | Block reason ->
                                match promotionGroupDto.Status with
                                | Draft
                                | Ready
                                | Running -> return Ok command
                                | _ ->
                                    return
                                        Error(
                                            GraceError.Create
                                                (PromotionGroupError.getErrorMessage PromotionGroupError.PromotionGroupCannotBeBlocked)
                                                metadata.CorrelationId
                                        )
                            | DeleteLogical (force, deleteReason) ->
                                match promotionGroupDto.Status with
                                | Running
                                | Succeeded ->
                                    return
                                        Error(
                                            GraceError.Create
                                                (PromotionGroupError.getErrorMessage PromotionGroupError.PromotionGroupCannotBeDeleted)
                                                metadata.CorrelationId
                                        )
                                | _ -> return Ok command
                    }

                let processCommand (command: PromotionGroupCommand) (metadata: EventMetadata) =
                    task {
                        let! promotionGroupEventType =
                            task {
                                match command with
                                | Create (promotionGroupId, ownerId, organizationId, repositoryId, targetBranchId, description, scheduledAt) ->
                                    return Created(promotionGroupId, ownerId, organizationId, repositoryId, targetBranchId, description, scheduledAt)
                                | AddPromotion promotionId ->
                                    let order = promotionGroupDto.Promotions.Length
                                    return PromotionAdded(promotionId, order)
                                | RemovePromotion promotionId -> return PromotionRemoved promotionId
                                | ReorderPromotions promotionIds -> return PromotionsReordered promotionIds
                                | Schedule scheduledAt -> return Scheduled scheduledAt
                                | MarkReady -> return MarkedReady
                                | Start -> return Started
                                | Complete success -> return Completed success
                                | Block reason -> return Blocked reason
                                | DeleteLogical (force, deleteReason) -> return LogicalDeleted(force, deleteReason)
                            }

                        let promotionGroupEvent = { Event = promotionGroupEventType; Metadata = metadata }
                        let! returnValue = this.ApplyEvent promotionGroupEvent

                        return returnValue
                    }

                task {
                    currentCommand <- getDiscriminatedUnionCaseName command
                    this.correlationId <- metadata.CorrelationId

                    match! isValid command metadata with
                    | Ok command -> return! processCommand command metadata
                    | Error error -> return Error error
                }
