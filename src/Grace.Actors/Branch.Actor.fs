namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open Grace.Actors.Commands.Branch
open Grace.Actors.Constants
open Grace.Actors.Events.Branch
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Dto.Branch
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors.Branch
open Microsoft.Extensions.Logging
open System
open System.Collections.Generic
open System.Runtime.Serialization
open System.Threading.Tasks
open NodaTime
open System.Text.Json
open System.Net.Http.Json

module Branch =

    // Branch should support logical deletes with physical deletes set using a Dapr Timer based on a repository-level setting.
    // Branch Deletion should enumerate and delete each reference in the branch.

    type BranchActor(host: ActorHost) =
        inherit Actor(host)

        let actorName = ActorName.Branch
        let log = host.LoggerFactory.CreateLogger("Branch.Actor")
        let dtoStateName = "branchDtoState"
        let eventsStateName = "branchEventsState"

        let mutable branchDto: BranchDto = BranchDto.Default
        let mutable branchEvents: List<BranchEvent> = null

        let mutable actorStartTime = Instant.MinValue
        let mutable logScope: IDisposable = null
        let mutable currentCommand = String.Empty

        /// Indicates that the actor is in an undefined state, and should be reset.
        let mutable isDisposed = false
        
        let updateDto branchEventType currentBranchDto =
            let newBranchDto = 
                match branchEventType with
                | Created (branchId, branchName, parentBranchId, basedOn, repositoryId, initialPermissions) -> 
                    let mutable branchDto = {BranchDto.Default with BranchId = branchId; BranchName = branchName; ParentBranchId = parentBranchId; BasedOn = basedOn; RepositoryId = repositoryId}
                    for referenceType in initialPermissions do
                        branchDto <- match referenceType with
                                        | ReferenceType.Promotion -> {branchDto with PromotionEnabled = true}
                                        | ReferenceType.Commit -> {branchDto with CommitEnabled = true}
                                        | ReferenceType.Checkpoint -> {branchDto with CheckpointEnabled = true}
                                        | ReferenceType.Save -> {branchDto with SaveEnabled = true}
                                        | ReferenceType.Tag -> {branchDto with TagEnabled = true}
                    branchDto
                | Rebased referenceId -> {currentBranchDto with BasedOn = referenceId}
                | NameSet branchName -> {currentBranchDto with BranchName = branchName}
                | Promoted (referenceId, directoryVersion, sha256Hash, referenceText) -> 
                    {currentBranchDto with LatestPromotion = referenceId; BasedOn = referenceId}
                | Committed (referenceId, directoryVersion, sha256Hash, referenceText) -> {currentBranchDto with LatestCommit = referenceId}
                | Checkpointed (referenceId, directoryVersion, sha256Hash, referenceText) -> {currentBranchDto with LatestCheckpoint = referenceId}
                | Saved (referenceId, directoryVersion, sha256Hash, referenceText) -> {currentBranchDto with LatestSave = referenceId}
                | Tagged (referenceId, directoryVersion, sha256Hash, referenceText) -> currentBranchDto
                | EnabledPromotion enabled -> {currentBranchDto with PromotionEnabled = enabled}
                | EnabledCommit enabled -> {currentBranchDto with CommitEnabled = enabled}
                | EnabledCheckpoint enabled -> {currentBranchDto with CheckpointEnabled = enabled}
                | EnabledSave enabled -> {currentBranchDto with SaveEnabled = enabled}
                | EnabledTag enabled -> {currentBranchDto with TagEnabled = enabled}
                | ReferenceRemoved _ -> currentBranchDto
                | LogicalDeleted (force, deleteReason) -> {currentBranchDto with DeletedAt = Some (getCurrentInstant()); DeleteReason = deleteReason}
                | PhysicalDeleted -> currentBranchDto // Do nothing because it's about to be deleted anyway.
                | Undeleted -> {currentBranchDto with DeletedAt = None; DeleteReason = String.Empty}

            {newBranchDto with UpdatedAt = Some (getCurrentInstant())}

        member private this.BranchEvents() =
            let stateManager = this.StateManager
            task {
                if branchEvents = null then
                    let! retrievedEvents = (Storage.RetrieveState<List<BranchEvent>> stateManager eventsStateName)
                    branchEvents <- match retrievedEvents with
                                           | Some retrievedEvents -> retrievedEvents; 
                                           | None -> List<BranchEvent>()
            
                return branchEvents
            }

        override this.OnActivateAsync() =
            let activateStartTime = getCurrentInstant()
            let stateManager = this.StateManager
            task {
                let! retrievedDto = Storage.RetrieveState<BranchDto> stateManager dtoStateName
                match retrievedDto with
                    | Some retrievedDto -> branchDto <- retrievedDto
                    | None -> branchDto <- BranchDto.Default
                
                let duration = getCurrentInstant().Minus(activateStartTime)
                log.LogInformation("{CurrentInstant}: Activated {ActorType} {ActorId}. Retrieved from storage in {duration}ms.", getCurrentInstantExtended(), actorName, host.Id, duration.TotalMilliseconds.ToString("F3"))
            } :> Task

        member private this.SetMaintenanceReminder() =
            this.RegisterReminderAsync("MaintenanceReminder", Array.empty<byte>, TimeSpan.FromDays(7.0), TimeSpan.FromDays(7.0))

        member private this.UnregisterMaintenanceReminder() =
            this.UnregisterReminderAsync("MaintenanceReminder")

        member private this.OnFirstWrite() =
            task {
                //let! _ = Constants.DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> this.SetMaintenanceReminder())
                ()
            } :> Task

        override this.OnPreActorMethodAsync(context) =
            actorStartTime <- getCurrentInstant()
            logScope <- log.BeginScope("Actor {actorName}", actorName)
            currentCommand <- String.Empty
            log.LogTrace("{CurrentInstant}: Started {ActorName}.{MethodName} Id: {Id}.", getCurrentInstantExtended(), actorName, context.MethodName, this.Id)
            
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

        member private this.ApplyEvent branchEvent =
            let stateManager = this.StateManager
            task {
                try
                    let! branchEvents = this.BranchEvents()
                    if branchEvents.Count = 0 then
                        do! this.OnFirstWrite()

                    branchEvents.Add(branchEvent)
                    do! Constants.DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(eventsStateName, branchEvents))
                
                    branchDto <- branchDto |> updateDto branchEvent.Event
                    do! Constants.DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(dtoStateName, branchDto))

                    // Publish the event to the rest of the world.
                    let graceEvent = Events.GraceEvent.BranchEvent branchEvent
                    let message = graceEvent |> serialize
                    do! daprClient.PublishEventAsync(Constants.GracePubSubService, Constants.GraceEventStreamTopic, message)
                    
                    //let httpClient = getHttpClient branchEvent.Metadata.CorrelationId
                    //httpClient.BaseAddress <- Uri $"http://127.0.0.1:5000"
                    //let! response = httpClient.PostAsync("/notifications/post", jsonContent graceEvent)
                    //if not <| response.IsSuccessStatusCode then
                    //    log.LogError("Failed to send SignalR notification. Event: {event}", message)

                    let returnValue = GraceReturnValue.Create "Branch command succeeded." branchEvent.Metadata.CorrelationId
                    returnValue.Properties.Add(nameof(RepositoryId), $"{branchDto.RepositoryId}")
                    returnValue.Properties.Add(nameof(BranchId), $"{branchDto.BranchId}")
                    returnValue.Properties.Add(nameof(BranchName), $"{branchDto.BranchName}")
                    returnValue.Properties.Add("ParentBranchId", $"{branchDto.ParentBranchId}")
                    returnValue.Properties.Add("EventType", $"{getDiscriminatedUnionFullName branchEvent.Event}")

                    // If the event has a referenceId, add it to the return properties.
                    if branchEvent.Metadata.Properties.ContainsKey(nameof(ReferenceId)) then  
                        returnValue.Properties.Add(nameof(ReferenceId), branchEvent.Metadata.Properties[nameof(ReferenceId)])
                    return Ok returnValue
                with ex ->
                    logToConsole (createExceptionResponse ex)
                    let graceError = GraceError.Create (BranchError.getErrorMessage FailedWhileApplyingEvent) branchEvent.Metadata.CorrelationId
                    return Error graceError
            }

        member private this.SchedulePhysicalDeletion(deleteReason) =
            this.RegisterReminderAsync(ReminderType.PhysicalDeletion, convertToByteArray deleteReason, Constants.DefaultPhysicalDeletionReminderTime, TimeSpan.FromMilliseconds(-1)).Result |> ignore

        interface IBranchActor with
            member this.Exists() =
                not <| (branchDto.BranchId = BranchDto.Default.BranchId) |> returnTask

            member this.Handle (command: BranchCommand) (metadata: EventMetadata) =
                let isValid (command: BranchCommand) (metadata: EventMetadata) =
                    task {
                        let! branchEvents = this.BranchEvents()
                        if branchEvents.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId) && (branchEvents.Count > 3) then
                            return Error (GraceError.Create (BranchError.getErrorMessage DuplicateCorrelationId) metadata.CorrelationId)
                        else
                            match command with 
                            | BranchCommand.Create (branchId, branchName, parentBranchId, basedOn, repositoryId, branchPermissions) ->
                                match branchDto.UpdatedAt with
                                | Some _ -> return Error (GraceError.Create (BranchError.getErrorMessage BranchAlreadyExists) metadata.CorrelationId) 
                                | None -> return Ok command
                            | _ -> 
                                match branchDto.UpdatedAt with
                                | Some _ -> return Ok command
                                | None -> return Error (GraceError.Create (BranchError.getErrorMessage BranchDoesNotExist) metadata.CorrelationId) 
                    }

                let addReference directoryId sha256Hash referenceText referenceType =
                    task {
                        let referenceId: ReferenceId = ReferenceId.NewGuid()
                        let actorId = Reference.GetActorId referenceId
                        let referenceActor = this.ProxyFactory.CreateActorProxy<IReferenceActor>(actorId, Constants.ActorName.Reference)
                        let! referenceDto = referenceActor.Create(referenceId, branchDto.BranchId, directoryId, sha256Hash, referenceType, referenceText)
                        //branchDto.References.Add(referenceDto.CreatedAt, referenceDto)
                        return referenceId
                    }

                let processCommand (command: BranchCommand) (metadata: EventMetadata) =
                    task {
                        try
                            let! event = 
                                task {
                                    match command with
                                    | Create (branchId, branchName, parentBranchId, basedOn, repositoryId, branchPermissions) ->
                                        return Created(branchId, branchName, parentBranchId, basedOn, repositoryId, branchPermissions)
                                    | Rebase referenceId -> return Rebased referenceId
                                    | SetName branchName -> return NameSet branchName
                                    | BranchCommand.Promote (directoryId, sha256Hash, referenceText) ->
                                        let! referenceId = addReference directoryId sha256Hash referenceText ReferenceType.Promotion
                                        metadata.Properties.Add(nameof(ReferenceId), $"{referenceId}")
                                        metadata.Properties.Add(nameof(DirectoryId), $"{directoryId}")
                                        metadata.Properties.Add(nameof(Sha256Hash), $"{sha256Hash}")
                                        metadata.Properties.Add(nameof(ReferenceText), $"{referenceText}")
                                        metadata.Properties.Add(nameof(BranchId), $"{this.Id}")
                                        return Promoted (referenceId, directoryId, sha256Hash, referenceText)
                                    | BranchCommand.Commit (directoryId, sha256Hash, referenceText) ->
                                        let! referenceId = addReference directoryId sha256Hash referenceText ReferenceType.Commit
                                        metadata.Properties.Add(nameof(ReferenceId), $"{referenceId}")
                                        metadata.Properties.Add(nameof(DirectoryId), $"{directoryId}")
                                        metadata.Properties.Add(nameof(Sha256Hash), $"{sha256Hash}")
                                        metadata.Properties.Add(nameof(ReferenceText), $"{referenceText}")
                                        metadata.Properties.Add(nameof(BranchId), $"{this.Id}")
                                        return Committed (referenceId, directoryId, sha256Hash, referenceText)
                                    | BranchCommand.Checkpoint (directoryId, sha256Hash, referenceText) -> 
                                        let! referenceId = addReference directoryId sha256Hash referenceText ReferenceType.Checkpoint
                                        metadata.Properties.Add(nameof(ReferenceId), $"{referenceId}")
                                        metadata.Properties.Add(nameof(DirectoryId), $"{directoryId}")
                                        metadata.Properties.Add(nameof(Sha256Hash), $"{sha256Hash}")
                                        metadata.Properties.Add(nameof(ReferenceText), $"{referenceText}")
                                        metadata.Properties.Add(nameof(BranchId), $"{this.Id}")
                                        return Checkpointed (referenceId, directoryId, sha256Hash, referenceText)
                                    | BranchCommand.Save (directoryId, sha256Hash, referenceText) -> 
                                        let! referenceId = addReference directoryId sha256Hash referenceText ReferenceType.Save
                                        metadata.Properties.Add(nameof(ReferenceId), $"{referenceId}")
                                        metadata.Properties.Add(nameof(DirectoryId), $"{directoryId}")
                                        metadata.Properties.Add(nameof(Sha256Hash), $"{sha256Hash}")
                                        metadata.Properties.Add(nameof(ReferenceText), $"{referenceText}")
                                        metadata.Properties.Add(nameof(BranchId), $"{this.Id}")
                                        return Saved (referenceId, directoryId, sha256Hash, referenceText)
                                    | BranchCommand.Tag (directoryId, sha256Hash, referenceText) -> 
                                        let! referenceId = addReference directoryId sha256Hash referenceText ReferenceType.Tag
                                        metadata.Properties.Add(nameof(ReferenceId), $"{referenceId}")
                                        metadata.Properties.Add(nameof(DirectoryId), $"{directoryId}")
                                        metadata.Properties.Add(nameof(Sha256Hash), $"{sha256Hash}")
                                        metadata.Properties.Add(nameof(ReferenceText), $"{referenceText}")
                                        metadata.Properties.Add(nameof(BranchId), $"{this.Id}")
                                        return Tagged (referenceId, directoryId, sha256Hash, referenceText)
                                    | EnablePromotion enabled -> return EnabledPromotion enabled
                                    | EnableCommit enabled -> return EnabledCommit enabled
                                    | EnableCheckpoint enabled -> return EnabledCheckpoint enabled
                                    | EnableSave enabled -> return EnabledSave enabled
                                    | EnableTag enabled -> return EnabledTag enabled
                                    | RemoveReference referenceId -> return ReferenceRemoved referenceId
                                    | DeleteLogical (force, deleteReason) ->
                                        this.SchedulePhysicalDeletion(deleteReason)
                                        return LogicalDeleted (force, deleteReason)
                                    | DeletePhysical ->
                                        isDisposed <- true
                                        return PhysicalDeleted
                                    | Undelete -> return Undeleted
                                }
                            return! this.ApplyEvent {Event = event; Metadata = metadata}
                        with ex ->
                            return Error (GraceError.Create $"{createExceptionResponse ex}" metadata.CorrelationId)
                    }

                task {
                    currentCommand <- getDistributedUnionCaseName command
                    match! isValid command metadata with
                    | Ok command -> return! processCommand command metadata 
                    | Error error -> return Error error
                }

            member this.Get() = branchDto |> returnTask

            member this.GetParentBranch() = 
                task {
                    let actorId = ActorId($"{branchDto.ParentBranchId}")
                    let branchActorProxy = this.Host.ProxyFactory.CreateActorProxy<IBranchActor>(actorId, ActorName.Branch)
                    return! branchActorProxy.Get()
                }

            member this.GetLatestCommit() = branchDto.LatestCommit |> returnTask

            member this.GetLatestPromotion() = branchDto.LatestPromotion |> returnTask

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
                        // Delete the references for this branch.


                        // Delete saved state for this actor.
                        let! deletedDtoState = stateManager.TryRemoveStateAsync(dtoStateName)
                        let! deletedEventsState = stateManager.TryRemoveStateAsync(eventsStateName)

                        // Mark the actor as disposed, in case someone tries to use it before Dapr GC's it.
                        isDisposed <- true

                        log.LogInformation("{currentInstant}: Deleted physical state for branch; RepositoryId: {repositoryId}; BranchId: {branchId}; BranchName: {branchName}; ParentBranchId: {parentBranchId}; deletedDtoState: {deletedDtoState}; deletedEventsState: {deletedEventsState}.", 
                            getCurrentInstantExtended(), branchDto.RepositoryId, branchDto.BranchId, branchDto.BranchName, branchDto.ParentBranchId, deletedDtoState, deletedEventsState)

                        // Set all values to default.
                        branchDto <- BranchDto.Default
                    } :> Task
                | _ -> failwith "Unknown reminder type."
