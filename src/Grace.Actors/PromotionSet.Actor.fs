namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Events
open Grace.Types.PromotionSet
open Grace.Types.Reminder
open Grace.Types.Types
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

module PromotionSet =

    type PromotionSetActor([<PersistentState(StateName.PromotionSet, Constants.GraceActorStorage)>] state: IPersistentState<List<PromotionSetEvent>>) =
        inherit Grain()

        static let actorName = ActorName.PromotionSet
        let log = loggerFactory.CreateLogger("PromotionSet.Actor")

        let mutable currentCommand = String.Empty
        let mutable promotionSetDto = PromotionSetDto.Default

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            promotionSetDto <-
                state.State
                |> Seq.fold (fun dto event -> PromotionSetDto.UpdateDto event dto) promotionSetDto

            Task.CompletedTask

        interface IGraceReminderWithGuidKey with
            member this.ScheduleReminderAsync reminderType delay reminderState correlationId =
                task {
                    let reminderDto =
                        ReminderDto.Create
                            actorName
                            $"{this.IdentityString}"
                            promotionSetDto.OwnerId
                            promotionSetDto.OrganizationId
                            promotionSetDto.RepositoryId
                            reminderType
                            (getFutureInstant delay)
                            reminderState
                            correlationId

                    do! createReminder reminderDto
                }
                :> Task

            member this.ReceiveReminderAsync(reminder: ReminderDto) : Task<Result<unit, GraceError>> =
                task {
                    this.correlationId <- reminder.CorrelationId

                    return
                        Error(
                            (GraceError.Create
                                $"{actorName} does not process reminder type {getDiscriminatedUnionCaseName reminder.ReminderType} with state {getDiscriminatedUnionCaseName reminder.State}."
                                this.correlationId)
                                .enhance ("IsRetryable", "false")
                        )
                }

        member private this.ApplyEvent(promotionSetEvent: PromotionSetEvent) =
            task {
                let correlationId = promotionSetEvent.Metadata.CorrelationId

                try
                    state.State.Add(promotionSetEvent)
                    do! state.WriteStateAsync()

                    promotionSetDto <-
                        promotionSetDto
                        |> PromotionSetDto.UpdateDto promotionSetEvent

                    let graceEvent = GraceEvent.PromotionSetEvent promotionSetEvent
                    do! publishGraceEvent graceEvent promotionSetEvent.Metadata

                    let graceReturnValue: GraceReturnValue<string> =
                        (GraceReturnValue.Create "Promotion set command succeeded." correlationId)
                            .enhance(nameof RepositoryId, promotionSetDto.RepositoryId)
                            .enhance(nameof PromotionSetId, promotionSetDto.PromotionSetId)
                            .enhance ("Status", getDiscriminatedUnionCaseName promotionSetDto.Status)

                    return Ok graceReturnValue
                with
                | ex ->
                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Failed to apply event for PromotionSetId: {PromotionSetId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId,
                        promotionSetDto.PromotionSetId
                    )

                    return
                        Error(
                            (GraceError.CreateWithException ex "Failed while applying PromotionSet event." correlationId)
                                .enhance(nameof RepositoryId, promotionSetDto.RepositoryId)
                                .enhance (nameof PromotionSetId, promotionSetDto.PromotionSetId)
                        )
            }

        interface IHasRepositoryId with
            member this.GetRepositoryId correlationId = promotionSetDto.RepositoryId |> returnTask

        interface IPromotionSetActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId

                not
                <| promotionSetDto.PromotionSetId.Equals(PromotionSetId.Empty)
                |> returnTask

            member this.IsDeleted correlationId =
                this.correlationId <- correlationId
                promotionSetDto.DeletedAt.IsSome |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId
                promotionSetDto |> returnTask

            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                state.State :> IReadOnlyList<PromotionSetEvent>
                |> returnTask

            member this.Handle command metadata =
                let isValid (promotionSetCommand: PromotionSetCommand) (eventMetadata: EventMetadata) =
                    task {
                        if state.State.Exists(fun ev -> ev.Metadata.CorrelationId = eventMetadata.CorrelationId) then
                            return Error(GraceError.Create "Duplicate correlation ID for PromotionSet command." eventMetadata.CorrelationId)
                        else
                            match promotionSetCommand with
                            | CreatePromotionSet _ when
                                promotionSetDto.PromotionSetId
                                <> PromotionSetId.Empty
                                ->
                                return Error(GraceError.Create "PromotionSet already exists." eventMetadata.CorrelationId)
                            | Apply when promotionSetDto.Status = PromotionSetStatus.Succeeded ->
                                return Error(GraceError.Create "PromotionSet has already been applied successfully." eventMetadata.CorrelationId)
                            | _ -> return Ok promotionSetCommand
                    }

                let processCommand (promotionSetCommand: PromotionSetCommand) (eventMetadata: EventMetadata) =
                    task {
                        let eventType =
                            match promotionSetCommand with
                            | CreatePromotionSet (promotionSetId, ownerId, organizationId, repositoryId, targetBranchId) ->
                                PromotionSetEventType.Created(promotionSetId, ownerId, organizationId, repositoryId, targetBranchId)
                            | UpdateInputPromotions promotionPointers -> PromotionSetEventType.InputPromotionsUpdated promotionPointers
                            | RecomputeStepsIfStale _ ->
                                PromotionSetEventType.RecomputeStarted(
                                    promotionSetDto.ComputedAgainstParentTerminalPromotionReferenceId
                                    |> Option.defaultValue ReferenceId.Empty
                                )
                            | ResolveConflicts _ -> PromotionSetEventType.Blocked("Manual conflict resolution recorded.", Option.None)
                            | Apply -> PromotionSetEventType.ApplyStarted
                            | DeleteLogical (force, deleteReason) -> PromotionSetEventType.LogicalDeleted(force, deleteReason)

                        let promotionSetEvent = { Event = eventType; Metadata = eventMetadata }
                        return! this.ApplyEvent promotionSetEvent
                    }

                task {
                    currentCommand <- getDiscriminatedUnionCaseName command
                    this.correlationId <- metadata.CorrelationId

                    match! isValid command metadata with
                    | Ok validCommand -> return! processCommand validCommand metadata
                    | Error validationError -> return Error validationError
                }
