namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open Grace.Actors.Commands.Organization
open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Events.Organization
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Dto.Organization
open Grace.Shared.Dto.Repository
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors.Organization
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Linq
open System.Runtime.Serialization
open System.Text.Json
open System.Threading.Tasks

module Organization =

    type PhysicalDeletionReminderState = (DeleteReason * CorrelationId)

    let GetActorId (organizationId: OrganizationId) = ActorId($"{organizationId}")

    type OrganizationActor(host: ActorHost) =
        inherit Actor(host)

        let actorName = ActorName.Organization
        let log = loggerFactory.CreateLogger("Organization.Actor")
        let mutable actorStartTime = Instant.MinValue
        let mutable logScope: IDisposable = null
        let mutable currentCommand = String.Empty

        let dtoStateName = StateName.OrganizationDto
        let eventsStateName = StateName.Organization

        let mutable organizationDto = OrganizationDto.Default
        let mutable organizationEvents: List<OrganizationEvent> = null

        /// Indicates that the actor is in an undefined state, and should be reset.
        let mutable isDisposed = false

        let updateDto organizationEvent currentOrganizationDto =
            let newOrganizationDto =
                match organizationEvent.Event with
                | Created(organizationId, organizationName, ownerId) ->
                    { OrganizationDto.Default with
                        OrganizationId = organizationId
                        OrganizationName = organizationName
                        OwnerId = ownerId
                        CreatedAt = organizationEvent.Metadata.Timestamp }
                | NameSet(organizationName) -> { currentOrganizationDto with OrganizationName = organizationName }
                | TypeSet(organizationType) -> { currentOrganizationDto with OrganizationType = organizationType }
                | SearchVisibilitySet(searchVisibility) -> { currentOrganizationDto with SearchVisibility = searchVisibility }
                | DescriptionSet(description) -> { currentOrganizationDto with Description = description }
                | LogicalDeleted(_, deleteReason) -> { currentOrganizationDto with DeleteReason = deleteReason; DeletedAt = Some(getCurrentInstant ()) }
                | PhysicalDeleted -> currentOrganizationDto // Do nothing because it's about to be deleted anyway.
                | Undeleted -> { currentOrganizationDto with DeletedAt = None; DeleteReason = String.Empty }

            { newOrganizationDto with UpdatedAt = Some organizationEvent.Metadata.Timestamp }

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync() =
            let activateStartTime = getCurrentInstant ()
            let stateManager = this.StateManager

            task {
                let mutable message = String.Empty
                let! retrievedDto = Storage.RetrieveState<OrganizationDto> stateManager dtoStateName

                let correlationId =
                    match memoryCache.GetCorrelationIdEntry this.Id with
                    | Some correlationId -> correlationId
                    | None -> String.Empty

                match retrievedDto with
                | Some retrievedDto ->
                    organizationDto <- retrievedDto
                    message <- "Retrieved from database"
                | None ->
                    organizationDto <- OrganizationDto.Default
                    message <- "Not found in database"

                let duration_ms = getPaddedDuration_ms activateStartTime

                log.LogInformation(
                    "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Activated {ActorType} {ActorId}. {message}.",
                    getCurrentInstantExtended (),
                    getMachineName,
                    duration_ms,
                    correlationId,
                    actorName,
                    host.Id,
                    message
                )
            }
            :> Task

        member private this.SetMaintenanceReminder() =
            this.RegisterReminderAsync(ReminderType.Maintenance, Array.empty<byte>, TimeSpan.FromDays(7.0), TimeSpan.FromDays(7.0))

        member private this.UnregisterMaintenanceReminder() = this.UnregisterReminderAsync(ReminderType.Maintenance)

        member private this.OnFirstWrite() =
            task {
                //let! _ = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> this.SetMaintenanceReminder())
                ()
            }

        override this.OnPreActorMethodAsync(context) =
            actorStartTime <- getCurrentInstant ()
            this.correlationId <- String.Empty
            logScope <- log.BeginScope("Actor {actorName}", actorName)
            currentCommand <- String.Empty

            log.LogTrace(
                "{CurrentInstant}: Started {ActorName}.{MethodName} OrganizationId: {Id}.",
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

            if String.IsNullOrEmpty(currentCommand) then
                log.LogInformation(
                    "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {ActorName}.{MethodName}; OrganizationId: {Id}.",
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
                    "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {ActorName}.{MethodName}; Command: {Command}; OrganizationId: {Id}.",
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

        // This is essentially an object-oriented implementation of the Lazy<T> pattern. I was having issues with Lazy<T>,
        //   and after a solid day wrestling with it, I dropped it and did this. Works a treat.
        member private this.OrganizationEvents() =
            let stateManager = this.StateManager

            task {
                if organizationEvents = null then
                    let! retrievedEvents = (Storage.RetrieveState<List<OrganizationEvent>> stateManager eventsStateName)

                    organizationEvents <-
                        match retrievedEvents with
                        | Some retrievedEvents -> retrievedEvents
                        | None -> List<OrganizationEvent>()

                return organizationEvents
            }

        member private this.ApplyEvent organizationEvent =
            let stateManager = this.StateManager

            task {
                try
                    let! organizationEvents = this.OrganizationEvents()

                    if organizationEvents.Count = 0 then do! this.OnFirstWrite()

                    organizationEvents.Add(organizationEvent)

                    do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(eventsStateName, organizationEvents))

                    // Publish the event to the rest of the world.
                    let graceEvent = Events.GraceEvent.OrganizationEvent organizationEvent
                    let message = serialize graceEvent
                    do! daprClient.PublishEventAsync(GracePubSubService, GraceEventStreamTopic, graceEvent)

                    // Update the Dto based on the current event.
                    organizationDto <- organizationDto |> updateDto organizationEvent

                    do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(dtoStateName, organizationDto))

                    let returnValue =
                        (GraceReturnValue.Create "Organization command succeeded." organizationEvent.Metadata.CorrelationId)
                            .enhance(nameof (OwnerId), $"{organizationDto.OwnerId}")
                            .enhance(nameof (OrganizationId), $"{organizationDto.OrganizationId}")
                            .enhance(nameof (OrganizationName), $"{organizationDto.OrganizationName}")
                            .enhance (nameof (OrganizationEventType), $"{getDiscriminatedUnionFullName organizationEvent.Event}")

                    return Ok returnValue
                with ex ->
                    let exceptionResponse = createExceptionResponse ex

                    let graceError =
                        GraceError.Create
                            (OrganizationError.getErrorMessage OrganizationError.FailedWhileApplyingEvent)
                            organizationEvent.Metadata.CorrelationId

                    graceError
                        .enhance("Exception details", exceptionResponse.``exception`` + exceptionResponse.innerException)
                        .enhance(nameof (OrganizationId), $"{organizationDto.OrganizationId}")
                        .enhance(nameof (OrganizationName), $"{organizationDto.OrganizationName}")
                        .enhance (nameof (OrganizationEventType), $"{getDiscriminatedUnionFullName organizationEvent.Event}")
                    |> ignore

                    return Error graceError
            }

        /// Deletes all of the repositories provided, by sending a DeleteLogical command to each one.
        member private this.LogicalDeleteRepositories(repositories: List<RepositoryDto>, metadata: EventMetadata, deleteReason: DeleteReason) =
            task {
                let results = ConcurrentQueue<GraceResult<string>>()

                // Loop through each repository and send a DeleteLogical command to it.
                do!
                    Parallel.ForEachAsync(
                        repositories,
                        Constants.ParallelOptions,
                        (fun repository ct ->
                            ValueTask(
                                task {
                                    if repository.DeletedAt |> Option.isNone then
                                        let repositoryActor = Repository.CreateActorProxy repository.RepositoryId metadata.CorrelationId

                                        let! result =
                                            repositoryActor.Handle
                                                (Commands.Repository.DeleteLogical(
                                                    true,
                                                    $"Cascaded from deleting organization. ownerId: {organizationDto.OwnerId}; organizationId: {organizationDto.OrganizationId}; organizationName: {organizationDto.OrganizationName}; deleteReason: {deleteReason}"
                                                ))
                                                metadata

                                        results.Enqueue(result)
                                }
                            ))
                    )

                // Check if any of the commands failed, and if so, return the first error.
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

        member private this.SchedulePhysicalDeletion(deleteReason, correlationId) =
            let (tuple: PhysicalDeletionReminderState) = (deleteReason, correlationId)

            this.RegisterReminderAsync(
                ReminderType.PhysicalDeletion,
                toByteArray tuple,
                Constants.DefaultPhysicalDeletionReminderTime,
                TimeSpan.FromMilliseconds(-1)
            )

        interface IOrganizationActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId
                Task.FromResult(if organizationDto.UpdatedAt.IsSome then true else false)

            member this.IsDeleted correlationId =
                this.correlationId <- correlationId
                Task.FromResult(if organizationDto.DeletedAt.IsSome then true else false)

            member this.Get correlationId =
                this.correlationId <- correlationId
                Task.FromResult(organizationDto)

            member this.RepositoryExists repositoryName correlationId =
                task {
                    this.correlationId <- correlationId
                    let actorProxy = RepositoryName.CreateActorProxy organizationDto.OwnerId organizationDto.OrganizationId repositoryName correlationId

                    match! actorProxy.GetRepositoryId(correlationId) with
                    | Some repositoryId -> return true
                    | None -> return false
                }

            member this.ListRepositories correlationId =
                task {
                    this.correlationId <- correlationId
                    let! organizationDtos = Services.getRepositories organizationDto.OrganizationId Int32.MaxValue false
                    let dict = organizationDtos.ToDictionary((fun repo -> repo.RepositoryId), (fun repo -> repo.RepositoryName))

                    return dict :> IReadOnlyDictionary<RepositoryId, RepositoryName>
                }
            //Task.FromResult(organizationDto.Repositories :> IReadOnlyDictionary<RepositoryId, RepositoryName>)

            member this.Handle (command: OrganizationCommand) metadata =
                let isValid (command: OrganizationCommand) (metadata: EventMetadata) =
                    task {
                        let! organizationEvents = this.OrganizationEvents()

                        if organizationEvents.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId) then
                            return Error(GraceError.Create (OrganizationError.getErrorMessage DuplicateCorrelationId) metadata.CorrelationId)
                        else
                            match command with
                            | OrganizationCommand.Create(organizationId, organizationName, ownerId) ->
                                match organizationDto.UpdatedAt with
                                | Some _ ->
                                    return Error(GraceError.Create (OrganizationError.getErrorMessage OrganizationIdAlreadyExists) metadata.CorrelationId)
                                | None -> return Ok command
                            | _ ->
                                match organizationDto.UpdatedAt with
                                | Some _ -> return Ok command
                                | None -> return Error(GraceError.Create (OrganizationError.getErrorMessage OrganizationIdDoesNotExist) metadata.CorrelationId)
                    }

                let processCommand (command: OrganizationCommand) (metadata: EventMetadata) =
                    task {
                        try
                            let! eventResult =
                                task {
                                    match command with
                                    | OrganizationCommand.Create(organizationId, organizationName, ownerId) ->
                                        return Ok(OrganizationEventType.Created(organizationId, organizationName, ownerId))
                                    | OrganizationCommand.SetName(organizationName) -> return Ok(OrganizationEventType.NameSet(organizationName))
                                    | OrganizationCommand.SetType(organizationType) -> return Ok(OrganizationEventType.TypeSet(organizationType))
                                    | OrganizationCommand.SetSearchVisibility(searchVisibility) ->
                                        return Ok(OrganizationEventType.SearchVisibilitySet(searchVisibility))
                                    | OrganizationCommand.SetDescription(description) -> return Ok(OrganizationEventType.DescriptionSet(description))
                                    | OrganizationCommand.DeleteLogical(force, deleteReason) ->
                                        // Get the list of branches that aren't already deleted.
                                        let! repositories = getRepositories organizationDto.OrganizationId Int32.MaxValue false

                                        // If the organization contains repositories, and any of them isn't already deleted, and the force flag is not set, return an error.
                                        if
                                            not <| force
                                            && repositories.Count > 0
                                            && repositories.Any(fun repository -> repository.DeletedAt |> Option.isNone)
                                        then
                                            return
                                                Error(
                                                    GraceError.CreateWithMetadata
                                                        (OrganizationError.getErrorMessage OrganizationContainsRepositories)
                                                        metadata.CorrelationId
                                                        metadata.Properties
                                                )
                                        else
                                            // Delete the repositories.
                                            match! this.LogicalDeleteRepositories(repositories, metadata, deleteReason) with
                                            | Ok _ ->
                                                let! deletionReminder = this.SchedulePhysicalDeletion(deleteReason, metadata.CorrelationId)
                                                return Ok(LogicalDeleted(force, deleteReason))
                                            | Error error -> return Error error
                                    | OrganizationCommand.DeletePhysical ->
                                        isDisposed <- true
                                        return Ok OrganizationEventType.PhysicalDeleted
                                    | OrganizationCommand.Undelete -> return Ok OrganizationEventType.Undeleted
                                }

                            match eventResult with
                            | Ok event -> return! this.ApplyEvent { Event = event; Metadata = metadata }
                            | Error error -> return Error error
                        with ex ->
                            return Error(GraceError.CreateWithMetadata $"{createExceptionResponse ex}" metadata.CorrelationId metadata.Properties)
                    }

                task {
                    this.correlationId <- metadata.CorrelationId
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
                    }
                    :> Task
                | ReminderType.PhysicalDeletion ->
                    task {
                        // Get values from state.
                        let (deleteReason, correlationId) = fromByteArray<PhysicalDeletionReminderState> state
                        this.correlationId <- correlationId

                        log.LogInformation(
                            "Received PhysicalDeletion reminder for organization; OrganizationId: {organizationId}; OrganizationName: {organizationName}; OwnerId: {ownerId}.",
                            organizationDto.OrganizationId,
                            organizationDto.OrganizationName,
                            organizationDto.OwnerId
                        )

                        // Physically delete the actor state.
                        let! deletedDtoState = stateManager.TryRemoveStateAsync(dtoStateName)
                        let! deletedEventsState = stateManager.TryRemoveStateAsync(eventsStateName)

                        // Mark the actor as disposed, in case someone tries to use it before Dapr GC's it.
                        isDisposed <- true

                        log.LogInformation(
                            "{currentInstant}: CorrelationId: {correlationId}; Deleted physical state for organization; OrganizationId: {organizationId}; OrganizationName: {organizationName}; OwnerId: {ownerId}; deleteReason: {deleteReason}.",
                            getCurrentInstantExtended (),
                            correlationId,
                            organizationDto.OrganizationId,
                            organizationDto.OrganizationName,
                            organizationDto.OwnerId,
                            deleteReason
                        )

                        // Set all values to default.
                        organizationDto <- OrganizationDto.Default
                    }
                    :> Task
                | _ -> failwith "Unknown reminder type."
