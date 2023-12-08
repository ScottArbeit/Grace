namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open FSharpPlus
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Actors.Commands.Repository
open Grace.Actors.Events.Repository
open Grace.Actors.Services
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
open System.Collections.Generic
open System.Linq
open System.Text.Json
open System.Threading.Tasks
open System.Runtime.Serialization
open Grace.Shared.Services
open System.Text
open FSharp.Control
open System.Collections.Concurrent

module Repository =
        
    let GetActorId (repositoryId: RepositoryId) = ActorId($"{repositoryId}")

    type RepositoryActor(host: ActorHost) =
        inherit Actor(host)

        let actorName = ActorName.Repository
        let log = loggerFactory.CreateLogger("Repository.Actor")
        let mutable actorStartTime = Instant.MinValue
        let mutable logScope: IDisposable = null
        let mutable currentCommand = String.Empty

        let dtoStateName = "repositoryDtoState"
        let eventsStateName = "repositoryEventsState"

        /// Indicates that the actor is in an undefined state, and should be reset.
        let mutable isDisposed = false

        let mutable repositoryDto = RepositoryDto.Default
        let mutable repositoryEvents: List<RepositoryEvent> = null

        override this.OnActivateAsync() =
            let activateStartTime = getCurrentInstant()
            let stateManager = this.StateManager
            task {
                let! retrievedDto = Storage.RetrieveState<RepositoryDto> stateManager dtoStateName
                match retrievedDto with
                    | Some retrievedDto -> repositoryDto <- retrievedDto
                    | None -> repositoryDto <- RepositoryDto.Default

                let duration = getCurrentInstant().Minus(activateStartTime)
                log.LogInformation("{CurrentInstant}: Activated {ActorType} {ActorId}. Retrieved from storage in {duration}ms.", getCurrentInstantExtended(), actorName, host.Id, duration.TotalMilliseconds.ToString("F3"))
            } :> Task

        override this.OnPreActorMethodAsync(context) =
            actorStartTime <- getCurrentInstant()
            logScope <- log.BeginScope("Actor {actorName}", actorName)
            currentCommand <- String.Empty
            log.LogTrace("{CurrentInstant}: Started {ActorName}.{MethodName}, Id: {Id}.", getCurrentInstantExtended(), actorName, context.MethodName, this.Id)

            // This checks if the actor is still active, but in an undefined state, which will _almost_ never happen.
            // isDisposed is set when the actor is deleted, or if an error occurs where we're not sure of the state and want to reload from the database.
            if isDisposed then
                this.OnActivateAsync().Wait()
                isDisposed <- false

            Task.CompletedTask
            
        override this.OnPostActorMethodAsync(context) =
            let duration_ms = (getCurrentInstant().Minus(actorStartTime).TotalMilliseconds).ToString("F3")
            if String.IsNullOrEmpty(currentCommand) then
                log.LogInformation("{CurrentInstant}: Finished {ActorName}.{MethodName}; Id: {Id}; Duration: {duration}ms.", getCurrentInstantExtended(), actorName, context.MethodName, this.Id, duration_ms)
            else
                log.LogInformation("{CurrentInstant}: Finished {ActorName}.{MethodName}; Command: {Command}; Id: {Id}; Duration: {duration}ms.", getCurrentInstantExtended(), actorName, context.MethodName, currentCommand, this.Id, duration_ms)
            logScope.Dispose()
            Task.CompletedTask

        member private this.SetMaintenanceReminder() =
            this.RegisterReminderAsync(ReminderType.Maintenance, Array.empty<byte>, TimeSpan.FromDays(7.0), TimeSpan.FromDays(7.0))

        member private this.UnregisterMaintenanceReminder() =
            this.UnregisterReminderAsync(ReminderType.Maintenance)

        member private this.OnFirstWrite() =
            task {
                //let! _ = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> this.SetMaintenanceReminder())
                ()
            }

        member private this.updateDto repositoryEvent currentRepositoryDto =
            let newRepositoryDto = 
                match repositoryEvent.Event with
                | Created (name, repositoryId, ownerId, organizationId) -> 
                    {RepositoryDto.Default with RepositoryName = name; 
                                                RepositoryId = repositoryId; 
                                                OwnerId = ownerId; 
                                                OrganizationId = organizationId; 
                                                ObjectStorageProvider = Constants.DefaultObjectStorageProvider;
                                                StorageAccountName = Constants.DefaultObjectStorageAccount;
                                                StorageContainerName = StorageContainerName Constants.DefaultObjectStorageContainerName}
                | Initialized -> {currentRepositoryDto with InitializedAt = Some (getCurrentInstant())}
                | ObjectStorageProviderSet objectStorageProvider -> {currentRepositoryDto with ObjectStorageProvider = objectStorageProvider}
                | StorageAccountNameSet storageAccountName ->  {currentRepositoryDto with StorageAccountName = storageAccountName}
                | StorageContainerNameSet containerName -> {currentRepositoryDto with StorageContainerName = containerName}
                | RepositoryStatusSet repositoryStatus -> {currentRepositoryDto with RepositoryStatus = repositoryStatus}
                | RepositoryVisibilitySet repositoryVisibility -> {currentRepositoryDto with RepositoryVisibility = repositoryVisibility}
                | RecordSavesSet recordSaves -> {currentRepositoryDto with RecordSaves = recordSaves}
                | DefaultServerApiVersionSet version -> {currentRepositoryDto with DefaultServerApiVersion = version}
                | DefaultBranchNameSet defaultBranchName -> {currentRepositoryDto with DefaultBranchName = defaultBranchName}
                | SaveDaysSet days -> {currentRepositoryDto with SaveDays = days}
                | CheckpointDaysSet days -> {currentRepositoryDto with CheckpointDays = days}
                | EnabledSingleStepPromotion enabled -> {currentRepositoryDto with EnabledSingleStepPromotion = enabled}
                | EnabledComplexPromotion enabled -> {currentRepositoryDto with EnabledComplexPromotion = enabled}
                | NameSet repositoryName -> {currentRepositoryDto with RepositoryName = repositoryName}
                | DescriptionSet description -> {currentRepositoryDto with Description = description}
                | LogicalDeleted _ -> {currentRepositoryDto with DeletedAt = Some (getCurrentInstant())}
                | PhysicalDeleted -> currentRepositoryDto // Do nothing because it's about to be deleted anyway.
                | Undeleted -> {currentRepositoryDto with DeletedAt = None; DeleteReason = String.Empty}

            {newRepositoryDto with UpdatedAt = Some (getCurrentInstant())}

        // This is essentially an object-oriented implementation of the Lazy<T> pattern. I was having issues with Lazy<T>, 
        //   and after a solid day wrestling with it, I dropped it and did this. Works a treat.
        member private this.RepositoryEvents() =
            let stateManager = this.StateManager
            task {
                if repositoryEvents = null then            
                    let! retrievedEvents = Storage.RetrieveState<List<RepositoryEvent>> stateManager eventsStateName
                    repositoryEvents <- match retrievedEvents with
                                           | Some retrievedEvents -> retrievedEvents;
                                           | None -> List<RepositoryEvent>()
            
                return repositoryEvents
            }

        member private this.ApplyEvent(repositoryEvent) =
            let stateManager = this.StateManager
            task {
                try
                    let! repositoryEvents = this.RepositoryEvents()
                    if repositoryEvents.Count = 0 then
                        do! this.OnFirstWrite()

                    repositoryEvents.Add(repositoryEvent)
                    do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(eventsStateName, repositoryEvents))
                    
                    repositoryDto <- repositoryDto |> this.updateDto repositoryEvent
                    do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(dtoStateName, repositoryDto))

                    let processGraceError (repositoryError: RepositoryError) repositoryEvent previousGraceError = 
                        Error (GraceError.Create $"{RepositoryError.getErrorMessage repositoryError}{Environment.NewLine}{previousGraceError.Error}" repositoryEvent.Metadata.CorrelationId)

                    // If we're creating a repository, we need to create the default branch, the initial promotion, and the initial directory.
                    //   Otherwise, just pass the event through.
                    let handleEvent = 
                        task {
                            match repositoryEvent.Event with
                            | Created (name, repositoryId, ownerId, organizationId) -> 
                                // Create the default branch.
                                let branchId = (Guid.NewGuid())
                                let branchActorId = ActorId($"{branchId}")
                                let branchActor = Services.actorProxyFactory.CreateActorProxy<IBranchActor>(branchActorId, ActorName.Branch)

                                // Only allow promotions and tags on the initial branch.
                                let initialBranchPermissions = [|ReferenceType.Promotion; ReferenceType.Tag|]
                                let createCommand = Commands.Branch.BranchCommand.Create (branchId, (BranchName Constants.InitialBranchName), Constants.DefaultParentBranchId, ReferenceId.Empty, repositoryId, initialBranchPermissions)
                                let! result = branchActor.Handle createCommand repositoryEvent.Metadata
                                match result with
                                | Ok branchGraceReturn -> 
                                    // Create an initial promotion with completely empty contents
                                    let emptyDirectoryId = DirectoryId.NewGuid()
                                    let emptySha256Hash = computeSha256ForDirectory "/" (List<LocalDirectoryVersion>()) (List<LocalFileVersion>())
                                    let! promotionResult = branchActor.Handle (Commands.Branch.BranchCommand.Promote(emptyDirectoryId, emptySha256Hash, (getLocalizedString StringResourceName.InitialPromotionMessage))) repositoryEvent.Metadata
                                    match promotionResult with
                                    | Ok promotionGraceReturn ->
                                        // Set current, empty directory as the based-on reference.
                                        let referenceId = Guid.Parse(promotionGraceReturn.Properties[nameof(ReferenceId)])
                                        let! rebaseResult = branchActor.Handle (Commands.Branch.BranchCommand.Rebase(referenceId)) repositoryEvent.Metadata
                                        match rebaseResult with
                                        | Ok rebaseGraceReturn -> 
                                            return Ok (branchId, referenceId)
                                        | Error graceError -> 
                                            return processGraceError FailedRebasingInitialBranch repositoryEvent graceError
                                    | Error graceError ->
                                        return processGraceError FailedCreatingInitialPromotion repositoryEvent graceError
                                | Error graceError ->
                                    return processGraceError FailedCreatingInitialBranch repositoryEvent graceError
                            | _ -> return Ok (BranchId.Empty, ReferenceId.Empty)
                        }

                    match! handleEvent with
                    | Ok (branchId, referenceId) ->                   
                        // Publish the event to the rest of the world.
                        let graceEvent = Events.GraceEvent.RepositoryEvent repositoryEvent
                        let message = graceEvent |> serialize
                        do! daprClient.PublishEventAsync(GracePubSubService, GraceEventStreamTopic, message)

                        let returnValue = GraceReturnValue.Create $"Repository command succeeded." repositoryEvent.Metadata.CorrelationId
                        returnValue.Properties.Add(nameof(OwnerId), $"{repositoryDto.OwnerId}")
                        returnValue.Properties.Add(nameof(OrganizationId), $"{repositoryDto.OrganizationId}")
                        returnValue.Properties.Add(nameof(RepositoryId), $"{repositoryDto.RepositoryId}")
                        returnValue.Properties.Add(nameof(RepositoryName), $"{repositoryDto.RepositoryName}")
                        if branchId <> BranchId.Empty then
                            returnValue.Properties.Add(nameof(BranchId), $"{branchId}")
                            returnValue.Properties.Add(nameof(BranchName), $"{Constants.InitialBranchName}")
                            returnValue.Properties.Add(nameof(ReferenceId), $"{referenceId}")
                        returnValue.Properties.Add("EventType", $"{getDiscriminatedUnionFullName repositoryEvent.Event}")
                        return Ok returnValue
                    | Error graceError ->
                        return Error graceError
                with ex -> 
                    let graceError = GraceError.Create (RepositoryError.getErrorMessage RepositoryError.FailedWhileApplyingEvent) repositoryEvent.Metadata.CorrelationId
                    return Error graceError
            }

        /// Deletes all of the branches provided, by sending a DeleteLogical command to each branch.
        member private this.LogicalDeleteBranches (branches: List<BranchDto>, metadata: EventMetadata, deleteReason: string) =
            task {
                let results = ConcurrentQueue<GraceResult<string>>()

                // Loop through each branch and send a DeleteLogical command to it.
                do! Parallel.ForEachAsync(branches, Constants.ParallelOptions, (fun branch ct ->
                    ValueTask(task {
                        if branch.DeletedAt |> Option.isNone then
                            let branchActor = this.ProxyFactory.CreateActorProxy<IBranchActor> (ActorId($"{branch.BranchId}"), Constants.ActorName.Branch)
                            let! result = branchActor.Handle (Commands.Branch.DeleteLogical (true, $"Cascaded from deleting repository. ownerId: {repositoryDto.OwnerId}; organizationId: {repositoryDto.OrganizationId}; repositoryId: {repositoryDto.RepositoryId}; repositoryName: {repositoryDto.RepositoryName}; deleteReason: {deleteReason}")) metadata
                            results.Enqueue(result)
                    })))

                // Check if any of the results were errors, and take the first one if so.
                let overallResult = results |> Seq.tryPick (fun result -> match result with | Ok _ -> None | Error error -> Some(error))

                match overallResult with
                | None -> return Ok ()
                | Some error -> return Error error
            }

        /// Schedule an actor reminder to delete the repository from the database.
        member private this.SchedulePhysicalDeletion(deleteReason) =
            this.RegisterReminderAsync(ReminderType.PhysicalDeletion, convertToByteArray deleteReason, Constants.DefaultPhysicalDeletionReminderTime, TimeSpan.FromMilliseconds(-1)).Result |> ignore

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
                        return Error (ExportError.Exception (createExceptionResponse ex))
                }

            member this.Import(events: IList<RepositoryEvent>) = 
                let stateManager = this.StateManager
                task {
                    try
                        let! repositoryEvents = this.RepositoryEvents()
                        repositoryEvents.Clear()
                        repositoryEvents.AddRange(events)
                        let newRepositoryDto = repositoryEvents.Aggregate(RepositoryDto.Default,
                            (fun state evnt -> (this.updateDto evnt state)))
                        do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(eventsStateName, this.RepositoryEvents))
                        do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(dtoStateName, newRepositoryDto))
                        return Ok repositoryEvents.Count
                    with ex -> 
                        return Error (ImportError.Exception (createExceptionResponse ex))
                }

        interface IRevertable<RepositoryDto> with
            member this.RevertBack(eventsToRevert: int) (persist: PersistAction) =
                let stateManager = this.StateManager
                task {
                    try
                        let! repositoryEvents = this.RepositoryEvents()
                        if repositoryEvents.Count > 0 then
                            let eventsToKeep = repositoryEvents.Count - eventsToRevert
                            if eventsToKeep <= 0 then  
                                return Error RevertError.OutOfRange
                            else
                                let revertedEvents = repositoryEvents.Take(eventsToKeep)
                                let newRepositoryDto = revertedEvents.Aggregate(RepositoryDto.Default,
                                    (fun state evnt -> (this.updateDto evnt state)))
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
                        return Error (RevertError.Exception (createExceptionResponse ex))
                }

            member this.RevertToInstant(whenToRevertTo: Instant) (persist: PersistAction) = 
                let stateManager = this.StateManager
                task {
                    try
                        let! repositoryEvents = this.RepositoryEvents()
                        if repositoryEvents.Count > 0 then
                            let revertedEvents = repositoryEvents.Where(fun evnt -> evnt.Metadata.Timestamp < whenToRevertTo)
                            if revertedEvents.Count() = 0 then  
                                return Error RevertError.OutOfRange
                            else
                                let newRepositoryDto = revertedEvents |> Seq.fold (fun state evnt -> 
                                    (this.updateDto evnt state)) RepositoryDto.Default
                                match persist with
                                | PersistAction.Save -> 
                                    task {
                                        repositoryEvents.Clear()
                                        repositoryEvents.AddRange(revertedEvents)
                                        do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(eventsStateName, revertedEvents))
                                        do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(dtoStateName, newRepositoryDto))
                                    } |> ignore
                                | DoNotSave -> ()
                                return Ok newRepositoryDto
                        else
                            return Error RevertError.EmptyEventList
                    with ex -> 
                        return Error (RevertError.Exception (createExceptionResponse ex))
                }

            member this.EventCount() = 
                task {
                    let! repositoryEvents = this.RepositoryEvents()
                    return repositoryEvents.Count
                }

        interface IRepositoryActor with
            member this.Get() = repositoryDto |> returnTask

            member this.GetObjectStorageProvider() = repositoryDto.ObjectStorageProvider |> returnTask

            member this.Exists() = repositoryDto.UpdatedAt.IsSome |> returnTask

            member this.IsEmpty() = repositoryDto.InitializedAt.IsNone |> returnTask

            member this.IsDeleted() = repositoryDto.DeletedAt.IsSome |> returnTask

            member this.Handle command metadata =
                let isValid command (metadata: EventMetadata) =
                    task {
                        let! repositoryEvents = this.RepositoryEvents()
                        if repositoryEvents.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId) then
                            return Error (GraceError.Create (RepositoryError.getErrorMessage DuplicateCorrelationId) metadata.CorrelationId)
                        else
                            match command with 
                                | RepositoryCommand.Create (_, _, _, _) ->
                                    match repositoryDto.UpdatedAt with
                                    | Some _ -> return Error (GraceError.Create (RepositoryError.getErrorMessage RepositoryIdAlreadyExists) metadata.CorrelationId)
                                    | None -> return Ok command
                                | _ -> 
                                    match repositoryDto.UpdatedAt with
                                    | Some _ -> return Ok command
                                    | None -> return Error (GraceError.Create (RepositoryError.getErrorMessage RepositoryIdDoesNotExist) metadata.CorrelationId)
                    }

                let processCommand command metadata =
                    task {
                        try
                            let! event =
                                task {
                                    match command with
                                    | Create (repositoryName, repositoryId, ownerId, organizationId) -> return Created (repositoryName, repositoryId, ownerId, organizationId)
                                    | Initialize -> return Initialized
                                    | SetObjectStorageProvider objectStorageProvider -> return ObjectStorageProviderSet objectStorageProvider
                                    | SetStorageAccountName storageAccountName -> return StorageAccountNameSet storageAccountName
                                    | SetStorageContainerName containerName -> return StorageContainerNameSet containerName
                                    | SetRepositoryStatus repositoryStatus -> return RepositoryStatusSet repositoryStatus
                                    | SetVisibility repositoryVisibility -> return RepositoryVisibilitySet repositoryVisibility
                                    | SetRecordSaves recordSaves -> return RecordSavesSet recordSaves
                                    | SetDefaultServerApiVersion version -> return DefaultServerApiVersionSet version
                                    | SetDefaultBranchName defaultBranchName -> return DefaultBranchNameSet defaultBranchName
                                    | SetSaveDays days -> return SaveDaysSet days
                                    | SetCheckpointDays days -> return CheckpointDaysSet days
                                    | EnableSingleStepPromotion enabled -> return EnabledSingleStepPromotion enabled
                                    | EnableComplexPromotion enabled -> return EnabledComplexPromotion enabled
                                    | SetName repositoryName -> return NameSet repositoryName
                                    | SetDescription description -> return DescriptionSet description
                                    | DeleteLogical (force, deleteReason) ->
                                        // Get the list of branches that aren't already deleted.
                                        let! branches = getBranches repositoryDto.RepositoryId Int32.MaxValue false

                                        this.SchedulePhysicalDeletion(deleteReason)

                                        // If any branches are not already deleted, and we're not forcing the deletion, then throw an exception.
                                        if not <| force && branches.Count > 0 && branches.Any(fun branch -> branch.DeletedAt |> Option.isNone) then
                                            //raise (ApplicationException($"{error}"))
                                            return LogicalDeleted (force, deleteReason)
                                        else
                                            // We have --force specified, so delete the branches that aren't already deleted.
                                            match! this.LogicalDeleteBranches(branches, metadata, deleteReason) with
                                            | Ok _ -> this.SchedulePhysicalDeletion(deleteReason)
                                            | Error error -> raise (ApplicationException($"{error}"))
                                            return LogicalDeleted (force, deleteReason)
                                    | DeletePhysical ->
                                        isDisposed <- true
                                        return PhysicalDeleted
                                    | RepositoryCommand.Undelete -> return Undeleted
                                }
                            
                            return! this.ApplyEvent {Event = event; Metadata = metadata}
                        with ex ->
                            return Error (GraceError.Create $"{createExceptionResponse ex}{Environment.NewLine}{metadata}" metadata.CorrelationId)
                    }

                task {
                    currentCommand <- getDistributedUnionCaseName command
                    match! isValid command metadata with
                    | Ok command -> return! processCommand command metadata
                    | Error error -> return Error error
                }

        interface IRemindable with
            override this.ReceiveReminderAsync(reminderName, state, dueTime, period) =
                let stateManager = this.StateManager
                match reminderName with
                | ReminderType.Maintenance ->
                    task {
                        // Do some maintenance
                        ()
                    } :> Task
                | ReminderType.PhysicalDeletion ->
                    task {
                        // Physically delete the actor state.
                        let! deletedDtoState = stateManager.TryRemoveStateAsync(dtoStateName)
                        let! deletedEventsState = stateManager.TryRemoveStateAsync(eventsStateName)

                        // Mark the actor as disposed, in case someone tries to use it before Dapr GC's it.
                        isDisposed <- true

                        log.LogInformation("{currentInstant}: Deleted physical state for repository; RepositoryId: {}; RepositoryName: {}; OrganizationId: {organizationId}; OwnerId: {ownerId}; deletedDtoState: {deletedDtoState}; deletedEventsState: {deletedEventsState}.",
                            getCurrentInstantExtended(), repositoryDto.RepositoryId, repositoryDto.RepositoryName, repositoryDto.OrganizationId, repositoryDto.OwnerId, deletedDtoState, deletedEventsState)

                        // Set all values to default.
                        repositoryDto <- RepositoryDto.Default
                    } :> Task
                | _ -> failwith "Unknown reminder type."
