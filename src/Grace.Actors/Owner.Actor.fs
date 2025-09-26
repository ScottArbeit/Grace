namespace Grace.Actors

open FSharp.Control
open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Actors.Interfaces
open Grace.Shared
open Grace.Shared.Constants
open Grace.Types
open Grace.Types.Organization
open Grace.Types.Owner
open Grace.Types.Reminder
open Grace.Types.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors.Owner
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open Orleans.Streaming
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Linq
open System.Runtime.Serialization
open System.Text.Json
open System.Threading.Tasks

module Owner =

    let log = loggerFactory.CreateLogger("Owner.Actor")

    type OwnerActor([<PersistentState(StateName.Owner, Constants.GraceActorStorage)>] state: IPersistentState<List<OwnerEvent>>) =
        inherit Grain()

        static let actorName = ActorName.Owner

        let mutable ownerDto = OwnerDto.Default

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            ownerDto <-
                state.State
                |> Seq.fold (fun ownerDto ownerEvent -> OwnerDto.UpdateDto ownerEvent ownerDto) OwnerDto.Default

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            Task.CompletedTask

        member private this.ApplyEvent ownerEvent =
            task {
                try
                    state.State.Add(ownerEvent)

                    //logToConsole $"In Owner.Actor.ApplyEvent(): Writing state. ownerEvent: {serialize ownerEvent}; state: {serialize state}."

                    do! state.WriteStateAsync()

                    // Update the Dto based on the current event.
                    ownerDto <- ownerDto |> OwnerDto.UpdateDto ownerEvent

                    // Publish the event to the rest of the world.
                    let graceEvent = Events.GraceEvent.OwnerEvent ownerEvent

                    let streamProvider = this.GetStreamProvider GraceEventStreamProvider
                    let stream = streamProvider.GetStream<Events.GraceEvent>(StreamId.Create(Constants.GraceEventStreamTopic, ownerDto.OwnerId))
                    do! stream.OnNextAsync(graceEvent)

                    let returnValue = GraceReturnValue.Create "Owner command succeeded." ownerEvent.Metadata.CorrelationId
                    logToConsole $"In Owner.Actor.ApplyEvent(): GraceReturnValue: {returnValue}"

                    returnValue
                        .enhance(nameof OwnerId, ownerDto.OwnerId)
                        .enhance(nameof OwnerName, ownerDto.OwnerName)
                        .enhance (nameof OwnerEventType, getDiscriminatedUnionFullName ownerEvent.Event)
                    |> ignore

                    return Ok returnValue
                with ex ->
                    let exceptionResponse = ExceptionResponse.Create ex
                    log.LogError(ex, "Exception in Owner.Actor: event: {event}", (serialize ownerEvent))
                    log.LogError("Exception details: {exception}", serialize exceptionResponse)
                    let graceError = GraceError.Create (OwnerError.getErrorMessage OwnerError.FailedWhileApplyingEvent) ownerEvent.Metadata.CorrelationId

                    graceError
                        .enhance("Exception details", exceptionResponse.``exception`` + exceptionResponse.innerException)
                        .enhance(nameof OwnerId, ownerDto.OwnerId)
                        .enhance(nameof OwnerName, ownerDto.OwnerName)
                        .enhance (nameof OwnerEventType, getDiscriminatedUnionFullName ownerEvent.Event)
                    |> ignore

                    return Error graceError
            }

        /// Sends a DeleteLogical command to each organization provided.
        member private this.LogicalDeleteOrganizations(organizations: OrganizationDto array, metadata: EventMetadata, deleteReason: DeleteReason) =
            // Loop through the orgs, sending a DeleteLogical command to each. If any of them fail, return the first error.
            task {
                let results = ConcurrentQueue<GraceResult<string>>()

                // Loop through each organization and send a DeleteLogical command to it.
                do!
                    Parallel.ForEachAsync(
                        organizations,
                        Constants.ParallelOptions,
                        (fun organization ct ->
                            ValueTask(
                                task {
                                    if organization.DeletedAt |> Option.isNone then
                                        let organizationActor = Organization.CreateActorProxy organization.OrganizationId metadata.CorrelationId

                                        let! result =
                                            organizationActor.Handle
                                                (Organization.DeleteLogical(
                                                    true,
                                                    $"Cascaded from deleting owner. ownerId: {ownerDto.OwnerId}; ownerName: {ownerDto.OwnerName}; deleteReason: {deleteReason}"
                                                ))
                                                metadata

                                        results.Enqueue(result)
                                }
                            ))
                    )

                // Check if any of the results were errors. If so, return the first one.
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
                    let reminder =
                        ReminderDto.Create
                            actorName
                            $"{this.IdentityString}"
                            ownerDto.OwnerId
                            Guid.Empty
                            Guid.Empty
                            reminderType
                            (getFutureInstant delay)
                            state
                            correlationId

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
                        let physicalDeletionReminderState = reminder.State :?> PhysicalDeletionReminderState
                        this.correlationId <- physicalDeletionReminderState.CorrelationId

                        // Delete saved state for this actor.
                        do! state.ClearStateAsync()

                        log.LogInformation(
                            "{CurrentInstant}: CorrelationId: {correlationId}; Deleted physical state for owner; OwnerId: {ownerId}; OwnerName: {ownerName}; deleteReason: {deleteReason}.",
                            getCurrentInstantExtended (),
                            physicalDeletionReminderState.CorrelationId,
                            ownerDto.OwnerId,
                            ownerDto.OwnerName,
                            physicalDeletionReminderState.DeleteReason
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

        interface IOwnerActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId
                ownerDto.UpdatedAt.IsSome |> returnTask

            member this.IsDeleted correlationId =
                this.correlationId <- correlationId
                ownerDto.DeletedAt.IsSome |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId
                ownerDto |> returnTask

            member this.OrganizationExists organizationName correlationId =
                task {
                    this.correlationId <- correlationId
                    let actorProxy = OrganizationName.CreateActorProxy ownerDto.OwnerId organizationName correlationId

                    match! actorProxy.GetOrganizationId(correlationId) with
                    | Some organizationId -> return true
                    | None -> return false
                }

            member this.ListOrganizations correlationId =
                task {
                    this.correlationId <- correlationId
                    let! organizationDtos = Services.getOrganizations ownerDto.OwnerId Int32.MaxValue false
                    let dict = organizationDtos.ToDictionary((fun org -> org.OrganizationId), (fun org -> org.OrganizationName))

                    return dict :> IReadOnlyDictionary<OrganizationId, OrganizationName>
                }

            member this.Handle command metadata =
                let isValid command (metadata: EventMetadata) =
                    task {
                        if state.State.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId) then
                            return Error(GraceError.Create (OwnerError.getErrorMessage DuplicateCorrelationId) metadata.CorrelationId)
                        else
                            match command with
                            | OwnerCommand.Create(_, _) ->
                                match ownerDto.UpdatedAt with
                                | Some _ -> return Error(GraceError.Create (OwnerError.getErrorMessage OwnerIdAlreadyExists) metadata.CorrelationId)
                                | None -> return Ok command
                            | _ ->
                                match ownerDto.UpdatedAt with
                                | Some _ -> return Ok command
                                | None -> return Error(GraceError.Create (OwnerError.getErrorMessage OwnerIdDoesNotExist) metadata.CorrelationId)
                    }

                let processCommand (command: OwnerCommand) (metadata: EventMetadata) =
                    task {
                        try
                            logToConsole
                                $"In Owner.Actor.ProcessCommand(): command.AssemblyQualifiedName: {command.GetType().AssemblyQualifiedName}; command.Assembly.Location: {command.GetType().Assembly.Location}; Command: {serialize command}; metadata.AssemblyQualifiedName: {metadata.GetType().AssemblyQualifiedName}; metadata.Assembly.Location: {metadata.GetType().Assembly.Location}; metadata.GetType().Attributes: {metadata.GetType().Attributes}; metadata: {serialize metadata}"

                            let! eventResult =
                                task {
                                    match command with
                                    | OwnerCommand.Create(ownerId, ownerName) -> return Ok(OwnerEventType.Created(ownerId, ownerName))
                                    | OwnerCommand.SetName newName ->
                                        // Clear the OwnerNameActor for the old name.
                                        let ownerNameActor = OwnerName.CreateActorProxy ownerDto.OwnerName metadata.CorrelationId
                                        do! ownerNameActor.ClearOwnerId metadata.CorrelationId
                                        memoryCache.RemoveOwnerNameEntry ownerDto.OwnerName

                                        // Set the OwnerNameActor for the new name.
                                        let ownerNameActor = OwnerName.CreateActorProxy ownerDto.OwnerName metadata.CorrelationId
                                        do! ownerNameActor.SetOwnerId ownerDto.OwnerId metadata.CorrelationId
                                        memoryCache.CreateOwnerNameEntry newName ownerDto.OwnerId

                                        return Ok(OwnerEventType.NameSet newName)
                                    | OwnerCommand.SetType ownerType -> return Ok(OwnerEventType.TypeSet ownerType)
                                    | OwnerCommand.SetSearchVisibility searchVisibility -> return Ok(OwnerEventType.SearchVisibilitySet searchVisibility)
                                    | OwnerCommand.SetDescription description -> return Ok(OwnerEventType.DescriptionSet description)
                                    | OwnerCommand.DeleteLogical(force, deleteReason) ->
                                        // Get the list of organizations that aren't already deleted.
                                        let! organizations = getOrganizations ownerDto.OwnerId Int32.MaxValue false

                                        // If the owner contains active organizations, and the force flag is not set, return an error.
                                        if
                                            not <| force
                                            && organizations.Length > 0
                                            && organizations.Any(fun organization -> organization.DeletedAt |> Option.isNone)
                                        then
                                            return
                                                Error(
                                                    GraceError.CreateWithMetadata
                                                        null
                                                        (OwnerError.getErrorMessage OwnerContainsOrganizations)
                                                        metadata.CorrelationId
                                                        metadata.Properties
                                                )
                                        else
                                            // Delete the organizations.
                                            match! this.LogicalDeleteOrganizations(organizations, metadata, deleteReason) with
                                            | Ok _ ->
                                                let (physicalDeletionReminderState: PhysicalDeletionReminderState) =
                                                    { DeleteReason = deleteReason; CorrelationId = metadata.CorrelationId }

                                                do!
                                                    (this :> IGraceReminderWithGuidKey).ScheduleReminderAsync
                                                        ReminderTypes.PhysicalDeletion
                                                        DefaultPhysicalDeletionReminderDuration
                                                        physicalDeletionReminderState
                                                        metadata.CorrelationId

                                                return Ok(LogicalDeleted(force, deleteReason))
                                            | Error error -> return Error error
                                    | OwnerCommand.DeletePhysical ->
                                        // Delete saved state for this actor.
                                        do! state.ClearStateAsync()

                                        // Deactivate the actor after the PhysicalDeletion is processed.
                                        this.DeactivateOnIdle()
                                        return Ok(OwnerEventType.PhysicalDeleted)
                                    | OwnerCommand.Undelete -> return Ok(OwnerEventType.Undeleted)
                                }

                            match eventResult with
                            | Ok event ->
                                //logToConsole $"In Owner.Actor.Handle(): GraceEvent: {serialize event}; Metadata: {serialize metadata}"
                                return! this.ApplyEvent { Event = event; Metadata = metadata }
                            | Error error -> return Error error
                        with ex ->
                            return Error(GraceError.CreateWithMetadata ex String.Empty metadata.CorrelationId metadata.Properties)
                    }

                task {
                    this.correlationId <- metadata.CorrelationId
                    RequestContext.Set(Constants.CurrentCommandProperty, getDiscriminatedUnionCaseName command)

                    match! isValid command metadata with
                    | Ok command -> return! processCommand command metadata
                    | Error error -> return Error error
                }
