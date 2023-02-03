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

module Branch =

    // Branch should support logical deletes with physical deletes set using a Dapr Timer based on a repository-level setting.
    // Branch Deletion should enumerate and delete each save, checkpoint, and commit in the branch.

    type BranchActor(host: ActorHost) =
        inherit Actor(host)

        let dtoStateName = "branchDtoState"
        let eventsStateName = "branchEventsState"

        let mutable branchDto: BranchDto = BranchDto.Default
        let mutable branchEvents: List<BranchEvent> = null
        let log = host.LoggerFactory.CreateLogger(nameof(BranchActor))

        let actorName = Constants.ActorName.Branch
        let mutable actorStartTime = Instant.MinValue
        let mutable logScope: IDisposable = null
        
        let updateDto branchEventType currentBranchDto =
            let newOrganizationDto = 
                match branchEventType with
                | Created (branchId, branchName, parentBranchId, repositoryId) -> {BranchDto.Default with BranchId = branchId; BranchName = branchName; ParentBranchId = parentBranchId; RepositoryId = repositoryId}
                | Rebased referenceId -> {currentBranchDto with BasedOn = referenceId}
                | NameSet branchName -> {currentBranchDto with BranchName = branchName}
                | Promoted (referenceId, directoryVersion, sha256Hash, referenceText) -> {currentBranchDto with LatestPromotion = referenceId}
                | Committed (referenceId, directoryVersion, sha256Hash, referenceText) -> {currentBranchDto with LatestCommit = referenceId}
                | Checkpointed (referenceId, directoryVersion, sha256Hash, referenceText) -> currentBranchDto
                | Saved (referenceId, directoryVersion, sha256Hash, referenceText) -> currentBranchDto
                | Tagged (referenceId, directoryVersion, sha256Hash, referenceText) -> currentBranchDto
                | EnabledPromotion enabled -> {currentBranchDto with EnabledPromotion = enabled}
                | EnabledCommit enabled -> {currentBranchDto with EnabledCommit = enabled}
                | EnabledCheckpoint enabled -> {currentBranchDto with EnabledCheckpoint = enabled}
                | EnabledSave enabled -> {currentBranchDto with EnabledSave = enabled}
                | EnabledTag enabled -> {currentBranchDto with EnabledTag = enabled}
                | ReferenceRemoved _ -> currentBranchDto
                | LogicalDeleted  -> {currentBranchDto with DeletedAt = Some (getCurrentInstant())}
                | PhysicalDeleted -> currentBranchDto // Do nothing because it's about to be deleted anyway.
                | Undeleted -> {currentBranchDto with DeletedAt = None}

            {newOrganizationDto with UpdatedAt = Some (getCurrentInstant())}

        member private this.BranchEvents =
            task {
                let stateManager = this.StateManager
                if branchEvents = null then
                    let! retrievedEvents = (Storage.RetrieveState<List<BranchEvent>> stateManager eventsStateName)
                    branchEvents <- match retrievedEvents with
                                           | Some retrievedEvents -> retrievedEvents; 
                                           | None -> List<BranchEvent>()
            
                return branchEvents
            }

        override this.OnActivateAsync() =
            let stateManager = this.StateManager
            log.LogInformation($"{getCurrentInstantExtended()} Activated BranchActor {host.Id}.")
            task {
                let! retrievedDto = Storage.RetrieveState<BranchDto> stateManager dtoStateName
                match retrievedDto with
                    | Some retrievedDto -> branchDto <- retrievedDto
                    | None -> branchDto <- BranchDto.Default
            } :> Task

        member private this.SetMaintenanceReminder() =
            this.RegisterReminderAsync("MaintenanceReminder", Array.empty<byte>, TimeSpan.FromDays(7.0), TimeSpan.FromDays(7.0))

        member private this.OnFirstWrite() =
            task {
                let! _ = Constants.DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> this.SetMaintenanceReminder())
                ()
            }

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

        member private this.ApplyEvent branchEvent =
            let stateManager = this.StateManager
            task {
                try
                    let! branchEvents = this.BranchEvents
                    if branchEvents.Count = 0 then
                        do! this.OnFirstWrite()

                    branchEvents.Add(branchEvent)
                    do! Constants.DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(eventsStateName, branchEvents))
                
                    branchDto <- branchDto |> updateDto branchEvent.Event
                    do! Constants.DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(dtoStateName, branchDto))

                    // Publish the event to the rest of the world.
                    let graceEvent = Events.GraceEvent.BranchEvent branchEvent
                    let message = JsonSerializer.Serialize<Events.GraceEvent>(graceEvent, options = Constants.JsonSerializerOptions)
                    do! daprClient.PublishEventAsync(Constants.GracePubSubService, Constants.GraceEventStreamTopic, message)

                    let returnValue = GraceReturnValue.Create "Branch command succeeded." branchEvent.Metadata.CorrelationId
                    returnValue.Properties.Add(nameof(RepositoryId), $"{branchDto.RepositoryId}")
                    returnValue.Properties.Add(nameof(BranchId), $"{branchDto.BranchId}")
                    returnValue.Properties.Add(nameof(BranchName), $"{branchDto.BranchName}")
                    returnValue.Properties.Add("ParentBranchId", $"{branchDto.ParentBranchId}")
                    returnValue.Properties.Add("EventType", $"{discriminatedUnionFullNameToString branchEvent.Event}")
                    if branchEvent.Metadata.Properties.ContainsKey("ReferenceId") then  
                        returnValue.Properties.Add("ReferenceId", branchEvent.Metadata.Properties["ReferenceId"])
                    return Ok returnValue
                with ex ->
                    let graceError = GraceError.Create (BranchError.getErrorMessage FailedWhileApplyingEvent) branchEvent.Metadata.CorrelationId
                    return Error graceError
            }

        member private this.SchedulePhysicalDeletion() =
            // Send pub/sub message to kick off branch deletion
            // Will include: enumerating and deleting each save, checkpoint, commit, and tag.
            task {
                return ()
            }

        interface IBranchActor with
            member this.Exists() =
                if branchDto.BranchId = BranchDto.Default.BranchId then
                    false |> returnTask
                else
                    true |> returnTask

            member this.Handle (command: BranchCommand) (metadata: EventMetadata) =
                let isValid (command: BranchCommand) (metadata: EventMetadata) =
                    task {
                        let! branchEvents = this.BranchEvents
                        if branchEvents.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId) then
                            return Error (GraceError.Create (BranchError.getErrorMessage DuplicateCorrelationId) metadata.CorrelationId)
                        else
                            match command with 
                            | BranchCommand.Create (branchId, branchName, parentBranchId, repositoryId) ->
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
                                    | Create (branchId, branchName, parentBranchId, repositoryId) ->
                                        return Created(branchId, branchName, parentBranchId, repositoryId)
                                    | Rebase referenceId -> return Rebased referenceId
                                    | SetName organizationName -> return NameSet (organizationName)
                                    | BranchCommand.Promote (directoryId, sha256Hash, referenceText) ->
                                        let! referenceId = addReference directoryId sha256Hash referenceText ReferenceType.Promotion
                                        metadata.Properties.Add("ReferenceId", $"{referenceId}")
                                        metadata.Properties.Add("DirectoryId", $"{directoryId}")
                                        metadata.Properties.Add("Sha256Hash", $"{sha256Hash}")
                                        metadata.Properties.Add("ReferenceText", $"{referenceText}")
                                        metadata.Properties.Add("BranchId", $"{this.Id}")
                                        return Promoted (referenceId, directoryId, sha256Hash, referenceText)
                                    | BranchCommand.Commit (directoryId, sha256Hash, referenceText) ->
                                        let! referenceId = addReference directoryId sha256Hash referenceText ReferenceType.Commit
                                        metadata.Properties.Add("ReferenceId", $"{referenceId}")
                                        metadata.Properties.Add("DirectoryId", $"{directoryId}")
                                        metadata.Properties.Add("Sha256Hash", $"{sha256Hash}")
                                        metadata.Properties.Add("ReferenceText", $"{referenceText}")
                                        metadata.Properties.Add("BranchId", $"{this.Id}")
                                        return Committed (referenceId, directoryId, sha256Hash, referenceText)
                                    | BranchCommand.Checkpoint (directoryId, sha256Hash, referenceText) -> 
                                        let! referenceId = addReference directoryId sha256Hash referenceText ReferenceType.Checkpoint
                                        metadata.Properties.Add("ReferenceId", $"{referenceId}")
                                        metadata.Properties.Add("DirectoryId", $"{directoryId}")
                                        metadata.Properties.Add("Sha256Hash", $"{sha256Hash}")
                                        metadata.Properties.Add("ReferenceText", $"{referenceText}")
                                        metadata.Properties.Add("BranchId", $"{this.Id}")
                                        return Checkpointed (referenceId, directoryId, sha256Hash, referenceText)
                                    | BranchCommand.Save (directoryId, sha256Hash, referenceText) -> 
                                        let! referenceId = addReference directoryId sha256Hash referenceText ReferenceType.Save
                                        metadata.Properties.Add("ReferenceId", $"{referenceId}")
                                        metadata.Properties.Add("DirectoryId", $"{directoryId}")
                                        metadata.Properties.Add("Sha256Hash", $"{sha256Hash}")
                                        metadata.Properties.Add("ReferenceText", $"{referenceText}")
                                        metadata.Properties.Add("BranchId", $"{this.Id}")
                                        return Saved (referenceId, directoryId, sha256Hash, referenceText)
                                    | BranchCommand.Tag (directoryId, sha256Hash, referenceText) -> 
                                        let! referenceId = addReference directoryId sha256Hash referenceText ReferenceType.Tag
                                        metadata.Properties.Add("ReferenceId", $"{referenceId}")
                                        metadata.Properties.Add("DirectoryId", $"{directoryId}")
                                        metadata.Properties.Add("Sha256Hash", $"{sha256Hash}")
                                        metadata.Properties.Add("ReferenceText", $"{referenceText}")
                                        metadata.Properties.Add("BranchId", $"{this.Id}")
                                        return Tagged (referenceId, directoryId, sha256Hash, referenceText)
                                    | EnablePromotion enabled -> return EnabledPromotion enabled
                                    | EnableCommit enabled -> return EnabledCommit enabled
                                    | EnableCheckpoint enabled -> return EnabledCheckpoint enabled
                                    | EnableSave enabled -> return EnabledSave enabled
                                    | EnableTag enabled -> return EnabledTag enabled
                                    | RemoveReference referenceId -> return ReferenceRemoved referenceId
                                    | DeleteLogical -> return LogicalDeleted
                                    | DeletePhysical ->
                                        task {
                                            do! this.SchedulePhysicalDeletion()
                                        } |> Async.AwaitTask |> ignore
                                        return PhysicalDeleted
                                    | Undelete -> return Undeleted
                                }
                            return! this.ApplyEvent {Event = event; Metadata = metadata}
                        with ex ->
                            return Error (GraceError.Create $"{createExceptionResponse ex}" metadata.CorrelationId)
                    }

                task {
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
