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
open Grace.Types.Types
open Grace.Types.WorkItem
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

module WorkItem =

    let internal hasDuplicateCorrelationId (events: seq<WorkItemEvent>) (metadata: EventMetadata) =
        events
        |> Seq.exists (fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId)


    type WorkItemActor([<PersistentState(StateName.WorkItem, Constants.GraceActorStorage)>] state: IPersistentState<List<WorkItemEvent>>) =
        inherit Grain()

        static let actorName = ActorName.WorkItem

        let log = loggerFactory.CreateLogger("WorkItem.Actor")

        let mutable currentCommand = String.Empty

        let mutable workItemDto = WorkItemDto.Default

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            workItemDto <-
                state.State
                |> Seq.fold (fun dto ev -> WorkItemDto.UpdateDto ev dto) workItemDto

            Task.CompletedTask

        member private this.ApplyEvent(workItemEvent: WorkItemEvent) =
            task {
                let correlationId = workItemEvent.Metadata.CorrelationId

                try
                    state.State.Add(workItemEvent)
                    do! state.WriteStateAsync()

                    workItemDto <- workItemDto |> WorkItemDto.UpdateDto workItemEvent

                    let graceEvent = GraceEvent.WorkItemEvent workItemEvent
                    do! publishGraceEvent graceEvent workItemEvent.Metadata

                    let returnValue =
                        (GraceReturnValue.Create "Work item command succeeded." correlationId)
                            .enhance(nameof RepositoryId, workItemDto.RepositoryId)
                            .enhance(nameof WorkItemId, workItemDto.WorkItemId)
                            .enhance (nameof WorkItemEventType, getDiscriminatedUnionFullName workItemEvent.Event)

                    return Ok returnValue
                with
                | ex ->
                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {hostName}; CorrelationId: {correlationId}; Failed to apply event {eventType} for work item {workItemId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId,
                        getDiscriminatedUnionCaseName workItemEvent.Event,
                        workItemDto.WorkItemId
                    )

                    let graceError =
                        (GraceError.CreateWithException ex (WorkItemError.getErrorMessage WorkItemError.FailedWhileApplyingEvent) correlationId)
                            .enhance (nameof WorkItemId, workItemDto.WorkItemId)

                    return Error graceError
            }

        interface IHasRepositoryId with
            member this.GetRepositoryId correlationId = workItemDto.RepositoryId |> returnTask

        interface IWorkItemActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId

                not
                <| workItemDto.WorkItemId.Equals(WorkItemDto.Default.WorkItemId)
                |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId
                workItemDto |> returnTask

            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                state.State :> IReadOnlyList<WorkItemEvent>
                |> returnTask

            member this.Handle command metadata =
                let isValid (command: WorkItemCommand) (metadata: EventMetadata) =
                    task {
                        if hasDuplicateCorrelationId state.State metadata then
                            return Error(GraceError.Create (WorkItemError.getErrorMessage WorkItemError.DuplicateCorrelationId) metadata.CorrelationId)
                        else
                            match command with
                            | Create _ ->
                                if workItemDto.WorkItemId <> WorkItemId.Empty then
                                    return Error(GraceError.Create (WorkItemError.getErrorMessage WorkItemError.WorkItemAlreadyExists) metadata.CorrelationId)
                                else
                                    return Ok command
                            | _ ->
                                if workItemDto.WorkItemId = WorkItemId.Empty then
                                    return Error(GraceError.Create (WorkItemError.getErrorMessage WorkItemError.WorkItemDoesNotExist) metadata.CorrelationId)
                                else
                                    return Ok command
                    }

                let processCommand (command: WorkItemCommand) (metadata: EventMetadata) =
                    task {
                        let! workItemEventType =
                            task {
                                match command with
                                | Create (workItemId, ownerId, organizationId, repositoryId, title, description) ->
                                    return Created(workItemId, ownerId, organizationId, repositoryId, title, description)
                                | SetTitle title -> return TitleSet title
                                | SetDescription description -> return DescriptionSet description
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
                                | LinkPromotionGroup promotionGroupId -> return PromotionGroupLinked promotionGroupId
                                | UnlinkPromotionGroup promotionGroupId -> return PromotionGroupUnlinked promotionGroupId
                                | LinkCandidate candidateId -> return CandidateLinked candidateId
                                | UnlinkCandidate candidateId -> return CandidateUnlinked candidateId
                                | LinkReviewPacket reviewPacketId -> return ReviewPacketLinked reviewPacketId
                                | UnlinkReviewPacket reviewPacketId -> return ReviewPacketUnlinked reviewPacketId
                                | LinkReviewCheckpoint reviewCheckpointId -> return ReviewCheckpointLinked reviewCheckpointId
                                | UnlinkReviewCheckpoint reviewCheckpointId -> return ReviewCheckpointUnlinked reviewCheckpointId
                                | LinkGateAttestation gateAttestationId -> return GateAttestationLinked gateAttestationId
                                | UnlinkGateAttestation gateAttestationId -> return GateAttestationUnlinked gateAttestationId
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
