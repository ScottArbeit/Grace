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
open Grace.Types.Common
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Globalization
open System.Threading.Tasks

module Reference =

    type ManifestSaveContributionPlan =
        {
            RepositoryId: RepositoryId
            ReferenceId: ReferenceId
            Manifest: FileManifest
            CounterCommand: RepositoryContentCounterCommand
            WorkflowRanges: ManifestContributionWorkflowRange array
        }

    let private manifestContributionOperationId (referenceId: ReferenceId) (storagePoolId: StoragePoolId) (manifestAddress: ManifestAddress) =
        RepositoryContentCounterOperationId $"reference:{referenceId:N}:{storagePoolId}:{manifestAddress}"

    let private repositoryContentCounterPrimaryKey (repositoryId: RepositoryId) (storagePoolId: StoragePoolId) (manifestAddress: ManifestAddress) =
        $"{repositoryId:N}|{storagePoolId}|{manifestAddress}"

    let private manifestContributionWorkflowPrimaryKey (repositoryId: RepositoryId) (storagePoolId: StoragePoolId) (manifestAddress: ManifestAddress) =
        $"{repositoryId:N}|{storagePoolId}|{manifestAddress}"

    let private counterCommandDirection command =
        match command with
        | RepositoryContentCounterCommand.AddReference _ -> ManifestContributionDirection.Increment
        | RepositoryContentCounterCommand.RemoveReference _ -> ManifestContributionDirection.Decrement

    let private workflowOperationId (referenceId: ReferenceId) (storagePoolId: StoragePoolId) (manifestAddress: ManifestAddress) direction =
        match direction with
        | ManifestContributionDirection.Increment ->
            ManifestContributionWorkflowOperationId $"reference:{referenceId:N}:{storagePoolId}:{manifestAddress}:fanout"
        | ManifestContributionDirection.Decrement ->
            ManifestContributionWorkflowOperationId $"reference-expiry:{referenceId:N}:{storagePoolId}:{manifestAddress}:fanout"

    let private workflowRangesForManifest (manifest: FileManifest) =
        let storagePoolId = manifest.StoragePoolId
        let seenContentBlocks = HashSet<ContentBlockAddress>()
        let ranges = ResizeArray<ManifestContributionWorkflowRange>()
        let mutable index = 0

        while index < manifest.Blocks.Count do
            let block = manifest.Blocks[index]

            if seenContentBlocks.Add block.Address then
                ranges.Add({ StoragePoolId = storagePoolId; ContentBlockAddress = block.Address; OrdinalStart = 0; OrdinalCount = 1 })

            index <- index + 1

        ranges.ToArray()

    let private workflowStartCommandForPlan plan =
        let direction = counterCommandDirection plan.CounterCommand

        ManifestContributionWorkflowCommand.Start
            {
                OperationId = workflowOperationId plan.ReferenceId plan.Manifest.StoragePoolId plan.Manifest.ManifestAddress direction
                RepositoryId = plan.RepositoryId
                StoragePoolId = plan.Manifest.StoragePoolId
                ManifestAddress = plan.Manifest.ManifestAddress
                Direction = direction
                Ranges = plan.WorkflowRanges
            }

    let private planManifestReferences
        repositoryId
        referenceId
        (manifestReferences: DirectoryVersion.ManifestReferenceForSaveBoundary seq)
        : ManifestSaveContributionPlan list
        =
        manifestReferences
        |> Seq.distinctBy (fun manifestReference -> manifestReference.Manifest.StoragePoolId, manifestReference.Manifest.ManifestAddress)
        |> Seq.map (fun manifestReference -> manifestReference.Manifest)
        |> Seq.toList
        |> List.map (fun manifest ->
            let operationId = manifestContributionOperationId referenceId manifest.StoragePoolId manifest.ManifestAddress

            {
                RepositoryId = repositoryId
                ReferenceId = referenceId
                Manifest = manifest
                CounterCommand = RepositoryContentCounterCommand.AddReference(operationId, repositoryId, manifest.StoragePoolId, manifest.ManifestAddress)
                WorkflowRanges = workflowRangesForManifest manifest
            })

    let planManifestSaveBoundaryForDirectoryVersions repositoryId referenceId (directoryVersions: DirectoryVersion seq) correlationId =
        let directoryVersionArray = directoryVersions |> Seq.toArray
        let manifestReferences = ResizeArray<DirectoryVersion.ManifestReferenceForSaveBoundary>()
        let mutable index = 0
        let mutable error: GraceError option = None

        while index < directoryVersionArray.Length
              && error.IsNone do
            match DirectoryVersion.getManifestReferencesForSaveBoundary directoryVersionArray[index] correlationId with
            | Error graceError -> error <- Some graceError
            | Ok directoryManifestReferences -> manifestReferences.AddRange directoryManifestReferences

            index <- index + 1

        match error with
        | Some graceError -> Error graceError
        | None -> Ok(planManifestReferences repositoryId referenceId manifestReferences)

    let validateRecursiveDirectoryVersionsComplete rootDirectoryVersionId (directoryVersions: DirectoryVersion seq) correlationId =
        let directoryVersionArray = directoryVersions |> Seq.toArray

        let directoryVersionIds =
            HashSet<DirectoryVersionId>(
                directoryVersionArray
                |> Seq.map (fun directoryVersion -> directoryVersion.DirectoryVersionId)
            )

        if not (directoryVersionIds.Contains rootDirectoryVersionId) then
            Error(
                (GraceError.Create "Recursive directory traversal did not include the root DirectoryVersion." correlationId)
                    .enhance (nameof DirectoryVersionId, rootDirectoryVersionId)
            )
        else
            let missingChild =
                directoryVersionArray
                |> Seq.collect (fun directoryVersion ->
                    directoryVersion.Directories
                    |> Seq.map (fun childDirectoryVersionId -> directoryVersion, childDirectoryVersionId))
                |> Seq.tryFind (fun (_, childDirectoryVersionId) -> not (directoryVersionIds.Contains childDirectoryVersionId))

            match missingChild with
            | Some (parentDirectoryVersion, childDirectoryVersionId) ->
                Error(
                    (GraceError.Create "Recursive directory traversal did not include a declared child DirectoryVersion." correlationId)
                        .enhance(nameof DirectoryVersionId, rootDirectoryVersionId)
                        .enhance("ParentDirectoryVersionId", parentDirectoryVersion.DirectoryVersionId)
                        .enhance ("ChildDirectoryVersionId", childDirectoryVersionId)
                )
            | None -> Ok directoryVersionArray

    let planManifestSaveBoundaryForRecursiveDirectoryVersions repositoryId referenceId rootDirectoryVersionId directoryVersions correlationId =
        match validateRecursiveDirectoryVersionsComplete rootDirectoryVersionId directoryVersions correlationId with
        | Error graceError -> Error graceError
        | Ok completeDirectoryVersions -> planManifestSaveBoundaryForDirectoryVersions repositoryId referenceId completeDirectoryVersions correlationId

    let planManifestSaveBoundary repositoryId referenceId (directoryVersion: DirectoryVersion) correlationId =
        planManifestSaveBoundaryForDirectoryVersions repositoryId referenceId [ directoryVersion ] correlationId

    let planManifestSaveExpiryBoundaryForDirectoryVersions repositoryId referenceId directoryVersions correlationId =
        match planManifestSaveBoundaryForDirectoryVersions repositoryId referenceId directoryVersions correlationId with
        | Error graceError -> Error graceError
        | Ok plans ->
            plans
            |> List.map (fun plan ->
                let operationId =
                    RepositoryContentCounterOperationId $"reference-expiry:{referenceId:N}:{plan.Manifest.StoragePoolId}:{plan.Manifest.ManifestAddress}"

                { plan with
                    CounterCommand =
                        RepositoryContentCounterCommand.RemoveReference(operationId, repositoryId, plan.Manifest.StoragePoolId, plan.Manifest.ManifestAddress)
                })
            |> Ok

    let planManifestSaveExpiryBoundary repositoryId referenceId (directoryVersion: DirectoryVersion) correlationId =
        planManifestSaveExpiryBoundaryForDirectoryVersions repositoryId referenceId [ directoryVersion ] correlationId

    let shouldApplyManifestExpiryBoundary (referenceDto: ReferenceDto) =
        referenceDto.ReferenceId <> ReferenceId.Empty
        && referenceDto.ReferenceType = ReferenceType.Save

    let planManifestSaveExpiryBoundaryForReferenceDirectoryVersions
        repositoryId
        referenceId
        rootDirectoryVersionId
        (referenceDto: ReferenceDto)
        (getRecursiveDirectoryVersions: unit -> Task<Grace.Types.DirectoryVersion.DirectoryVersionDto array>)
        correlationId
        =
        task {
            if shouldApplyManifestExpiryBoundary referenceDto then
                let! directoryVersionDtos = getRecursiveDirectoryVersions ()

                match
                    planManifestSaveBoundaryForRecursiveDirectoryVersions
                        repositoryId
                        referenceId
                        rootDirectoryVersionId
                        (directoryVersionDtos
                         |> Seq.map (fun directoryVersionDto -> directoryVersionDto.DirectoryVersion))
                        correlationId
                    with
                | Error graceError -> return Error graceError
                | Ok plans ->
                    return
                        plans
                        |> List.map (fun plan ->
                            let operationId =
                                RepositoryContentCounterOperationId
                                    $"reference-expiry:{referenceId:N}:{plan.Manifest.StoragePoolId}:{plan.Manifest.ManifestAddress}"

                            { plan with
                                CounterCommand =
                                    RepositoryContentCounterCommand.RemoveReference(
                                        operationId,
                                        repositoryId,
                                        plan.Manifest.StoragePoolId,
                                        plan.Manifest.ManifestAddress
                                    )
                            })
                        |> Ok
            else
                return Ok []
        }

    let validateReferenceRootDirectoryVersionHashes correlationId repositoryId directoryId sha256Hash blake3Hash (directoryVersion: DirectoryVersion) =
        let rootRelativePath = directoryVersion.RelativePath

        let isRootDirectoryRelativePath =
            rootRelativePath = Constants.RootDirectoryPath
            || rootRelativePath = "/"

        let directoryVersionBlake3IsEmpty = String.IsNullOrWhiteSpace(string directoryVersion.Blake3Hash)
        let commandBlake3IsEmpty = String.IsNullOrWhiteSpace(string blake3Hash)

        let isLegacyEmptyBlake3Root =
            isRootDirectoryRelativePath
            && directoryVersionBlake3IsEmpty
            && commandBlake3IsEmpty

        if directoryVersion.DirectoryVersionId = DirectoryVersionId.Empty then
            Error(
                (GraceError.Create "Reference root DirectoryVersion does not exist." correlationId)
                    .enhance(nameof RepositoryId, repositoryId)
                    .enhance (nameof DirectoryVersionId, directoryId)
            )
        elif not isRootDirectoryRelativePath then
            Error(
                (GraceError.Create "Reference root DirectoryVersion must use the repository root path." correlationId)
                    .enhance(nameof RepositoryId, repositoryId)
                    .enhance(nameof DirectoryVersionId, directoryId)
                    .enhance (nameof RelativePath, directoryVersion.RelativePath)
            )
        elif directoryVersionBlake3IsEmpty
             && not commandBlake3IsEmpty then
            Error(
                (GraceError.Create "Reference root DirectoryVersion must include Blake3Hash before reference creation." correlationId)
                    .enhance(nameof RepositoryId, repositoryId)
                    .enhance (nameof DirectoryVersionId, directoryId)
            )
        elif commandBlake3IsEmpty
             && not isLegacyEmptyBlake3Root then
            Error(
                (GraceError.Create "Reference command must include the root DirectoryVersion Blake3Hash." correlationId)
                    .enhance(nameof RepositoryId, repositoryId)
                    .enhance (nameof DirectoryVersionId, directoryId)
            )
        elif directoryVersion.Sha256Hash <> sha256Hash then
            Error(
                (GraceError.Create "Reference command Sha256Hash does not match the root DirectoryVersion Sha256Hash." correlationId)
                    .enhance(nameof RepositoryId, repositoryId)
                    .enhance(nameof DirectoryVersionId, directoryId)
                    .enhance (nameof Sha256Hash, sha256Hash)
            )
        elif directoryVersion.Blake3Hash <> blake3Hash
             && not isLegacyEmptyBlake3Root then
            Error(
                (GraceError.Create "Reference command Blake3Hash does not match the root DirectoryVersion Blake3Hash." correlationId)
                    .enhance(nameof RepositoryId, repositoryId)
                    .enhance(nameof DirectoryVersionId, directoryId)
                    .enhance (nameof Blake3Hash, blake3Hash)
            )
        else
            Ok()

    let internal repairLegacyCreatedEventBlake3
        (getDirectoryVersion: RepositoryId -> DirectoryVersionId -> CorrelationId -> Task<DirectoryVersion>)
        (referenceEvent: ReferenceEvent)
        =
        task {
            match referenceEvent.Event with
            | Created (referenceId, ownerId, organizationId, repositoryId, branchId, directoryId, sha256Hash, blake3Hash, referenceType, referenceText, links) when
                String.IsNullOrWhiteSpace(string blake3Hash)
                ->
                let! directoryVersion = getDirectoryVersion repositoryId directoryId referenceEvent.Metadata.CorrelationId

                match ReferenceDto.TryGetLegacyRootDirectoryHashRepair directoryId sha256Hash blake3Hash directoryVersion with
                | Some (repairedSha256Hash, repairedBlake3Hash) ->
                    return
                        { referenceEvent with
                            Event =
                                Created(
                                    referenceId,
                                    ownerId,
                                    organizationId,
                                    repositoryId,
                                    branchId,
                                    directoryId,
                                    repairedSha256Hash,
                                    repairedBlake3Hash,
                                    referenceType,
                                    referenceText,
                                    links
                                )
                        },
                        true
                | None -> return referenceEvent, false
            | _ -> return referenceEvent, false
        }

    let internal createCommandMatchesReference (referenceDto: ReferenceDto) command =
        match command with
        | Create (referenceId, ownerId, organizationId, repositoryId, branchId, directoryId, sha256Hash, blake3Hash, referenceType, referenceText, links) ->
            referenceDto.UpdatedAt.IsSome
            && referenceDto.ReferenceId = referenceId
            && referenceDto.OwnerId = ownerId
            && referenceDto.OrganizationId = organizationId
            && referenceDto.RepositoryId = repositoryId
            && referenceDto.BranchId = branchId
            && referenceDto.DirectoryId = directoryId
            && referenceDto.Sha256Hash = sha256Hash
            && referenceDto.Blake3Hash = blake3Hash
            && referenceDto.ReferenceType = referenceType
            && referenceDto.ReferenceText = referenceText
            && (referenceDto.Links |> Seq.toArray) = (links |> Seq.toArray)
        | _ -> false

    let tryCreateManifestContributionStart plan intent =
        match intent with
        | RepositoryContentCounterIntent.IncrementManifestReferenceCount (repositoryId, storagePoolId, manifestAddress) when
            repositoryId = plan.RepositoryId
            && storagePoolId = plan.Manifest.StoragePoolId
            && manifestAddress = plan.Manifest.ManifestAddress
            && counterCommandDirection plan.CounterCommand = ManifestContributionDirection.Increment
            ->
            Some(workflowStartCommandForPlan plan)
        | RepositoryContentCounterIntent.DecrementManifestReferenceCount (repositoryId, storagePoolId, manifestAddress) when
            repositoryId = plan.RepositoryId
            && storagePoolId = plan.Manifest.StoragePoolId
            && manifestAddress = plan.Manifest.ManifestAddress
            && counterCommandDirection plan.CounterCommand = ManifestContributionDirection.Decrement
            ->
            Some(workflowStartCommandForPlan plan)
        | _ -> None

    let private counterCommandOperationId command =
        match command with
        | RepositoryContentCounterCommand.AddReference (operationId, _, _, _)
        | RepositoryContentCounterCommand.RemoveReference (operationId, _, _, _) -> Some operationId

    let private commandCrossedZero operationId command events =
        let mutable referenceCount = 0L
        let mutable crossedZero = false

        for counterEvent in events do
            match counterEvent.Event with
            | RepositoryContentCounterEventType.ReferenceAdded (eventOperationId, _, _, _) ->
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

    let private createRepositoryContentCounterActor repositoryId storagePoolId manifestAddress correlationId =
        let grain =
            orleansClient.CreateActorProxyWithCorrelationId<IRepositoryContentCounterActor>(
                repositoryContentCounterPrimaryKey repositoryId storagePoolId manifestAddress,
                correlationId
            )

        let orleansContext = Dictionary<string, obj>()
        orleansContext.Add(nameof RepositoryId, repositoryId)
        orleansContext.Add(nameof StoragePoolId, storagePoolId)
        orleansContext.Add(Constants.ActorNameProperty, ActorName.RepositoryContentCounter)
        memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
        grain

    let private createManifestContributionWorkflowActor repositoryId storagePoolId manifestAddress correlationId =
        let grain =
            orleansClient.CreateActorProxyWithCorrelationId<IManifestContributionWorkflowActor>(
                manifestContributionWorkflowPrimaryKey repositoryId storagePoolId manifestAddress,
                correlationId
            )

        let orleansContext = Dictionary<string, obj>()
        orleansContext.Add(nameof RepositoryId, repositoryId)
        orleansContext.Add(nameof StoragePoolId, storagePoolId)
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

                let counterActor =
                    createRepositoryContentCounterActor plan.RepositoryId plan.Manifest.StoragePoolId plan.Manifest.ManifestAddress metadata.CorrelationId

                match! counterActor.Handle plan.CounterCommand metadata with
                | Error graceError -> error <- Some graceError
                | Ok counterReturnValue ->
                    let! counterEvents = counterActor.GetEvents metadata.CorrelationId

                    match tryCreateManifestContributionStartForCounterDecision plan counterReturnValue.ReturnValue counterEvents with
                    | None -> ()
                    | Some startCommand ->
                        let workflowActor =
                            createManifestContributionWorkflowActor
                                plan.RepositoryId
                                plan.Manifest.StoragePoolId
                                plan.Manifest.ManifestAddress
                                metadata.CorrelationId

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
            task {
                let activateStartTime = getCurrentInstant ()

                logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

                let getDirectoryVersion repositoryId directoryId correlationId =
                    task {
                        let directoryVersionActorProxy = DirectoryVersion.CreateActorProxy directoryId repositoryId correlationId
                        let! directoryVersionDto = directoryVersionActorProxy.Get correlationId
                        return directoryVersionDto.DirectoryVersion
                    }

                let! repairResults =
                    state.State
                    |> Seq.map (fun referenceEvent -> task { return! repairLegacyCreatedEventBlake3 getDirectoryVersion referenceEvent })
                    |> Task.WhenAll

                let repairedLegacyCreatedEvent =
                    repairResults
                    |> Array.exists (fun (_, wasRepaired) -> wasRepaired)

                if repairedLegacyCreatedEvent then
                    state.State.Clear()
                    state.State.AddRange(repairResults |> Array.map fst)
                    do! state.WriteStateAsync()

                referenceDto <-
                    repairResults
                    |> Array.map fst
                    |> Seq.fold (fun referenceDto event -> ReferenceDto.UpdateDto event referenceDto) referenceDto
            }
            :> Task

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

                        let! boundaryResult =
                            task {
                                if shouldApplyManifestExpiryBoundary referenceDto then
                                    let directoryVersionActorProxy =
                                        DirectoryVersion.CreateActorProxy
                                            physicalDeletionReminderState.DirectoryVersionId
                                            physicalDeletionReminderState.RepositoryId
                                            this.correlationId

                                    match!
                                        planManifestSaveExpiryBoundaryForReferenceDirectoryVersions
                                            physicalDeletionReminderState.RepositoryId
                                            referenceId
                                            physicalDeletionReminderState.DirectoryVersionId
                                            referenceDto
                                            (fun () -> directoryVersionActorProxy.GetRecursiveDirectoryVersions false this.correlationId)
                                            this.correlationId
                                        with
                                    | Error graceError -> return Error graceError
                                    | Ok plans -> return! applyManifestContributionBoundary plans (EventMetadata.New this.correlationId "GraceSystem")
                                else
                                    return Ok()
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
                    | Created (referenceId,
                               ownerId,
                               organizationId,
                               repositoryId,
                               branchId,
                               directoryId,
                               sha256Hash,
                               blake3Hash,
                               referenceType,
                               referenceText,
                               links) ->
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
                                            Blake3Hash = referenceDto.Blake3Hash
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
                                            Blake3Hash = referenceDto.Blake3Hash
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
                            if createCommandMatchesReference referenceDto command then
                                return Ok command
                            else
                                return Error(GraceError.Create (getErrorMessage ReferenceError.DuplicateCorrelationId) metadata.CorrelationId)
                        else
                            match command with
                            | Create (referenceId,
                                      ownerId,
                                      organizationId,
                                      repositoryId,
                                      branchId,
                                      directoryId,
                                      sha256Hash,
                                      blake3Hash,
                                      referenceType,
                                      referenceText,
                                      links) ->
                                match referenceDto.UpdatedAt with
                                | Some _ when createCommandMatchesReference referenceDto command -> return Ok command
                                | Some _ -> return Error(GraceError.Create (getErrorMessage ReferenceError.ReferenceAlreadyExists) metadata.CorrelationId)
                                | None -> return Ok command
                            | _ ->
                                match referenceDto.UpdatedAt with
                                | Some _ -> return Ok command
                                | None -> return Error(GraceError.Create (getErrorMessage ReferenceError.ReferenceIdDoesNotExist) metadata.CorrelationId)
                    }

                let processCommand (command: ReferenceCommand) (metadata: EventMetadata) =
                    let appliesManifestBoundary referenceType = referenceType = ReferenceType.Save

                    let applyReferenceManifestBoundary referenceId repositoryId directoryId referenceType =
                        task {
                            if not (appliesManifestBoundary referenceType) then
                                return Ok()
                            else
                                let directoryVersionActorProxy = DirectoryVersion.CreateActorProxy directoryId repositoryId metadata.CorrelationId
                                let! directoryVersionDtos = directoryVersionActorProxy.GetRecursiveDirectoryVersions false metadata.CorrelationId

                                match
                                    planManifestSaveBoundaryForRecursiveDirectoryVersions
                                        repositoryId
                                        referenceId
                                        directoryId
                                        (directoryVersionDtos
                                         |> Seq.map (fun directoryVersionDto -> directoryVersionDto.DirectoryVersion))
                                        metadata.CorrelationId
                                    with
                                | Error graceError -> return Error graceError
                                | Ok plans -> return! applyManifestContributionBoundary plans metadata
                        }

                    let applyReferenceManifestExpiryBoundary referenceId repositoryId directoryId referenceType =
                        task {
                            if not (appliesManifestBoundary referenceType) then
                                return Ok()
                            else
                                let directoryVersionActorProxy = DirectoryVersion.CreateActorProxy directoryId repositoryId metadata.CorrelationId
                                let! directoryVersionDtos = directoryVersionActorProxy.GetRecursiveDirectoryVersions false metadata.CorrelationId

                                match!
                                    planManifestSaveExpiryBoundaryForReferenceDirectoryVersions
                                        repositoryId
                                        referenceId
                                        directoryId
                                        referenceDto
                                        (fun () -> Task.FromResult directoryVersionDtos)
                                        metadata.CorrelationId
                                    with
                                | Error graceError -> return Error graceError
                                | Ok plans -> return! applyManifestContributionBoundary plans metadata
                        }

                    let existingReferenceReturnValue () =
                        (GraceReturnValue.Create referenceDto metadata.CorrelationId)
                            .enhance(nameof RepositoryId, referenceDto.RepositoryId)
                            .enhance(nameof BranchId, referenceDto.BranchId)
                            .enhance(nameof ReferenceId, referenceDto.ReferenceId)
                            .enhance(nameof DirectoryVersionId, referenceDto.DirectoryId)
                            .enhance(nameof ReferenceType, getDiscriminatedUnionCaseName referenceDto.ReferenceType)
                            .enhance (
                                nameof ReferenceEventType,
                                getDiscriminatedUnionFullName (
                                    Created(
                                        referenceDto.ReferenceId,
                                        referenceDto.OwnerId,
                                        referenceDto.OrganizationId,
                                        referenceDto.RepositoryId,
                                        referenceDto.BranchId,
                                        referenceDto.DirectoryId,
                                        referenceDto.Sha256Hash,
                                        referenceDto.Blake3Hash,
                                        referenceDto.ReferenceType,
                                        referenceDto.ReferenceText,
                                        referenceDto.Links
                                    )
                                )
                            )

                    let validateRootDirectoryVersionHashes repositoryId directoryId sha256Hash blake3Hash =
                        task {
                            let directoryVersionActorProxy = DirectoryVersion.CreateActorProxy directoryId repositoryId metadata.CorrelationId
                            let! directoryVersionDto = directoryVersionActorProxy.Get metadata.CorrelationId

                            return
                                validateReferenceRootDirectoryVersionHashes
                                    metadata.CorrelationId
                                    repositoryId
                                    directoryId
                                    sha256Hash
                                    blake3Hash
                                    directoryVersionDto.DirectoryVersion
                        }

                    task {
                        match command with
                        | Create (referenceId, _, _, repositoryId, _, directoryId, _, _, referenceType, _, _) when
                            createCommandMatchesReference referenceDto command
                            ->
                            match! applyReferenceManifestBoundary referenceId repositoryId directoryId referenceType with
                            | Ok () -> return Ok(existingReferenceReturnValue ())
                            | Error graceError -> return Error graceError
                        | _ ->
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
                                              blake3Hash,
                                              referenceType,
                                              referenceText,
                                              links) ->
                                        match! validateRootDirectoryVersionHashes repositoryId directoryId sha256Hash blake3Hash with
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
                                                        blake3Hash,
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
                                                Blake3Hash = referenceDto.Blake3Hash
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
                                            applyReferenceManifestExpiryBoundary
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

                                match returnValue, referenceEventType with
                                | Ok graceReturnValue, Created (referenceId, _, _, repositoryId, _, directoryId, _, _, referenceType, _, _) ->
                                    match! applyReferenceManifestBoundary referenceId repositoryId directoryId referenceType with
                                    | Ok () -> return Ok graceReturnValue
                                    | Error graceError -> return Error graceError
                                | _ -> return returnValue
                            | Error graceError -> return Error graceError
                    }

                task {
                    currentCommand <- $"{getDiscriminatedUnionCaseName command} {getDiscriminatedUnionCaseName referenceDto.ReferenceType}"
                    this.correlationId <- metadata.CorrelationId

                    match! isValid command metadata with
                    | Ok command -> return! processCommand command metadata
                    | Error error -> return Error error
                }
