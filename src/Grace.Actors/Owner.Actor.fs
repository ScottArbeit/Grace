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
open Grace.Shared.Commands.Owner
open Grace.Shared.Constants
open Grace.Shared.Dto.Organization
open Grace.Shared.Dto.Owner
open Grace.Shared.Events.Owner
open Grace.Types.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors.Owner
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

module Owner =

    /// The data types stored in physical deletion reminders.
    type PhysicalDeletionReminderState = (DeleteReason * CorrelationId)

    let log = loggerFactory.CreateLogger("Owner.Actor")

    type OwnerActor([<PersistentState(StateName.Owner, Constants.GraceActorStorage)>] state: IPersistentState<List<OwnerEvent>>) =
        inherit Grain()

        static let actorName = ActorName.Owner

        let mutable ownerDto = OwnerDto.Default

        let updateDto ownerEvent currentOwnerDto =
            let newOwnerDto =
                match ownerEvent.Event with
                | Created(ownerId, ownerName) -> { OwnerDto.Default with OwnerId = ownerId; OwnerName = ownerName; CreatedAt = ownerEvent.Metadata.Timestamp }
                | NameSet(ownerName) -> { currentOwnerDto with OwnerName = ownerName }
                | TypeSet(ownerType) -> { currentOwnerDto with OwnerType = ownerType }
                | SearchVisibilitySet(searchVisibility) -> { currentOwnerDto with SearchVisibility = searchVisibility }
                | DescriptionSet(description) -> { currentOwnerDto with Description = description }
                | LogicalDeleted(_, deleteReason) -> { currentOwnerDto with DeletedAt = Some(getCurrentInstant ()); DeleteReason = deleteReason }
                | PhysicalDeleted -> currentOwnerDto // Do nothing because it's about to be deleted anyway.
                | Undeleted -> { currentOwnerDto with DeletedAt = None; DeleteReason = String.Empty }

            { newOwnerDto with UpdatedAt = Some ownerEvent.Metadata.Timestamp }

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            logActorActivation log this.IdentityString (getActorActivationMessage state.RecordExists)

            ownerDto <-
                state.State
                |> Seq.fold (fun ownerDto ownerEvent -> updateDto ownerEvent ownerDto) OwnerDto.Default

            Task.CompletedTask

        member private this.ApplyEvent ownerEvent =
            task {
                try
                    state.State.Add(ownerEvent)

                    do! state.WriteStateAsync()

                    // Update the Dto based on the current event.
                    ownerDto <- ownerDto |> updateDto ownerEvent

                    // Publish the event to the rest of the world.
                    let graceEvent = Events.GraceEvent.OwnerEvent ownerEvent
                    do! daprClient.PublishEventAsync(GracePubSubService, GraceEventStreamTopic, graceEvent)

                    let returnValue = GraceReturnValue.Create "Owner command succeeded." ownerEvent.Metadata.CorrelationId
                    logToConsole $"In Owner.Actor.ApplyEvent(): GraceReturnValue: {returnValue}"

                    returnValue
                        .enhance(nameof (OwnerId), $"{ownerDto.OwnerId}")
                        .enhance(nameof (OwnerName), $"{ownerDto.OwnerName}")
                        .enhance (nameof (OwnerEventType), $"{getDiscriminatedUnionFullName ownerEvent.Event}")
                    |> ignore

                    return Ok returnValue
                with ex ->
                    let exceptionResponse = ExceptionResponse.Create ex
                    log.LogError(ex, "Exception in Owner.Actor: event: {event}", (serialize ownerEvent))
                    let graceError = GraceError.Create (OwnerError.getErrorMessage OwnerError.FailedWhileApplyingEvent) ownerEvent.Metadata.CorrelationId

                    graceError
                        .enhance("Exception details", exceptionResponse.``exception`` + exceptionResponse.innerException)
                        .enhance(nameof (OwnerId), $"{ownerDto.OwnerId}")
                        .enhance(nameof (OwnerName), $"{ownerDto.OwnerName}")
                        .enhance (nameof (OwnerEventType), $"{getDiscriminatedUnionFullName ownerEvent.Event}")
                    |> ignore

                    return Error graceError
            }

        /// Sends a DeleteLogical command to each organization provided.
        member private this.LogicalDeleteOrganizations(organizations: List<OrganizationDto>, metadata: EventMetadata, deleteReason: DeleteReason) =
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
                                                (Commands.Organization.DeleteLogical(
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

                        // Delete saved state for this actor.
                        do! state.ClearStateAsync()

                        log.LogInformation(
                            "{CurrentInstant}: CorrelationId: {correlationId}; Deleted physical state for owner; OwnerId: {ownerId}; OwnerName: {ownerName}; deleteReason: {deleteReason}.",
                            getCurrentInstantExtended (),
                            correlationId,
                            ownerDto.OwnerId,
                            ownerDto.OwnerName,
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
                                            && organizations.Count > 0
                                            && organizations.Any(fun organization -> organization.DeletedAt |> Option.isNone)
                                        then
                                            return
                                                Error(
                                                    GraceError.CreateWithMetadata
                                                        (OwnerError.getErrorMessage OwnerContainsOrganizations)
                                                        metadata.CorrelationId
                                                        metadata.Properties
                                                )
                                        else
                                            // Delete the organizations.
                                            match! this.LogicalDeleteOrganizations(organizations, metadata, deleteReason) with
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
                                logToConsole $"In Owner.Actor.Handle(): GraceEvent: {serialize event}; Metadata: {serialize metadata}"
                                return! this.ApplyEvent { Event = event; Metadata = metadata }
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
