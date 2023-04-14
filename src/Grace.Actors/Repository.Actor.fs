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
open Grace.Shared.Constants
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

module Repository =
        
    let GetActorId (repositoryId: RepositoryId) = ActorId($"{repositoryId}")

    type RepositoryActor(host: ActorHost) =
        inherit Actor(host)

        let actorName = Constants.ActorName.Repository
        let log = host.LoggerFactory.CreateLogger(actorName)
        let mutable actorStartTime = Instant.MinValue
        let mutable logScope: IDisposable = null
        let mutable currentCommand = String.Empty

        let dtoStateName = "repositoryDtoState"
        let eventsStateName = "repositoryEventsState"

        let mutable repositoryDto = RepositoryDto.Default
        let mutable repositoryEvents: List<RepositoryEvent> = null

        override this.OnActivateAsync() =
            let stateManager = this.StateManager
            log.LogInformation("{CurrentInstant}: Activated {ActorType} {ActorId}.", getCurrentInstantExtended(), this.GetType().Name, host.Id)
            task {
                let! retrievedDto = Storage.RetrieveState<RepositoryDto> stateManager dtoStateName
                match retrievedDto with
                    | Some retrievedDto -> repositoryDto <- retrievedDto
                    | None -> repositoryDto <- RepositoryDto.Default

                //let! retrievedEvents = Storage.RetrieveState<List<RepositoryEvent>> stateManager eventsStateName
                //match retrievedEvents with
                //    | Some retrievedEvents -> repositoryEvents.AddRange(retrievedEvents)
                //    | None -> this.onFirstActivation()
            } :> Task

        member private this.SetMaintenanceReminder() =
            this.RegisterReminderAsync("MaintenanceReminder", Array.empty<byte>, TimeSpan.FromDays(7.0), TimeSpan.FromDays(7.0))

        member private this.UnregisterMaintenanceReminder() =
            this.UnregisterReminderAsync("MaintenanceReminder")

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
                                                ObjectStorageProvider = Constants.defaultObjectStorageProvider;
                                                StorageAccountName = Constants.defaultObjectStorageAccount;
                                                StorageContainerName = StorageContainerName Constants.defaultObjectStorageContainerName}
                | ObjectStorageProviderSet objectStorageProvider -> {currentRepositoryDto with ObjectStorageProvider = objectStorageProvider}
                | StorageAccountNameSet storageAccountName ->  {currentRepositoryDto with StorageAccountName = storageAccountName}
                | StorageContainerNameSet containerName -> {currentRepositoryDto with StorageContainerName = containerName}
                | RepositoryStatusSet repositoryStatus -> {currentRepositoryDto with RepositoryStatus = repositoryStatus}
                | RepositoryVisibilitySet repositoryVisibility -> {currentRepositoryDto with RepositoryVisibility = repositoryVisibility}
                | BranchAdded branchName -> currentRepositoryDto.Branches.Add branchName |> ignore
                                            currentRepositoryDto
                | BranchDeleted branchName -> currentRepositoryDto.Branches.Remove branchName |> ignore
                                              currentRepositoryDto
                | RecordSavesSet recordSaves -> {currentRepositoryDto with RecordSaves = recordSaves}
                | DefaultServerApiVersionSet version -> {currentRepositoryDto with DefaultServerApiVersion = version}
                | DefaultBranchNameSet defaultBranchName -> {currentRepositoryDto with DefaultBranchName = defaultBranchName}
                | SaveDaysSet days -> {currentRepositoryDto with SaveDays = days}
                | CheckpointDaysSet days -> {currentRepositoryDto with CheckpointDays = days}
                | EnabledSingleStepPromotion enabled -> {currentRepositoryDto with EnabledSingleStepPromotion = enabled}
                | EnabledComplexPromotion enabled -> {currentRepositoryDto with EnabledComplexPromotion = enabled}
                | DescriptionSet description -> {currentRepositoryDto with Description = description}
                | LogicalDeleted _ -> {currentRepositoryDto with DeletedAt = Some (getCurrentInstant())}
                | PhysicalDeleted _ -> currentRepositoryDto // Do nothing because it's about to be deleted anyway.
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

                    // If we're creating a repository, we need to create the default branch.
                    let! handleCreate = 
                        task {
                            match repositoryEvent.Event with
                            | Created (name, repositoryId, ownerId, organizationId) -> 
                                let branchId = (Guid.NewGuid())
                                let branchActorId = ActorId($"{branchId}")
                                let branchActor = Services.ActorProxyFactory.CreateActorProxy<IBranchActor>(branchActorId, ActorName.Branch)
                                let! result = branchActor.Handle (Commands.Branch.BranchCommand.Create (branchId, (BranchName Constants.InitialBranchName), Constants.DefaultParentBranchId, ReferenceId.Empty, repositoryId)) repositoryEvent.Metadata
                                match result with
                                | Ok branchGraceReturn -> 
                                    // Create an initial promotion with completely empty contents
                                    let emptyDirectoryId = DirectoryId.NewGuid()
                                    let emptySha256Hash = computeSha256ForDirectory "/" (List<LocalDirectoryVersion>()) (List<LocalFileVersion>())
                                    let! promotionResult = branchActor.Handle (Commands.Branch.BranchCommand.Promote(emptyDirectoryId, emptySha256Hash, (getLocalizedString StringResourceName.InitialPromotionMessage))) repositoryEvent.Metadata
                                    match promotionResult with
                                    | Ok promotionGraceReturn ->
                                        let referenceId = Guid.Parse(promotionGraceReturn.Properties[nameof(ReferenceId)])
                                        let! rebaseResult = branchActor.Handle (Commands.Branch.BranchCommand.Rebase(referenceId)) repositoryEvent.Metadata
                                        match rebaseResult with
                                        | Ok rebaseGraceReturn -> 
                                            return Ok (branchId, referenceId)
                                        | Error graceError -> 
                                            let graceError = GraceError.Create $"{RepositoryError.getErrorMessage FailedRebasingInitialBranch}{Environment.NewLine}{graceError.Error}" repositoryEvent.Metadata.CorrelationId
                                            return Error graceError
                                    | Error graceError ->
                                        let graceError = GraceError.Create $"{RepositoryError.getErrorMessage FailedCreatingInitialPromotion}{Environment.NewLine}{graceError.Error}" repositoryEvent.Metadata.CorrelationId
                                        return Error graceError
                                | Error graceError ->
                                    let graceError = GraceError.Create $"{RepositoryError.getErrorMessage FailedCreatingInitialBranch}{Environment.NewLine}{graceError.Error}" repositoryEvent.Metadata.CorrelationId
                                    return Error graceError
                            | _ -> return Ok (BranchId.Empty, ReferenceId.Empty)
                        }

                    match handleCreate with
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
                        returnValue.Properties.Add("EventType", $"{discriminatedUnionFullNameToString repositoryEvent.Event}")
                        return Ok returnValue
                    | Error graceError ->
                        return Error graceError
                with ex -> 
                    let graceError = GraceError.Create (RepositoryError.getErrorMessage RepositoryError.FailedWhileApplyingEvent) repositoryEvent.Metadata.CorrelationId
                    return Error graceError
            }

        member private this.SchedulePhysicalDeletion() =
            // Send pub/sub message to kick off repository deletion
            // Will include: enumerating and deleting each branch, which should catch each save, checkpoint, commit, and
            // I *think* that should cascade down to every Directory and File being removed from Appearances lists,
            // and therefore should see the deletion of all of the Actor instances, but need to make sure that's true.
            task {
                return ()
            }

        override this.OnPreActorMethodAsync(context) =
            actorStartTime <- getCurrentInstant()
            logScope <- log.BeginScope("Actor {actorName}", actorName)
            currentCommand <- String.Empty
            //log.LogInformation("{CurrentInstant}: Started {ActorName}.{MethodName}, Id: {Id}.", getCurrentInstantExtended(), actorName, context.MethodName, this.Id.GetId())
            Task.CompletedTask
            
        override this.OnPostActorMethodAsync(context) =
            let duration = getCurrentInstant().Minus(actorStartTime)
            if String.IsNullOrEmpty(currentCommand) then
                log.LogInformation("{CurrentInstant}: Finished {ActorName}.{MethodName}; Id: {Id}; Duration: {duration}ms.", $"{getCurrentInstantExtended(),-28}", actorName, context.MethodName, this.Id.GetId(), duration.TotalMilliseconds.ToString("F3"))
            else
                log.LogInformation("{CurrentInstant}: Finished {ActorName}.{MethodName}; Command: {Command}; Id: {Id}; Duration: {duration}ms.", $"{getCurrentInstantExtended(),-28}", actorName, context.MethodName, currentCommand, this.Id.GetId(), duration.TotalMilliseconds.ToString("F3"))
            logScope.Dispose()
            Task.CompletedTask

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
            member this.Get() =
                Task.FromResult(repositoryDto)

            member this.GetObjectStorageProvider() =
                Task.FromResult(repositoryDto.ObjectStorageProvider)

            member this.Exists() =
                Task.FromResult(if repositoryDto.UpdatedAt.IsSome then true else false)

            member this.IsDeleted() =
                Task.FromResult(if repositoryDto.DeletedAt.IsSome then true else false)

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
                                    | SetObjectStorageProvider objectStorageProvider -> return ObjectStorageProviderSet objectStorageProvider
                                    | SetStorageAccountName storageAccountName -> return StorageAccountNameSet storageAccountName
                                    | SetStorageContainerName containerName -> return StorageContainerNameSet containerName
                                    | SetRepositoryStatus repositoryStatus -> return RepositoryStatusSet repositoryStatus
                                    | SetVisibility repositoryVisibility -> return RepositoryVisibilitySet repositoryVisibility
                                    | AddBranch branchName -> return BranchAdded branchName
                                    | DeleteBranch branchName -> return BranchDeleted branchName
                                    | SetRecordSaves recordSaves -> return RecordSavesSet recordSaves
                                    | SetDefaultServerApiVersion version -> return DefaultServerApiVersionSet version
                                    | SetDefaultBranchName defaultBranchName -> return DefaultBranchNameSet defaultBranchName
                                    | SetSaveDays days -> return SaveDaysSet days
                                    | SetCheckpointDays days -> return CheckpointDaysSet days
                                    | EnableSingleStepPromotion enabled -> return EnabledSingleStepPromotion enabled
                                    | EnableComplexPromotion enabled -> return EnabledComplexPromotion enabled
                                    | SetDescription description -> return DescriptionSet description
                                    | DeleteLogical (force, deleteReason) -> 
                                        //do! this.UnregisterMaintenanceReminder()
                                        return LogicalDeleted (force, deleteReason)
                                    | DeletePhysical ->
                                        do! this.SchedulePhysicalDeletion()
                                        return PhysicalDeleted
                                    | RepositoryCommand.Undelete -> return Undeleted
                                }
                            
                            return! this.ApplyEvent {Event = event; Metadata = metadata}
                        with ex ->
                            return Error (GraceError.Create $"{createExceptionResponse ex}{Environment.NewLine}{metadata}" metadata.CorrelationId)
                    }

                task {
                    currentCommand <- discriminatedUnionCaseNameToString command
                    match! isValid command metadata with
                    | Ok command -> return! processCommand command metadata
                    | Error error -> return Error error
                }

        interface IRemindable with
            member this.ReceiveReminderAsync(reminderName, state, dueTime, period) =
                match reminderName with
                | "MaintenanceReminder" ->
                    task {
                        // Do some maintenance
                        ()
                    } :> Task
                | _ -> Task.CompletedTask
