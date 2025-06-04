namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Shared
open Grace.Shared.Commands.Organization
open Grace.Shared.Constants
open Grace.Shared.Dto.Organization
open Grace.Shared.Dto.Repository
open Grace.Shared.Events.Organization
open Grace.Types.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors.Organization
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Linq
open System.Runtime.Serialization
open System.Text.Json
open System.Threading.Tasks

module Organization =

    /// The data types stored in physical deletion reminders.
    type PhysicalDeletionReminderState = (DeleteReason * CorrelationId)

    let log = loggerFactory.CreateLogger("Organization.Actor")

    type OrganizationActor([<PersistentState(StateName.Organization, Constants.GraceActorStorage)>] state: IPersistentState<List<OrganizationEvent>>) =
        inherit Grain()

        static let actorName = ActorName.Organization

        let mutable organizationDto = OrganizationDto.Default

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

        override this.OnActivateAsync(ct) =
            logActorActivation log this.IdentityString (getActorActivationMessage state.RecordExists)
            organizationDto <- Seq.fold (fun organizationDto event -> updateDto event organizationDto) organizationDto state.State
            Task.CompletedTask

        member private this.ApplyEvent organizationEvent =
            task {
                try
                    state.State.Add(organizationEvent)

                    do! state.WriteStateAsync()

                    // Update the Dto based on the current event.
                    organizationDto <- organizationDto |> updateDto organizationEvent

                    // Publish the event to the rest of the world.
                    let graceEvent = Events.GraceEvent.OrganizationEvent organizationEvent
                    do! daprClient.PublishEventAsync(GracePubSubService, GraceEventStreamTopic, graceEvent)

                    let returnValue =
                        (GraceReturnValue.Create "Organization command succeeded." organizationEvent.Metadata.CorrelationId)
                            .enhance(nameof (OwnerId), $"{organizationDto.OwnerId}")
                            .enhance(nameof (OrganizationId), $"{organizationDto.OrganizationId}")
                            .enhance(nameof (OrganizationName), $"{organizationDto.OrganizationName}")
                            .enhance (nameof (OrganizationEventType), $"{getDiscriminatedUnionFullName organizationEvent.Event}")

                    return Ok returnValue
                with ex ->
                    let exceptionResponse = ExceptionResponse.Create ex

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
                                        let! repositoryActor = Repository.CreateActorProxy repository.RepositoryId metadata.CorrelationId

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

        interface IGraceReminderWithGuidKey with
            /// Schedules a Grace reminder.
            member this.ScheduleReminderAsync reminderType delay state correlationId =
                task {
                    let reminder = ReminderDto.Create actorName $"{this.IdentityString}" Guid.Empty reminderType (getFutureInstant delay) state correlationId
                    do! createReminder reminder
                }
                :> Task

            /// Receives a Grace reminder.
            member this.ReceiveReminderAsync(reminder: ReminderDto) : Task<Result<unit, GraceError>> =
                task {
                    this.correlationId <- reminder.CorrelationId

                    match reminder.ReminderType with
                    | ReminderTypes.PhysicalDeletion ->
                        // Get values from state.
                        let (deleteReason, correlationId) = deserialize<PhysicalDeletionReminderState> reminder.State
                        this.correlationId <- correlationId

                        do! state.ClearStateAsync()

                        log.LogInformation(
                            "{CurrentInstant}: CorrelationId: {correlationId}; Deleted physical state for organization; OrganizationId: {organizationId}; OrganizationName: {organizationName}; OwnerId: {ownerId}; deleteReason: {deleteReason}.",
                            getCurrentInstantExtended (),
                            correlationId,
                            organizationDto.OrganizationId,
                            organizationDto.OrganizationName,
                            organizationDto.OwnerId,
                            deleteReason
                        )

                        // Deactivate the actor after the PhysicalDeletion reminder is processed.
                        this.DeactivateOnIdle()
                        return Ok()
                    | _ ->
                        return
                            Error(
                                GraceError.Create
                                    $"{actorName} does not process reminder type {getDiscriminatedUnionCaseName reminder.ReminderType}."
                                    this.correlationId
                            )
                }

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
                        if state.State.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId) then
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
                                                let (reminderState: PhysicalDeletionReminderState) = (deleteReason, metadata.CorrelationId)

                                                do!
                                                    (this :> IGraceReminderWithGuidKey).ScheduleReminderAsync
                                                        ReminderTypes.PhysicalDeletion
                                                        DefaultPhysicalDeletionReminderDuration
                                                        (serialize reminderState)
                                                        metadata.CorrelationId

                                                return Ok(LogicalDeleted(force, deleteReason))
                                            | Error error -> return Error error
                                    | OrganizationCommand.DeletePhysical ->
                                        // Delete saved state for this actor.
                                        do! state.ClearStateAsync()

                                        // Deactivate the actor after the PhysicalDeletion is processed.
                                        this.DeactivateOnIdle()
                                    
                                        return Ok OrganizationEventType.PhysicalDeleted
                                    | OrganizationCommand.Undelete -> return Ok OrganizationEventType.Undeleted
                                }

                            match eventResult with
                            | Ok event -> return! this.ApplyEvent { Event = event; Metadata = metadata }
                            | Error error -> return Error error
                        with ex ->
                            return Error(GraceError.CreateWithMetadata $"{ExceptionResponse.Create ex}" metadata.CorrelationId metadata.Properties)
                    }

                task {
                    this.correlationId <- metadata.CorrelationId
                    RequestContext.Set(Constants.CurrentCommandProperty, getDiscriminatedUnionCaseName command)

                    match! isValid command metadata with
                    | Ok command -> return! processCommand command metadata
                    | Error error -> return Error error
                }
