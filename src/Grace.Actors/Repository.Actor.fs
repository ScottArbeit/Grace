namespace Grace.Actors

open FSharp.Control
open FSharpPlus
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Shared
open Grace.Shared.Combinators
open Grace.Shared.Constants
open Grace.Shared.Resources.Text
open Grace.Shared.Resources.Utilities
open Grace.Types.Branch
open Grace.Types.DirectoryVersion
open Grace.Types.Reminder
open Grace.Types.Repository
open Grace.Types.Events
open Grace.Types.Common
open Grace.Types.Library
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Globalization
open System.Linq
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks
open System.Runtime.Serialization
open Grace.Shared.Services

/// Groups Orleans actor helpers for repository keys, proxies, state, or workflow transitions.
module Repository =

    /// Derives a versioned deterministic actor identity from existing durable Repository workflow identity.
    let internal buildInitialWorkflowId role (repositoryId: RepositoryId) =
        let seed = $"grace.repository.initial-{role}.v1|{repositoryId:N}"
        let hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed))
        let guidBytes = hash[0..15]
        guidBytes[6] <- (guidBytes[6] &&& 0x0Fuy) ||| 0x50uy
        guidBytes[8] <- (guidBytes[8] &&& 0x3Fuy) ||| 0x80uy
        Guid(guidBytes)

    /// Compares a Repository Create command with the immutable facts in its durable Created event.
    let internal createCommandMatchesCreationEvent repositoryEventType command =
        match repositoryEventType, command with
        | Created (createdRepositoryName, createdRepositoryId, createdOwnerId, createdOrganizationId, createdObjectStorageProvider),
          RepositoryCommand.Create (repositoryName, repositoryId, ownerId, organizationId, objectStorageProvider) ->
            createdRepositoryName = repositoryName
            && createdRepositoryId = repositoryId
            && createdOwnerId = ownerId
            && createdOrganizationId = organizationId
            && createdObjectStorageProvider = objectStorageProvider
        | _ -> false

    /// Classifies Repository Create replay from the first immutable Created event despite later mutable events.
    let internal createCommandMatchesCreationHistory repositoryEventTypes command =
        repositoryEventTypes
        |> Seq.tryPick (fun repositoryEventType ->
            match repositoryEventType with
            | Created _ -> Some repositoryEventType
            | _ -> None)
        |> Option.exists (fun createdEvent -> createCommandMatchesCreationEvent createdEvent command)

    /// Reports whether a persisted deterministic bootstrap directory matches the Repository workflow's immutable empty-root contract.
    let internal initialDirectoryMatches (expected: DirectoryVersion) (persisted: DirectoryVersion) =
        persisted.DirectoryVersionId = expected.DirectoryVersionId
        && persisted.OwnerId = expected.OwnerId
        && persisted.OrganizationId = expected.OrganizationId
        && persisted.RepositoryId = expected.RepositoryId
        && persisted.RelativePath = expected.RelativePath
        && persisted.Sha256Hash = expected.Sha256Hash
        && persisted.Blake3Hash = expected.Blake3Hash
        && persisted.Directories.SequenceEqual(expected.Directories)
        && persisted.Files.SequenceEqual(expected.Files)
        && persisted.Size = expected.Size

    /// Implements the Orleans grain for repository actor.
    type RepositoryActor
        (
            [<PersistentState(StateName.Repository, Constants.GraceActorStorage)>] state: IPersistentState<List<RepositoryEvent>>,
            grainFactory: IGrainFactory
        ) =
        inherit Grain()

        static let actorName = ActorName.Repository

        let log = loggerFactory.CreateLogger("Repository.Actor")

        let mutable repositoryDto = RepositoryDto.Default

        /// Reports whether a command exactly replays the immutable persisted Repository creation facts.
        let createCommandMatchesPersistedCreation command =
            state.State
            |> Seq.map (fun repositoryEvent -> repositoryEvent.Event)
            |> fun repositoryEventTypes -> createCommandMatchesCreationHistory repositoryEventTypes command

        /// Stores the correlation id used by this actor while reporting timings and errors.
        member val private correlationId: CorrelationId = String.Empty with get, set

        /// Requests deactivation without exposing the inherited protected call to a task state machine.
        member private this.DeactivateActorOnIdle() = this.DeactivateOnIdle()

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            repositoryDto <-
                state.State
                |> Seq.fold
                    (fun repositoryDto repositoryEvent ->
                        repositoryDto
                        |> RepositoryDto.UpdateDto repositoryEvent)
                    RepositoryDto.Default

            Task.CompletedTask

        /// Applies one persisted Repository event to this activation's in-memory state.
        member private this.ApplyEvent repositoryEvent =
            task {
                try
                    let matchingCreatedRetry =
                        match repositoryEvent.Event with
                        | Created (repositoryName, repositoryId, ownerId, organizationId, objectStorageProvider) ->
                            createCommandMatchesPersistedCreation (
                                RepositoryCommand.Create(repositoryName, repositoryId, ownerId, organizationId, objectStorageProvider)
                            )
                        | _ -> false

                    if not matchingCreatedRetry then
                        // Add the new event to the list of events, and write the state to storage.
                        state.State.Add repositoryEvent
                        do! state.WriteStateAsync()

                        // Update the repositoryDto with the new event.
                        repositoryDto <-
                            repositoryDto
                            |> RepositoryDto.UpdateDto repositoryEvent

                    /// Concatenates repository errors into a single GraceError instance.
                    let processGraceError (repositoryError: RepositoryError) repositoryEvent previousGraceError =
                        Error(
                            GraceError.Create
                                $"{getErrorMessage repositoryError}{Environment.NewLine}{previousGraceError.Error}"
                                repositoryEvent.Metadata.CorrelationId
                        )

                    // If we're creating a repository, we need to create the default branch, the initial promotion, and the initial directory.
                    //   Otherwise, just pass the event through.
                    let handleEvent =
                        task {
                            match repositoryEvent.Event with
                            | Created (name, repositoryId, ownerId, organizationId, objectStorageProvider) ->
                                let actorRepositoryId = this.GetPrimaryKey()
                                let libraryActor = grainFactory.GetGrain<IRepositoryLibraryActor>(actorRepositoryId)

                                do!
                                    libraryActor.InitializeCatalog
                                        (LibraryCatalogDto.CreateInitial(
                                            actorRepositoryId,
                                            repositoryEvent.Metadata.Timestamp,
                                            repositoryEvent.Metadata.Principal
                                        ))
                                        repositoryEvent.Metadata.CorrelationId

                                // Create the default branch.
                                let branchId = buildInitialWorkflowId "branch" repositoryId
                                let initialPromotionReferenceId = buildInitialWorkflowId "promotion-reference" repositoryId
                                let branchActor = Branch.CreateActorProxy branchId repositoryDto.RepositoryId this.correlationId

                                // Only allow promotions and tags on the initial branch.
                                let initialBranchPermissions =
                                    [|
                                        ReferenceType.Promotion
                                        ReferenceType.Tag
                                        ReferenceType.External
                                    |]

                                let createInitialBranchCommand =
                                    BranchCommand.Create(
                                        branchId,
                                        InitialBranchName,
                                        DefaultParentBranchId,
                                        ReferenceId.Empty,
                                        buildInitialWorkflowId "rebase-reference" repositoryId,
                                        ownerId,
                                        organizationId,
                                        repositoryId,
                                        initialBranchPermissions
                                    )

                                match! branchActor.Handle createInitialBranchCommand repositoryEvent.Metadata with
                                | Ok branchGraceReturn ->
                                    logToConsole $"In Repository.Actor.handleEvent: Successfully created the new branch."
                                    // Create an empty directory version, and use that for the initial promotion
                                    let emptyDirectoryId = buildInitialWorkflowId "directory-version" repositoryId

                                    let emptyDirectoryEntries = Array.Empty<DirectoryVersionPreimageEntry>()
                                    let emptySha256Hash = computeSha256ForDirectoryEntries RootDirectoryPath emptyDirectoryEntries
                                    let emptyBlake3Hash = computeBlake3ForDirectory RootDirectoryPath emptyDirectoryEntries

                                    let directoryVersionActorProxy =
                                        DirectoryVersion.CreateActorProxy emptyDirectoryId repositoryDto.RepositoryId this.correlationId

                                    let emptyDirectoryVersion =
                                        DirectoryVersion.CreateWithHashes
                                            emptyDirectoryId
                                            repositoryDto.OwnerId
                                            repositoryDto.OrganizationId
                                            repositoryDto.RepositoryId
                                            RootDirectoryPath
                                            emptySha256Hash
                                            emptyBlake3Hash
                                            (List<DirectoryVersionId>())
                                            (List<FileVersion>())
                                            0L

                                    let! directoryResult =
                                        task {
                                            let! directoryExists = directoryVersionActorProxy.Exists repositoryEvent.Metadata.CorrelationId

                                            if directoryExists then
                                                let! persistedDirectory = directoryVersionActorProxy.Get repositoryEvent.Metadata.CorrelationId

                                                if initialDirectoryMatches emptyDirectoryVersion persistedDirectory.DirectoryVersion then
                                                    return Ok()
                                                else
                                                    return
                                                        Error(
                                                            GraceError.Create
                                                                "The deterministic initial DirectoryVersion identity already contains conflicting data."
                                                                repositoryEvent.Metadata.CorrelationId
                                                        )
                                            else
                                                match!
                                                    directoryVersionActorProxy.Handle
                                                        (DirectoryVersionCommand.Create(emptyDirectoryVersion, repositoryDto))
                                                        repositoryEvent.Metadata
                                                    with
                                                | Ok _ -> return Ok()
                                                | Error graceError -> return Error graceError
                                        }

                                    logToConsole $"In Repository.Actor.handleEvent: Successfully created the empty directory version."

                                    let! promotionResult =
                                        branchActor.Handle
                                            (BranchCommand.Promote(
                                                initialPromotionReferenceId,
                                                emptyDirectoryId,
                                                emptySha256Hash,
                                                emptyBlake3Hash,
                                                (getLocalizedString StringResourceName.InitialPromotionMessage)
                                            ))
                                            repositoryEvent.Metadata

                                    logToConsole $"In Repository.Actor.handleEvent: After trying to create the first promotion."

                                    match directoryResult, promotionResult with
                                    | (Ok (), Ok promotionGraceReturnValue) ->
                                        logToConsole $"In Repository.Actor.handleEvent: Successfully created the initial promotion."

                                        //logToConsole $"promotionGraceReturnValue.Properties:"

                                        //promotionGraceReturnValue.Properties
                                        //|> Seq.iter (fun kv -> logToConsole $"  {kv.Key}: {kv.Value}")
                                        // Set current, empty directory as the based-on reference.
                                        let referenceId = Guid.Parse($"{promotionGraceReturnValue.Properties[nameof ReferenceId]}")

                                        //logToConsole $"In Repository.Actor.handleEvent: Before trying to rebase the initial branch."
                                        //let! rebaseResult = branchActor.Handle (Commands.Branch.BranchCommand.Rebase(referenceId)) repositoryEvent.Metadata
                                        //logToConsole $"In Repository.Actor.handleEvent: After trying to rebase the initial branch."


                                        //match rebaseResult with
                                        //| Ok rebaseGraceReturn -> return Ok(branchId, referenceId)
                                        //| Error graceError -> return processGraceError FailedRebasingInitialBranch repositoryEvent graceError
                                        return Ok(branchId, referenceId)
                                    | (_, Error graceError) -> return processGraceError FailedCreatingInitialPromotion repositoryEvent graceError
                                    | (Error graceError, _) -> return processGraceError FailedCreatingEmptyDirectoryVersion repositoryEvent graceError
                                | Error graceError ->
                                    logToConsole $"In Repository.Actor.handleEvent: Failed to create the new branch."
                                    return processGraceError FailedCreatingInitialBranch repositoryEvent graceError
                            | _ -> return Ok(BranchId.Empty, ReferenceId.Empty)
                        }

                    match! handleEvent with
                    | Ok (branchId, referenceId) ->
                        // Publish the event to the rest of the world.
                        let graceEvent = GraceEvent.RepositoryEvent repositoryEvent
                        do! publishGraceEvent graceEvent repositoryEvent.Metadata

                        let returnValue = GraceReturnValue.Create $"Repository command succeeded." repositoryEvent.Metadata.CorrelationId

                        returnValue
                            .enhance(nameof OwnerId, repositoryDto.OwnerId)
                            .enhance(nameof OrganizationId, repositoryDto.OrganizationId)
                            .enhance(nameof RepositoryId, repositoryDto.RepositoryId)
                            .enhance(nameof RepositoryName, repositoryDto.RepositoryName)
                            .enhance (nameof RepositoryEventType, getDiscriminatedUnionFullName repositoryEvent.Event)
                        |> ignore

                        if branchId <> BranchId.Empty then
                            returnValue
                                .enhance(nameof BranchId, branchId)
                                .enhance(nameof BranchName, Constants.InitialBranchName)
                                .enhance (nameof ReferenceId, referenceId)
                            |> ignore

                        returnValue.Properties.Add("EventType", getDiscriminatedUnionFullName repositoryEvent.Event)

                        return Ok returnValue
                    | Error graceError -> return Error graceError
                with
                | ex ->
                    let exceptionResponse = ExceptionResponse.Create ex

                    let graceError = GraceError.Create (getErrorMessage RepositoryError.FailedWhileApplyingEvent) repositoryEvent.Metadata.CorrelationId

                    graceError
                        .enhance(
                            "Exception details",
                            exceptionResponse.``exception``
                            + exceptionResponse.innerException
                        )
                        .enhance(nameof OwnerId, repositoryDto.OwnerId)
                        .enhance(nameof OrganizationId, repositoryDto.OrganizationId)
                        .enhance(nameof RepositoryId, repositoryDto.RepositoryId)
                        .enhance(nameof RepositoryName, repositoryDto.RepositoryName)
                        .enhance (nameof RepositoryEventType, getDiscriminatedUnionFullName repositoryEvent.Event)
                    |> ignore

                    return Error graceError
            }

        /// Deletes all of the branches provided, by sending a DeleteLogical command to each branch.
        member private this.LogicalDeleteBranches(branches: BranchDto array, metadata: EventMetadata, deleteReason: DeleteReason) =
            task {
                let results = ConcurrentQueue<GraceResult<string>>()

                // Loop through each branch and send a DeleteLogical command to it.
                do!
                    Parallel.ForEachAsync(
                        branches,
                        Constants.ParallelOptions,
                        (fun branch ct ->
                            ValueTask(
                                task {
                                    if branch.DeletedAt |> Option.isNone then
                                        let branchActor = Branch.CreateActorProxy branch.BranchId branch.RepositoryId this.correlationId

                                        let childMetadata = EventMetadata.New metadata.CorrelationId GraceSystemUser
                                        childMetadata.Properties[ nameof RepositoryId ] <- $"{repositoryDto.RepositoryId}"

                                        childMetadata.Properties[ "RepositoryLogicalDeleteDays" ] <-
                                            repositoryDto.LogicalDeleteDays.ToString("F", CultureInfo.InvariantCulture)

                                        let! result =
                                            branchActor.Handle
                                                (BranchCommand.DeleteLogical(
                                                    true,
                                                    $"Cascaded from deleting repository. ownerId: {repositoryDto.OwnerId}; organizationId: {repositoryDto.OrganizationId}; repositoryId: {repositoryDto.RepositoryId}; repositoryName: {repositoryDto.RepositoryName}; deleteReason: {deleteReason}",
                                                    false,
                                                    None
                                                ))
                                                childMetadata

                                        results.Enqueue(result)
                                }
                            ))
                    )

                // Check if any of the results were errors, and take the first one if so.
                let overallResult =
                    results
                    |> Seq.tryPick (fun result ->
                        match result with
                        | Ok _ -> None
                        | Error error -> Some(error))

                match overallResult with
                | None -> return Ok()
                | Some error -> return Error error
            }

        interface IHasRepositoryId with
            /// Returns the repository id recorded in this Repository actor state.
            member this.GetRepositoryId correlationId = repositoryDto.RepositoryId |> returnTask

        interface IGraceReminderWithGuidKey with
            /// Schedules a Grace reminder.
            member this.ScheduleReminderAsync reminderType delay state correlationId =
                task {
                    let reminder =
                        ReminderDto.Create
                            actorName
                            $"{this.IdentityString}"
                            repositoryDto.OwnerId
                            repositoryDto.OrganizationId
                            repositoryDto.RepositoryId
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
                    match reminder.ReminderType, reminder.State with
                    | ReminderTypes.PhysicalDeletion, ReminderState.RepositoryPhysicalDeletion physicalDeletionReminderState ->
                        this.correlationId <- physicalDeletionReminderState.CorrelationId

                        do! state.ClearStateAsync()

                        log.LogInformation(
                            "{CurrentInstant}: Node: {hostName}; CorrelationId: {correlationId}; Deleted physical state for repository; RepositoryId: {}; RepositoryName: {}; OrganizationId: {organizationId}; OwnerId: {ownerId}; deleteReason: {deleteReason}.",
                            getCurrentInstantExtended (),
                            getMachineName,
                            physicalDeletionReminderState.CorrelationId,
                            repositoryDto.RepositoryId,
                            repositoryDto.RepositoryName,
                            repositoryDto.OrganizationId,
                            repositoryDto.OwnerId,
                            physicalDeletionReminderState.DeleteReason
                        )

                        this.DeactivateActorOnIdle()
                        return Ok()
                    | reminderType, state ->
                        return
                            Error(
                                GraceError.Create
                                    $"{actorName} does not process reminder type {getDiscriminatedUnionCaseName reminderType} with state {getDiscriminatedUnionCaseName state}."
                                    this.correlationId
                            )
                }

        interface IExportable<RepositoryEvent> with
            /// Coordinates export logic for the Repository actor.
            member this.Export() =
                task {
                    try
                        if state.State.Count > 0 then
                            return Ok state.State
                        else
                            return Error ExportError.EventListIsEmpty
                    with
                    | ex -> return Error(ExportError.Exception(ExceptionResponse.Create ex))
                }

            /// Coordinates import logic for the Repository actor.
            member this.Import(events: IReadOnlyList<RepositoryEvent>) =
                task {
                    try
                        state.State.Clear()
                        state.State.AddRange(events)
                        do! state.WriteStateAsync()
                        return Ok events.Count
                    with
                    | ex -> return Error(ImportError.Exception(ExceptionResponse.Create ex))
                }

        interface IRevertable<RepositoryDto> with
            /// Coordinates revert back logic for the Repository actor.
            member this.RevertBack (eventsToRevert: int) (persist: PersistAction) =
                task {
                    try
                        let repositoryEvents = state.State

                        if repositoryEvents.Count > 0 then
                            let eventsToKeep = repositoryEvents.Count - eventsToRevert

                            if eventsToKeep <= 0 then
                                return Error RevertError.OutOfRange
                            else
                                let revertedEvents = repositoryEvents.Take eventsToKeep

                                let newRepositoryDto = revertedEvents.Aggregate(RepositoryDto.Default, (fun state evnt -> (RepositoryDto.UpdateDto evnt state)))

                                match persist with
                                | PersistAction.Save ->
                                    state.State.Clear()
                                    state.State.AddRange revertedEvents
                                    do! state.WriteStateAsync()
                                | DoNotSave -> ()

                                return Ok newRepositoryDto
                        else
                            return Error RevertError.EmptyEventList
                    with
                    | ex -> return Error(RevertError.Exception(ExceptionResponse.Create ex))
                }

            /// Coordinates revert to instant logic for the Repository actor.
            member this.RevertToInstant (whenToRevertTo: Instant) (persist: PersistAction) =
                task {
                    try
                        let repositoryEvents = state.State

                        if repositoryEvents.Count > 0 then
                            let revertedEvents = repositoryEvents.Where(fun evnt -> evnt.Metadata.Timestamp < whenToRevertTo)

                            if revertedEvents.Count() = 0 then
                                return Error RevertError.OutOfRange
                            else
                                let newRepositoryDto =
                                    revertedEvents
                                    |> Seq.fold (fun state evnt -> (RepositoryDto.UpdateDto evnt state)) RepositoryDto.Default

                                match persist with
                                | PersistAction.Save ->
                                    task {
                                        state.State.Clear()
                                        state.State.AddRange revertedEvents
                                        do! state.WriteStateAsync()
                                    }
                                    |> ignore
                                | DoNotSave -> ()

                                return Ok newRepositoryDto
                        else
                            return Error RevertError.EmptyEventList
                    with
                    | ex -> return Error(RevertError.Exception(ExceptionResponse.Create ex))
                }

            /// Coordinates event count logic for the Repository actor.
            member this.EventCount() = task { return state.State.Count }

        interface IRepositoryActor with
            /// Returns the current Repository actor state snapshot.
            member this.Get correlationId =
                this.correlationId <- correlationId
                repositoryDto |> returnTask

            /// Returns object storage provider data from the Repository actor state or related storage.
            member this.GetObjectStorageProvider correlationId =
                this.correlationId <- correlationId
                repositoryDto.ObjectStorageProvider |> returnTask

            /// Reports whether this Repository actor has persisted state.
            member this.Exists correlationId =
                this.correlationId <- correlationId
                repositoryDto.UpdatedAt.IsSome |> returnTask

            /// Reports whether the repository has any persisted domain events.
            member this.IsEmpty correlationId =
                this.correlationId <- correlationId
                repositoryDto.InitializedAt.IsNone |> returnTask

            /// Reports whether this Repository actor state is marked logically deleted.
            member this.IsDeleted correlationId =
                this.correlationId <- correlationId
                repositoryDto.DeletedAt.IsSome |> returnTask

            /// Routes a public actor command to the domain operation that validates and persists it.
            member this.Handle command metadata =
                /// Checks whether command validation succeeded before emitting the domain event.
                let isValid command (metadata: EventMetadata) =
                    task {
                        let matchingCreateRetry = createCommandMatchesPersistedCreation command

                        if state.State.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId)
                           && not matchingCreateRetry then
                            return Error(GraceError.Create (getErrorMessage RepositoryError.DuplicateCorrelationId) metadata.CorrelationId)
                        else
                            match command with
                            | RepositoryCommand.Create (_, _, _, _, _) ->
                                match repositoryDto.UpdatedAt with
                                | Some _ when matchingCreateRetry -> return Ok command
                                | Some _ -> return Error(GraceError.Create (getErrorMessage RepositoryError.RepositoryIdAlreadyExists) metadata.CorrelationId)
                                | None -> return Ok command
                            | _ ->
                                match repositoryDto.UpdatedAt with
                                | Some _ -> return Ok command
                                | None -> return Error(GraceError.Create (getErrorMessage RepositoryError.RepositoryIdDoesNotExist) metadata.CorrelationId)
                    }

                /// Runs Repository command decisions, applies emitted events, and persists the result.
                let processCommand command (metadata: EventMetadata) =
                    task {
                        try
                            let! event =
                                task {
                                    match command with
                                    | Create (repositoryName, repositoryId, ownerId, organizationId, objectStorageProvider) ->
                                        return Created(repositoryName, repositoryId, ownerId, organizationId, objectStorageProvider)
                                    | SetStoragePoolId storagePoolId when String.IsNullOrWhiteSpace storagePoolId ->
                                        return raise (ApplicationException("Repository StoragePoolId cannot be blank."))
                                    | SetStoragePoolId storagePoolId when
                                        storagePoolId
                                        <> StoragePoolId Constants.DefaultStoragePoolId
                                        ->
                                        return
                                            raise (
                                                ApplicationException(
                                                    $"StoragePoolId '{storagePoolId}' is not configured. Non-default StoragePool routing fails closed until configured pool loading exists."
                                                )
                                            )
                                    | SetStoragePoolId storagePoolId -> return StoragePoolIdSet storagePoolId
                                    | Initialize -> return Initialized
                                    | SetObjectStorageProvider objectStorageProvider -> return ObjectStorageProviderSet objectStorageProvider
                                    | SetStorageAccountName storageAccountName -> return StorageAccountNameSet storageAccountName
                                    | SetStorageContainerName containerName -> return StorageContainerNameSet containerName
                                    | SetRepositoryStatus repositoryStatus -> return RepositoryStatusSet repositoryStatus
                                    | SetRepositoryType repositoryType -> return RepositoryTypeSet repositoryType
                                    | SetAllowsLargeFiles allowsLargeFiles -> return AllowsLargeFilesSet allowsLargeFiles
                                    | SetAnonymousAccess anonymousAccess -> return AnonymousAccessSet anonymousAccess
                                    | SetRecordSaves recordSaves -> return RecordSavesSet recordSaves
                                    | SetDefaultServerApiVersion version -> return DefaultServerApiVersionSet version
                                    | SetDefaultBranchName defaultBranchName -> return DefaultBranchNameSet defaultBranchName
                                    | SetLogicalDeleteDays days -> return LogicalDeleteDaysSet days
                                    | SetSaveDays days -> return SaveDaysSet days
                                    | SetCheckpointDays days -> return CheckpointDaysSet days
                                    | SetDirectoryVersionCacheDays days -> return DirectoryVersionCacheDaysSet days
                                    | SetDiffCacheDays days -> return DiffCacheDaysSet days
                                    | SetName repositoryName -> return NameSet repositoryName
                                    | SetDescription description -> return DescriptionSet description
                                    | SetConflictResolutionPolicy policy -> return ConflictResolutionPolicySet policy
                                    | DeleteLogical (force, deleteReason) ->
                                        // Get the list of branches that aren't already deleted.
                                        let! branches =
                                            getBranches
                                                repositoryDto.OwnerId
                                                repositoryDto.OrganizationId
                                                repositoryDto.RepositoryId
                                                Int32.MaxValue
                                                false
                                                metadata.CorrelationId

                                        // If any branches are not already deleted, and we're not forcing the deletion, then throw an exception.
                                        if not <| force
                                           && branches.Length > 0
                                           && branches.Any(fun branch -> branch.DeletedAt |> Option.isNone) then
                                            return LogicalDeleted(force, deleteReason)
                                        else
                                            // We have --force specified, so delete the branches that aren't already deleted.
                                            match! this.LogicalDeleteBranches(branches, metadata, deleteReason) with
                                            | Ok _ ->
                                                let physicalDeletionReminderState = { DeleteReason = deleteReason; CorrelationId = metadata.CorrelationId }

                                                do!
                                                    (this :> IGraceReminderWithGuidKey)
                                                        .ScheduleReminderAsync
                                                        ReminderTypes.PhysicalDeletion
                                                        (Duration.FromDays(float repositoryDto.LogicalDeleteDays))
                                                        (ReminderState.RepositoryPhysicalDeletion physicalDeletionReminderState)
                                                        metadata.CorrelationId

                                                ()
                                            | Error error -> raise (ApplicationException($"{error}"))

                                            return LogicalDeleted(force, deleteReason)
                                    | DeletePhysical ->
                                        // Delete the state from storage, and deactivate the actor.
                                        do! state.ClearStateAsync()
                                        this.DeactivateActorOnIdle()
                                        return PhysicalDeleted
                                    | RepositoryCommand.Undelete -> return Undeleted
                                }

                            return! this.ApplyEvent { Event = event; Metadata = metadata }
                        with
                        | ex -> return Error(GraceError.Create $"{ExceptionResponse.Create ex}{Environment.NewLine}{metadata}" metadata.CorrelationId)
                    }

                task {
                    this.correlationId <- metadata.CorrelationId
                    RequestContext.Set(Constants.CurrentCommandProperty, getDiscriminatedUnionCaseName command)

                    match! isValid command metadata with
                    | Ok command -> return! processCommand command metadata
                    | Error error -> return Error error
                }
