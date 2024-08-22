namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Client
open Dapr.Actors.Runtime
open Grace.Actors.Commands.DirectoryVersion
open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Events.DirectoryVersion
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Services
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors.DirectoryVersion
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Linq
open System.Threading.Tasks
open OrganizationName

module DirectoryVersion =

    let GetActorId (directoryId: DirectoryVersionId) = ActorId($"{directoryId}")

    type DirectoryVersionDto =
        { DirectoryVersion: DirectoryVersion
          RecursiveSize: int64
          DeletedAt: Instant option
          DeleteReason: DeleteReason }

        static member Default =
            { DirectoryVersion = DirectoryVersion.Default; RecursiveSize = Constants.InitialDirectorySize; DeletedAt = None; DeleteReason = String.Empty }

    type DirectoryVersionActor(host: ActorHost) =
        inherit Actor(host)

        let actorName = ActorName.DirectoryVersion
        let log = loggerFactory.CreateLogger("DirectoryVersion.Actor")
        let eventsStateName = StateName.DirectoryVersion
        let directoryVersionCacheStateName = StateName.DirectoryVersionCache
        let directoryVersionEvents = List<DirectoryVersionEvent>()

        let mutable directoryVersionDto = DirectoryVersionDto.Default
        let mutable actorStartTime = Instant.MinValue
        let mutable logScope: IDisposable = null
        let mutable currentCommand = String.Empty

        /// Indicates that the actor is in an undefined state, and should be reset.
        let mutable isDisposed = false

        let updateDto directoryVersionEvent currentDirectoryVersionDto =
            match directoryVersionEvent with
            | Created directoryVersion -> { currentDirectoryVersionDto with DirectoryVersion = directoryVersion }
            | RecursiveSizeSet recursiveSize -> { currentDirectoryVersionDto with RecursiveSize = recursiveSize }
            | LogicalDeleted deleteReason -> { currentDirectoryVersionDto with DeletedAt = Some(getCurrentInstant ()); DeleteReason = deleteReason }
            | PhysicalDeleted -> currentDirectoryVersionDto // Do nothing because it's about to be deleted anyway.
            | Undeleted -> { currentDirectoryVersionDto with DeletedAt = None; DeleteReason = String.Empty }

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync() =
            let activateStartTime = getCurrentInstant ()
            let stateManager = this.StateManager

            task {
                let mutable message = String.Empty

                try
                    let! retrievedEvents = Storage.RetrieveState<List<DirectoryVersionEvent>> stateManager eventsStateName

                    match retrievedEvents with
                    | Some retrievedEvents ->
                        // Load the existing events into memory.
                        directoryVersionEvents.AddRange(retrievedEvents)

                        // Apply the events to build the current Dto.
                        directoryVersionDto <-
                            retrievedEvents
                            |> Seq.fold
                                (fun directoryVersionDto directoryVersionEvent -> directoryVersionDto |> updateDto directoryVersionEvent.Event)
                                DirectoryVersionDto.Default

                        message <- "Retrieved from database"
                    | None -> message <- "Not found in database"
                with ex ->
                    let exc = createExceptionResponse ex

                    log.LogError("{CurrentInstant} Error in {ActorType} {ActorId}.", getCurrentInstantExtended (), this.GetType().Name, host.Id)

                    log.LogError("{CurrentInstant} {ExceptionDetails}", getCurrentInstantExtended (), exc.ToString())

                let duration_ms = getPaddedDuration_ms activateStartTime

                log.LogInformation(
                    "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId:             ; Activated {ActorType} {ActorId}. {message}.",
                    getCurrentInstantExtended (),
                    getMachineName,
                    duration_ms,
                    actorName,
                    host.Id,
                    message
                )
            }
            :> Task

        override this.OnPreActorMethodAsync(context) =
            this.correlationId <- String.Empty

            if context.CallType = ActorCallType.ReminderMethod then
                log.LogInformation(
                    "{CurrentInstant}: Reminder {ActorName}.{MethodName} Id: {Id}.",
                    getCurrentInstantExtended (),
                    actorName,
                    context.MethodName,
                    this.Id
                )

            actorStartTime <- getCurrentInstant ()
            currentCommand <- String.Empty
            logScope <- log.BeginScope("Actor {actorName}", actorName)

            log.LogTrace("{CurrentInstant}: Started {ActorName}.{MethodName} Id: {Id}.", getCurrentInstantExtended (), actorName, context.MethodName, this.Id)

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
                    "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {ActorName}.{MethodName}; Id: {Id}.",
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
                    "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {ActorName}.{MethodName}; Command: {Command}; Id: {Id}.",
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

        member private this.SetReminderToDeleteCachedState() =
            //this.RegisterReminderAsync(ReminderType.DeleteCachedState, Array.empty<byte>, TimeSpan.FromDays(1.0), TimeSpan.Zero)
            Task.CompletedTask

        member private this.OnFirstWrite() = ()

        //try
        //    let result = this.RegisterReminderAsync("DeleteCachedState", Array.empty<byte>, TimeSpan.FromDays(1.0), TimeSpan.Zero)
        //    ()
        //with ex ->
        //    log.LogError("{CurrentInstant}: Error in {methodName}. Exception: {exception}", getCurrentInstantExtended(), nameof(this.SetReminderToDeleteCachedState), createExceptionResponse ex)

        /// Sets a Dapr Actor reminder to perform a physical deletion of this owner.
        member private this.SchedulePhysicalDeletion(deleteReason, correlationId) =
            this
                .RegisterReminderAsync(
                    ReminderType.PhysicalDeletion,
                    toByteArray (deleteReason, correlationId),
                    Constants.DefaultPhysicalDeletionReminderTime,
                    TimeSpan.FromMilliseconds(-1)
                )
                .Wait()

        member private this.ApplyEvent directoryVersionEvent =
            let stateManager = this.StateManager

            task {
                try
                    if directoryVersionEvents.Count = 0 then this.OnFirstWrite()

                    // Add the event to the list of events, and save it to actor state.
                    directoryVersionEvents.Add(directoryVersionEvent)
                    do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync(eventsStateName, directoryVersionEvents))

                    // Update the Dto with the event.
                    directoryVersionDto <- directoryVersionDto |> updateDto directoryVersionEvent.Event

                    // Publish the event to the rest of the world.
                    let graceEvent = Events.GraceEvent.DirectoryVersionEvent directoryVersionEvent
                    do! daprClient.PublishEventAsync(GracePubSubService, GraceEventStreamTopic, graceEvent)

                    let returnValue = GraceReturnValue.Create "Directory version command succeeded." directoryVersionEvent.Metadata.CorrelationId

                    returnValue
                        .enhance(nameof (RepositoryId), $"{directoryVersionDto.DirectoryVersion.RepositoryId}")
                        .enhance(nameof (DirectoryVersionId), $"{directoryVersionDto.DirectoryVersion.DirectoryVersionId}")
                        .enhance(nameof (Sha256Hash), $"{directoryVersionDto.DirectoryVersion.Sha256Hash}")
                        .enhance (nameof (DirectoryVersionEventType), $"{getDiscriminatedUnionFullName directoryVersionEvent.Event}")
                    |> ignore

                    return Ok returnValue
                with ex ->
                    let exceptionResponse = createExceptionResponse ex

                    let graceError =
                        GraceError.Create (DirectoryVersionError.getErrorMessage FailedWhileApplyingEvent) directoryVersionEvent.Metadata.CorrelationId

                    graceError
                        .enhance("Exception details", exceptionResponse.``exception`` + exceptionResponse.innerException)
                        .enhance(nameof (RepositoryId), $"{directoryVersionDto.DirectoryVersion.RepositoryId}")
                        .enhance(nameof (DirectoryVersionId), $"{directoryVersionDto.DirectoryVersion.DirectoryVersionId}")
                        .enhance(nameof (Sha256Hash), $"{directoryVersionDto.DirectoryVersion.Sha256Hash}")
                        .enhance (nameof (DirectoryVersionEventType), $"{getDiscriminatedUnionFullName directoryVersionEvent.Event}")
                    |> ignore

                    return Error graceError
            }

        interface IRemindable with
            member this.ReceiveReminderAsync(reminderName, state, dueTime, period) =
                let stateManager = this.StateManager

                match reminderName with
                | ReminderType.DeleteCachedState ->
                    task {
                        try // Temporary hack while some existing reminders with an older state are still in the system.
                            // Get values from state.ds
                            let (deleteReason, correlationId) = fromByteArray<string * string> state
                            this.correlationId <- correlationId
                        with ex ->
                            ()

                        let! deleteSucceeded = Storage.DeleteState stateManager directoryVersionCacheStateName
                        ()
                    }
                    :> Task
                | _ -> Task.CompletedTask

        interface IDirectoryVersionActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId

                (directoryVersionDto.DirectoryVersion.DirectoryVersionId
                 <> DirectoryVersion.Default.DirectoryVersionId)
                |> returnTask

            member this.Delete correlationId =
                this.correlationId <- correlationId

                GraceResult.Error(GraceError.Create "Not implemented" correlationId)
                |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId
                directoryVersionDto.DirectoryVersion |> returnTask

            member this.GetCreatedAt correlationId =
                this.correlationId <- correlationId
                directoryVersionDto.DirectoryVersion.CreatedAt |> returnTask

            member this.GetDirectories correlationId =
                this.correlationId <- correlationId
                directoryVersionDto.DirectoryVersion.Directories |> returnTask

            member this.GetFiles correlationId =
                this.correlationId <- correlationId
                directoryVersionDto.DirectoryVersion.Files |> returnTask

            member this.GetSha256Hash correlationId =
                this.correlationId <- correlationId
                directoryVersionDto.DirectoryVersion.Sha256Hash |> returnTask

            member this.GetSize correlationId =
                this.correlationId <- correlationId
                directoryVersionDto.DirectoryVersion.Size |> returnTask

            member this.GetRecursiveDirectoryVersions (forceRegenerate: bool) correlationId =
                this.correlationId <- correlationId
                let stateManager = this.StateManager

                task {
                    try
                        // Check if the subdirectory versions have already been generated and cached.
                        let cachedSubdirectoryVersions =
                            task {
                                if not <| forceRegenerate then
                                    return! Storage.RetrieveState<DirectoryVersion array> stateManager directoryVersionCacheStateName
                                else
                                    return None
                            }

                        // If they have already been generated, return them.
                        match! cachedSubdirectoryVersions with
                        | Some subdirectoryVersions ->
                            log.LogDebug("In DirectoryVersionActor.GetDirectoryVersionsRecursive({id}). Retrieved SubdirectoryVersions from cache.", this.Id)

                            return subdirectoryVersions.ToArray()
                        // If they haven't, generate them by calling each subdirectory in parallel.
                        | None ->
                            log.LogDebug(
                                "In DirectoryVersionActor.GetDirectoryVersionsRecursive({id}). SubdirectoryVersions will be generated. forceRegenerate: {forceRegenerate}",
                                this.Id,
                                forceRegenerate
                            )

                            let subdirectoryVersions = ConcurrentQueue<DirectoryVersion>()
                            subdirectoryVersions.Enqueue(directoryVersionDto.DirectoryVersion)

                            do!
                                Parallel.ForEachAsync(
                                    directoryVersionDto.DirectoryVersion.Directories,
                                    Constants.ParallelOptions,
                                    (fun directoryId ct ->
                                        ValueTask(
                                            task {
                                                let subdirectoryActor = DirectoryVersion.CreateActorProxy directoryId correlationId

                                                let! subdirectoryContents = subdirectoryActor.GetRecursiveDirectoryVersions forceRegenerate correlationId

                                                for directoryVersion in subdirectoryContents do
                                                    subdirectoryVersions.Enqueue(directoryVersion)
                                            }
                                        ))
                                )

                            let subdirectoryVersionsList =
                                subdirectoryVersions.ToArray()
                                |> Array.sortBy (fun directoryVersion -> directoryVersion.RelativePath)

                            logToConsole $"In DirectoryVersionActor.GetDirectoryVersionsRecursive({this.Id}); Storing subdirectoryVersion list."
                            do! Storage.SaveState stateManager directoryVersionCacheStateName subdirectoryVersionsList

                            log.LogDebug("In DirectoryVersionActor.GetDirectoryVersionsRecursive({id}); Storing subdirectoryVersion list.", this.Id)

                            let! _ = this.SetReminderToDeleteCachedState()

                            log.LogDebug("In DirectoryVersionActor.GetDirectoryVersionsRecursive({id}); Delete cached state reminder was set.", this.Id)

                            return subdirectoryVersionsList
                    with ex ->
                        log.LogError(
                            "{CurrentInstant}: Error in {methodName}. Exception: {exception}",
                            getCurrentInstantExtended (),
                            nameof (this.SetReminderToDeleteCachedState),
                            createExceptionResponse ex
                        )

                        return Array.Empty<DirectoryVersion>()
                }

            member this.Handle command metadata =
                let isValid command (metadata: EventMetadata) =
                    task {
                        match command with
                        | DirectoryVersionCommand.Create directoryVersion ->
                            if
                                directoryVersionEvents.Any(fun e ->
                                    match e.Event with
                                    | Created _ -> true
                                    | _ -> false)
                            then
                                return
                                    Error(
                                        GraceError.Create
                                            (DirectoryVersionError.getErrorMessage DirectoryVersionError.DirectoryAlreadyExists)
                                            metadata.CorrelationId
                                    )
                            else
                                return Ok command

                        | _ ->
                            if directoryVersionDto.DirectoryVersion.CreatedAt = DirectoryVersion.Default.CreatedAt then
                                return Error(GraceError.Create (DirectoryVersionError.getErrorMessage DirectoryDoesNotExist) metadata.CorrelationId)
                            else
                                return Ok command
                    }

                let processCommand (command: DirectoryVersionCommand) (metadata: EventMetadata) =
                    task {
                        try
                            let! eventResult =
                                task {
                                    match command with
                                    | Create directoryVersion -> return Ok(Created directoryVersion)
                                    | SetRecursiveSize recursiveSize -> return Ok(RecursiveSizeSet recursiveSize)
                                    | DeleteLogical deleteReason ->
                                        this.SchedulePhysicalDeletion(deleteReason, metadata.CorrelationId)
                                        return Ok(LogicalDeleted deleteReason)
                                    | DeletePhysical ->
                                        isDisposed <- true
                                        return Ok(PhysicalDeleted)
                                    | Undelete -> return Ok(Undeleted)
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

            member this.GetRecursiveSize correlationId =
                this.correlationId <- correlationId

                task {
                    if directoryVersionDto.RecursiveSize = Constants.InitialDirectorySize then
                        let! directoryVersions = (this :> IDirectoryVersionActor).GetRecursiveDirectoryVersions false correlationId

                        let recursiveSize = directoryVersions |> Seq.sumBy (fun directoryVersion -> directoryVersion.Size)

                        match! (this :> IDirectoryVersionActor).Handle (SetRecursiveSize recursiveSize) (EventMetadata.New correlationId "GraceSystem") with
                        | Ok returnValue -> return recursiveSize
                        | Error error -> return Constants.InitialDirectorySize
                    else
                        return directoryVersionDto.RecursiveSize
                }
