namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Shared
open Grace.Shared.Commands
open Grace.Shared.Commands.Branch
open Grace.Shared.Constants
open Grace.Shared.Dto.Branch
open Grace.Shared.Dto.Reference
open Grace.Shared.Events.Branch
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors.Branch
open Grace.Types.Types
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Diagnostics
open System.Linq
open System.Runtime.Serialization
open System.Text
open System.Threading.Tasks
open System.Text.Json
open System.Net.Http.Json
open FSharpPlus.Data.MultiMap
open System.Threading

module Branch =

    // Branch should support logical deletes with physical deletes set using a Dapr Timer based on a repository-level setting.
    // Branch Deletion should enumerate and delete each reference in the branch.

    /// The data types stored in physical deletion reminders.
    type PhysicalDeletionReminderState = (RepositoryId * BranchId * BranchName * ParentBranchId * DeleteReason * CorrelationId)

    type BranchActor([<PersistentState(StateName.Branch, Constants.GraceActorStorage)>] state: IPersistentState<List<BranchEvent>>, log: ILogger<BranchActor>) =
        inherit Grain()

        static let actorName = ActorName.Branch

        let mutable branchDto: BranchDto = BranchDto.Default

        let mutable currentCommand = String.Empty

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
                                        let! referenceActorProxy = Reference.CreateActorProxy basedOn repositoryId correlationId
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
                            let! referenceActorProxy = Reference.CreateActorProxy referenceId branchDto.RepositoryId branchEvent.Metadata.CorrelationId
                            let! referenceDto = referenceActorProxy.Get branchEvent.Metadata.CorrelationId
                            return { currentBranchDto with BasedOn = referenceDto }
                        }
                    | NameSet branchName -> { currentBranchDto with BranchName = branchName } |> returnTask
                    | Assigned(referenceDto, directoryVersion, sha256Hash, referenceText) ->
                        { currentBranchDto with LatestPromotion = referenceDto; BasedOn = referenceDto; ShouldRecomputeLatestReferences = true }
                        |> returnTask
                    | Promoted(referenceDto, directoryVersion, sha256Hash, referenceText) ->
                        { currentBranchDto with LatestPromotion = referenceDto; BasedOn = referenceDto; ShouldRecomputeLatestReferences = true }
                        |> returnTask
                    | Committed(referenceDto, directoryVersion, sha256Hash, referenceText) ->
                        { currentBranchDto with LatestCommit = referenceDto; ShouldRecomputeLatestReferences = true }
                        |> returnTask
                    | Checkpointed(referenceDto, directoryVersion, sha256Hash, referenceText) ->
                        { currentBranchDto with LatestCheckpoint = referenceDto; ShouldRecomputeLatestReferences = true }
                        |> returnTask
                    | Saved(referenceDto, directoryVersion, sha256Hash, referenceText) ->
                        { currentBranchDto with LatestSave = referenceDto; ShouldRecomputeLatestReferences = true }
                        |> returnTask
                    | Tagged(referenceDto, directoryVersion, sha256Hash, referenceText) ->
                        { currentBranchDto with ShouldRecomputeLatestReferences = true } |> returnTask
                    | ExternalCreated(referenceDto, directoryVersion, sha256Hash, referenceText) ->
                        { currentBranchDto with ShouldRecomputeLatestReferences = true } |> returnTask
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

                // Get the enabled reference types. This allows us to limit the ReferenceTypes we search for.
                let enabledReferenceTypes = List<ReferenceType>()

                if branchDto.PromotionEnabled then
                    enabledReferenceTypes.Add(ReferenceType.Promotion)

                if branchDto.CommitEnabled then enabledReferenceTypes.Add(ReferenceType.Commit)

                if branchDto.CheckpointEnabled then
                    enabledReferenceTypes.Add(ReferenceType.Checkpoint)

                if branchDto.SaveEnabled then enabledReferenceTypes.Add(ReferenceType.Save)
                if branchDto.TagEnabled then enabledReferenceTypes.Add(ReferenceType.Tag)

                if branchDto.ExternalEnabled then
                    enabledReferenceTypes.Add(ReferenceType.External)

                if branchDto.AutoRebaseEnabled then
                    enabledReferenceTypes.Add(ReferenceType.Rebase)

                let referenceTypes = enabledReferenceTypes.ToArray()

                // Get the latest references.
                let! latestReferences = getLatestReferenceByReferenceTypes referenceTypes branchDto.BranchId

                // Get the latest reference of any type.
                let latestReference =
                    latestReferences.Values
                        .OrderByDescending(fun referenceDto -> referenceDto.UpdatedAt)
                        .FirstOrDefault(ReferenceDto.Default)

                newBranchDto <- { newBranchDto with LatestReference = latestReference }

                // Get the latest reference of each type.
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

                        let! basedOnReferenceActorProxy = Reference.CreateActorProxy basedOnReferenceId branchDto.RepositoryId correlationId
                        let! basedOnReferenceDto = basedOnReferenceActorProxy.Get correlationId

                        newBranchDto <- { newBranchDto with BasedOn = basedOnReferenceDto }
                    | External -> ()
                    | Tag -> ()

                return { newBranchDto with ShouldRecomputeLatestReferences = false }
            }

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            let correlationId =
                match memoryCache.GetCorrelationIdEntry this.IdentityString with
                | Some correlationId -> correlationId
                | None -> String.Empty

            logActorActivation log this.IdentityString (getActorActivationMessage state.RecordExists)

            Task.CompletedTask

        member private this.ApplyEvent branchEvent =
            task {
                try
                    // Update the branchDto with the event.
                    let! updatedBranchDto = branchDto |> updateDto branchEvent this.correlationId
                    branchDto <- updatedBranchDto
                    branchEvent.Metadata.Properties[nameof (RepositoryId)] <- $"{branchDto.RepositoryId}"

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
                        state.State.Add branchEvent
                        do! state.WriteStateAsync()

                        // Publish the event to the rest of the world.
                        let graceEvent = Events.GraceEvent.BranchEvent branchEvent
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

        interface IGraceReminderWithGuidKey with
            /// Schedules a Grace reminder.
            member this.ScheduleReminderAsync reminderType delay state correlationId =
                task {
                    let reminder =
                        ReminderDto.Create actorName $"{this.IdentityString}" branchDto.RepositoryId reminderType (getFutureInstant delay) state correlationId

                    do! createReminder reminder
                }
                :> Task

            /// Receives a Grace reminder.
            member this.ReceiveReminderAsync(reminder: ReminderDto) : Task<Result<unit, GraceError>> =
                this.correlationId <- reminder.CorrelationId

                task {
                    match reminder.ReminderType with
                    | ReminderTypes.PhysicalDeletion ->
                        // Get values from state.
                        let (repositoryId, branchId, branchName, parentBranchId, deleteReason, correlationId) =
                            deserialize<PhysicalDeletionReminderState> reminder.State

                        this.correlationId <- correlationId

                        // Delete saved state for this actor.
                        do! state.ClearStateAsync()

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

        interface IHasRepositoryId with
            member this.GetRepositoryId correlationId = branchDto.RepositoryId |> returnTask

        interface IBranchActor with

            member this.GetEvents correlationId =
                task {
                    this.correlationId <- correlationId
                    return state.State :> IReadOnlyList<BranchEvent>
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
                            state.State.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId)
                            && (state.State.Count > 3)
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
                        let! referenceActor = Reference.CreateActorProxy referenceId repositoryId this.correlationId

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
                                            let! referenceActorProxy = Reference.CreateActorProxy basedOn repositoryId this.correlationId
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
                                        metadata.Properties[nameof (BranchId)] <- $"{this.GetGrainId().GetGuidKey()}"
                                        metadata.Properties[nameof (BranchName)] <- $"{branchDto.BranchName}"

                                        // We need to get the reference that we're rebasing on, so we can get the directoryId and sha256Hash.
                                        let! referenceActorProxy = Reference.CreateActorProxy referenceId branchDto.RepositoryId this.correlationId
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
                                        let! repositoryActorProxy = Repository.CreateActorProxy branchDto.RepositoryId metadata.CorrelationId
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
                                                            let! referenceActorProxy =
                                                                Reference.CreateActorProxy reference.ReferenceId branchDto.RepositoryId metadata.CorrelationId

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
                                            (this :> IGraceReminderWithGuidKey).ScheduleReminderAsync
                                                ReminderTypes.PhysicalDeletion
                                                (Duration.FromDays(float repositoryDto.LogicalDeleteDays))
                                                (serialize reminderState)
                                                metadata.CorrelationId

                                        return Ok(LogicalDeleted(force, deleteReason))
                                    | DeletePhysical ->
                                        // Delete the state from storage, and deactivate the actor.
                                        do! state.ClearStateAsync()
                                        this.DeactivateOnIdle()
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
                    let! branchActorProxy = Branch.CreateActorProxy branchDto.ParentBranchId branchDto.RepositoryId correlationId

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
