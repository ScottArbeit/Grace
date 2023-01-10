namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open FSharpPlus
open Grace.Actors.Interfaces
open Grace.Actors.Commands.Repository
open Grace.Actors.Events.Repository
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Dto.Repository
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

module Repository =
        
    let GetActorId (repositoryId: RepositoryId) = ActorId($"{repositoryId}")

    type RepositoryActor(host: ActorHost) =
        inherit Actor(host)

        let actorName = Constants.ActorName.Repository
        let log = host.LoggerFactory.CreateLogger(actorName)
        let mutable actorStartTime = Instant.MinValue
        let mutable logScope: IDisposable = null

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

        member private this.OnFirstWrite() =
            task {
                let! _ = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> this.SetMaintenanceReminder())
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
                | SingleStepMergeEnabled enabled -> {currentRepositoryDto with EnabledSingleStepMerge = enabled}
                | ComplexMergeEnabled enabled -> {currentRepositoryDto with EnabledComplexMerge = enabled}
                | DescriptionSet description -> {currentRepositoryDto with Description = description}
                | LogicalDeleted _ -> {currentRepositoryDto with DeletedAt = Some (getCurrentInstant())}
                | PhysicalDeleted _ -> currentRepositoryDto // Do nothing because it's about to be deleted anyway.
                | Undeleted -> {currentRepositoryDto with DeletedAt = None; DeleteReason = String.Empty}

            {newRepositoryDto with UpdatedAt = Some (getCurrentInstant())}

        member private this.ApplyEvent(repositoryEvent) =
            let stateManager = this.StateManager
            let (repositoryEvents: List<RepositoryEvent>) = this.RepositoryEvents
            task {
                try
                    if repositoryEvents.Count = 0 then
                        do! this.OnFirstWrite()

                    repositoryEvents.Add(repositoryEvent)
                    do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(eventsStateName, repositoryEvents))
                    
                    repositoryDto <- repositoryDto |> this.updateDto repositoryEvent
                    do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(dtoStateName, repositoryDto))

                    // Publish the event to the rest of the world.
                    let graceEvent = Events.GraceEvent.RepositoryEvent repositoryEvent
                    let message = JsonSerializer.Serialize<Events.GraceEvent>(graceEvent, options = Constants.JsonSerializerOptions)
                    do! daprClient.PublishEventAsync(GracePubSubService, GraceEventStreamTopic, message)

                    let returnValue = GraceReturnValue.Create $"Repository command succeeded." repositoryEvent.Metadata.CorrelationId
                    returnValue.Properties.Add(nameof(OwnerId), $"{repositoryDto.OwnerId}")
                    returnValue.Properties.Add(nameof(OrganizationId), $"{repositoryDto.OrganizationId}")
                    returnValue.Properties.Add(nameof(RepositoryId), $"{repositoryDto.RepositoryId}")
                    returnValue.Properties.Add(nameof(RepositoryName), $"{repositoryDto.RepositoryName}")
                    returnValue.Properties.Add("EventType", $"{discriminatedUnionFullNameToString repositoryEvent.Event}")
                    return Ok returnValue
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

        member private this.RepositoryEvents with get() = (
            let stateManager = this.StateManager
            if repositoryEvents = null then            
                let retrievedEvents = (Storage.RetrieveState<List<RepositoryEvent>> stateManager eventsStateName).Result
                repositoryEvents <- match retrievedEvents with
                                       | Some retrievedEvents -> retrievedEvents; 
                                       | None -> List<RepositoryEvent>()
            
            repositoryEvents
        )

        override this.OnPreActorMethodAsync(context) =
            actorStartTime <- getCurrentInstant()
            logScope <- log.BeginScope("Actor {actorName}", actorName)
            //log.LogInformation("{CurrentInstant}: Started {ActorName}.{MethodName} Id: {Id}.", getCurrentInstantExtended(), actorName, context.MethodName, this.Id.GetId())
            Task.CompletedTask
            
        override this.OnPostActorMethodAsync(context) =
            let duration = getCurrentInstant().Minus(actorStartTime)
            log.LogInformation("{CurrentInstant}: Finished {ActorName}.{MethodName} Id: {Id}; Duration: {duration}ms.", getCurrentInstantExtended(), actorName, context.MethodName, this.Id.GetId(), duration.TotalMilliseconds.ToString("F3"))
            logScope.Dispose()
            Task.CompletedTask

        interface IExportable<RepositoryEvent> with
            member this.Export() = 
                task {
                    try
                        if this.RepositoryEvents.Count > 0 then
                            return Ok this.RepositoryEvents
                        else
                            return Error ExportError.EventListIsEmpty
                    with ex ->
                        return Error (ExportError.Exception (createExceptionResponse ex))
                }

            member this.Import(events: IList<RepositoryEvent>) = 
                let stateManager = this.StateManager
                task {
                    try
                        this.RepositoryEvents.Clear()
                        this.RepositoryEvents.AddRange(events)
                        let newRepositoryDto = this.RepositoryEvents.Aggregate(RepositoryDto.Default,
                            (fun state evnt -> (this.updateDto evnt state)))
                        do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(eventsStateName, this.RepositoryEvents))
                        do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(dtoStateName, newRepositoryDto))
                        return Ok this.RepositoryEvents.Count
                    with ex -> 
                        return Error (ImportError.Exception (createExceptionResponse ex))
                }

        interface IRevertable<RepositoryDto> with
            member this.RevertBack(eventsToRevert: int) (persist: PersistAction) =
                let stateManager = this.StateManager
                task {
                    try
                        if this.RepositoryEvents.Count > 0 then
                            let eventsToKeep = this.RepositoryEvents.Count - eventsToRevert
                            if eventsToKeep <= 0 then  
                                return Error RevertError.OutOfRange
                            else
                                let revertedEvents = this.RepositoryEvents.Take(eventsToKeep)
                                let newRepositoryDto = revertedEvents.Aggregate(RepositoryDto.Default,
                                    (fun state evnt -> (this.updateDto evnt state)))
                                match persist with
                                | PersistAction.Save ->
                                    this.RepositoryEvents.Clear()
                                    this.RepositoryEvents.AddRange(revertedEvents)
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
                        if this.RepositoryEvents.Count > 0 then
                            let revertedEvents = this.RepositoryEvents.Where(fun evnt -> evnt.Metadata.Timestamp < whenToRevertTo)
                            if revertedEvents.Count() = 0 then  
                                return Error RevertError.OutOfRange
                            else
                                let newRepositoryDto = revertedEvents |> Seq.fold (fun state evnt -> 
                                    (this.updateDto evnt state)) RepositoryDto.Default
                                match persist with
                                | PersistAction.Save -> 
                                    task {
                                        this.RepositoryEvents.Clear()
                                        this.RepositoryEvents.AddRange(revertedEvents)
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
                Task.FromResult(this.RepositoryEvents.Count)

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
                    if this.RepositoryEvents.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId) then
                        Error (GraceError.Create (RepositoryError.getErrorMessage DuplicateCorrelationId) metadata.CorrelationId)
                    else
                        match command with 
                            | RepositoryCommand.Create (_, _, _, _) ->
                                match repositoryDto.UpdatedAt with
                                | Some _ -> Error (GraceError.Create (RepositoryError.getErrorMessage RepositoryIdAlreadyExists) metadata.CorrelationId)
                                | None -> Ok command
                            | _ -> 
                                match repositoryDto.UpdatedAt with
                                | Some _ -> Ok command
                                | None -> Error (GraceError.Create (RepositoryError.getErrorMessage RepositoryIdDoesNotExist) metadata.CorrelationId)

                let processCommand command metadata =
                    task {
                        try
                            let event =
                                match command with
                                | Create (repositoryName, repositoryId, ownerId, organizationId) -> Created (repositoryName, repositoryId, ownerId, organizationId)
                                | SetObjectStorageProvider objectStorageProvider ->  ObjectStorageProviderSet objectStorageProvider
                                | SetStorageAccountName storageAccountName -> StorageAccountNameSet storageAccountName
                                | SetStorageContainerName containerName -> StorageContainerNameSet containerName
                                | SetRepositoryStatus repositoryStatus -> RepositoryStatusSet repositoryStatus
                                | SetVisibility repositoryVisibility -> RepositoryVisibilitySet repositoryVisibility
                                | AddBranch branchName -> BranchAdded branchName
                                | DeleteBranch branchName -> BranchDeleted branchName
                                | SetRecordSaves recordSaves -> RecordSavesSet recordSaves
                                | SetDefaultServerApiVersion version -> DefaultServerApiVersionSet version
                                | SetDefaultBranchName defaultBranchName -> DefaultBranchNameSet defaultBranchName
                                | SetSaveDays days -> SaveDaysSet days
                                | SetCheckpointDays days -> CheckpointDaysSet days
                                | EnableSingleStepMerge enabled -> SingleStepMergeEnabled enabled
                                | EnableComplexMerge enabled -> ComplexMergeEnabled enabled
                                | SetDescription description -> DescriptionSet description
                                | DeleteLogical (force, deleteReason) -> LogicalDeleted (force, deleteReason)
                                | DeletePhysical ->
                                    (task {
                                        do! this.SchedulePhysicalDeletion()
                                    }).Wait()
                                    PhysicalDeleted
                                | RepositoryCommand.Undelete -> Undeleted
                            
                            return! this.ApplyEvent {Event = event; Metadata = metadata}
                        with ex ->
                            return Error (GraceError.Create $"{createExceptionResponse ex}" metadata.CorrelationId)
                    }

                match isValid command metadata with
                | Ok command -> processCommand command metadata
                | Error error -> Task.FromResult(Error error)

        interface IRemindable with
            member this.ReceiveReminderAsync(reminderName, state, dueTime, period) =
                match reminderName with
                | "MaintenanceReminder" ->
                    task {
                        // Do some maintenance
                        ()
                    } :> Task
                | _ -> Task.CompletedTask
