namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Artifact
open Grace.Types.Events
open Grace.Types.Common
open Grace.Types.Reminder
open Grace.Types.WorkItem
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

/// Groups Orleans actor helpers for artifact keys, proxies, state, or workflow transitions.
module Artifact =

    /// Tests whether a persisted reminder is the same deletion generation, excluding diagnostic creation and correlation values.
    let isSamePhysicalDeletionReminder (existing: ReminderDto) (candidate: ReminderDto) =
        let sameState =
            match existing.State, candidate.State with
            | ReminderState.ArtifactPhysicalDeletion existingState, ReminderState.ArtifactPhysicalDeletion candidateState ->
                existingState.ArtifactId = candidateState.ArtifactId
                && existingState.RepositoryId = candidateState.RepositoryId
                && existingState.WorkItemId = candidateState.WorkItemId
                && existingState.DeletionGeneration = candidateState.DeletionGeneration
                && existingState.DeletedAt = candidateState.DeletedAt
                && existingState.PhysicalDeletionAt = candidateState.PhysicalDeletionAt
            | _ -> false

        existing.Class = candidate.Class
        && existing.ReminderId = candidate.ReminderId
        && existing.ActorName = candidate.ActorName
        && existing.ActorId = candidate.ActorId
        && existing.OwnerId = candidate.OwnerId
        && existing.OrganizationId = candidate.OrganizationId
        && existing.RepositoryId = candidate.RepositoryId
        && existing.ReminderType = candidate.ReminderType
        && existing.ReminderTime = candidate.ReminderTime
        && sameState

    /// Tests whether a reminder still names the exact durable tombstone generation held by an Artifact actor.
    let matchesPhysicalDeletionState (artifact: ArtifactMetadata) (reminderState: PhysicalDeletionReminderState) =
        artifact.ArtifactId = reminderState.ArtifactId
        && artifact.RepositoryId = reminderState.RepositoryId
        && artifact.WorkItemId = Some reminderState.WorkItemId
        && artifact.DeletionGeneration = reminderState.DeletionGeneration
        && artifact.DeletedAt = Some reminderState.DeletedAt
        && artifact.PhysicalDeletionAt = Some reminderState.PhysicalDeletionAt

    /// Reports whether metadata represents a work-item-owned reviewer attachment governed by the deletion lifecycle.
    let isOwnedReviewerAttachment (artifact: ArtifactMetadata) =
        artifact.WorkItemId.IsSome
        && match artifact.ArtifactType with
           | ArtifactType.AgentSummary
           | ArtifactType.Prompt
           | ArtifactType.ReviewNotes -> true
           | ArtifactType.Other kind ->
               kind.Equals("summary", StringComparison.OrdinalIgnoreCase)
               || kind.Equals("agentsummary", StringComparison.OrdinalIgnoreCase)
               || kind.Equals("prompt", StringComparison.OrdinalIgnoreCase)
               || kind.Equals("notes", StringComparison.OrdinalIgnoreCase)
               || kind.Equals("reviewnotes", StringComparison.OrdinalIgnoreCase)
           | _ -> false

    /// Revalidates the exact owning WorkItem link from the actor snapshot used at the Artifact mutation boundary.
    let hasCurrentWorkItemLink (artifact: ArtifactMetadata) (workItemId: WorkItemId) (workItem: WorkItemDto) =
        artifact.WorkItemId = Some workItemId
        && workItem.WorkItemId = workItemId
        && workItem.RepositoryId = artifact.RepositoryId
        && (workItem.ArtifactIds
            |> List.contains artifact.ArtifactId)

    /// Implements the Orleans grain for artifact actor.
    type ArtifactActor([<PersistentState(StateName.Artifact, Constants.GraceActorStorage)>] eventState: IPersistentState<List<ArtifactEvent>>) =
        inherit Grain()

        static let actorName = ActorName.Artifact
        let log = loggerFactory.CreateLogger("Artifact.Actor")

        let mutable currentCommand = String.Empty
        let mutable artifact = ArtifactMetadata.Default

        /// Stores the correlation id used by this actor while reporting timings and errors.
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage eventState.RecordExists)

            artifact <-
                eventState.State
                |> Seq.fold (fun dto event -> ArtifactMetadata.UpdateDto event dto) artifact

            Task.CompletedTask

        /// Applies one persisted Artifact event to this activation's in-memory state.
        member private this.ApplyEvent(artifactEvent: ArtifactEvent) =
            task {
                let correlationId = artifactEvent.Metadata.CorrelationId

                try
                    let updatedArtifact =
                        artifact
                        |> ArtifactMetadata.UpdateDto artifactEvent

                    eventState.State.Add(artifactEvent)
                    do! eventState.WriteStateAsync()

                    artifact <- updatedArtifact

                    let graceEvent = GraceEvent.ArtifactEvent artifactEvent
                    do! publishGraceEvent graceEvent artifactEvent.Metadata

                    let graceReturnValue: GraceReturnValue<string> =
                        (GraceReturnValue.Create "Artifact command succeeded." correlationId)
                            .enhance(nameof RepositoryId, artifact.RepositoryId)
                            .enhance (nameof ArtifactId, artifact.ArtifactId)

                    return Ok graceReturnValue
                with
                | ex ->
                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Failed to apply event for ArtifactId: {ArtifactId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId,
                        artifact.ArtifactId
                    )

                    return
                        Error(
                            (GraceError.CreateWithException ex "Failed while applying Artifact event." correlationId)
                                .enhance(nameof RepositoryId, artifact.RepositoryId)
                                .enhance (nameof ArtifactId, artifact.ArtifactId)
                        )
            }

        /// Persists the deterministic reminder for the active attachment deletion generation.
        member private this.EnsurePhysicalDeletionReminder() =
            task {
                match artifact.WorkItemId, artifact.DeletedAt, artifact.PhysicalDeletionAt with
                | Some workItemId, Some deletedAt, Some physicalDeletionAt when artifact.DeletionGeneration <> Guid.Empty ->
                    let reminderState: PhysicalDeletionReminderState =
                        {
                            ArtifactId = artifact.ArtifactId
                            RepositoryId = artifact.RepositoryId
                            WorkItemId = workItemId
                            DeletionGeneration = artifact.DeletionGeneration
                            DeletedAt = deletedAt
                            PhysicalDeletionAt = physicalDeletionAt
                            CorrelationId = this.correlationId
                        }

                    let reminder =
                        ReminderDto.CreateWithId
                            artifact.DeletionGeneration
                            actorName
                            this.IdentityString
                            artifact.OwnerId
                            artifact.OrganizationId
                            artifact.RepositoryId
                            ReminderTypes.PhysicalDeletion
                            physicalDeletionAt
                            (ReminderState.ArtifactPhysicalDeletion reminderState)
                            this.correlationId

                    let reminderActorProxy = Reminder.CreateActorProxy artifact.DeletionGeneration this.correlationId
                    let! existing = reminderActorProxy.GetOrAdd reminder this.correlationId

                    if isSamePhysicalDeletionReminder existing reminder then
                        return Ok()
                    else
                        return Error(GraceError.Create "The attachment deletion reminder identity is already used by different state." this.correlationId)
                | _ -> return Error(GraceError.Create "The attachment deletion state is incomplete." this.correlationId)
            }

        /// Applies one cleanup progress snapshot without losing the recoverable tombstone on retryable failure.
        member private this.ApplyCleanupProgress(eventName: string, updatedArtifact: ArtifactMetadata, metadata: EventMetadata) =
            ArtifactEvent.FromMetadata(eventName, updatedArtifact, metadata)
            |> this.ApplyEvent

        /// Revalidates the reminder identity and converges idempotent blob, link, and actor-state cleanup.
        member private this.ConvergePhysicalDeletion(reminder: ReminderDto, reminderState: PhysicalDeletionReminderState) =
            task {
                let identityMatches = matchesPhysicalDeletionState artifact reminderState

                if not identityMatches then
                    return Ok()
                else
                    let repositoryActorProxy = Repository.CreateActorProxy artifact.OrganizationId artifact.RepositoryId reminder.CorrelationId
                    let! repositoryDto = repositoryActorProxy.Get reminder.CorrelationId

                    if repositoryDto.OwnerId <> artifact.OwnerId
                       || repositoryDto.OrganizationId
                          <> artifact.OrganizationId
                       || repositoryDto.RepositoryId
                          <> artifact.RepositoryId then
                        return Error(GraceError.Create "Attachment cleanup repository identity no longer matches stored artifact state." reminder.CorrelationId)
                    else
                        let workItemActorProxy = WorkItem.CreateActorProxy reminderState.WorkItemId artifact.RepositoryId reminder.CorrelationId
                        let! workItemDto = workItemActorProxy.Get reminder.CorrelationId

                        if workItemDto.RepositoryId <> artifact.RepositoryId
                           || workItemDto.WorkItemId <> reminderState.WorkItemId then
                            return
                                Error(GraceError.Create "Attachment cleanup work-item identity no longer matches stored artifact state." reminder.CorrelationId)
                        elif
                            not artifact.BlobDeleted
                            && not
                                (
                                    workItemDto.ArtifactIds
                                    |> List.contains artifact.ArtifactId
                                )
                        then
                            return Error(GraceError.Create "Attachment cleanup cannot prove the current owning work-item link." reminder.CorrelationId)
                        else
                            let mutable cleanupError: GraceError option = None

                            if not artifact.BlobDeleted then
                                match! deleteArtifactBlobIfExists repositoryDto artifact.BlobPath reminder.CorrelationId with
                                | Error graceError -> cleanupError <- Some graceError
                                | Ok _ ->
                                    let cleanupMetadata = EventMetadata.New $"{reminderState.DeletionGeneration:N}:artifact-blob-deleted" GraceSystemUser

                                    match! this.ApplyCleanupProgress(ArtifactEventNames.BlobDeleted, { artifact with BlobDeleted = true }, cleanupMetadata) with
                                    | Error graceError -> cleanupError <- Some graceError
                                    | Ok _ -> ()

                            if cleanupError.IsNone
                               && not artifact.WorkItemLinkRemoved then
                                let! currentWorkItem = workItemActorProxy.Get reminder.CorrelationId

                                if currentWorkItem.ArtifactIds
                                   |> List.contains artifact.ArtifactId then
                                    let unlinkCorrelationId = $"{reminderState.DeletionGeneration:N}:artifact-physical-unlink"
                                    let unlinkMetadata = EventMetadata.New unlinkCorrelationId GraceSystemUser

                                    match! workItemActorProxy.Handle (WorkItemCommand.UnlinkArtifact artifact.ArtifactId) unlinkMetadata with
                                    | Error graceError -> cleanupError <- Some graceError
                                    | Ok _ -> ()
                                else
                                    let! events = workItemActorProxy.GetEvents reminder.CorrelationId
                                    let unlinkCorrelationId = $"{reminderState.DeletionGeneration:N}:artifact-physical-unlink"

                                    if
                                        not
                                            (
                                                events
                                                |> Seq.exists (fun event ->
                                                    event.Metadata.CorrelationId = unlinkCorrelationId
                                                    && event.Event = WorkItemEventType.ArtifactUnlinked artifact.ArtifactId)
                                            )
                                    then
                                        cleanupError <-
                                            Some(GraceError.Create "Attachment cleanup found an unproven missing work-item link." reminder.CorrelationId)

                                if cleanupError.IsNone then
                                    let cleanupMetadata =
                                        EventMetadata.New $"{reminderState.DeletionGeneration:N}:artifact-workitem-link-removed" GraceSystemUser

                                    match!
                                        this.ApplyCleanupProgress
                                            (
                                                ArtifactEventNames.WorkItemLinkRemoved,
                                                { artifact with WorkItemLinkRemoved = true },
                                                cleanupMetadata
                                            )
                                        with
                                    | Error graceError -> cleanupError <- Some graceError
                                    | Ok _ -> ()

                            match cleanupError with
                            | Some graceError -> return Error graceError
                            | None ->
                                do! eventState.ClearStateAsync()
                                artifact <- ArtifactMetadata.Default
                                this.DeactivateOnIdle()
                                return Ok()
            }

        interface IHasRepositoryId with
            /// Returns the repository id recorded in this Artifact actor state.
            member this.GetRepositoryId correlationId = artifact.RepositoryId |> returnTask

        interface IArtifactActor with
            /// Schedules an Artifact reminder through the shared durable reminder actor.
            member this.ScheduleReminderAsync reminderType delay state correlationId =
                task {
                    let reminder =
                        ReminderDto.Create
                            actorName
                            this.IdentityString
                            artifact.OwnerId
                            artifact.OrganizationId
                            artifact.RepositoryId
                            reminderType
                            (getFutureInstant delay)
                            state
                            correlationId

                    do! createReminder reminder
                }
                :> Task

            /// Receives only exact Artifact physical-deletion reminders; stale generations are inert.
            member this.ReceiveReminderAsync(reminder: ReminderDto) =
                task {
                    this.correlationId <- reminder.CorrelationId

                    match reminder.ReminderType, reminder.State with
                    | ReminderTypes.PhysicalDeletion, ReminderState.ArtifactPhysicalDeletion reminderState ->
                        return! this.ConvergePhysicalDeletion(reminder, reminderState)
                    | reminderType, state ->
                        return
                            Error(
                                GraceError.Create
                                    $"{actorName} does not process reminder type {getDiscriminatedUnionCaseName reminderType} with state {getDiscriminatedUnionCaseName state}."
                                    reminder.CorrelationId
                            )
                }

            /// Reports whether this Artifact actor has persisted state.
            member this.Exists correlationId =
                this.correlationId <- correlationId

                not
                <| artifact.ArtifactId.Equals(ArtifactId.Empty)
                |> returnTask

            /// Returns the current Artifact actor state snapshot.
            member this.Get correlationId =
                this.correlationId <- correlationId

                if artifact.ArtifactId = ArtifactId.Empty then Option.None else Some artifact
                |> returnTask

            /// Returns the persisted Artifact event stream for replay or audit.
            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                eventState.State :> IReadOnlyList<ArtifactEvent>
                |> returnTask

            /// Serializes retained generic unlink with Artifact lifecycle mutations and rejects owned reviewer attachments.
            member this.UnlinkFromWorkItem workItemId repositoryId metadata =
                task {
                    this.correlationId <- metadata.CorrelationId

                    if isOwnedReviewerAttachment artifact then
                        return Error(GraceError.Create "Owned reviewer attachments must be deleted through the attachment lifecycle." metadata.CorrelationId)
                    else
                        let workItemActorProxy = WorkItem.CreateActorProxy workItemId repositoryId metadata.CorrelationId
                        return! workItemActorProxy.Handle (WorkItemCommand.UnlinkArtifact(this.GetPrimaryKey())) metadata
                }

            /// Routes a public actor command to the domain operation that validates and persists it.
            member this.Handle command metadata =
                /// Checks whether command validation succeeded before emitting the domain event.
                let isValid (artifactCommand: ArtifactCommand) (eventMetadata: EventMetadata) =
                    task {
                        let duplicateCorrelation = eventState.State.Exists(fun ev -> ev.Metadata.CorrelationId = eventMetadata.CorrelationId)

                        let convergedReplay =
                            (String.Equals(artifactCommand.Command, ArtifactCommandNames.DeleteLogical, StringComparison.OrdinalIgnoreCase)
                             && artifact.IsDeleted)
                            || (String.Equals(artifactCommand.Command, ArtifactCommandNames.Undelete, StringComparison.OrdinalIgnoreCase)
                                && not artifact.IsDeleted)

                        if duplicateCorrelation && not convergedReplay then
                            return Error(GraceError.Create "Duplicate correlation ID for Artifact command." eventMetadata.CorrelationId)
                        else
                            match artifactCommand.Command with
                            | command when
                                not
                                    (
                                        String.Equals(command, ArtifactCommandNames.Create, StringComparison.OrdinalIgnoreCase)
                                        || String.Equals(command, ArtifactCommandNames.DeleteLogical, StringComparison.OrdinalIgnoreCase)
                                        || String.Equals(command, ArtifactCommandNames.Undelete, StringComparison.OrdinalIgnoreCase)
                                    )
                                ->
                                return
                                    Error(
                                        (GraceError.Create "Unsupported Artifact command." eventMetadata.CorrelationId)
                                            .enhance ("Command", artifactCommand.Command)
                                    )
                            | command when
                                String.Equals(command, ArtifactCommandNames.Create, StringComparison.OrdinalIgnoreCase)
                                && artifact.ArtifactId <> ArtifactId.Empty
                                ->
                                return Error(GraceError.Create "Artifact already exists." eventMetadata.CorrelationId)
                            | command when
                                not (String.Equals(command, ArtifactCommandNames.Create, StringComparison.OrdinalIgnoreCase))
                                && artifact.ArtifactId = ArtifactId.Empty
                                ->
                                return Error(GraceError.Create "Artifact does not exist." eventMetadata.CorrelationId)
                            | command when
                                String.Equals(command, ArtifactCommandNames.DeleteLogical, StringComparison.OrdinalIgnoreCase)
                                && (artifact.WorkItemId
                                    <> Some artifactCommand.WorkItemId
                                    || String.IsNullOrWhiteSpace artifactCommand.DeleteReason
                                    || artifactCommand.DeletionGeneration = Guid.Empty
                                    || artifactCommand.DeletedAtUnixTimeTicks <= 0L
                                    || artifactCommand.PhysicalDeletionAtUnixTimeTicks < artifactCommand.DeletedAtUnixTimeTicks
                                    || (match artifact.ArtifactType with
                                        | ArtifactType.AgentSummary
                                        | ArtifactType.Prompt
                                        | ArtifactType.ReviewNotes -> false
                                        | _ -> true))
                                ->
                                return Error(GraceError.Create "Artifact ownership or delete reason is invalid." eventMetadata.CorrelationId)
                            | command when
                                String.Equals(command, ArtifactCommandNames.Undelete, StringComparison.OrdinalIgnoreCase)
                                && artifact.WorkItemId
                                   <> Some artifactCommand.WorkItemId
                                ->
                                return Error(GraceError.Create "Artifact ownership is invalid." eventMetadata.CorrelationId)
                            | _ ->
                                if
                                    String.Equals(artifactCommand.Command, ArtifactCommandNames.DeleteLogical, StringComparison.OrdinalIgnoreCase)
                                    || String.Equals(artifactCommand.Command, ArtifactCommandNames.Undelete, StringComparison.OrdinalIgnoreCase)
                                then
                                    let workItemActorProxy =
                                        WorkItem.CreateActorProxy artifactCommand.WorkItemId artifact.RepositoryId eventMetadata.CorrelationId

                                    let! currentWorkItem = workItemActorProxy.Get eventMetadata.CorrelationId

                                    if hasCurrentWorkItemLink artifact artifactCommand.WorkItemId currentWorkItem then
                                        return Ok artifactCommand
                                    else
                                        return Error(GraceError.Create "The current owning work-item link cannot be proved." eventMetadata.CorrelationId)
                                else
                                    return Ok artifactCommand
                    }

                /// Runs Artifact command decisions, applies emitted events, and persists the result.
                let processCommand (artifactCommand: ArtifactCommand) (eventMetadata: EventMetadata) =
                    task {
                        if String.Equals(artifactCommand.Command, ArtifactCommandNames.Create, StringComparison.OrdinalIgnoreCase) then
                            let createdArtifact = artifactCommand.ToCreated()
                            let artifactEvent = ArtifactEvent.FromCreated(ArtifactEventNames.Created, createdArtifact, eventMetadata)
                            return! this.ApplyEvent artifactEvent
                        elif String.Equals(artifactCommand.Command, ArtifactCommandNames.DeleteLogical, StringComparison.OrdinalIgnoreCase) then
                            if artifact.IsDeleted then
                                match! this.EnsurePhysicalDeletionReminder() with
                                | Ok _ -> return Ok(GraceReturnValue.Create "Artifact is already logically deleted." eventMetadata.CorrelationId)
                                | Error graceError -> return Error graceError
                            else
                                let deletedAt = Instant.FromUnixTimeTicks artifactCommand.DeletedAtUnixTimeTicks
                                let physicalDeletionAt = Instant.FromUnixTimeTicks artifactCommand.PhysicalDeletionAtUnixTimeTicks

                                let updatedArtifact =
                                    { artifact with
                                        DeletedAt = Some deletedAt
                                        DeleteReason = artifactCommand.DeleteReason
                                        DeletionGeneration = artifactCommand.DeletionGeneration
                                        PhysicalDeletionAt = Some physicalDeletionAt
                                        BlobDeleted = false
                                        WorkItemLinkRemoved = false
                                    }

                                match! this.ApplyEvent(ArtifactEvent.FromMetadata(ArtifactEventNames.LogicalDeleted, updatedArtifact, eventMetadata)) with
                                | Error graceError -> return Error graceError
                                | Ok graceReturnValue ->
                                    match! this.EnsurePhysicalDeletionReminder() with
                                    | Ok _ -> return Ok graceReturnValue
                                    | Error graceError -> return Error graceError
                        else
                            match artifact.DeletedAt, artifact.PhysicalDeletionAt with
                            | None, _ -> return Ok(GraceReturnValue.Create "Artifact is not logically deleted." eventMetadata.CorrelationId)
                            | Some _, Some deadline when getCurrentInstant () >= deadline ->
                                return Error(GraceError.Create "The attachment recovery deadline has passed." eventMetadata.CorrelationId)
                            | Some _, _ ->
                                let updatedArtifact =
                                    { artifact with
                                        DeletedAt = None
                                        DeleteReason = String.Empty
                                        DeletionGeneration = Guid.Empty
                                        PhysicalDeletionAt = None
                                        BlobDeleted = false
                                        WorkItemLinkRemoved = false
                                    }

                                return! this.ApplyEvent(ArtifactEvent.FromMetadata(ArtifactEventNames.Undeleted, updatedArtifact, eventMetadata))
                    }

                task {
                    currentCommand <- command.Command
                    this.correlationId <- metadata.CorrelationId

                    match! isValid command metadata with
                    | Ok validCommand -> return! processCommand validCommand metadata
                    | Error validationError -> return Error validationError
                }
