namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open FSharp.Control
open FSharpPlus
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Actors.Commands.Repository
open Grace.Actors.Context
open Grace.Actors.Events.Repository
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Shared
open Grace.Shared.Combinators
open Grace.Shared.Constants
open Grace.Shared.Dto.Branch
open Grace.Shared.Dto.Repository
open Grace.Shared.Resources.Text
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors.Repository
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Linq
open System.Text
open System.Text.Json
open System.Threading.Tasks
open System.Runtime.Serialization
open Grace.Shared.Services
open Dapr.Client.Autogen.Grpc.v1

module Repository =

    /// The data types stored in physical deletion reminders.
    type PhysicalDeletionReminderState = (DeleteReason * CorrelationId)

    let log = loggerFactory.CreateLogger("Repository.Actor")

    type RepositoryActor(host: ActorHost) =
        inherit Actor(host)

        static let actorName = ActorName.Repository
        static let dtoStateName = StateName.RepositoryDto
        static let eventsStateName = StateName.Repository

        let mutable actorStartTime = Instant.MinValue
        let mutable logScope: IDisposable = null
        let mutable currentCommand = String.Empty
        let mutable stateManager = Unchecked.defaultof<IActorStateManager>

        /// Indicates that the actor is in an undefined state, and should be reset.
        let mutable isDisposed = false

        let mutable repositoryDto = RepositoryDto.Default
        let mutable repositoryEvents: List<RepositoryEvent> = null

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync() =
            let activateStartTime = getCurrentInstant ()
            stateManager <- this.StateManager

            task {
                let correlationId =
                    match memoryCache.GetCorrelationIdEntry this.Id with
                    | Some correlationId -> correlationId
                    | None -> String.Empty

                try
                    let! retrievedDto = Storage.RetrieveState<RepositoryDto> stateManager dtoStateName correlationId

                    match retrievedDto with
                    | Some retrievedDto -> repositoryDto <- retrievedDto
                    | None -> repositoryDto <- RepositoryDto.Default

                    logActorActivation log activateStartTime correlationId actorName host.Id (getActorActivationMessage retrievedDto)
                with ex ->
                    let exc = ExceptionResponse.Create ex
                    log.LogError("{CurrentInstant} Error activating {ActorType} {ActorId}.", getCurrentInstantExtended (), this.GetType().Name, host.Id)
                    log.LogError("{CurrentInstant} {ExceptionDetails}", getCurrentInstantExtended (), exc.ToString())
                    logActorActivation log activateStartTime correlationId actorName this.Id "Exception occurred during activation."
            }
            :> Task

        override this.OnPreActorMethodAsync(context) =
            actorStartTime <- getCurrentInstant ()
            this.correlationId <- String.Empty
            logScope <- log.BeginScope("Actor {actorName}", actorName)
            currentCommand <- String.Empty

            log.LogTrace(
                "{CurrentInstant}: Started {ActorName}.{MethodName}, RepositoryId: {Id}.",
                getCurrentInstantExtended (),
                actorName,
                context.MethodName,
                this.Id
            )

            // This checks if the actor is still active, but in an undefined state, which will _almost_ never happen.
            // isDisposed is set when the actor is deleted, or if an error occurs where we're not sure of the state and want to reload from the database.
            if isDisposed then
                this.OnActivateAsync().Wait()
                isDisposed <- false

            Task.CompletedTask

        override this.OnPostActorMethodAsync(context) =
            let duration_ms = getPaddedDuration_ms actorStartTime

            if
                String.IsNullOrEmpty(currentCommand)
                && not <| (context.MethodName = "ReceiveReminderAsync")
            then
                log.LogInformation(
                    "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {ActorName}.{MethodName}; RepositoryId: {Id}.",
                    getCurrentInstantExtended (),
                    getMachineName,
                    duration_ms,
                    this.correlationId,
                    actorName,
                    context.MethodName,
                    this.Id
                )
            else
                log.LogInformation(
                    "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {ActorName}.{MethodName}; Command: {Command}; RepositoryId: {Id}.",
                    getCurrentInstantExtended (),
                    getMachineName,
                    duration_ms,
                    this.correlationId,
                    actorName,
                    context.MethodName,
                    currentCommand,
                    this.Id
                )

            logScope.Dispose()
            Task.CompletedTask

        member private this.updateDto repositoryEvent currentRepositoryDto =
            let newRepositoryDto =
                match repositoryEvent.Event with
                | Created(name, repositoryId, ownerId, organizationId) ->
                    { RepositoryDto.Default with
                        RepositoryName = name
                        RepositoryId = repositoryId
                        OwnerId = ownerId
                        OrganizationId = organizationId
                        ObjectStorageProvider = Constants.DefaultObjectStorageProvider
                        StorageAccountName = Constants.DefaultObjectStorageAccount
                        StorageContainerName = $"{repositoryId}"
                        CreatedAt = repositoryEvent.Metadata.Timestamp }
                | Initialized -> { currentRepositoryDto with InitializedAt = Some(getCurrentInstant ()) }
                | ObjectStorageProviderSet objectStorageProvider -> { currentRepositoryDto with ObjectStorageProvider = objectStorageProvider }
                | StorageAccountNameSet storageAccountName -> { currentRepositoryDto with StorageAccountName = storageAccountName }
                | StorageContainerNameSet containerName -> { currentRepositoryDto with StorageContainerName = containerName }
                | RepositoryStatusSet repositoryStatus -> { currentRepositoryDto with RepositoryStatus = repositoryStatus }
                | RepositoryTypeSet repositoryType -> { currentRepositoryDto with RepositoryType = repositoryType }
                | RecordSavesSet recordSaves -> { currentRepositoryDto with RecordSaves = recordSaves }
                | DefaultServerApiVersionSet version -> { currentRepositoryDto with DefaultServerApiVersion = version }
                | DefaultBranchNameSet defaultBranchName -> { currentRepositoryDto with DefaultBranchName = defaultBranchName }
                | LogicalDeleteDaysSet days -> { currentRepositoryDto with LogicalDeleteDays = days }
                | SaveDaysSet days -> { currentRepositoryDto with SaveDays = days }
                | CheckpointDaysSet days -> { currentRepositoryDto with CheckpointDays = days }
                | DirectoryVersionCacheDaysSet days -> { currentRepositoryDto with DirectoryVersionCacheDays = days }
                | DiffCacheDaysSet days -> { currentRepositoryDto with DiffCacheDays = days }
                | NameSet repositoryName -> { currentRepositoryDto with RepositoryName = repositoryName }
                | DescriptionSet description -> { currentRepositoryDto with Description = description }
                | LogicalDeleted _ -> { currentRepositoryDto with DeletedAt = Some(getCurrentInstant ()) }
                | PhysicalDeleted -> currentRepositoryDto // Do nothing because it's about to be deleted anyway.
                | Undeleted -> { currentRepositoryDto with DeletedAt = None; DeleteReason = String.Empty }
                | AllowsLargeFilesSet allowsLargeFiles -> { currentRepositoryDto with AllowsLargeFiles = allowsLargeFiles }
                | AnonymousAccessSet anonymousAccess -> { currentRepositoryDto with AnonymousAccess = anonymousAccess }

            { newRepositoryDto with UpdatedAt = Some repositoryEvent.Metadata.Timestamp }

        // This is essentially an object-oriented implementation of the Lazy<T> pattern. I was having issues with Lazy<T>,
        //   and after a solid day wrestling with it, I dropped it and did this. Works a treat.
        member private this.RepositoryEvents() =
            task {
                if repositoryEvents = null then
                    let! retrievedEvents = Storage.RetrieveState<List<RepositoryEvent>> stateManager eventsStateName this.correlationId

                    repositoryEvents <-
                        match retrievedEvents with
                        | Some retrievedEvents -> retrievedEvents
                        | None -> List<RepositoryEvent>()

                return repositoryEvents
            }

        member private this.ApplyEvent(repositoryEvent) =
            task {
                try
                    let! repositoryEvents = this.RepositoryEvents()

                    repositoryEvents.Add(repositoryEvent)

                    do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(eventsStateName, repositoryEvents))

                    repositoryDto <- repositoryDto |> this.updateDto repositoryEvent

                    do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(dtoStateName, repositoryDto))

                    let processGraceError (repositoryError: RepositoryError) repositoryEvent previousGraceError =
                        Error(
                            GraceError.Create
                                $"{RepositoryError.getErrorMessage repositoryError}{Environment.NewLine}{previousGraceError.Error}"
                                repositoryEvent.Metadata.CorrelationId
                        )

                    // If we're creating a repository, we need to create the default branch, the initial promotion, and the initial directory.
                    //   Otherwise, just pass the event through.
                    let handleEvent =
                        task {
                            match repositoryEvent.Event with
                            | Created(name, repositoryId, ownerId, organizationId) ->
                                // Create the default branch.
                                let branchId = (Guid.NewGuid())
                                let branchActor = Branch.CreateActorProxy branchId this.correlationId

                                // Only allow promotions and tags on the initial branch.
                                let initialBranchPermissions = [| ReferenceType.Promotion; ReferenceType.Tag; ReferenceType.External |]

                                let createInitialBranchCommand =
                                    Commands.Branch.BranchCommand.Create(
                                        branchId,
                                        InitialBranchName,
                                        DefaultParentBranchId,
                                        ReferenceId.Empty,
                                        repositoryId,
                                        initialBranchPermissions
                                    )

                                match! branchActor.Handle createInitialBranchCommand repositoryEvent.Metadata with
                                | Ok branchGraceReturn ->
                                    logToConsole $"In Repository.Actor.handleEvent: Successfully created the new branch."
                                    // Create an empty directory version, and use that for the initial promotion
                                    let emptyDirectoryId = DirectoryVersionId.NewGuid()

                                    let emptySha256Hash = computeSha256ForDirectory RootDirectoryPath (List<LocalDirectoryVersion>()) (List<LocalFileVersion>())

                                    let directoryVersionActorProxy = DirectoryVersion.CreateActorProxy emptyDirectoryId this.correlationId

                                    let emptyDirectoryVersion =
                                        DirectoryVersion.Create
                                            emptyDirectoryId
                                            repositoryDto.RepositoryId
                                            RootDirectoryPath
                                            emptySha256Hash
                                            (List<DirectoryVersionId>())
                                            (List<FileVersion>())
                                            0L

                                    let! directoryResult =
                                        directoryVersionActorProxy.Handle
                                            (Commands.DirectoryVersion.DirectoryVersionCommand.Create(emptyDirectoryVersion))
                                            repositoryEvent.Metadata

                                    logToConsole $"In Repository.Actor.handleEvent: Successfully created the empty directory version."

                                    let! promotionResult =
                                        branchActor.Handle
                                            (Commands.Branch.BranchCommand.Promote(
                                                emptyDirectoryId,
                                                emptySha256Hash,
                                                (getLocalizedString StringResourceName.InitialPromotionMessage)
                                            ))
                                            repositoryEvent.Metadata

                                    logToConsole $"In Repository.Actor.handleEvent: After trying to create the first promotion."

                                    match directoryResult, promotionResult with
                                    | (Ok directoryVersionGraceReturnValue, Ok promotionGraceReturnValue) ->
                                        logToConsole $"In Repository.Actor.handleEvent: Successfully created the initial promotion."
                                        logToConsole $"promotionGraceReturnValue.Properties:"

                                        promotionGraceReturnValue.Properties
                                        |> Seq.iter (fun kv -> logToConsole $"  {kv.Key}: {kv.Value}")
                                        // Set current, empty directory as the based-on reference.
                                        let referenceId = Guid.Parse(promotionGraceReturnValue.Properties[nameof (ReferenceId)])

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
                    | Ok(branchId, referenceId) ->
                        // Publish the event to the rest of the world.
                        let graceEvent = Events.GraceEvent.RepositoryEvent repositoryEvent
                        let message = serialize graceEvent
                        do! daprClient.PublishEventAsync(GracePubSubService, GraceEventStreamTopic, graceEvent)

                        let returnValue = GraceReturnValue.Create $"Repository command succeeded." repositoryEvent.Metadata.CorrelationId

                        returnValue
                            .enhance(nameof (OwnerId), $"{repositoryDto.OwnerId}")
                            .enhance(nameof (OrganizationId), $"{repositoryDto.OrganizationId}")
                            .enhance(nameof (RepositoryId), $"{repositoryDto.RepositoryId}")
                            .enhance(nameof (RepositoryName), $"{repositoryDto.RepositoryName}")
                            .enhance (nameof (RepositoryEventType), $"{getDiscriminatedUnionFullName repositoryEvent.Event}")
                        |> ignore

                        if branchId <> BranchId.Empty then
                            returnValue
                                .enhance(nameof (BranchId), $"{branchId}")
                                .enhance(nameof (BranchName), $"{Constants.InitialBranchName}")
                                .enhance (nameof (ReferenceId), $"{referenceId}")
                            |> ignore

                        returnValue.Properties.Add("EventType", $"{getDiscriminatedUnionFullName repositoryEvent.Event}")

                        return Ok returnValue
                    | Error graceError -> return Error graceError
                with ex ->
                    let exceptionResponse = ExceptionResponse.Create ex

                    let graceError =
                        GraceError.Create (RepositoryError.getErrorMessage RepositoryError.FailedWhileApplyingEvent) repositoryEvent.Metadata.CorrelationId

                    graceError
                        .enhance("Exception details", exceptionResponse.``exception`` + exceptionResponse.innerException)
                        .enhance(nameof (OwnerId), $"{repositoryDto.OwnerId}")
                        .enhance(nameof (OrganizationId), $"{repositoryDto.OrganizationId}")
                        .enhance(nameof (RepositoryId), $"{repositoryDto.RepositoryId}")
                        .enhance(nameof (RepositoryName), $"{repositoryDto.RepositoryName}")
                        .enhance (nameof (RepositoryEventType), $"{getDiscriminatedUnionFullName repositoryEvent.Event}")
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
                                        let branchActor = Branch.CreateActorProxy branch.BranchId this.correlationId

                                        let! result =
                                            branchActor.Handle
                                                (Commands.Branch.DeleteLogical(
                                                    true,
                                                    $"Cascaded from deleting repository. ownerId: {repositoryDto.OwnerId}; organizationId: {repositoryDto.OrganizationId}; repositoryId: {repositoryDto.RepositoryId}; repositoryName: {repositoryDto.RepositoryName}; deleteReason: {deleteReason}"
                                                ))
                                                metadata

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

        interface IGraceReminder with
            /// Schedules a Grace reminder.
            member this.ScheduleReminderAsync reminderType delay state correlationId =
                task {
                    let reminder = ReminderDto.Create actorName $"{this.Id}" reminderType (getFutureInstant delay) state correlationId
                    do! createReminder reminder
                }
                :> Task

            /// Receives a Grace reminder.
            member this.ReceiveReminderAsync(reminder: ReminderDto) : Task<Result<unit, GraceError>> =
                task {
                    match reminder.ReminderType with
                    | ReminderTypes.PhysicalDeletion ->
                        // Get values from state.
                        let (deleteReason, correlationId) = deserialize<PhysicalDeletionReminderState> reminder.State
                        this.correlationId <- correlationId

                        // Physically delete the actor state.
                        let! deletedDtoState = stateManager.TryRemoveStateAsync(dtoStateName)
                        let! deletedEventsState = stateManager.TryRemoveStateAsync(eventsStateName)

                        // Mark the actor as disposed, in case someone tries to use it before Dapr GC's it.
                        isDisposed <- true

                        log.LogInformation(
                            "{CurrentInstant}: CorrelationId: {correlationId}; Deleted physical state for repository; RepositoryId: {}; RepositoryName: {}; OrganizationId: {organizationId}; OwnerId: {ownerId}; deleteReason: {deleteReason}.",
                            getCurrentInstantExtended (),
                            correlationId,
                            repositoryDto.RepositoryId,
                            repositoryDto.RepositoryName,
                            repositoryDto.OrganizationId,
                            repositoryDto.OwnerId,
                            deleteReason
                        )

                        // Set all values to default.
                        repositoryDto <- RepositoryDto.Default
                        return Ok()
                    | _ ->
                        return
                            Error(
                                GraceError.Create
                                    $"{actorName} does not process reminder type {getDiscriminatedUnionCaseName reminder.ReminderType}."
                                    this.correlationId
                            )
                }

        interface IExportable<RepositoryEvent> with
            member this.Export() =
                task {
                    try
                        let! repositoryEvents = this.RepositoryEvents()

                        if repositoryEvents.Count > 0 then
                            return Ok repositoryEvents
                        else
                            return Error ExportError.EventListIsEmpty
                    with ex ->
                        return Error(ExportError.Exception(ExceptionResponse.Create ex))
                }

            member this.Import(events: IReadOnlyList<RepositoryEvent>) =
                task {
                    try
                        let! repositoryEvents = this.RepositoryEvents()
                        repositoryEvents.Clear()
                        repositoryEvents.AddRange(events)

                        let newRepositoryDto = repositoryEvents.Aggregate(RepositoryDto.Default, (fun state evnt -> (this.updateDto evnt state)))

                        do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(eventsStateName, this.RepositoryEvents))

                        do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(dtoStateName, newRepositoryDto))

                        return Ok repositoryEvents.Count
                    with ex ->
                        return Error(ImportError.Exception(ExceptionResponse.Create ex))
                }

        interface IRevertable<RepositoryDto> with
            member this.RevertBack (eventsToRevert: int) (persist: PersistAction) =
                task {
                    try
                        let! repositoryEvents = this.RepositoryEvents()

                        if repositoryEvents.Count > 0 then
                            let eventsToKeep = repositoryEvents.Count - eventsToRevert

                            if eventsToKeep <= 0 then
                                return Error RevertError.OutOfRange
                            else
                                let revertedEvents = repositoryEvents.Take(eventsToKeep)

                                let newRepositoryDto = revertedEvents.Aggregate(RepositoryDto.Default, (fun state evnt -> (this.updateDto evnt state)))

                                match persist with
                                | PersistAction.Save ->
                                    repositoryEvents.Clear()
                                    repositoryEvents.AddRange(revertedEvents)

                                    do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(eventsStateName, revertedEvents))

                                    do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(dtoStateName, newRepositoryDto))
                                | DoNotSave -> ()

                                return Ok newRepositoryDto
                        else
                            return Error RevertError.EmptyEventList
                    with ex ->
                        return Error(RevertError.Exception(ExceptionResponse.Create ex))
                }

            member this.RevertToInstant (whenToRevertTo: Instant) (persist: PersistAction) =
                task {
                    try
                        let! repositoryEvents = this.RepositoryEvents()

                        if repositoryEvents.Count > 0 then
                            let revertedEvents = repositoryEvents.Where(fun evnt -> evnt.Metadata.Timestamp < whenToRevertTo)

                            if revertedEvents.Count() = 0 then
                                return Error RevertError.OutOfRange
                            else
                                let newRepositoryDto =
                                    revertedEvents
                                    |> Seq.fold (fun state evnt -> (this.updateDto evnt state)) RepositoryDto.Default

                                match persist with
                                | PersistAction.Save ->
                                    task {
                                        repositoryEvents.Clear()
                                        repositoryEvents.AddRange(revertedEvents)

                                        do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(eventsStateName, revertedEvents))

                                        do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(dtoStateName, newRepositoryDto))
                                    }
                                    |> ignore
                                | DoNotSave -> ()

                                return Ok newRepositoryDto
                        else
                            return Error RevertError.EmptyEventList
                    with ex ->
                        return Error(RevertError.Exception(ExceptionResponse.Create ex))
                }

            member this.EventCount() =
                task {
                    let! repositoryEvents = this.RepositoryEvents()
                    return repositoryEvents.Count
                }

        interface IRepositoryActor with
            member this.Get correlationId =
                this.correlationId <- correlationId
                repositoryDto |> returnTask

            member this.GetObjectStorageProvider correlationId =
                this.correlationId <- correlationId
                repositoryDto.ObjectStorageProvider |> returnTask

            member this.Exists correlationId =
                this.correlationId <- correlationId
                repositoryDto.UpdatedAt.IsSome |> returnTask

            member this.IsEmpty correlationId =
                this.correlationId <- correlationId
                repositoryDto.InitializedAt.IsNone |> returnTask

            member this.IsDeleted correlationId =
                this.correlationId <- correlationId
                repositoryDto.DeletedAt.IsSome |> returnTask

            member this.Handle command metadata =
                let isValid command (metadata: EventMetadata) =
                    task {
                        let! repositoryEvents = this.RepositoryEvents()

                        if repositoryEvents.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId) then
                            return Error(GraceError.Create (RepositoryError.getErrorMessage DuplicateCorrelationId) metadata.CorrelationId)
                        else
                            match command with
                            | RepositoryCommand.Create(_, _, _, _) ->
                                match repositoryDto.UpdatedAt with
                                | Some _ -> return Error(GraceError.Create (RepositoryError.getErrorMessage RepositoryIdAlreadyExists) metadata.CorrelationId)
                                | None -> return Ok command
                            | _ ->
                                match repositoryDto.UpdatedAt with
                                | Some _ -> return Ok command
                                | None -> return Error(GraceError.Create (RepositoryError.getErrorMessage RepositoryIdDoesNotExist) metadata.CorrelationId)
                    }

                let processCommand command (metadata: EventMetadata) =
                    task {
                        try
                            let! event =
                                task {
                                    match command with
                                    | Create(repositoryName, repositoryId, ownerId, organizationId) ->
                                        return Created(repositoryName, repositoryId, ownerId, organizationId)
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
                                    | DeleteLogical(force, deleteReason) ->
                                        // Get the list of branches that aren't already deleted.
                                        let! branches = getBranches repositoryDto.RepositoryId Int32.MaxValue false metadata.CorrelationId

                                        // If any branches are not already deleted, and we're not forcing the deletion, then throw an exception.
                                        if
                                            not <| force
                                            && branches.Length > 0
                                            && branches.Any(fun branch -> branch.DeletedAt |> Option.isNone)
                                        then
                                            return LogicalDeleted(force, deleteReason)
                                        else
                                            // We have --force specified, so delete the branches that aren't already deleted.
                                            match! this.LogicalDeleteBranches(branches, metadata, deleteReason) with
                                            | Ok _ ->
                                                let (reminderState: PhysicalDeletionReminderState) = (deleteReason, metadata.CorrelationId)

                                                do!
                                                    (this :> IGraceReminder).ScheduleReminderAsync
                                                        ReminderTypes.PhysicalDeletion
                                                        (Duration.FromDays(float repositoryDto.LogicalDeleteDays))
                                                        (serialize reminderState)
                                                        metadata.CorrelationId

                                                ()
                                            | Error error -> raise (ApplicationException($"{error}"))

                                            return LogicalDeleted(force, deleteReason)
                                    | DeletePhysical ->
                                        isDisposed <- true
                                        return PhysicalDeleted
                                    | RepositoryCommand.Undelete -> return Undeleted
                                }

                            return! this.ApplyEvent { Event = event; Metadata = metadata }
                        with ex ->
                            return Error(GraceError.Create $"{ExceptionResponse.Create ex}{Environment.NewLine}{metadata}" metadata.CorrelationId)
                    }

                task {
                    this.correlationId <- metadata.CorrelationId
                    currentCommand <- getDiscriminatedUnionCaseName command

                    match! isValid command metadata with
                    | Ok command -> return! processCommand command metadata
                    | Error error -> return Error error
                }
