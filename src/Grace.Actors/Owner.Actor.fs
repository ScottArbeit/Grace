namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open FSharp.Control
open Grace.Actors.Commands.Owner
open Grace.Actors.Services
open Grace.Actors.Events.Owner
open Grace.Actors.Interfaces
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Dto.Organization
open Grace.Shared.Dto.Owner
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors.Owner
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Linq
open System.Runtime.Serialization
open System.Text.Json
open System.Threading.Tasks
open Constants

module Owner =

    let GetActorId (ownerId: OwnerId) = ActorId($"{ownerId}")

    type OwnerActor(host: ActorHost) =
        inherit Actor(host)

        let actorName = Constants.ActorName.Owner
        let log = loggerFactory.CreateLogger("Owner.Actor")
        let mutable actorStartTime = Instant.MinValue
        let mutable logScope: IDisposable = null
        let mutable currentCommand = String.Empty

        let dtoStateName = "ownerDtoState"
        let eventsStateName = "ownerEventsState"

        let mutable ownerDto = OwnerDto.Default
        let mutable ownerEvents: List<OwnerEvent> = null
        
        /// Indicates that the actor is in an undefined state, and should be reset.
        let mutable isDisposed = false

        let updateDto ownerEvent currentOwnerDto =
            let newOwnerDto =
                match ownerEvent with
                | Created (ownerId, ownerName) -> {OwnerDto.Default with OwnerId = ownerId; OwnerName = ownerName}
                | NameSet (ownerName) -> {currentOwnerDto with OwnerName = ownerName}
                | TypeSet (ownerType) -> {currentOwnerDto with OwnerType = ownerType}
                | SearchVisibilitySet (searchVisibility) -> {currentOwnerDto with SearchVisibility = searchVisibility}
                | DescriptionSet (description) -> {currentOwnerDto with Description = description}
                | LogicalDeleted (_, deleteReason) -> {currentOwnerDto with DeletedAt = Some (getCurrentInstant()); DeleteReason = deleteReason}
                | PhysicalDeleted -> currentOwnerDto // Do nothing because it's about to be deleted anyway.
                | Undeleted -> {currentOwnerDto with DeletedAt = None; DeleteReason = String.Empty}

            {newOwnerDto with UpdatedAt = Some (getCurrentInstant())}

        override this.OnActivateAsync() =
            let activateStartTime = getCurrentInstant()
            let stateManager = this.StateManager
            task {
                let mutable message = String.Empty
                let! retrievedDto = Storage.RetrieveState<OwnerDto> stateManager (dtoStateName)
                match retrievedDto with
                    | Some retrievedDto -> 
                        ownerDto <- retrievedDto
                        message <- "Retrieved from database."
                    | None -> 
                        ownerDto <- OwnerDto.Default
                        message <- "Not found in database."

                let duration_ms = getCurrentInstant().Minus(activateStartTime).TotalMilliseconds.ToString("F3")
                log.LogInformation("{CurrentInstant}: Activated {ActorType} {ActorId}. {message} Duration: {duration_ms}ms.", getCurrentInstantExtended(), actorName, host.Id, message, duration_ms)
            } :> Task

        member private this.SetMaintenanceReminder() =
            this.RegisterReminderAsync(ReminderType.Maintenance, Array.empty<byte>, TimeSpan.FromDays(7.0), TimeSpan.FromDays(7.0))

        member private this.UnregisterMaintenanceReminder() =
            this.UnregisterReminderAsync(ReminderType.Maintenance)

        member private this.OnFirstWrite() =
            task {
                //let! _ = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> this.SetMaintenanceReminder())
                ()
            }

        override this.OnPreActorMethodAsync(context) =
            if context.CallType = ActorCallType.ReminderMethod then
                log.LogInformation("{CurrentInstant}: Reminder {ActorName}.{MethodName} Id: {Id}.", getCurrentInstantExtended(), actorName, context.MethodName, this.Id)
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
                log.LogInformation("{CurrentInstant}: Finished {ActorName}.{MethodName}; Id: {Id}; Duration: {duration_ms}ms.", getCurrentInstantExtended(), actorName, context.MethodName, this.Id, duration_ms)
            else
                log.LogInformation("{CurrentInstant}: Finished {ActorName}.{MethodName}; Command: {Command}; Id: {Id}; Duration: {duration_ms}ms.", getCurrentInstantExtended(), actorName, context.MethodName, currentCommand, this.Id, duration_ms)    
            logScope.Dispose()
            Task.CompletedTask
            
        // This is essentially an object-oriented implementation of the Lazy<T> pattern. I was having issues with Lazy<T>, 
        //   and after a solid day wrestling with it, I dropped it and did this. Works a treat.
        member private this.OwnerEvents() =
            let stateManager = this.StateManager
            task {
                if ownerEvents = null then
                    let! retrievedEvents = Storage.RetrieveState<List<OwnerEvent>> stateManager eventsStateName
                    ownerEvents <- match retrievedEvents with
                                   | Some retrievedEvents -> retrievedEvents; 
                                   | None -> List<OwnerEvent>()
                return ownerEvents
            }

        member private this.ApplyEvent ownerEvent =
            let stateManager = this.StateManager
            task {
                try
                    let! ownerEvents = this.OwnerEvents()
                    if ownerEvents.Count = 0 then
                        do! this.OnFirstWrite()

                    ownerEvents.Add(ownerEvent)
                    do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(eventsStateName, ownerEvents))
                
                    ownerDto <- ownerDto |> updateDto ownerEvent.Event
                    do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(dtoStateName, ownerDto))

                    // Publish the event to the rest of the world.
                    let graceEvent = Events.GraceEvent.OwnerEvent ownerEvent
                    let message = serialize graceEvent
                    do! daprClient.PublishEventAsync(GracePubSubService, GraceEventStreamTopic, graceEvent)

                    let returnValue = GraceReturnValue.Create "Owner command succeeded." ownerEvent.Metadata.CorrelationId
                    returnValue.Properties.Add(nameof(OwnerId), $"{ownerDto.OwnerId}")
                    returnValue.Properties.Add(nameof(OwnerName), $"{ownerDto.OwnerName}")
                    returnValue.Properties.Add("EventType", $"{getDiscriminatedUnionFullName ownerEvent.Event}")
                    return Ok returnValue
                with ex ->
                    let graceError = GraceError.Create (OwnerError.getErrorMessage OwnerError.FailedWhileApplyingEvent) ownerEvent.Metadata.CorrelationId
                    let exceptionResponse = createExceptionResponse ex
                    graceError.Properties.Add("Exception details", exceptionResponse.``exception`` + exceptionResponse.innerException)
                    return Error graceError
            }

        /// Sends a DeleteLogical command to each organization provided.
        member private this.LogicalDeleteOrganizations(organizations: List<OrganizationDto>, metadata: EventMetadata, deleteReason: string) =
            // Loop through the orgs, sending a DeleteLogical command to each. If any of them fail, return the first error.
            task {
                let results = ConcurrentQueue<GraceResult<string>>()

                // Loop through each organization and send a DeleteLogical command to it.
                do! Parallel.ForEachAsync(organizations, Constants.ParallelOptions, (fun organization ct ->
                    ValueTask(task {
                        if organization.DeletedAt |> Option.isNone then
                            let organizationActor = actorProxyFactory.CreateActorProxy<IOrganizationActor> (ActorId($"{organization.OrganizationId}"), Constants.ActorName.Organization)
                            let! result = organizationActor.Handle (Commands.Organization.DeleteLogical (true, $"Cascaded from deleting owner. ownerId: {ownerDto.OwnerId}; ownerName: {ownerDto.OwnerName}; deleteReason: {deleteReason}")) metadata
                            results.Enqueue(result)
                    })))

                // Check if any of the results were errors. If so, return the first one.
                let overallResult = results |> Seq.tryPick (fun result -> match result with | Ok _ -> None | Error error -> Some(error))

                match overallResult with
                | None -> return Ok ()
                | Some error -> return Error error
            }

        /// Sets a Dapr Actor reminder to perform a physical deletion of this owner.
        member private this.SchedulePhysicalDeletion(deleteReason) =
            this.RegisterReminderAsync(ReminderType.PhysicalDeletion, convertToByteArray deleteReason, Constants.DefaultPhysicalDeletionReminderTime, TimeSpan.FromMilliseconds(-1)).Result |> ignore

        interface IOwnerActor with
            member this.Exists (correlationId) = ownerDto.UpdatedAt.IsSome |> returnTask

            member this.IsDeleted (correlationId) = ownerDto.DeletedAt.IsSome |> returnTask

            member this.Get (correlationId) = ownerDto |> returnTask

            member this.OrganizationExists organizationName correlationId = ownerDto.Organizations.ContainsValue(OrganizationName organizationName) |> returnTask

            member this.ListOrganizations (correlationId) = ownerDto.Organizations :> IReadOnlyDictionary<OrganizationId, OrganizationName> |> returnTask

            member this.Handle command metadata =
                let isValid command (metadata: EventMetadata) =
                    task {
                        let! ownerEvents = this.OwnerEvents()
                        if ownerEvents.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId) then
                            return Error (GraceError.Create (OwnerError.getErrorMessage DuplicateCorrelationId) metadata.CorrelationId)
                        else
                            match command with 
                            | OwnerCommand.Create (_, _) ->
                                match ownerDto.UpdatedAt with
                                | Some _ -> return Error (GraceError.Create (OwnerError.getErrorMessage OwnerIdAlreadyExists) metadata.CorrelationId)
                                | None -> return Ok command
                            | _ -> 
                                match ownerDto.UpdatedAt with
                                | Some _ -> return Ok command
                                | None -> return Error (GraceError.Create (OwnerError.getErrorMessage OwnerIdDoesNotExist) metadata.CorrelationId)
                    }

                let processCommand (command: OwnerCommand) (metadata: EventMetadata) =
                    task {
                        try
                            let! eventResult = 
                                task {
                                    match command with
                                    | OwnerCommand.Create (ownerId, ownerName) -> return Ok (OwnerEventType.Created (ownerId, ownerName))
                                    | OwnerCommand.SetName ownerName -> return Ok (OwnerEventType.NameSet ownerName)
                                    | OwnerCommand.SetType ownerType -> return Ok (OwnerEventType.TypeSet ownerType)
                                    | OwnerCommand.SetSearchVisibility searchVisibility -> return Ok (OwnerEventType.SearchVisibilitySet searchVisibility)
                                    | OwnerCommand.SetDescription description -> return Ok (OwnerEventType.DescriptionSet description)
                                    | OwnerCommand.DeleteLogical (force, deleteReason) ->
                                        // Get the list of organizations that aren't already deleted.
                                        let! organizations = getOrganizations ownerDto.OwnerId Int32.MaxValue false

                                        // If the owner contains active organizations, and the force flag is not set, return an error.
                                        if not <| force && organizations.Count > 0 && organizations.Any(fun organization -> organization.DeletedAt |> Option.isNone) then
                                            return Error (GraceError.CreateWithMetadata (OwnerError.getErrorMessage OwnerContainsOrganizations) metadata.CorrelationId metadata.Properties)
                                        else
                                            // Delete the organizations.
                                            match! this.LogicalDeleteOrganizations(organizations, metadata, deleteReason) with
                                            | Ok _ -> 
                                                this.SchedulePhysicalDeletion(deleteReason)
                                                return Ok (LogicalDeleted (force, deleteReason))
                                            | Error error -> return Error error
                                    | OwnerCommand.DeletePhysical ->
                                        isDisposed <- true
                                        return Ok (OwnerEventType.PhysicalDeleted)
                                    | OwnerCommand.Undelete -> return Ok (OwnerEventType.Undeleted)
                                }

                            match eventResult with
                            | Ok event -> return! this.ApplyEvent {Event = event; Metadata = metadata}
                            | Error error -> return Error error
                        with ex ->
                            return Error (GraceError.CreateWithMetadata $"{createExceptionResponse ex}" metadata.CorrelationId metadata.Properties)
                    }

                task {
                    currentCommand <- getDiscriminatedUnionCaseName command
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
                        // Delete saved state for this actor.
                        let! deletedDtoState = stateManager.TryRemoveStateAsync(dtoStateName)
                        let! deletedEventsState = stateManager.TryRemoveStateAsync(eventsStateName)

                        // Mark the actor as disposed, in case someone tries to use it before Dapr GC's it.
                        isDisposed <- true
                        log.LogInformation("{currentInstant}: Deleted physical state for owner; OwnerId: {ownerId}; OwnerName: {ownerName}; deletedDtoState: {deletedDtoState}; deletedEventsState: {deletedEventsState}.", 
                            getCurrentInstantExtended(), ownerDto.OwnerId, ownerDto.OwnerName, deletedDtoState, deletedEventsState)

                        // Set all values to default.
                        ownerDto <- OwnerDto.Default
                    } :> Task
                | _ -> failwith "Unknown reminder type."
