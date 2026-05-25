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
open Grace.Types.ManifestContributionWorkflow
open Grace.Types.Reference
open Grace.Types.Reminder
open Grace.Types.RepositoryContentCounter
open Grace.Types.Types
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Globalization
open System.Threading.Tasks

module Reference =

    [<Literal>]
    let private DefaultStoragePoolId = "default"

    type ManifestSaveContributionPlan =
        {
            RepositoryId: RepositoryId
            ReferenceId: ReferenceId
            Manifest: FileManifest
            CounterCommand: RepositoryContentCounterCommand
            WorkflowRanges: ManifestContributionWorkflowRange array
        }

    let private manifestContributionOperationId (referenceId: ReferenceId) (manifestAddress: ManifestAddress) =
        RepositoryContentCounterOperationId $"save:{referenceId:N}:{manifestAddress}"

    let private repositoryContentCounterPrimaryKey (repositoryId: RepositoryId) (manifestAddress: ManifestAddress) = $"{repositoryId:N}|{manifestAddress}"

    let private manifestContributionWorkflowPrimaryKey (repositoryId: RepositoryId) (manifestAddress: ManifestAddress) = $"{repositoryId:N}|{manifestAddress}"

    let private counterCommandDirection command =
        match command with
        | RepositoryContentCounterCommand.AddReference _ -> ManifestContributionDirection.Increment
        | RepositoryContentCounterCommand.RemoveReference _ -> ManifestContributionDirection.Decrement

    let private workflowOperationId (referenceId: ReferenceId) (manifestAddress: ManifestAddress) direction =
        match direction with
        | ManifestContributionDirection.Increment -> ManifestContributionWorkflowOperationId $"save:{referenceId:N}:{manifestAddress}:fanout"
        | ManifestContributionDirection.Decrement -> ManifestContributionWorkflowOperationId $"save-expiry:{referenceId:N}:{manifestAddress}:fanout"

    let private workflowRangesForManifest (manifest: FileManifest) =
        let ranges = ResizeArray<ManifestContributionWorkflowRange>()
        let mutable index = 0

        while index < manifest.Blocks.Count do
            let block = manifest.Blocks[index]

            ranges.Add({ StoragePoolId = StoragePoolId DefaultStoragePoolId; ContentBlockAddress = block.Address; OrdinalStart = index; OrdinalCount = 1 })

            index <- index + 1

        ranges.ToArray()

    let private workflowStartCommandForPlan plan =
        let direction = counterCommandDirection plan.CounterCommand

        ManifestContributionWorkflowCommand.Start
            {
                OperationId = workflowOperationId plan.ReferenceId plan.Manifest.ManifestAddress direction
                RepositoryId = plan.RepositoryId
                ManifestAddress = plan.Manifest.ManifestAddress
                Direction = direction
                Ranges = plan.WorkflowRanges
            }

    let planManifestSaveBoundary repositoryId referenceId (directoryVersion: DirectoryVersion) correlationId =
        match DirectoryVersion.getManifestReferencesForSaveBoundary directoryVersion correlationId with
        | Error graceError -> Error graceError
        | Ok manifests ->
            manifests
            |> List.map (fun manifest ->
                let operationId = manifestContributionOperationId referenceId manifest.ManifestAddress

                {
                    RepositoryId = repositoryId
                    ReferenceId = referenceId
                    Manifest = manifest
                    CounterCommand = RepositoryContentCounterCommand.AddReference(operationId, repositoryId, manifest.ManifestAddress)
                    WorkflowRanges = workflowRangesForManifest manifest
                })
            |> Ok

    let planManifestSaveExpiryBoundary repositoryId referenceId (directoryVersion: DirectoryVersion) correlationId =
        planManifestSaveBoundary repositoryId referenceId directoryVersion correlationId
        |> Result.map (fun plans ->
            plans
            |> List.map (fun plan ->
                let operationId = RepositoryContentCounterOperationId $"save-expiry:{referenceId:N}:{plan.Manifest.ManifestAddress}"

                { plan with CounterCommand = RepositoryContentCounterCommand.RemoveReference(operationId, repositoryId, plan.Manifest.ManifestAddress) }))

    let tryCreateManifestContributionStart plan intent =
        match intent with
        | RepositoryContentCounterIntent.IncrementManifestReferenceCount (repositoryId, manifestAddress) when
            repositoryId = plan.RepositoryId
            && manifestAddress = plan.Manifest.ManifestAddress
            && counterCommandDirection plan.CounterCommand = ManifestContributionDirection.Increment
            ->
            Some(workflowStartCommandForPlan plan)
        | RepositoryContentCounterIntent.DecrementManifestReferenceCount (repositoryId, manifestAddress) when
            repositoryId = plan.RepositoryId
            && manifestAddress = plan.Manifest.ManifestAddress
            && counterCommandDirection plan.CounterCommand = ManifestContributionDirection.Decrement
            ->
            Some(workflowStartCommandForPlan plan)
        | _ -> None

    let private counterCommandOperationId command =
        match command with
        | RepositoryContentCounterCommand.AddReference (operationId, _, _)
        | RepositoryContentCounterCommand.RemoveReference (operationId, _, _) -> Some operationId

    let private commandCrossedZero operationId command events =
        let mutable referenceCount = 0L
        let mutable crossedZero = false

        for counterEvent in events do
            match counterEvent.Event with
            | RepositoryContentCounterEventType.ReferenceAdded (eventOperationId, _, _) ->
                if eventOperationId = operationId
                   && referenceCount = 0L
                   && counterCommandDirection command = ManifestContributionDirection.Increment then
                    crossedZero <- true

                referenceCount <- referenceCount + 1L
            | RepositoryContentCounterEventType.ReferenceRemoved eventOperationId ->
                if eventOperationId = operationId
                   && referenceCount = 1L
                   && counterCommandDirection command = ManifestContributionDirection.Decrement then
                    crossedZero <- true

                referenceCount <- max 0L (referenceCount - 1L)

        crossedZero

    let tryCreateManifestContributionStartForCounterDecision plan (decision: RepositoryContentCounterDecision) events =
        let startFromIntent =
            decision.Intents
            |> List.tryPick (tryCreateManifestContributionStart plan)

        match startFromIntent with
        | Some startCommand -> Some startCommand
        | None when decision.WasIdempotentReplay ->
            match counterCommandOperationId plan.CounterCommand with
            | Some operationId when commandCrossedZero operationId plan.CounterCommand events -> Some(workflowStartCommandForPlan plan)
            | None
            | Some _ -> None
        | None -> None

    let private createRepositoryContentCounterActor repositoryId manifestAddress correlationId =
        let grain =
            orleansClient.CreateActorProxyWithCorrelationId<IRepositoryContentCounterActor>(
                repositoryContentCounterPrimaryKey repositoryId manifestAddress,
                correlationId
            )

        let orleansContext = Dictionary<string, obj>()
        orleansContext.Add(nameof RepositoryId, repositoryId)
        orleansContext.Add(Constants.ActorNameProperty, ActorName.RepositoryContentCounter)
        memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
        grain

    let private createManifestContributionWorkflowActor repositoryId manifestAddress correlationId =
        let grain =
            orleansClient.CreateActorProxyWithCorrelationId<IManifestContributionWorkflowActor>(
                manifestContributionWorkflowPrimaryKey repositoryId manifestAddress,
                correlationId
            )

        let orleansContext = Dictionary<string, obj>()
        orleansContext.Add(nameof RepositoryId, repositoryId)
        orleansContext.Add(Constants.ActorNameProperty, ActorName.ManifestContributionWorkflow)
        memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
        grain

    let private applyManifestContributionBoundary (plans: ManifestSaveContributionPlan list) (metadata: EventMetadata) =
        task {
            let planArray = plans |> List.toArray
            let mutable planIndex = 0
            let mutable error: GraceError option = None

            while planIndex < planArray.Length && error.IsNone do
                let plan = planArray[planIndex]

                let counterActor = createRepositoryContentCounterActor plan.RepositoryId plan.Manifest.ManifestAddress metadata.CorrelationId

                match! counterActor.Handle plan.CounterCommand metadata with
                | Error graceError -> error <- Some graceError
                | Ok counterReturnValue ->
                    let! counterEvents = counterActor.GetEvents metadata.CorrelationId

                    match tryCreateManifestContributionStartForCounterDecision plan counterReturnValue.ReturnValue counterEvents with
                    | None -> ()
                    | Some startCommand ->
                        let workflowActor = createManifestContributionWorkflowActor plan.RepositoryId plan.Manifest.ManifestAddress metadata.CorrelationId

                        match! workflowActor.Handle startCommand metadata with
                        | Ok _ -> ()
                        | Error graceError -> error <- Some graceError

                planIndex <- planIndex + 1

            match error with
            | Some graceError -> return Error graceError
            | None -> return Ok()
        }

    type ReferenceActor([<PersistentState(StateName.Reference, Constants.GraceActorStorage)>] state: IPersistentState<List<ReferenceEvent>>) =
        inherit Grain()

        static let actorName = ActorName.Reference

        let log = loggerFactory.CreateLogger("Reference.Actor")

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

                    match reminder.ReminderType, reminder.State with
                    | ReminderTypes.PhysicalDeletion, ReminderState.ReferencePhysicalDeletion physicalDeletionReminderState ->

                        this.correlationId <- physicalDeletionReminderState.CorrelationId

                        let referenceId =
                            if referenceDto.ReferenceId = ReferenceId.Empty then
                                this.GetPrimaryKey()
                            else
                                referenceDto.ReferenceId

                        let directoryVersionActorProxy =
                            DirectoryVersion.CreateActorProxy
                                physicalDeletionReminderState.DirectoryVersionId
                                physicalDeletionReminderState.RepositoryId
                                this.correlationId

                        let! directoryVersionDto = directoryVersionActorProxy.Get this.correlationId

                        let! boundaryResult =
                            task {
                                match
                                    planManifestSaveExpiryBoundary
                                        physicalDeletionReminderState.RepositoryId
                                        referenceId
                                        directoryVersionDto.DirectoryVersion
                                        this.correlationId
                                    with
                                | Error graceError -> return Error graceError
                                | Ok plans -> return! applyManifestContributionBoundary plans (EventMetadata.New this.correlationId "GraceSystem")
                            }

                        match boundaryResult with
                        | Error graceError -> return Error graceError
                        | Ok () ->
                            // Mark the branch as needing to update its latest references.
                            let branchActorProxy =
                                Branch.CreateActorProxy physicalDeletionReminderState.BranchId physicalDeletionReminderState.RepositoryId this.correlationId

                            do! branchActorProxy.MarkForRecompute physicalDeletionReminderState.CorrelationId

                            // Delete saved state for this actor.
                            do! state.ClearStateAsync()

                            log.LogInformation(
                                "{CurrentInstant}: Node: {hostName}; CorrelationId: {correlationId}; Deleted physical state for reference; RepositoryId: {RepositoryId}; BranchId: {BranchId}; ReferenceId: {ReferenceId}; DirectoryVersionId: {DirectoryVersionId}; deleteReason: {deleteReason}.",
                                getCurrentInstantExtended (),
                                getMachineName,
                                physicalDeletionReminderState.CorrelationId,
                                physicalDeletionReminderState.RepositoryId,
                                physicalDeletionReminderState.BranchId,
                                referenceId,
                                physicalDeletionReminderState.DirectoryVersionId,
                                physicalDeletionReminderState.DeleteReason
                            )

                            this.DeactivateOnIdle()
                            return Ok()
                    | reminderType, state ->
                        return
                            Error(
                                (GraceError.Create
                                    $"{actorName} does not process reminder type {getDiscriminatedUnionCaseName reminderType} with state {getDiscriminatedUnionCaseName state}."
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
                    referenceDto <-
                        referenceDto
                        |> ReferenceDto.UpdateDto referenceEvent

                    // Publish the event to the rest of the world.
                    let graceEvent = GraceEvent.ReferenceEvent referenceEvent
                    do! publishGraceEvent graceEvent referenceEvent.Metadata

                    // If this is a Save or Checkpoint reference, schedule a physical deletion based on the default delays from the repository.
                    match referenceEvent.Event with
                    | Created (referenceId, ownerId, organizationId, repositoryId, branchId, directoryId, sha256Hash, referenceType, referenceText, links) ->
                        do!
                            match referenceDto.ReferenceType with
                            | ReferenceType.Save ->
                                task {
                                    let repositoryActorProxy = Repository.CreateActorProxy referenceDto.OrganizationId referenceDto.RepositoryId correlationId
                                    let! repositoryDto = repositoryActorProxy.Get correlationId

                                    let reminderState: PhysicalDeletionReminderState =
                                        {
                                            RepositoryId = referenceDto.RepositoryId
                                            BranchId = referenceDto.BranchId
                                            DirectoryVersionId = referenceDto.DirectoryId
                                            Sha256Hash = referenceDto.Sha256Hash
                                            DeleteReason = $"Save: automatic deletion after {repositoryDto.SaveDays} days"
                                            CorrelationId = correlationId
                                        }

                                    do!
                                        (this :> IGraceReminderWithGuidKey)
                                            .ScheduleReminderAsync
                                            ReminderTypes.PhysicalDeletion
                                            (Duration.FromDays(float repositoryDto.SaveDays))
                                            (ReminderState.ReferencePhysicalDeletion reminderState)
                                            correlationId
                                }
                            | ReferenceType.Checkpoint ->
                                task {
                                    let repositoryActorProxy = Repository.CreateActorProxy referenceDto.OrganizationId referenceDto.RepositoryId correlationId
                                    let! repositoryDto = repositoryActorProxy.Get correlationId

                                    let reminderState: PhysicalDeletionReminderState =
                                        {
                                            RepositoryId = referenceDto.RepositoryId
                                            BranchId = referenceDto.BranchId
                                            DirectoryVersionId = referenceDto.DirectoryId
                                            Sha256Hash = referenceDto.Sha256Hash
                                            DeleteReason = $"Checkpoint: automatic deletion after {repositoryDto.CheckpointDays} days"
                                            CorrelationId = correlationId
                                        }

                                    do!
                                        (this :> IGraceReminderWithGuidKey)
                                            .ScheduleReminderAsync
                                            ReminderTypes.PhysicalDeletion
                                            (Duration.FromDays(float repositoryDto.CheckpointDays))
                                            (ReminderState.ReferencePhysicalDeletion reminderState)
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
                with
                | ex ->
                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {hostName}; CorrelationId: {correlationId}; Failed to apply event {eventType} for reference {referenceId} in repository {repositoryId} on branch {branchId} with directory version {directoryVersionId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
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

                not
                <| referenceDto.ReferenceId.Equals(ReferenceDto.Default.ReferenceId)
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
                            | Create (referenceId, ownerId, organizationId, repositoryId, branchId, directoryId, sha256Hash, referenceType, referenceText, links) ->
                                match referenceDto.UpdatedAt with
                                | Some _ -> return Error(GraceError.Create (getErrorMessage ReferenceError.ReferenceAlreadyExists) metadata.CorrelationId)
                                | None -> return Ok command
                            | _ ->
                                match referenceDto.UpdatedAt with
                                | Some _ -> return Ok command
                                | None -> return Error(GraceError.Create (getErrorMessage ReferenceError.ReferenceIdDoesNotExist) metadata.CorrelationId)
                    }

                let processCommand (command: ReferenceCommand) (metadata: EventMetadata) =
                    let applySaveManifestBoundary referenceId repositoryId directoryId referenceType =
                        task {
                            if referenceType <> ReferenceType.Save then
                                return Ok()
                            else
                                let directoryVersionActorProxy = DirectoryVersion.CreateActorProxy directoryId repositoryId metadata.CorrelationId
                                let! directoryVersionDto = directoryVersionActorProxy.Get metadata.CorrelationId

                                match planManifestSaveBoundary repositoryId referenceId directoryVersionDto.DirectoryVersion metadata.CorrelationId with
                                | Error graceError -> return Error graceError
                                | Ok plans -> return! applyManifestContributionBoundary plans metadata
                        }

                    let applySaveExpiryManifestBoundary referenceId repositoryId directoryId referenceType =
                        task {
                            if referenceType <> ReferenceType.Save then
                                return Ok()
                            else
                                let directoryVersionActorProxy = DirectoryVersion.CreateActorProxy directoryId repositoryId metadata.CorrelationId
                                let! directoryVersionDto = directoryVersionActorProxy.Get metadata.CorrelationId

                                match planManifestSaveExpiryBoundary repositoryId referenceId directoryVersionDto.DirectoryVersion metadata.CorrelationId with
                                | Error graceError -> return Error graceError
                                | Ok plans -> return! applyManifestContributionBoundary plans metadata
                        }

                    task {
                        let! (referenceEventTypeResult: Result<ReferenceEventType, GraceError>) =
                            task {
                                match command with
                                | Create (referenceId,
                                          ownerId,
                                          organizationId,
                                          repositoryId,
                                          branchId,
                                          directoryId,
                                          sha256Hash,
                                          referenceType,
                                          referenceText,
                                          links) ->
                                    match! applySaveManifestBoundary referenceId repositoryId directoryId referenceType with
                                    | Ok () ->
                                        return
                                            Ok(
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
                                            )
                                    | Error graceError -> return Error graceError
                                | AddLink link -> return Ok(LinkAdded link)
                                | RemoveLink link -> return Ok(LinkRemoved link)
                                | DeleteLogical (force, deleteReason) ->
                                    let tryGetLogicalDeleteDaysFromMetadata () =
                                        match metadata.Properties.TryGetValue("RepositoryLogicalDeleteDays") with
                                        | true, value ->
                                            let mutable parsed = 0.0f

                                            if Single.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, &parsed) then
                                                Some parsed
                                            else
                                                None
                                        | _ -> None

                                    let! logicalDeleteDays =
                                        match tryGetLogicalDeleteDaysFromMetadata () with
                                        | Some days -> Task.FromResult days
                                        | None ->
                                            task {
                                                let repositoryActorProxy =
                                                    Repository.CreateActorProxy referenceDto.OrganizationId referenceDto.RepositoryId this.correlationId

                                                let! repositoryDto = repositoryActorProxy.Get this.correlationId
                                                return repositoryDto.LogicalDeleteDays
                                            }

                                    let reminderState: PhysicalDeletionReminderState =
                                        {
                                            RepositoryId = referenceDto.RepositoryId
                                            BranchId = referenceDto.BranchId
                                            DirectoryVersionId = referenceDto.DirectoryId
                                            Sha256Hash = referenceDto.Sha256Hash
                                            DeleteReason = deleteReason
                                            CorrelationId = metadata.CorrelationId
                                        }

                                    do!
                                        (this :> IGraceReminderWithGuidKey)
                                            .ScheduleReminderAsync
                                            ReminderTypes.PhysicalDeletion
                                            (Duration.FromDays(float logicalDeleteDays))
                                            (ReminderState.ReferencePhysicalDeletion reminderState)
                                            metadata.CorrelationId

                                    return Ok(LogicalDeleted(force, deleteReason))
                                | DeletePhysical ->
                                    match!
                                        applySaveExpiryManifestBoundary
                                            referenceDto.ReferenceId
                                            referenceDto.RepositoryId
                                            referenceDto.DirectoryId
                                            referenceDto.ReferenceType
                                        with
                                    | Ok () ->
                                        // Delete the actor state and mark the actor as deactivated.
                                        do! state.ClearStateAsync()
                                        this.DeactivateOnIdle()
                                        return Ok PhysicalDeleted
                                    | Error graceError -> return Error graceError
                                | Undelete -> return Ok Undeleted
                            }

                        match referenceEventTypeResult with
                        | Ok referenceEventType ->
                            let referenceEvent: ReferenceEvent = { Event = referenceEventType; Metadata = metadata }
                            let! returnValue = this.ApplyEvent referenceEvent
                            return returnValue
                        | Error graceError -> return Error graceError
                    }

                task {
                    currentCommand <- $"{getDiscriminatedUnionCaseName command} {getDiscriminatedUnionCaseName referenceDto.ReferenceType}"
                    this.correlationId <- metadata.CorrelationId

                    match! isValid command metadata with
                    | Ok command -> return! processCommand command metadata
                    | Error error -> return Error error
                }
