namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open Grace.Actors.Commands
open Grace.Actors.Commands.Branch
open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Events.Branch
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Dto.Branch
open Grace.Shared.Dto.Reference
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors.Branch
open Microsoft.Extensions.Logging
open System
open System.Collections.Generic
open System.Diagnostics
open System.Linq
open System.Runtime.Serialization
open System.Text
open System.Threading.Tasks
open NodaTime
open System.Text.Json
open System.Net.Http.Json
open FSharpPlus.Data.MultiMap
open System.Threading

module Branch =

    // Branch should support logical deletes with physical deletes set using a Dapr Timer based on a repository-level setting.
    // Branch Deletion should enumerate and delete each reference in the branch.

    type PhysicalDeletionReminderState = (RepositoryId * BranchId * BranchName * ParentBranchId * DeleteReason * CorrelationId)
    let log = loggerFactory.CreateLogger("Branch.Actor")

    type BranchActor(host: ActorHost) =
        inherit Actor(host)

        static let actorName = ActorName.Branch
        static let eventsStateName = StateName.Branch

        let mutable branchDto: BranchDto = BranchDto.Default
        let branchEvents = List<BranchEvent>()

        let mutable actorStartTime = Instant.MinValue
        let mutable logScope: IDisposable = null
        let mutable currentCommand = String.Empty
        let mutable stateManager = Unchecked.defaultof<IActorStateManager>

        /// Indicates that the actor is in an undefined state, and should be reset.
        let mutable isDisposed = false

        let updateDto branchEvent correlationId currentBranchDto =
            task {
                let branchEventType = branchEvent.Event

                let! newBranchDto =
                    match branchEventType with
                    | Created(branchId, branchName, parentBranchId, basedOn, repositoryId, initialPermissions) ->
                        task {
                            let! referenceDto =
                                if basedOn <> ReferenceId.Empty then
                                    task {
                                        let referenceActorProxy = Reference.CreateActorProxy basedOn correlationId
                                        return! referenceActorProxy.Get correlationId
                                    }
                                else
                                    ReferenceDto.Default |> returnTask

                            let mutable branchDto =
                                { BranchDto.Default with
                                    BranchId = branchId
                                    BranchName = branchName
                                    ParentBranchId = parentBranchId
                                    BasedOn = referenceDto
                                    RepositoryId = repositoryId
                                    CreatedAt = branchEvent.Metadata.Timestamp }

                            for referenceType in initialPermissions do
                                branchDto <-
                                    match referenceType with
                                    | Promotion -> { branchDto with PromotionEnabled = true }
                                    | Commit -> { branchDto with CommitEnabled = true }
                                    | Checkpoint -> { branchDto with CheckpointEnabled = true }
                                    | Save -> { branchDto with SaveEnabled = true }
                                    | Tag -> { branchDto with TagEnabled = true }
                                    | External -> { branchDto with ExternalEnabled = true }
                                    | Rebase -> branchDto // Rebase is always allowed. (Auto-rebase is optional, but rebase itself is always allowed.)

                            return branchDto
                        }
                    | Rebased referenceId ->
                        task {
                            let referenceActorProxy = Reference.CreateActorProxy referenceId correlationId
                            let! referenceDto = referenceActorProxy.Get branchEvent.Metadata.CorrelationId
                            return { currentBranchDto with BasedOn = referenceDto }
                        }
                    | NameSet branchName -> { currentBranchDto with BranchName = branchName } |> returnTask
                    | Assigned(referenceDto, directoryVersion, sha256Hash, referenceText) ->
                        { currentBranchDto with LatestPromotion = referenceDto; BasedOn = referenceDto }
                        |> returnTask
                    | Promoted(referenceDto, directoryVersion, sha256Hash, referenceText) ->
                        { currentBranchDto with LatestPromotion = referenceDto; BasedOn = referenceDto }
                        |> returnTask
                    | Committed(referenceDto, directoryVersion, sha256Hash, referenceText) ->
                        { currentBranchDto with LatestCommit = referenceDto } |> returnTask
                    | Checkpointed(referenceDto, directoryVersion, sha256Hash, referenceText) ->
                        { currentBranchDto with LatestCheckpoint = referenceDto } |> returnTask
                    | Saved(referenceDto, directoryVersion, sha256Hash, referenceText) -> { currentBranchDto with LatestSave = referenceDto } |> returnTask
                    | Tagged(referenceDto, directoryVersion, sha256Hash, referenceText) -> currentBranchDto |> returnTask // No changes to currentBranchDto.
                    | ExternalCreated(referenceDto, directoryVersion, sha256Hash, referenceText) -> currentBranchDto |> returnTask // No changes to currentBranchDto.
                    | EnabledAssign enabled -> { currentBranchDto with AssignEnabled = enabled } |> returnTask
                    | EnabledPromotion enabled -> { currentBranchDto with PromotionEnabled = enabled } |> returnTask
                    | EnabledCommit enabled -> { currentBranchDto with CommitEnabled = enabled } |> returnTask
                    | EnabledCheckpoint enabled -> { currentBranchDto with CheckpointEnabled = enabled } |> returnTask
                    | EnabledSave enabled -> { currentBranchDto with SaveEnabled = enabled } |> returnTask
                    | EnabledTag enabled -> { currentBranchDto with TagEnabled = enabled } |> returnTask
                    | EnabledExternal enabled -> { currentBranchDto with ExternalEnabled = enabled } |> returnTask
                    | EnabledAutoRebase enabled -> { currentBranchDto with AutoRebaseEnabled = enabled } |> returnTask
                    | ReferenceRemoved _ -> currentBranchDto |> returnTask
                    | LogicalDeleted(force, deleteReason) ->
                        { currentBranchDto with DeletedAt = Some(getCurrentInstant ()); DeleteReason = deleteReason }
                        |> returnTask
                    | PhysicalDeleted -> currentBranchDto |> returnTask // Do nothing because it's about to be deleted anyway.
                    | Undeleted ->
                        { currentBranchDto with DeletedAt = None; DeleteReason = String.Empty }
                        |> returnTask

                return { newBranchDto with UpdatedAt = Some branchEvent.Metadata.Timestamp }
            }

        /// Updates the branchDto with the latest reference of each type from the branch.
        let updateLatestReferences (branchDto: BranchDto) correlationId =
            task {
                let mutable newBranchDto = branchDto

                // Get the latest references and update the dto.
                let referenceTypes = [| Save; Checkpoint; Commit; Promotion; Rebase |]

                let! latestReferences = getLatestReferenceByReferenceTypes referenceTypes branchDto.BranchId

                for kvp in latestReferences do
                    let referenceDto = kvp.Value

                    match kvp.Key with
                    | Save -> newBranchDto <- { newBranchDto with LatestSave = referenceDto }
                    | Checkpoint -> newBranchDto <- { newBranchDto with LatestCheckpoint = referenceDto }
                    | Commit -> newBranchDto <- { newBranchDto with LatestCommit = referenceDto }
                    | Promotion -> newBranchDto <- { newBranchDto with LatestPromotion = referenceDto; BasedOn = referenceDto }
                    | Rebase ->
                        let basedOnLink =
                            kvp.Value.Links
                            |> Array.find (fun link ->
                                match link with
                                | ReferenceLinkType.BasedOn _ -> true)

                        let basedOnReferenceId =
                            match basedOnLink with
                            | ReferenceLinkType.BasedOn referenceId -> referenceId

                        let basedOnReferenceActorProxy = Reference.CreateActorProxy basedOnReferenceId correlationId
                        let! basedOnReferenceDto = basedOnReferenceActorProxy.Get correlationId

                        newBranchDto <- { newBranchDto with BasedOn = basedOnReferenceDto }
                    | External -> ()
                    | Tag -> ()

                return { newBranchDto with ShouldRecomputeLatestReferences = false }
            }

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync() =
            let activateStartTime = getCurrentInstant ()
            stateManager <- this.StateManager

            task {
                let correlationId =
                    match memoryCache.GetCorrelationIdEntry this.Id with
                    | Some correlationId -> correlationId
                    | None -> String.Empty

                try
                    let! retrievedEvents = Storage.RetrieveState<List<BranchEvent>> stateManager eventsStateName correlationId

                    match retrievedEvents with
                    | Some retrievedEvents ->
                        // Load the branchEvents from the retrieved events.
                        branchEvents.AddRange(retrievedEvents)

                        // Apply all events to the branchDto.
                        for branchEvent in retrievedEvents do
                            let! updatedBranchDto = branchDto |> updateDto branchEvent correlationId
                            branchDto <- updatedBranchDto

                        let! branchDtoWithLatestReferences = updateLatestReferences branchDto correlationId
                        branchDto <- branchDtoWithLatestReferences
                    | None -> ()

                    let message = $"Branch name: {branchDto.BranchName}; {getActorActivationMessage retrievedEvents}"
                    logActorActivation log activateStartTime correlationId actorName this.Id message
                with ex ->
                    let exc = ExceptionResponse.Create ex
                    log.LogError("{CurrentInstant} Error activating {ActorType} {ActorId}.", getCurrentInstantExtended (), this.GetType().Name, host.Id)
                    log.LogError("{CurrentInstant} {ExceptionDetails}", getCurrentInstantExtended (), exc.ToString())
                    logActorActivation log activateStartTime correlationId actorName this.Id "Exception occurred during activation."
            }
            :> Task

        member private this.SetMaintenanceReminder() =
            this.RegisterReminderAsync("MaintenanceReminder", Array.empty<byte>, TimeSpan.FromDays(7.0), TimeSpan.FromDays(7.0))

        member private this.UnregisterMaintenanceReminder() = this.UnregisterReminderAsync("MaintenanceReminder")

        member private this.OnFirstWrite() =
            task {
                //let! _ = Constants.DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> this.SetMaintenanceReminder())
                ()
            }
            :> Task

        override this.OnPreActorMethodAsync(context) =
            this.correlationId <- String.Empty
            actorStartTime <- getCurrentInstant ()
            logScope <- log.BeginScope("Actor {actorName}", actorName)
            currentCommand <- String.Empty

            log.LogTrace(
                "{CurrentInstant}: Started {ActorName}.{MethodName} BranchId: {Id}.",
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

            let threadCounts =
                match memoryCache.GetThreadCountEntry() with
                | Some threadCount -> threadCount
                | None ->
                    let threads = $"ThreadCount: {ThreadPool.ThreadCount}; PendingWorkItemCount: {ThreadPool.PendingWorkItemCount}"

                    memoryCache.CreateThreadCountEntry(threads)
                    threads

            if
                String.IsNullOrEmpty(currentCommand)
                && not <| (context.MethodName = "ReceiveReminderAsync")
            then
                log.LogInformation(
                    "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {ActorName}.{MethodName}; RepositoryId: {RepositoryId}; BranchId: {Id}; BranchName: {BranchName}; {ThreadCounts}.",
                    getCurrentInstantExtended (),
                    getMachineName,
                    duration_ms,
                    this.correlationId,
                    actorName,
                    context.MethodName,
                    branchDto.RepositoryId,
                    this.Id,
                    branchDto.BranchName,
                    threadCounts
                )
            else
                log.LogInformation(
                    "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {ActorName}.{MethodName}; Command: {Command}; RepositoryId: {RepositoryId}; BranchId: {Id}; BranchName: {BranchName}; ThreadCounts: {ThreadCounts}.",
                    getCurrentInstantExtended (),
                    getMachineName,
                    duration_ms,
                    this.correlationId,
                    actorName,
                    context.MethodName,
                    currentCommand,
                    branchDto.RepositoryId,
                    this.Id,
                    branchDto.BranchName,
                    threadCounts
                )

            logScope.Dispose()
            Task.CompletedTask

        member private this.ApplyEvent branchEvent =
            task {
                try
                    if branchEvents |> Seq.isEmpty then do! this.OnFirstWrite()

                    // Update the branchDto with the event.
                    let! updatedBranchDto = branchDto |> updateDto branchEvent this.correlationId
                    branchDto <- updatedBranchDto

                    match branchEvent.Event with
                    // Don't save these reference creation events, and don't send them as events; that was done by the Reference actor when the reference was created.
                    | Assigned(referenceDto, _, _, _)
                    | Promoted(referenceDto, _, _, _)
                    | Committed(referenceDto, _, _, _)
                    | Checkpointed(referenceDto, _, _, _)
                    | Saved(referenceDto, _, _, _)
                    | Tagged(referenceDto, _, _, _)
                    | ExternalCreated(referenceDto, _, _, _) -> branchEvent.Metadata.Properties[nameof (ReferenceId)] <- $"{referenceDto.ReferenceId}"
                    | Rebased referenceId -> branchEvent.Metadata.Properties[nameof (ReferenceId)] <- $"{referenceId}"
                    // Save the rest of the events.
                    | _ ->
                        // For all other events, add the event to the branchEvents list, and save it to actor state.
                        branchEvents.Add(branchEvent)
                        do! Storage.SaveState stateManager eventsStateName branchEvents branchEvent.Metadata.CorrelationId

                        // Publish the event to the rest of the world.
                        let graceEvent = Events.GraceEvent.BranchEvent branchEvent
                        let message = serialize graceEvent
                        do! daprClient.PublishEventAsync(GracePubSubService, GraceEventStreamTopic, graceEvent)

                    let returnValue = GraceReturnValue.Create "Branch command succeeded." branchEvent.Metadata.CorrelationId

                    returnValue
                        .enhance(nameof (RepositoryId), $"{branchDto.RepositoryId}")
                        .enhance(nameof (BranchId), $"{branchDto.BranchId}")
                        .enhance(nameof (BranchName), $"{branchDto.BranchName}")
                        .enhance(nameof (ParentBranchId), $"{branchDto.ParentBranchId}")
                        .enhance (nameof (BranchEventType), $"{getDiscriminatedUnionFullName branchEvent.Event}")
                    |> ignore

                    // If the event has a referenceId, add it to the return properties.
                    if branchEvent.Metadata.Properties.ContainsKey(nameof (ReferenceId)) then
                        returnValue.Properties.Add(nameof (ReferenceId), branchEvent.Metadata.Properties[nameof (ReferenceId)])

                    return Ok returnValue
                with ex ->
                    let exceptionResponse = ExceptionResponse.Create ex
                    let graceError = GraceError.Create (BranchError.getErrorMessage FailedWhileApplyingEvent) branchEvent.Metadata.CorrelationId

                    graceError
                        .enhance("Exception details", exceptionResponse.``exception`` + exceptionResponse.innerException)
                        .enhance(nameof (RepositoryId), $"{branchDto.RepositoryId}")
                        .enhance(nameof (BranchId), $"{branchDto.BranchId}")
                        .enhance(nameof (BranchName), $"{branchDto.BranchName}")
                        .enhance(nameof (ParentBranchId), $"{branchDto.ParentBranchId}")
                        .enhance (nameof (BranchEventType), $"{getDiscriminatedUnionFullName branchEvent.Event}")
                    |> ignore

                    // If the event has a referenceId, add it to the return properties.
                    if branchEvent.Metadata.Properties.ContainsKey(nameof (ReferenceId)) then
                        graceError.enhance (nameof (ReferenceId), branchEvent.Metadata.Properties[nameof (ReferenceId)])
                        |> ignore

                    return Error graceError
            }

        interface IGraceReminder with
            /// Schedules a Grace reminder.
            member this.ScheduleReminderAsync reminderType delay state correlationId =
                task {
                    let reminder = ReminderDto.Create actorName $"{this.Id}" reminderType (getFutureInstant delay) state correlationId
                    do! createReminder reminder
                }
                :> Task

            /// Receives a Grace reminder.
            member this.ReceiveReminderAsync(reminder: ReminderDto) : Task<Result<unit, GraceError>> =
                task {
                    match reminder.ReminderType with
                    | ReminderTypes.PhysicalDeletion ->
                        // Get values from state.
                        let (repositoryId, branchId, branchName, parentBranchId, deleteReason, correlationId) =
                            deserialize<PhysicalDeletionReminderState> reminder.State

                        this.correlationId <- correlationId

                        // Delete saved state for this actor.
                        let! deletedEventsState = Storage.DeleteState stateManager eventsStateName

                        log.LogInformation(
                            "{CurrentInstant}: CorrelationId: {correlationId}; Deleted physical state for branch; RepositoryId: {repositoryId}; BranchId: {branchId}; BranchName: {branchName}; ParentBranchId: {parentBranchId}; deleteReason: {deleteReason}.",
                            getCurrentInstantExtended (),
                            correlationId,
                            repositoryId,
                            branchId,
                            branchName,
                            parentBranchId,
                            deleteReason
                        )

                        // Set all values to default.
                        branchDto <- BranchDto.Default
                        branchEvents.Clear()

                        // Mark the actor as disposed, in case someone tries to use it before Dapr GC's it.
                        isDisposed <- true
                        return Ok()
                    | _ ->
                        return
                            Error(
                                GraceError.Create
                                    $"{actorName} does not process reminder type {getDiscriminatedUnionCaseName reminder.ReminderType}."
                                    this.correlationId
                            )
                }

        interface IBranchActor with

            member this.GetEvents correlationId =
                task {
                    this.correlationId <- correlationId
                    return branchEvents :> IReadOnlyList<BranchEvent>
                }

            member this.Exists correlationId =
                this.correlationId <- correlationId
                branchDto.UpdatedAt.IsSome |> returnTask

            member this.IsDeleted correlationId =
                this.correlationId <- correlationId
                branchDto.DeletedAt.IsSome |> returnTask

            member this.Handle command metadata =
                let isValid (command: BranchCommand) (metadata: EventMetadata) =
                    task {
                        if
                            branchEvents.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId)
                            && (branchEvents.Count > 3)
                        then
                            return Error(GraceError.Create (BranchError.getErrorMessage DuplicateCorrelationId) metadata.CorrelationId)
                        else
                            match command with
                            | BranchCommand.Create(branchId, branchName, parentBranchId, basedOn, repositoryId, branchPermissions) ->
                                match branchDto.UpdatedAt with
                                | Some _ -> return Error(GraceError.Create (BranchError.getErrorMessage BranchAlreadyExists) metadata.CorrelationId)
                                | None -> return Ok command
                            | _ ->
                                match branchDto.UpdatedAt with
                                | Some _ -> return Ok command
                                | None -> return Error(GraceError.Create (BranchError.getErrorMessage BranchDoesNotExist) metadata.CorrelationId)
                    }

                let addReference repositoryId branchId directoryId sha256Hash referenceText referenceType links =
                    task {
                        let referenceId: ReferenceId = ReferenceId.NewGuid()
                        let referenceActor = Reference.CreateActorProxy referenceId this.correlationId

                        let referenceDto =
                            { ReferenceDto.Default with
                                ReferenceId = referenceId
                                RepositoryId = repositoryId
                                BranchId = branchId
                                DirectoryId = directoryId
                                Sha256Hash = sha256Hash
                                ReferenceText = referenceText
                                ReferenceType = referenceType
                                Links = links
                                CreatedAt = metadata.Timestamp
                                UpdatedAt = Some metadata.Timestamp }

                        let referenceCommand = Reference.ReferenceCommand.Create referenceDto

                        match! referenceActor.Handle referenceCommand metadata with
                        | Ok _ -> return Ok referenceDto
                        | Error error -> return Error error
                    }

                let addReferenceToCurrentBranch = addReference branchDto.RepositoryId branchDto.BranchId

                let processCommand (command: BranchCommand) (metadata: EventMetadata) =
                    task {
                        try
                            let! event =
                                task {
                                    match command with
                                    | Create(branchId, branchName, parentBranchId, basedOn, repositoryId, branchPermissions) ->
                                        // Add an initial Rebase reference to this branch that points to the BasedOn reference, unless we're creating `main`.
                                        if branchName <> InitialBranchName then
                                            // We need to get the reference that we're rebasing on, so we can get the directoryId and sha256Hash.
                                            let referenceActorProxy = Reference.CreateActorProxy basedOn this.correlationId
                                            let! promotionDto = referenceActorProxy.Get this.correlationId

                                            match!
                                                addReference
                                                    repositoryId
                                                    branchId
                                                    promotionDto.DirectoryId
                                                    promotionDto.Sha256Hash
                                                    promotionDto.ReferenceText
                                                    ReferenceType.Rebase
                                                    [| ReferenceLinkType.BasedOn promotionDto.ReferenceId |]
                                            with
                                            | Ok rebaseReferenceDto ->
                                                //logToConsole $"In BranchActor.Handle.processCommand: rebaseReferenceDto: {rebaseReferenceDto}."
                                                ()
                                            | Error error ->
                                                logToConsole
                                                    $"In BranchActor.Handle.processCommand: Error rebasing on referenceId: {basedOn}. promotionDto: {serialize promotionDto}"

                                        memoryCache.CreateBranchNameEntry $"{repositoryId}" branchName branchId

                                        return Ok(Created(branchId, branchName, parentBranchId, basedOn, repositoryId, branchPermissions))
                                    | BranchCommand.Rebase referenceId ->
                                        metadata.Properties["BasedOn"] <- $"{referenceId}"
                                        metadata.Properties[nameof (ReferenceId)] <- $"{referenceId}"
                                        metadata.Properties[nameof (RepositoryId)] <- $"{branchDto.RepositoryId}"
                                        metadata.Properties[nameof (BranchId)] <- $"{this.Id}"
                                        metadata.Properties[nameof (BranchName)] <- $"{branchDto.BranchName}"

                                        // We need to get the reference that we're rebasing on, so we can get the directoryId and sha256Hash.
                                        let referenceActorProxy = Reference.CreateActorProxy referenceId this.correlationId
                                        let! promotionDto = referenceActorProxy.Get metadata.CorrelationId

                                        // Add the Rebase reference to this branch.
                                        match!
                                            addReferenceToCurrentBranch
                                                promotionDto.DirectoryId
                                                promotionDto.Sha256Hash
                                                promotionDto.ReferenceText
                                                ReferenceType.Rebase
                                                [| ReferenceLinkType.BasedOn promotionDto.ReferenceId |]
                                        with
                                        | Ok rebaseReferenceDto ->
                                            //logToConsole $"In BranchActor.Handle.processCommand: rebaseReferenceDto: {rebaseReferenceDto}."
                                            return Ok(Rebased referenceId)
                                        | Error error ->
                                            logToConsole
                                                $"In BranchActor.Handle.processCommand: Error rebasing on referenceId: {referenceId}. promotionDto: {serialize promotionDto}"

                                            return Error error
                                    | SetName branchName -> return Ok(NameSet branchName)
                                    | EnableAssign enabled -> return Ok(EnabledAssign enabled)
                                    | EnablePromotion enabled -> return Ok(EnabledPromotion enabled)
                                    | EnableCommit enabled -> return Ok(EnabledCommit enabled)
                                    | EnableCheckpoint enabled -> return Ok(EnabledCheckpoint enabled)
                                    | EnableSave enabled -> return Ok(EnabledSave enabled)
                                    | EnableTag enabled -> return Ok(EnabledTag enabled)
                                    | EnableExternal enabled -> return Ok(EnabledExternal enabled)
                                    | EnableAutoRebase enabled -> return Ok(EnabledAutoRebase enabled)
                                    | BranchCommand.Assign(directoryVersionId, sha256Hash, referenceText) ->
                                        match! addReferenceToCurrentBranch directoryVersionId sha256Hash referenceText ReferenceType.Promotion Array.empty with
                                        | Ok referenceDto -> return Ok(Assigned(referenceDto, directoryVersionId, sha256Hash, referenceText))
                                        | Error error -> return Error error
                                    | BranchCommand.Promote(directoryVersionId, sha256Hash, referenceText) ->
                                        match! addReferenceToCurrentBranch directoryVersionId sha256Hash referenceText ReferenceType.Promotion Array.empty with
                                        | Ok referenceDto -> return Ok(Promoted(referenceDto, directoryVersionId, sha256Hash, referenceText))
                                        | Error error -> return Error error
                                    | BranchCommand.Commit(directoryVersionId, sha256Hash, referenceText) ->
                                        match! addReferenceToCurrentBranch directoryVersionId sha256Hash referenceText ReferenceType.Commit Array.empty with
                                        | Ok referenceDto -> return Ok(Committed(referenceDto, directoryVersionId, sha256Hash, referenceText))
                                        | Error error -> return Error error
                                    | BranchCommand.Checkpoint(directoryVersionId, sha256Hash, referenceText) ->
                                        match! addReferenceToCurrentBranch directoryVersionId sha256Hash referenceText ReferenceType.Checkpoint Array.empty with
                                        | Ok referenceDto -> return Ok(Checkpointed(referenceDto, directoryVersionId, sha256Hash, referenceText))
                                        | Error error -> return Error error
                                    | BranchCommand.Save(directoryVersionId, sha256Hash, referenceText) ->
                                        match! addReferenceToCurrentBranch directoryVersionId sha256Hash referenceText ReferenceType.Save Array.empty with
                                        | Ok referenceDto -> return Ok(Saved(referenceDto, directoryVersionId, sha256Hash, referenceText))
                                        | Error error -> return Error error
                                    | BranchCommand.Tag(directoryVersionId, sha256Hash, referenceText) ->
                                        match! addReferenceToCurrentBranch directoryVersionId sha256Hash referenceText ReferenceType.Tag Array.empty with
                                        | Ok referenceDto -> return Ok(Tagged(referenceDto, directoryVersionId, sha256Hash, referenceText))
                                        | Error error -> return Error error
                                    | BranchCommand.CreateExternal(directoryVersionId, sha256Hash, referenceText) ->
                                        match! addReferenceToCurrentBranch directoryVersionId sha256Hash referenceText ReferenceType.External Array.empty with
                                        | Ok referenceDto -> return Ok(ExternalCreated(referenceDto, directoryVersionId, sha256Hash, referenceText))
                                        | Error error -> return Error error
                                    | RemoveReference referenceId -> return Ok(ReferenceRemoved referenceId)
                                    | DeleteLogical(force, deleteReason) ->
                                        let repositoryActorProxy = Repository.CreateActorProxy branchDto.RepositoryId metadata.CorrelationId
                                        let! repositoryDto = repositoryActorProxy.Get(metadata.CorrelationId)

                                        // Delete the references for this branch.
                                        let! references = getReferences branchDto.BranchId Int32.MaxValue metadata.CorrelationId

                                        do!
                                            Parallel.ForEachAsync(
                                                references,
                                                Constants.ParallelOptions,
                                                (fun reference ct ->
                                                    ValueTask(
                                                        task {
                                                            let referenceActorProxy = Reference.CreateActorProxy reference.ReferenceId metadata.CorrelationId
                                                            let metadata = EventMetadata.New metadata.CorrelationId GraceSystemUser

                                                            match!
                                                                referenceActorProxy.Handle
                                                                    (Reference.ReferenceCommand.DeleteLogical(true, deleteReason))
                                                                    metadata
                                                            with
                                                            | Ok _ -> ()
                                                            | Error error ->
                                                                log.LogError(
                                                                    "{CurrentInstant}: Error deleting reference {ReferenceId}: {Error}",
                                                                    getCurrentInstantExtended (),
                                                                    reference.ReferenceId,
                                                                    error
                                                                )

                                                        }
                                                        :> Task
                                                    ))
                                            )

                                        let (reminderState: PhysicalDeletionReminderState) =
                                            (branchDto.RepositoryId,
                                             branchDto.BranchId,
                                             branchDto.BranchName,
                                             branchDto.ParentBranchId,
                                             deleteReason,
                                             metadata.CorrelationId)

                                        do!
                                            (this :> IGraceReminder).ScheduleReminderAsync
                                                ReminderTypes.PhysicalDeletion
                                                (Duration.FromDays(float repositoryDto.LogicalDeleteDays))
                                                (serialize reminderState)
                                                metadata.CorrelationId

                                        return Ok(LogicalDeleted(force, deleteReason))
                                    | DeletePhysical ->
                                        isDisposed <- true
                                        return Ok PhysicalDeleted
                                    | Undelete -> return Ok Undeleted
                                }

                            match event with
                            | Ok event -> return! this.ApplyEvent { Event = event; Metadata = metadata }
                            | Error error -> return Error error
                        with ex ->
                            return Error(GraceError.Create $"{ExceptionResponse.Create ex}" metadata.CorrelationId)
                    }

                task {
                    currentCommand <- getDiscriminatedUnionCaseName command
                    this.correlationId <- metadata.CorrelationId

                    match! isValid command metadata with
                    | Ok command -> return! processCommand command metadata
                    | Error error -> return Error error
                }

            member this.Get correlationId =
                task {
                    this.correlationId <- correlationId

                    if branchDto.ShouldRecomputeLatestReferences then
                        let! branchDtoWithLatestReferences = updateLatestReferences branchDto correlationId
                        branchDto <- branchDtoWithLatestReferences

                    return branchDto
                }

            member this.GetParentBranch correlationId =
                task {
                    this.correlationId <- correlationId
                    let branchActorProxy = Branch.CreateActorProxy branchDto.ParentBranchId correlationId

                    return! branchActorProxy.Get correlationId
                }

            member this.GetLatestCommit correlationId =
                this.correlationId <- correlationId
                branchDto.LatestCommit |> returnTask

            member this.GetLatestPromotion correlationId =
                this.correlationId <- correlationId
                branchDto.LatestPromotion |> returnTask

            member this.MarkForRecompute(correlationId: CorrelationId) : Task =
                this.correlationId <- correlationId
                branchDto <- { branchDto with ShouldRecomputeLatestReferences = true }
                Task.CompletedTask
