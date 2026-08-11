namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.Events
open Grace.Types.Common
open Grace.Types.WorkItem
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

/// Groups Orleans actor helpers for work item keys, proxies, state, or workflow transitions.
module WorkItem =

    /// Checks whether the request correlation id already appears in persisted events.
    let internal hasDuplicateCorrelationId (events: seq<WorkItemEvent>) (metadata: EventMetadata) =
        events
        |> Seq.exists (fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId)

    /// Describes whether a WorkItem event candidate became durable before the actor can update its projection or publish it.
    type internal WorkItemEventPersistenceOutcome =
        | Persisted
        | FailedRecovered of candidateIsDurable: bool * writeError: exn
        | FailedUnrecoverable of writeError: exn * reloadError: exn

    /// Rebuilds the actor projection from the full durable WorkItem event stream after activation or uncertain persistence.
    let internal rebuildWorkItemState (events: seq<WorkItemEvent>) =
        events
        |> Seq.fold (fun currentState workItemEvent -> WorkItemState.UpdateState workItemEvent currentState) WorkItemState.Default

    /// Confirms a reloaded durable stream contains this exact correlation and domain event candidate.
    let internal containsDurableWorkItemEventCandidate (candidate: WorkItemEvent) (events: seq<WorkItemEvent>) =
        events
        |> Seq.exists (fun persisted ->
            persisted.Metadata.CorrelationId = candidate.Metadata.CorrelationId
            && persisted.Event = candidate.Event)

    /// Persists a copied event stream so failed writes cannot leave an activation-only WorkItem event for later commands to publish.
    let internal persistWorkItemEventWithDurableRecovery (state: IPersistentState<List<WorkItemEvent>>) workItemEvent =
        task {
            let originalEvents = state.State
            let candidateEvents = List<WorkItemEvent>(originalEvents)
            candidateEvents.Add(workItemEvent)
            state.State <- candidateEvents

            try
                do! state.WriteStateAsync()
                return Persisted
            with
            | writeError ->
                try
                    do! state.ReadStateAsync()
                    return FailedRecovered(containsDurableWorkItemEventCandidate workItemEvent state.State, writeError)
                with
                | reloadError ->
                    state.State <- originalEvents
                    return FailedUnrecoverable(writeError, reloadError)
        }

    /// Indicates that a WorkItem actor must deactivate rather than continue from state that could not be reloaded after a write failure.
    let internal workItemPersistenceRequiresDeactivation outcome =
        match outcome with
        | FailedUnrecoverable _ -> true
        | Persisted
        | FailedRecovered _ -> false


    /// Implements the Orleans grain for work item actor.
    type WorkItemActor([<PersistentState(StateName.WorkItem, Constants.GraceActorStorage)>] state: IPersistentState<List<WorkItemEvent>>) =
        inherit Grain()

        static let actorName = ActorName.WorkItem

        let log = loggerFactory.CreateLogger("WorkItem.Actor")

        let mutable currentCommand = String.Empty

        let mutable workItemState = WorkItemState.Default

        /// Stores the correlation id used by this actor while reporting timings and errors.
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            workItemState <- state.State |> rebuildWorkItemState

            Task.CompletedTask

        /// Completes the success path only after this activation has a projection derived from the durable event stream.
        member private this.CompletePersistedEvent(workItemEvent: WorkItemEvent) =
            task {
                let correlationId = workItemEvent.Metadata.CorrelationId
                let graceEvent = GraceEvent.WorkItemEvent workItemEvent
                do! publishGraceEvent graceEvent workItemEvent.Metadata

                return
                    (GraceReturnValue.Create "Work item command succeeded." correlationId)
                        .enhance(nameof RepositoryId, workItemState.WorkItem.RepositoryId)
                        .enhance(nameof WorkItemId, workItemState.WorkItem.WorkItemId)
                        .enhance (nameof WorkItemEventType, getDiscriminatedUnionFullName workItemEvent.Event)
            }

        /// Applies one persisted WorkItem event to this activation's in-memory state.
        member private this.ApplyEvent(workItemEvent: WorkItemEvent) =
            task {
                let correlationId = workItemEvent.Metadata.CorrelationId

                try
                    match! persistWorkItemEventWithDurableRecovery state workItemEvent with
                    | Persisted ->
                        workItemState <-
                            workItemState
                            |> WorkItemState.UpdateState workItemEvent

                        let! returnValue = this.CompletePersistedEvent workItemEvent
                        return Ok returnValue
                    | FailedRecovered (candidateIsDurable, writeError) ->
                        workItemState <- rebuildWorkItemState state.State

                        if candidateIsDurable then
                            let! returnValue = this.CompletePersistedEvent workItemEvent
                            return Ok returnValue
                        else
                            return raise writeError
                    | FailedUnrecoverable (writeError, reloadError) as outcome ->
                        if workItemPersistenceRequiresDeactivation outcome then this.DeactivateOnIdle()

                        return raise (AggregateException("WorkItem event persistence failed and durable state could not be reloaded.", writeError, reloadError))
                with
                | ex ->
                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {hostName}; CorrelationId: {correlationId}; Failed to apply event {eventType} for work item {workItemId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId,
                        getDiscriminatedUnionCaseName workItemEvent.Event,
                        workItemState.WorkItem.WorkItemId
                    )

                    let graceError =
                        (GraceError.CreateWithException ex (WorkItemError.getErrorMessage WorkItemError.FailedWhileApplyingEvent) correlationId)
                            .enhance (nameof WorkItemId, workItemState.WorkItem.WorkItemId)

                    return Error graceError
            }

        interface IHasRepositoryId with
            /// Returns the repository id recorded in this WorkItem actor state.
            member this.GetRepositoryId correlationId = workItemState.WorkItem.RepositoryId |> returnTask

        interface IWorkItemActor with
            /// Reports whether this WorkItem actor has persisted state.
            member this.Exists correlationId =
                this.correlationId <- correlationId

                not
                <| workItemState.WorkItem.WorkItemId.Equals(WorkItemDto.Default.WorkItemId)
                |> returnTask

            /// Returns the current WorkItem actor state snapshot.
            member this.Get correlationId =
                this.correlationId <- correlationId
                workItemState.WorkItem |> returnTask

            /// Returns the actor-only description reference for server hydration without exposing storage facts publicly.
            member this.GetState correlationId =
                this.correlationId <- correlationId
                workItemState |> returnTask

            /// Returns the persisted WorkItem event stream for replay or audit.
            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                state.State :> IReadOnlyList<WorkItemEvent>
                |> returnTask

            /// Routes a public actor command to the domain operation that validates and persists it.
            member this.Handle command metadata =
                /// Checks whether command validation succeeded before emitting the domain event.
                let isValid (command: WorkItemCommand) (metadata: EventMetadata) =
                    task {
                        if hasDuplicateCorrelationId state.State metadata then
                            return Error(GraceError.Create (WorkItemError.getErrorMessage WorkItemError.DuplicateCorrelationId) metadata.CorrelationId)
                        else
                            match command with
                            | Create _ ->
                                if workItemState.WorkItem.WorkItemId
                                   <> WorkItemId.Empty then
                                    return Error(GraceError.Create (WorkItemError.getErrorMessage WorkItemError.WorkItemAlreadyExists) metadata.CorrelationId)
                                else
                                    return Ok command
                            | _ ->
                                if workItemState.WorkItem.WorkItemId = WorkItemId.Empty then
                                    return Error(GraceError.Create (WorkItemError.getErrorMessage WorkItemError.WorkItemDoesNotExist) metadata.CorrelationId)
                                else
                                    return Ok command
                    }

                /// Runs WorkItem command decisions, applies emitted events, and persists the result.
                let processCommand (command: WorkItemCommand) (metadata: EventMetadata) =
                    task {
                        let! workItemEventType =
                            task {
                                match command with
                                | Create (workItemId, workItemNumber, ownerId, organizationId, repositoryId, title, description) ->
                                    return Created(workItemId, workItemNumber, ownerId, organizationId, repositoryId, title, description)
                                | SetTitle title -> return TitleSet title
                                | SetDescription description -> return DescriptionSet description
                                | ClearDescription description -> return DescriptionCleared description
                                | SetStatus status -> return StatusSet status
                                | AddParticipant userId -> return ParticipantAdded userId
                                | RemoveParticipant userId -> return ParticipantRemoved userId
                                | AddTag tag -> return TagAdded tag
                                | RemoveTag tag -> return TagRemoved tag
                                | SetConstraints constraints -> return ConstraintsSet constraints
                                | SetNotes notes -> return NotesSet notes
                                | SetArchitecturalNotes notes -> return ArchitecturalNotesSet notes
                                | SetMigrationNotes notes -> return MigrationNotesSet notes
                                | AddExternalRef reference -> return ExternalRefAdded reference
                                | RemoveExternalRef reference -> return ExternalRefRemoved reference
                                | LinkBranch branchId -> return BranchLinked branchId
                                | UnlinkBranch branchId -> return BranchUnlinked branchId
                                | LinkReference referenceId -> return ReferenceLinked referenceId
                                | UnlinkReference referenceId -> return ReferenceUnlinked referenceId
                                | LinkArtifact artifactId -> return ArtifactLinked artifactId
                                | UnlinkArtifact artifactId -> return ArtifactUnlinked artifactId
                                | LinkPromotionSet promotionSetId -> return PromotionSetLinked promotionSetId
                                | UnlinkPromotionSet promotionSetId -> return PromotionSetUnlinked promotionSetId
                                | LinkReviewNotes reviewNotesId -> return ReviewNotesLinked reviewNotesId
                                | UnlinkReviewNotes reviewNotesId -> return ReviewNotesUnlinked reviewNotesId
                                | LinkReviewCheckpoint reviewCheckpointId -> return ReviewCheckpointLinked reviewCheckpointId
                                | UnlinkReviewCheckpoint reviewCheckpointId -> return ReviewCheckpointUnlinked reviewCheckpointId
                                | LinkValidationResult validationResultId -> return ValidationResultLinked validationResultId
                                | UnlinkValidationResult validationResultId -> return ValidationResultUnlinked validationResultId
                            }

                        let workItemEvent = { Event = workItemEventType; Metadata = metadata }
                        return! this.ApplyEvent workItemEvent
                    }

                task {
                    currentCommand <- getDiscriminatedUnionCaseName command
                    this.correlationId <- metadata.CorrelationId
                    RequestContext.Set(Constants.CurrentCommandProperty, getDiscriminatedUnionCaseName command)

                    match! isValid command metadata with
                    | Ok command -> return! processCommand command metadata
                    | Error error -> return Error error
                }
