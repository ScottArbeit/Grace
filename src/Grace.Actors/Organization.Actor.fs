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

        member private this.OnFirstWrite() =
            task {
                let! _ = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> this.SetMaintenanceReminder())
                ()
            }

        override this.OnPreActorMethodAsync(context) =
            actorStartTime <- getCurrentInstant()
            logScope <- log.BeginScope("Actor {actorName}", actorName)
            log.LogInformation("{CurrentInstant}: Starting actor {ActorName}; Method: {MethodName}.", getCurrentInstantExtended(), actorName, context.MethodName)
            Task.CompletedTask
            
        override this.OnPostActorMethodAsync(context) =
            let duration = getCurrentInstant().Minus(actorStartTime)
            log.LogInformation("{CurrentInstant}: Finished actor {actorName}; Method: {methodName}; Duration: {duration}ms.", getCurrentInstantExtended(), actorName, context.MethodName, duration.TotalMilliseconds.ToString("F3"))
            logScope.Dispose()
            Task.CompletedTask
            
        member private this.OrganizationEvents with get() = (
            let stateManager = this.StateManager
            if organizationEvents = null then            
                let retrievedEvents = (Storage.RetrieveState<List<OrganizationEvent>> stateManager eventsStateName).Result
                organizationEvents <- match retrievedEvents with
                                       | Some retrievedEvents -> retrievedEvents; 
                                       | None -> List<OrganizationEvent>()
            
            organizationEvents
        )

        member private this.ApplyEvent organizationEvent =
            let stateManager = this.StateManager
            task {
                try
                    if this.OrganizationEvents.Count = 0 then
                        do! this.OnFirstWrite()

                    this.OrganizationEvents.Add(organizationEvent)
                    do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(eventsStateName, this.OrganizationEvents))
                
                    // Publish the event to the rest of the world.
                    let graceEvent = Events.GraceEvent.OrganizationEvent organizationEvent
                    let message = JsonSerializer.Serialize<Events.GraceEvent>(graceEvent, options = Constants.JsonSerializerOptions)
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
                let isValid (command: OrganizationCommand) (metadata: EventMetadata)=
                    if this.OrganizationEvents.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId) then
                        Error (GraceError.Create (OrganizationError.getErrorMessage DuplicateCorrelationId) metadata.CorrelationId)
                    else
                        match command with 
                        | OrganizationCommand.Create (organizationId, organizationName, ownerId) ->
                            match organizationDto.UpdatedAt with
                            | Some _ -> Error (GraceError.Create (OrganizationError.getErrorMessage OrganizationAlreadyExists) metadata.CorrelationId) 
                            | None -> Ok command
                        | _ -> 
                            match organizationDto.UpdatedAt with
                            | Some _ -> Ok command
                            | None -> Error (GraceError.Create (OrganizationError.getErrorMessage OrganizationIdDoesNotExist) metadata.CorrelationId) 

                let processCommand (command: OrganizationCommand) metadata =
                    task {
                        try
                            let event = 
                                match command with
                                | OrganizationCommand.Create (organizationId, organizationName, ownerId) -> OrganizationEventType.Created (organizationId, organizationName, ownerId)
                                | OrganizationCommand.SetName (organizationName) -> OrganizationEventType.NameSet (organizationName)
                                | OrganizationCommand.SetType (organizationType) -> OrganizationEventType.TypeSet (organizationType)
                                | OrganizationCommand.SetSearchVisibility (searchVisibility) -> OrganizationEventType.SearchVisibilitySet (searchVisibility)
                                | OrganizationCommand.SetDescription (description) -> OrganizationEventType.DescriptionSet (description)
                                | OrganizationCommand.AddRepository (repositoryId, repositoryName) -> OrganizationEventType.RepositoryAdded (repositoryId, repositoryName)
                                | OrganizationCommand.DeleteRepository repositoryId -> OrganizationEventType.RepositoryDeleted (repositoryId)
                                | OrganizationCommand.DeleteLogical (force, deleteReason) -> OrganizationEventType.LogicalDeleted (force, deleteReason)
                                | OrganizationCommand.DeletePhysical ->
                                    task {
                                        do! this.SchedulePhysicalDeletion()
                                    } |> Async.AwaitTask |> ignore
                                    OrganizationEventType.PhysicalDeleted
                                | OrganizationCommand.Undelete -> OrganizationEventType.Undeleted
                            
                            return! this.ApplyEvent {Event = event; Metadata = metadata}
                        with ex ->
                            return Error (GraceError.Create $"{createExceptionResponse ex}" metadata.CorrelationId)
                    }

                match isValid command metadata with
                | Ok command -> processCommand command metadata 
                | Error error -> Task.FromResult(Error error)
