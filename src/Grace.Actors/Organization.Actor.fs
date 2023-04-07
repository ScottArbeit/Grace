namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open Grace.Actors.Commands.Organization
open Grace.Actors.Constants
open Grace.Actors.Events.Organization
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Dto.Organization
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors.Organization
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Generic
open System.Runtime.Serialization
open System.Text.Json
open System.Threading.Tasks

module Organization =

    let GetActorId (organizationId: OrganizationId) = ActorId($"{organizationId}")

    type OrganizationActor(host: ActorHost) = 
        inherit Actor(host)

        let actorName = Constants.ActorName.Organization
        let log = host.LoggerFactory.CreateLogger(actorName)
        let mutable actorStartTime = Instant.MinValue
        let mutable logScope: IDisposable = null
        let mutable currentCommand = String.Empty

        let dtoStateName = "organizationDtoState"
        let eventsStateName = "organizationEventsState"

        let mutable organizationDto = OrganizationDto.Default
        let mutable organizationEvents: List<OrganizationEvent> = null
        let logger = host.LoggerFactory.CreateLogger(nameof(OrganizationActor))

        let updateDto (organizationEventType: OrganizationEventType) currentOrganizationDto =
            let newOrganizationDto = 
                match organizationEventType with
                | Created (organizationId, organizationName, ownerId) -> {OrganizationDto.Default with OrganizationId = organizationId; OrganizationName = organizationName; OwnerId = ownerId}
                | NameSet (organizationName) -> {currentOrganizationDto with OrganizationName = organizationName}
                | TypeSet (organizationType) -> {currentOrganizationDto with OrganizationType = organizationType}
                | SearchVisibilitySet (searchVisibility) -> {currentOrganizationDto with SearchVisibility = searchVisibility}
                | DescriptionSet (description) -> {currentOrganizationDto with Description = description}
                | RepositoryAdded (repositoryId, repositoryName) -> currentOrganizationDto.Repositories.Add(repositoryId, repositoryName); currentOrganizationDto
                | RepositoryDeleted (repositoryId) -> currentOrganizationDto.Repositories.Remove(repositoryId) |> ignore; currentOrganizationDto
                | LogicalDeleted (_, deleteReason) -> {currentOrganizationDto with DeleteReason = deleteReason; DeletedAt = Some (getCurrentInstant())}
                | PhysicalDeleted _ -> currentOrganizationDto // Do nothing because it's about to be deleted anyway.
                | Undeleted -> {currentOrganizationDto with DeletedAt = None; DeleteReason = String.Empty}

            {newOrganizationDto with UpdatedAt = Some (getCurrentInstant())}

        override this.OnActivateAsync() =
            let stateManager = this.StateManager
            logger.LogInformation("{CurrentInstant} Activated {ActorType} {ActorId}.", getCurrentInstantExtended(), this.GetType().Name, host.Id)
            task {
                let! retrievedDto = Storage.RetrieveState<OrganizationDto> stateManager dtoStateName
                match retrievedDto with
                    | Some retrievedDto -> organizationDto <- retrievedDto
                    | None -> organizationDto <- OrganizationDto.Default
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
        member private this.OrganizationEvents() =
            let stateManager = this.StateManager
            task {
                if organizationEvents = null then            
                    let! retrievedEvents = (Storage.RetrieveState<List<OrganizationEvent>> stateManager eventsStateName)
                    organizationEvents <- match retrievedEvents with
                                           | Some retrievedEvents -> retrievedEvents; 
                                           | None -> List<OrganizationEvent>()
            
                return organizationEvents
            }

        member private this.ApplyEvent organizationEvent =
            let stateManager = this.StateManager
            task {
                try
                    let! organizationEvents = this.OrganizationEvents()
                    if organizationEvents.Count = 0 then
                        do! this.OnFirstWrite()

                    organizationEvents.Add(organizationEvent)
                    do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(eventsStateName, this.OrganizationEvents))
                
                    // Publish the event to the rest of the world.
                    let graceEvent = Events.GraceEvent.OrganizationEvent organizationEvent
                    let message = graceEvent |> serialize
                    do! daprClient.PublishEventAsync(GracePubSubService, GraceEventStreamTopic, message)

                    organizationDto <- organizationDto |> updateDto organizationEvent.Event
                    do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(dtoStateName, organizationDto))
                    let returnValue = GraceReturnValue.Create "Organization command succeeded." organizationEvent.Metadata.CorrelationId
                    returnValue.Properties.Add(nameof(OwnerId), $"{organizationDto.OwnerId}")
                    returnValue.Properties.Add(nameof(OrganizationId), $"{organizationDto.OrganizationId}")
                    returnValue.Properties.Add(nameof(OrganizationName), $"{organizationDto.OrganizationName}")
                    returnValue.Properties.Add("EventType", $"{discriminatedUnionFullNameToString organizationEvent.Event}")
                    return Ok returnValue
                with ex -> 
                    let graceError = GraceError.Create (OrganizationError.getErrorMessage OrganizationError.FailedWhileApplyingEvent) organizationEvent.Metadata.CorrelationId
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

        interface IOrganizationActor with
            member this.Exists() =
                Task.FromResult(if organizationDto.UpdatedAt.IsSome then true else false)

            member this.IsDeleted() =
                Task.FromResult(if organizationDto.DeletedAt.IsSome then true else false)

            member this.GetDto() =
                Task.FromResult(organizationDto)

            member this.RepositoryExists repositoryName = 
                this.Logger.LogInformation("Inside F(x)")
                Task.FromResult(false)

            member this.ListRepositories() =
                Task.FromResult(organizationDto.Repositories :> IReadOnlyDictionary<RepositoryId, RepositoryName>)

            member this.Handle (command: OrganizationCommand) metadata =
                let isValid (command: OrganizationCommand) (metadata: EventMetadata) =
                    task {
                        let! organizationEvents = this.OrganizationEvents()
                        if organizationEvents.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId) then
                            return Error (GraceError.Create (OrganizationError.getErrorMessage DuplicateCorrelationId) metadata.CorrelationId)
                        else
                            match command with 
                            | OrganizationCommand.Create (organizationId, organizationName, ownerId) ->
                                match organizationDto.UpdatedAt with
                                | Some _ -> return Error (GraceError.Create (OrganizationError.getErrorMessage OrganizationAlreadyExists) metadata.CorrelationId) 
                                | None -> return Ok command
                            | _ -> 
                                match organizationDto.UpdatedAt with
                                | Some _ -> return Ok command
                                | None -> return Error (GraceError.Create (OrganizationError.getErrorMessage OrganizationIdDoesNotExist) metadata.CorrelationId) 
                    }

                let processCommand (command: OrganizationCommand) metadata =
                    task {
                        try
                            let! event = 
                                task {
                                    match command with
                                    | OrganizationCommand.Create (organizationId, organizationName, ownerId) -> return OrganizationEventType.Created (organizationId, organizationName, ownerId)
                                    | OrganizationCommand.SetName (organizationName) -> return OrganizationEventType.NameSet (organizationName)
                                    | OrganizationCommand.SetType (organizationType) -> return OrganizationEventType.TypeSet (organizationType)
                                    | OrganizationCommand.SetSearchVisibility (searchVisibility) -> return OrganizationEventType.SearchVisibilitySet (searchVisibility)
                                    | OrganizationCommand.SetDescription (description) -> return OrganizationEventType.DescriptionSet (description)
                                    | OrganizationCommand.AddRepository (repositoryId, repositoryName) -> return OrganizationEventType.RepositoryAdded (repositoryId, repositoryName)
                                    | OrganizationCommand.DeleteRepository repositoryId -> return OrganizationEventType.RepositoryDeleted (repositoryId)
                                    | OrganizationCommand.DeleteLogical (force, deleteReason) ->
                                        //do! this.UnregisterMaintenanceReminder()
                                        return OrganizationEventType.LogicalDeleted (force, deleteReason)
                                    | OrganizationCommand.DeletePhysical ->
                                        do! this.SchedulePhysicalDeletion()
                                        return OrganizationEventType.PhysicalDeleted
                                    | OrganizationCommand.Undelete -> return OrganizationEventType.Undeleted
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