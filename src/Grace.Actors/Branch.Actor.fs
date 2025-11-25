namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.Reference
open Grace.Types.Reminder
open Grace.Types.Repository
open Grace.Types.Branch
open Grace.Types.Events
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

    type BranchActor([<PersistentState(StateName.Branch, Constants.GraceActorStorage)>] state: IPersistentState<List<BranchEvent>>) =
        inherit Grain()

        static let actorName = ActorName.Branch

        let log = loggerFactory.CreateLogger("Branch.Actor")

        let mutable branchDto: BranchDto = BranchDto.Default

        let mutable currentCommand = String.Empty

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
                let! latestReferences = getLatestReferenceByReferenceTypes referenceTypes branchDto.RepositoryId branchDto.BranchId

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
                            |> Seq.find (fun link ->
                                match link with
                                | ReferenceLinkType.BasedOn _ -> true
                                | ReferenceLinkType.IncludedInPromotionGroup _ -> false
                                | ReferenceLinkType.PromotionGroupTerminal _ -> false)

                        let basedOnReferenceId =
                            match basedOnLink with
                            | ReferenceLinkType.BasedOn referenceId -> referenceId
                            | _ -> ReferenceId.Empty

                        let basedOnReferenceActorProxy = Reference.CreateActorProxy basedOnReferenceId branchDto.RepositoryId correlationId
                        let! basedOnReferenceDto = basedOnReferenceActorProxy.Get correlationId

                        newBranchDto <- { newBranchDto with BasedOn = basedOnReferenceDto }
                    | External -> ()
                    | Tag -> ()

                return { newBranchDto with ShouldRecomputeLatestReferences = false }
            }

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            branchDto <-
                state.State
                |> Seq.fold (fun branchDto branchEvent -> branchDto |> BranchDto.UpdateDto branchEvent) BranchDto.Default

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            Task.CompletedTask

        member private this.ApplyEvent branchEvent =
            task {
                try
                    // If the branchEvent is Created or Rebased, we need to get the reference that the branch is based on for updating the branchDto.
                    match branchEvent.Event with
                    | Created(branchId, branchName, parentBranchId, basedOn, ownerId, organizationId, repositoryId, branchPermissions) ->
                        let! basedOnReferenceDto =
                            if basedOn <> ReferenceId.Empty then
                                task {
                                    let referenceActorProxy = Reference.CreateActorProxy basedOn repositoryId branchEvent.Metadata.CorrelationId
                                    return! referenceActorProxy.Get branchEvent.Metadata.CorrelationId
                                }
                            else
                                ReferenceDto.Default |> returnTask

                        branchEvent.Metadata.Properties["basedOnReferenceDto"] <- serialize basedOnReferenceDto
                    | Rebased basedOn ->
                        let referenceActorProxy = Reference.CreateActorProxy basedOn branchDto.RepositoryId branchEvent.Metadata.CorrelationId
                        let! basedOnReferenceDto = referenceActorProxy.Get branchEvent.Metadata.CorrelationId
                        branchEvent.Metadata.Properties["basedOnReferenceDto"] <- serialize basedOnReferenceDto
                    | _ -> ()

                    // Update the branchDto with the event.
                    branchDto <- branchDto |> BranchDto.UpdateDto branchEvent
                    branchEvent.Metadata.Properties[nameof RepositoryId] <- branchDto.RepositoryId

                    match branchEvent.Event with
                    // Don't save these reference creation events, and don't send them as events; that was done by the Reference actor when the reference was created.
                    | Assigned(referenceDto, _, _, _)
                    | Promoted(referenceDto, _, _, _)
                    | Committed(referenceDto, _, _, _)
                    | Checkpointed(referenceDto, _, _, _)
                    | Saved(referenceDto, _, _, _)
                    | Tagged(referenceDto, _, _, _)
                    | ExternalCreated(referenceDto, _, _, _) -> branchEvent.Metadata.Properties[nameof ReferenceId] <- referenceDto.ReferenceId
                    | Rebased referenceId -> branchEvent.Metadata.Properties[nameof ReferenceId] <- referenceId
                    // Save the rest of the events.
                    | _ ->
                        // For all other events, add the event to the branchEvents list, and save it to actor state.
                        state.State.Add branchEvent
                        do! state.WriteStateAsync()

                        // Publish the event to the rest of the world.
                        let graceEvent = GraceEvent.BranchEvent branchEvent
                        let streamProvider = this.GetStreamProvider GraceEventStreamProvider
                        let stream = streamProvider.GetStream<GraceEvent>(StreamId.Create(GraceEventStreamTopic, branchDto.BranchId))
                        do! stream.OnNextAsync(graceEvent)

                    let returnValue = GraceReturnValue.Create "Branch command succeeded." branchEvent.Metadata.CorrelationId

                    returnValue
                        .enhance(nameof RepositoryId, branchDto.RepositoryId)
                        .enhance(nameof BranchId, branchDto.BranchId)
                        .enhance(nameof BranchName, branchDto.BranchName)
                        .enhance(nameof ParentBranchId, branchDto.ParentBranchId)
                        .enhance (nameof BranchEventType, getDiscriminatedUnionFullName branchEvent.Event)
                    |> ignore

                    // If the event has a referenceId, add it to the return properties.
                    if branchEvent.Metadata.Properties.ContainsKey(nameof ReferenceId) then
                        returnValue.Properties.Add(nameof ReferenceId, branchEvent.Metadata.Properties[nameof ReferenceId] :?> Guid)

                    // If there are child branch results, add them to the return properties.
                    if branchEvent.Metadata.Properties.ContainsKey("ChildBranchResults") then
                        returnValue.Properties.Add("ChildBranchResults", branchEvent.Metadata.Properties["ChildBranchResults"])

                    return Ok returnValue
                with ex ->
                    let graceError = GraceError.CreateWithException ex (getErrorMessage BranchError.FailedWhileApplyingEvent) branchEvent.Metadata.CorrelationId

                    graceError
                        .enhance(nameof RepositoryId, branchDto.RepositoryId)
                        .enhance(nameof BranchId, branchDto.BranchId)
                        .enhance(nameof BranchName, branchDto.BranchName)
                        .enhance(nameof ParentBranchId, branchDto.ParentBranchId)
                        .enhance (nameof BranchEventType, getDiscriminatedUnionFullName branchEvent.Event)
                    |> ignore

                    // If the event has a referenceId, add it to the return properties.
                    if branchEvent.Metadata.Properties.ContainsKey(nameof ReferenceId) then
                        graceError.enhance (nameof ReferenceId, branchEvent.Metadata.Properties[nameof ReferenceId])
                        |> ignore

                    return Error graceError
            }

        interface IGraceReminderWithGuidKey with
            /// Schedules a Grace reminder.
            member this.ScheduleReminderAsync reminderType delay state correlationId =
                task {
                    let reminder =
                        ReminderDto.Create
                            actorName
                            $"{this.IdentityString}"
                            branchDto.OwnerId
                            branchDto.OrganizationId
                            branchDto.RepositoryId
                            reminderType
                            (getFutureInstant delay)
                            state
                            correlationId

                    do! createReminder reminder
                }
                :> Task

            /// Receives a Grace reminder.
            member this.ReceiveReminderAsync(reminder: ReminderDto) : Task<Result<unit, GraceError>> =
                this.correlationId <- reminder.CorrelationId

                task {
                    match reminder.ReminderType, reminder.State with
                    | ReminderTypes.PhysicalDeletion, ReminderState.BranchPhysicalDeletion physicalDeletionReminderState ->
                        this.correlationId <- physicalDeletionReminderState.CorrelationId

                        // Delete saved state for this actor.
                        do! state.ClearStateAsync()

                        log.LogInformation(
                            "{CurrentInstant}: Node: {hostName}; CorrelationId: {correlationId}; Deleted physical state for branch; RepositoryId: {repositoryId}; BranchId: {branchId}; BranchName: {branchName}; ParentBranchId: {parentBranchId}; deleteReason: {deleteReason}.",
                            getCurrentInstantExtended (),
                            getMachineName,
                            physicalDeletionReminderState.CorrelationId,
                            physicalDeletionReminderState.RepositoryId,
                            physicalDeletionReminderState.BranchId,
                            physicalDeletionReminderState.BranchName,
                            physicalDeletionReminderState.ParentBranchId,
                            physicalDeletionReminderState.DeleteReason
                        )

                        this.DeactivateOnIdle()
                        return Ok()
                    | reminderType, state ->
                        return
                            Error(
                                GraceError.Create
                                    $"{actorName} does not process reminder type {getDiscriminatedUnionCaseName reminderType} with state {getDiscriminatedUnionCaseName state}."
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
                            return Error(GraceError.Create (getErrorMessage BranchError.DuplicateCorrelationId) metadata.CorrelationId)
                        else
                            match command with
                            | BranchCommand.Create(branchId, branchName, parentBranchId, basedOn, ownerId, organizationId, repositoryId, branchPermissions) ->
                                match branchDto.UpdatedAt with
                                | Some _ -> return Error(GraceError.Create (BranchError.getErrorMessage BranchAlreadyExists) metadata.CorrelationId)
                                | None -> return Ok command
                            | _ ->
                                match branchDto.UpdatedAt with
                                | Some _ -> return Ok command
                                | None -> return Error(GraceError.Create (getErrorMessage BranchError.BranchDoesNotExist) metadata.CorrelationId)
                    }

                let addReference ownerId organizationId repositoryId branchId directoryId sha256Hash referenceText referenceType links =
                    task {
                        let referenceId: ReferenceId = ReferenceId.NewGuid()
                        let referenceActor = Reference.CreateActorProxy referenceId repositoryId this.correlationId

                        let referenceCommand =
                            ReferenceCommand.Create(
                                referenceId,
                                ownerId,
                                organizationId,
                                repositoryId,
                                branchId,
                                directoryId,
                                sha256Hash,
                                referenceType,
                                referenceText,
                                links
                            )

                        return! referenceActor.Handle referenceCommand metadata
                    }

                let addReferenceToCurrentBranch = addReference branchDto.OwnerId branchDto.OrganizationId branchDto.RepositoryId branchDto.BranchId

                let processCommand (command: BranchCommand) (metadata: EventMetadata) =
                    task {
                        try
                            //logToConsole
                            //    $"In BranchActor.Handle.processCommand: command: {getDiscriminatedUnionFullName command}; metadata: {serialize metadata}."

                            let! event =
                                task {
                                    match command with
                                    | Create(branchId, branchName, parentBranchId, basedOn, ownerId, organizationId, repositoryId, branchPermissions) ->
                                        // Add an initial Rebase reference to this branch that points to the BasedOn reference, unless we're creating `main`.
                                        if branchName <> InitialBranchName then
                                            // We need to get the reference that we're rebasing on, so we can get the DirectoryId and Sha256Hash.
                                            let referenceActorProxy = Reference.CreateActorProxy basedOn repositoryId this.correlationId
                                            let! promotionDto = referenceActorProxy.Get this.correlationId

                                            match!
                                                addReference
                                                    branchDto.OwnerId
                                                    branchDto.OrganizationId
                                                    repositoryId
                                                    branchId
                                                    promotionDto.DirectoryId
                                                    promotionDto.Sha256Hash
                                                    promotionDto.ReferenceText
                                                    ReferenceType.Rebase
                                                    [ ReferenceLinkType.BasedOn promotionDto.ReferenceId ]
                                            with
                                            | Ok _ ->
                                                //logToConsole $"In BranchActor.Handle.processCommand: rebaseReferenceDto: {rebaseReferenceDto}."
                                                ()
                                            | Error error ->
                                                logToConsole
                                                    $"In BranchActor.Handle.processCommand: Error rebasing on referenceId: {basedOn}. promotionDto: {serialize promotionDto}"

                                        memoryCache.CreateBranchNameEntry(repositoryId, branchName, branchId)

                                        return
                                            Ok(Created(branchId, branchName, parentBranchId, basedOn, ownerId, organizationId, repositoryId, branchPermissions))
                                    | BranchCommand.Rebase referenceId ->
                                        metadata.Properties["BasedOn"] <- $"{referenceId}"
                                        metadata.Properties[nameof ReferenceId] <- $"{referenceId}"
                                        metadata.Properties[nameof RepositoryId] <- $"{branchDto.RepositoryId}"
                                        metadata.Properties[nameof BranchId] <- $"{this.GetGrainId().GetGuidKey()}"
                                        metadata.Properties[nameof BranchName] <- $"{branchDto.BranchName}"

                                        // We need to get the reference that we're rebasing on, so we can get the directoryId and sha256Hash.
                                        let referenceActorProxy = Reference.CreateActorProxy referenceId branchDto.RepositoryId this.correlationId
                                        let! promotionDto = referenceActorProxy.Get metadata.CorrelationId

                                        // Add the Rebase reference to this branch.
                                        match!
                                            addReferenceToCurrentBranch
                                                promotionDto.DirectoryId
                                                promotionDto.Sha256Hash
                                                promotionDto.ReferenceText
                                                ReferenceType.Rebase
                                                [ ReferenceLinkType.BasedOn promotionDto.ReferenceId ]
                                        with
                                        | Ok rebaseReferenceDto ->
                                            //logToConsole $"In BranchActor.Handle.processCommand: rebaseReferenceDto: {rebaseReferenceDto}."
                                            return Ok(Rebased referenceId)
                                        | Error error ->
                                            log.LogError(
                                                "{CurrentInstant}: Error rebasing on referenceId: {referenceId}; promotionDto: {promotionDto}.\n{Error}",
                                                getCurrentInstantExtended (),
                                                referenceId,
                                                serialize promotionDto,
                                                error
                                            )

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
                                    | SetPromotionMode promotionMode -> return Ok(PromotionModeSet promotionMode)
                                    | UpdateParentBranch newParentBranchId -> return Ok(ParentBranchUpdated newParentBranchId)
                                    | BranchCommand.Assign(directoryVersionId, sha256Hash, referenceText) ->
                                        match! addReferenceToCurrentBranch directoryVersionId sha256Hash referenceText ReferenceType.Promotion List.empty with
                                        | Ok returnValue -> return Ok(Assigned(returnValue.ReturnValue, directoryVersionId, sha256Hash, referenceText))
                                        | Error error -> return Error error
                                    | BranchCommand.Promote(directoryVersionId, sha256Hash, referenceText) ->
                                        match! addReferenceToCurrentBranch directoryVersionId sha256Hash referenceText ReferenceType.Promotion List.empty with
                                        | Ok returnValue -> return Ok(Promoted(returnValue.ReturnValue, directoryVersionId, sha256Hash, referenceText))
                                        | Error error -> return Error error
                                    | BranchCommand.Commit(directoryVersionId, sha256Hash, referenceText) ->
                                        match! addReferenceToCurrentBranch directoryVersionId sha256Hash referenceText ReferenceType.Commit List.empty with
                                        | Ok returnValue -> return Ok(Committed(returnValue.ReturnValue, directoryVersionId, sha256Hash, referenceText))
                                        | Error error -> return Error error
                                    | BranchCommand.Checkpoint(directoryVersionId, sha256Hash, referenceText) ->
                                        match! addReferenceToCurrentBranch directoryVersionId sha256Hash referenceText ReferenceType.Checkpoint List.empty with
                                        | Ok returnValue -> return Ok(Checkpointed(returnValue.ReturnValue, directoryVersionId, sha256Hash, referenceText))
                                        | Error error -> return Error error
                                    | BranchCommand.Save(directoryVersionId, sha256Hash, referenceText) ->
                                        match! addReferenceToCurrentBranch directoryVersionId sha256Hash referenceText ReferenceType.Save List.empty with
                                        | Ok returnValue -> return Ok(Saved(returnValue.ReturnValue, directoryVersionId, sha256Hash, referenceText))
                                        | Error error -> return Error error
                                    | BranchCommand.Tag(directoryVersionId, sha256Hash, referenceText) ->
                                        match! addReferenceToCurrentBranch directoryVersionId sha256Hash referenceText ReferenceType.Tag List.empty with
                                        | Ok returnValue -> return Ok(Tagged(returnValue.ReturnValue, directoryVersionId, sha256Hash, referenceText))
                                        | Error error -> return Error error
                                    | BranchCommand.CreateExternal(directoryVersionId, sha256Hash, referenceText) ->
                                        match! addReferenceToCurrentBranch directoryVersionId sha256Hash referenceText ReferenceType.External List.empty with
                                        | Ok returnValue -> return Ok(ExternalCreated(returnValue.ReturnValue, directoryVersionId, sha256Hash, referenceText))
                                        | Error error -> return Error error
                                    | RemoveReference referenceId -> return Ok(ReferenceRemoved referenceId)
                                    | DeleteLogical(force, deleteReason, reassignChildBranches, newParentBranchId) ->
                                        // Check for child branches
                                        let! childBranches =
                                            getChildBranches branchDto.RepositoryId branchDto.BranchId Int32.MaxValue false metadata.CorrelationId

                                        if childBranches.Length > 0 && not reassignChildBranches && not force then
                                            // Cannot delete branch with children without reassigning or forcing deletion
                                            return
                                                Error(
                                                    GraceError.Create
                                                        (BranchError.getErrorMessage BranchError.CannotDeleteBranchesWithChildrenWithoutReassigningChildren)
                                                        metadata.CorrelationId
                                                )
                                        else
                                            // Track results for child branch operations
                                            let childBranchResults = System.Collections.Concurrent.ConcurrentBag<string>()

                                            // If force is set and there are child branches, delete them recursively
                                            if force && childBranches.Length > 0 then
                                                do!
                                                    Parallel.ForEachAsync(
                                                        childBranches,
                                                        Constants.ParallelOptions,
                                                        (fun childBranch ct ->
                                                            ValueTask(
                                                                task {
                                                                    let childBranchActorProxy =
                                                                        Branch.CreateActorProxy
                                                                            childBranch.BranchId
                                                                            branchDto.RepositoryId
                                                                            metadata.CorrelationId

                                                                    let childMetadata = EventMetadata.New metadata.CorrelationId GraceSystemUser

                                                                    // Recursively delete child branch with force
                                                                    match! childBranchActorProxy.Handle (DeleteLogical(true, $"Parent branch {branchDto.BranchName} is being deleted.", false, None)) childMetadata with
                                                                    | Ok _ ->
                                                                        childBranchResults.Add($"Deleted child branch: {childBranch.BranchName}")
                                                                    | Error error ->
                                                                        log.LogError(
                                                                            "{CurrentInstant}: Error deleting child branch {ChildBranchId}: {Error}",
                                                                            getCurrentInstantExtended (),
                                                                            childBranch.BranchId,
                                                                            error
                                                                        )
                                                                        childBranchResults.Add($"Failed to delete child branch: {childBranch.BranchName}")
                                                                }
                                                                :> Task
                                                            ))
                                                    )

                                            // If reassigning children, determine the new parent and update them
                                            if reassignChildBranches && childBranches.Length > 0 then
                                                let targetParentBranchId =
                                                    match newParentBranchId with
                                                    | Some id -> id
                                                    | None -> branchDto.ParentBranchId // Use the deleted branch's parent

                                                // Reassign all child branches to the new parent
                                                do!
                                                    Parallel.ForEachAsync(
                                                        childBranches,
                                                        Constants.ParallelOptions,
                                                        (fun childBranch ct ->
                                                            ValueTask(
                                                                task {
                                                                    let childBranchActorProxy =
                                                                        Branch.CreateActorProxy
                                                                            childBranch.BranchId
                                                                            branchDto.RepositoryId
                                                                            metadata.CorrelationId

                                                                    let childMetadata = EventMetadata.New metadata.CorrelationId GraceSystemUser

                                                                    match! childBranchActorProxy.Handle (UpdateParentBranch targetParentBranchId) childMetadata with
                                                                    | Ok _ ->
                                                                        childBranchResults.Add($"Reassigned child branch: {childBranch.BranchName}")
                                                                    | Error error ->
                                                                        log.LogError(
                                                                            "{CurrentInstant}: Error updating parent branch for child {ChildBranchId}: {Error}",
                                                                            getCurrentInstantExtended (),
                                                                            childBranch.BranchId,
                                                                            error
                                                                        )
                                                                        childBranchResults.Add($"Failed to reassign child branch: {childBranch.BranchName}")
                                                                }
                                                                :> Task
                                                            ))
                                                    )

                                            // Now proceed with the deletion regardless of reassignment
                                            let repositoryActorProxy =
                                                Repository.CreateActorProxy branchDto.OrganizationId branchDto.RepositoryId metadata.CorrelationId

                                            let! repositoryDto = repositoryActorProxy.Get(metadata.CorrelationId)

                                            // Delete the references for this branch.
                                            let! references = getReferences branchDto.RepositoryId branchDto.BranchId Int32.MaxValue metadata.CorrelationId

                                            do!
                                                Parallel.ForEachAsync(
                                                    references,
                                                    Constants.ParallelOptions,
                                                    (fun reference ct ->
                                                        ValueTask(
                                                            task {
                                                                let referenceActorProxy =
                                                                    Reference.CreateActorProxy
                                                                        reference.ReferenceId
                                                                        branchDto.RepositoryId
                                                                        metadata.CorrelationId

                                                                let metadata = EventMetadata.New metadata.CorrelationId GraceSystemUser

                                                                match!
                                                                    referenceActorProxy.Handle
                                                                        (ReferenceCommand.DeleteLogical(
                                                                            true,
                                                                            $"Branch {branchDto.BranchName} is being deleted."
                                                                        ))
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

                                            let (physicalDeletionReminderState: PhysicalDeletionReminderState) =
                                                { RepositoryId = branchDto.RepositoryId
                                                  BranchId = branchDto.BranchId
                                                  BranchName = branchDto.BranchName
                                                  ParentBranchId = branchDto.ParentBranchId
                                                  DeleteReason = deleteReason
                                                  CorrelationId = metadata.CorrelationId }

                                            do!
                                                (this :> IGraceReminderWithGuidKey).ScheduleReminderAsync
                                                    ReminderTypes.PhysicalDeletion
                                                    (Duration.FromDays(float repositoryDto.LogicalDeleteDays))
                                                    (ReminderState.BranchPhysicalDeletion physicalDeletionReminderState)
                                                    metadata.CorrelationId

                                            // Add child branch results to metadata for output
                                            if childBranchResults.Count > 0 then
                                                metadata.Properties["ChildBranchResults"] <- childBranchResults.ToArray() |> String.concat Environment.NewLine

                                            return Ok(LogicalDeleted(force, deleteReason, reassignChildBranches, newParentBranchId))
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
                            log.LogError(
                                ex,
                                "{CurrentInstant}: In Branch.Actor.Handle.processCommand: Error processing command {Command}.",
                                getCurrentInstantExtended (),
                                getDiscriminatedUnionFullName command
                            )

                            return Error(GraceError.CreateWithException ex String.Empty metadata.CorrelationId)
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
                    let branchActorProxy = Branch.CreateActorProxy branchDto.ParentBranchId branchDto.RepositoryId correlationId

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
