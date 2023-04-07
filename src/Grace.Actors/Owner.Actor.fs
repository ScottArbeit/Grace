namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open Grace.Actors.Commands.Owner
open Grace.Actors.Services
open Grace.Actors.Events.Owner
open Grace.Actors.Interfaces
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Dto.Owner
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors.Owner
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Generic
open System.Runtime.Serialization
open System.Text.Json
open System.Threading.Tasks

module Owner =

    let GetActorId (ownerId: OwnerId) = ActorId($"{ownerId}")

    type OwnerActor(host: ActorHost) = 
        inherit Actor(host)

        let actorName = Constants.ActorName.Owner
        let log = host.LoggerFactory.CreateLogger(actorName)
        let mutable actorStartTime = Instant.MinValue
        let mutable logScope: IDisposable = null
        let mutable currentCommand = String.Empty

        let dtoStateName = "ownerDtoState"
        let eventsStateName = "ownerEventsState"

        let mutable ownerDto = OwnerDto.Default
        let mutable ownerEvents: List<OwnerEvent> = null

        let updateDto ownerEvent currentOwnerDto =
            let newOwnerDto =
                match ownerEvent with
                | Created (ownerId, ownerName) -> {OwnerDto.Default with OwnerId = ownerId; OwnerName = ownerName}
                | NameSet (ownerName) -> {currentOwnerDto with OwnerName = ownerName}
                | TypeSet (ownerType) -> {currentOwnerDto with OwnerType = ownerType}
                | SearchVisibilitySet (searchVisibility) -> {currentOwnerDto with SearchVisibility = searchVisibility}
                | DescriptionSet (description) -> {currentOwnerDto with Description = description}
                | OrganizationAdded (organizationId, organizationName) -> currentOwnerDto.Organizations.TryAdd(organizationId, organizationName) |> ignore; currentOwnerDto
                | OrganizationDeleted (organizationId) -> currentOwnerDto.Organizations.Remove(organizationId) |> ignore; currentOwnerDto
                | LogicalDeleted (_, deleteReason) -> {currentOwnerDto with DeletedAt = Some (getCurrentInstant()); DeleteReason = deleteReason}
                | PhysicalDeleted _ -> currentOwnerDto // Do nothing because it's about to be deleted anyway.
                | Undeleted -> {currentOwnerDto with DeletedAt = None; DeleteReason = String.Empty}

            {newOwnerDto with UpdatedAt = Some (getCurrentInstant())}

        override this.OnActivateAsync() =
            let stateManager = this.StateManager
            log.LogInformation("{CurrentInstant}: Activated {ActorType} {ActorId}.", getCurrentInstantExtended(), this.GetType().Name, host.Id)
            task {
                let! retrievedDto = Storage.RetrieveState<OwnerDto> stateManager (dtoStateName)
                match retrievedDto with
                    | Some retrievedDto -> ownerDto <- retrievedDto
                    | None -> ownerDto <- OwnerDto.Default
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

        override this.OnPreActorMethodAsync(context) =
            actorStartTime <- getCurrentInstant()
            logScope <- log.BeginScope("Actor {actorName}", actorName)
            currentCommand <- String.Empty
            //log.LogInformation("{CurrentInstant}: Started {ActorName}.{MethodName} Id: {Id}.", getCurrentInstantExtended(), actorName, context.MethodName, this.Id.GetId())
            Task.CompletedTask
            
        override this.OnPostActorMethodAsync(context) =
            let duration = getCurrentInstant().Minus(actorStartTime)
            if String.IsNullOrEmpty(currentCommand) then
                log.LogInformation("{CurrentInstant}: Finished {ActorName}.{MethodName}; Id: {Id}; Duration: {duration}ms.", $"{getCurrentInstantExtended(),-28}", actorName, context.MethodName, this.Id.GetId(), duration.TotalMilliseconds.ToString("F3"))
            else
                log.LogInformation("{CurrentInstant}: Finished {ActorName}.{MethodName}; Command: {Command}; Id: {Id}; Duration: {duration}ms.", $"{getCurrentInstantExtended(),-28}", actorName, context.MethodName, currentCommand, this.Id.GetId(), duration.TotalMilliseconds.ToString("F3"))
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
                    let message = graceEvent |> serialize
                    do! daprClient.PublishEventAsync(GracePubSubService, GraceEventStreamTopic, message)

                    let returnValue = GraceReturnValue.Create "Owner command succeeded." ownerEvent.Metadata.CorrelationId
                    returnValue.Properties.Add(nameof(OwnerId), $"{ownerDto.OwnerId}")
                    returnValue.Properties.Add(nameof(OwnerName), $"{ownerDto.OwnerName}")
                    returnValue.Properties.Add("EventType", $"{discriminatedUnionFullNameToString ownerEvent.Event}")
                    return Ok returnValue
                with ex ->
                    let graceError = GraceError.Create (OwnerError.getErrorMessage OwnerError.FailedWhileApplyingEvent) ownerEvent.Metadata.CorrelationId
                    let exceptionResponse = createExceptionResponse ex
                    graceError.Properties.Add("Exception details", exceptionResponse.``exception`` + exceptionResponse.innerException)
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

        interface IOwnerActor with
            member this.Exists() =
                Task.FromResult(if ownerDto.UpdatedAt.IsSome then true else false)

            member this.IsDeleted() =
                Task.FromResult(if ownerDto.DeletedAt.IsSome then true else false)

            member this.GetDto() =
                Task.FromResult(ownerDto)

            member this.OrganizationExists organizationName = 
                Task.FromResult(ownerDto.Organizations.ContainsValue(OrganizationName organizationName))

            member this.ListOrganizations() =
                Task.FromResult(ownerDto.Organizations :> IReadOnlyDictionary<OrganizationId, OrganizationName>)

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

                let processCommand (command: OwnerCommand) metadata =
                    task {
                        try
                            let! event = 
                                task {
                                    match command with
                                    | OwnerCommand.Create (ownerId, ownerName) -> return OwnerEventType.Created (ownerId, ownerName)
                                    | OwnerCommand.SetName ownerName -> return OwnerEventType.NameSet ownerName
                                    | OwnerCommand.SetType ownerType -> return OwnerEventType.TypeSet ownerType
                                    | OwnerCommand.SetSearchVisibility searchVisibility -> return OwnerEventType.SearchVisibilitySet searchVisibility
                                    | OwnerCommand.SetDescription description -> return OwnerEventType.DescriptionSet description
                                    | OwnerCommand.AddOrganization (repositoryId, repositoryName) -> return OwnerEventType.OrganizationAdded (repositoryId, repositoryName)
                                    | OwnerCommand.DeleteOrganization repositoryId -> return OwnerEventType.OrganizationDeleted repositoryId
                                    | OwnerCommand.DeleteLogical (force, deleteReason) ->
                                        //do! this.UnregisterMaintenanceReminder()
                                        return OwnerEventType.LogicalDeleted (force, deleteReason)
                                    | OwnerCommand.DeletePhysical ->
                                        do! this.SchedulePhysicalDeletion()
                                        return OwnerEventType.PhysicalDeleted
                                    | OwnerCommand.Undelete -> return OwnerEventType.Undeleted
                                }
                            return! this.ApplyEvent {Event = event; Metadata = metadata}
                        with ex ->
                            return Error (GraceError.Create $"{createExceptionResponse ex}" metadata.CorrelationId)
                    }

                task {
                    currentCommand <- discriminatedUnionCaseNameToString command
                    match! isValid command metadata with
                    | Ok command -> return! processCommand command metadata
                    | Error error -> return Error error
                }
