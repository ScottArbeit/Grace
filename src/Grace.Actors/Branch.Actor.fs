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
open Grace.Types.Common
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.Linq
open System.Runtime.Serialization
open System.Text
open System.Threading.Tasks
open System.Text.Json
open System.Net.Http.Json
open FSharpPlus.Data.MultiMap
open System.Threading

/// Groups Orleans actor helpers for branch keys, proxies, state, or workflow transitions.
module Branch =

    /// Classifies whether a caller-owned Reference identity is new, an exact retry, or conflicting reuse.
    type internal ReferenceOperationDisposition =
        | NewReference
        | MatchingRetry
        | ConflictingReference

    /// Compares a Reference Create command with durable state under its caller-owned identity.
    let internal classifyReferenceOperation command existingReference =
        match command, existingReference with
        | ReferenceCommand.Create _, Some referenceDto ->
            if Reference.createCommandMatchesReference referenceDto command then
                MatchingRetry
            else
                ConflictingReference
        | ReferenceCommand.Create _, None -> NewReference
        | _ -> invalidArg (nameof command) "Reference operation classification requires a Reference Create command."

    /// Preserves duplicate-correlation rejection except when a durable exact Reference retry proves idempotency.
    let internal shouldRejectDuplicateCorrelation hasDuplicateCorrelation disposition =
        hasDuplicateCorrelation
        && disposition <> MatchingRetry

    /// Emits a Branch Reference transition only when the caller-owned Reference does not already exist.
    let internal shouldApplyReferenceEvent disposition = disposition = NewReference

    /// Keeps caller-owned operation identity when a projection event carries a different Reference id.
    let internal applyReferenceIdMetadata (properties: IDictionary<string, string>) referenceId =
        if not (properties.ContainsKey(nameof ReferenceId)) then
            properties[nameof ReferenceId] <- $"{referenceId}"

    /// Selects Reference types whose durable history can reconstruct the public Branch projection.
    let internal projectionReconstructionReferenceTypes (branchDto: BranchDto) =
        let referenceTypes = List<ReferenceType>()

        // A later permission snapshot cannot erase an ordinary Promotion that previously changed the branch base.
        referenceTypes.Add(ReferenceType.Promotion)

        if branchDto.CommitEnabled then referenceTypes.Add(ReferenceType.Commit)

        if branchDto.CheckpointEnabled then referenceTypes.Add(ReferenceType.Checkpoint)

        if branchDto.SaveEnabled then referenceTypes.Add(ReferenceType.Save)
        if branchDto.TagEnabled then referenceTypes.Add(ReferenceType.Tag)

        if branchDto.ExternalEnabled then referenceTypes.Add(ReferenceType.External)

        if branchDto.AutoRebaseEnabled then referenceTypes.Add(ReferenceType.Rebase)

        referenceTypes.ToArray()

    /// Compares a Branch Create command with the immutable facts in its durable Created event.
    let internal createCommandMatchesCreationEvent branchEventType command =
        match branchEventType, command with
        | Created (createdBranchId,
                   createdBranchName,
                   createdParentBranchId,
                   createdBasedOn,
                   createdOwnerId,
                   createdOrganizationId,
                   createdRepositoryId,
                   createdPermissions),
          BranchCommand.Create (branchId, branchName, parentBranchId, basedOn, _, ownerId, organizationId, repositoryId, initialPermissions) ->
            let durablePermissions = HashSet<ReferenceType>(createdPermissions)

            createdBranchId = branchId
            && createdBranchName = branchName
            && createdParentBranchId = parentBranchId
            && createdBasedOn = basedOn
            && createdOwnerId = ownerId
            && createdOrganizationId = organizationId
            && createdRepositoryId = repositoryId
            && durablePermissions.SetEquals(initialPermissions)
        | _ -> false

    /// Reports whether a Branch event is published from the Branch actor rather than by its Reference actor.
    let internal shouldPublishBranchEvent branchEventType =
        match branchEventType with
        | Assigned _
        | Promoted _
        | Committed _
        | Checkpointed _
        | Saved _
        | Tagged _
        | ExternalCreated _
        | Rebased _ -> false
        | _ -> true

    /// Reports whether a Branch event belongs in the durable stream consumed by ordered Watch replay.
    let internal shouldPersistBranchEvent branchEventType =
        match branchEventType with
        | Committed _
        | Checkpointed _
        | Saved _ -> true
        | _ -> shouldPublishBranchEvent branchEventType

    /// Extracts the durable Reference projection carried by one Branch Reference transition.
    let internal tryGetReferenceFromBranchEvent branchEventType =
        match branchEventType with
        | Assigned (referenceDto, _, _, _, _)
        | Promoted (referenceDto, _, _, _, _)
        | Committed (referenceDto, _, _, _, _)
        | Checkpointed (referenceDto, _, _, _, _)
        | Saved (referenceDto, _, _, _, _)
        | Tagged (referenceDto, _, _, _, _)
        | ExternalCreated (referenceDto, _, _, _, _) -> Some referenceDto
        | _ -> None

    /// Reconciles one successfully republished durable Reference into missing or older Branch projection slots.
    let internal reconcileReferenceProjection (branchDto: BranchDto) (referenceDto: ReferenceDto) =
        let shouldAdvance (currentReference: ReferenceDto) =
            if currentReference.ReferenceId = ReferenceId.Empty then
                true
            else
                match currentReference.UpdatedAt, referenceDto.UpdatedAt with
                | None, Some _ -> true
                | Some currentTimestamp, Some referenceTimestamp -> referenceTimestamp > currentTimestamp
                | _ -> false

        let advanceLatestReference = shouldAdvance branchDto.LatestReference
        let mutable recovered = branchDto
        let mutable typedProjectionChanged = false

        let updateTypedProjection currentReference update =
            if shouldAdvance currentReference then
                recovered <- update recovered referenceDto
                typedProjectionChanged <- true

        match referenceDto.ReferenceType with
        | ReferenceType.Promotion ->
            updateTypedProjection recovered.LatestPromotion (fun branch reference -> { branch with LatestPromotion = reference; BasedOn = reference })
        | ReferenceType.Commit -> updateTypedProjection recovered.LatestCommit (fun branch reference -> { branch with LatestCommit = reference })
        | ReferenceType.Checkpoint -> updateTypedProjection recovered.LatestCheckpoint (fun branch reference -> { branch with LatestCheckpoint = reference })
        | ReferenceType.Save -> updateTypedProjection recovered.LatestSave (fun branch reference -> { branch with LatestSave = reference })
        | ReferenceType.Tag
        | ReferenceType.External
        | ReferenceType.Rebase -> ()

        let projectionChanged = typedProjectionChanged || advanceLatestReference

        recovered <-
            { recovered with
                LatestReference = if advanceLatestReference then referenceDto else recovered.LatestReference
                ShouldRecomputeLatestReferences =
                    recovered.ShouldRecomputeLatestReferences
                    || projectionChanged
            }

        recovered, projectionChanged

    /// Reconciles a durable Rebase Reference without allowing a late retry to replace a newer branch base.
    let internal reconcileRebaseProjection (branchDto: BranchDto) (referenceDto: ReferenceDto) (basedOnReferenceDto: ReferenceDto) =
        let recovered, projectionChanged = reconcileReferenceProjection branchDto referenceDto

        if projectionChanged then
            { recovered with BasedOn = basedOnReferenceDto }, true
        else
            recovered, false

    /// Returns the PromotionSet that owns a generated promotion Reference, if one is linked.
    let internal tryGetPromotionSetId (referenceDto: ReferenceDto) =
        referenceDto.Links
        |> Seq.tryPick (fun link ->
            match link with
            | ReferenceLinkType.IncludedInPromotionSet promotionSetId -> Some promotionSetId
            | _ -> None)

    /// Reports whether a PromotionSet Reference carries the matching terminal link required for public projection.
    let internal isPromotionSetTerminalReference promotionSetId (referenceDto: ReferenceDto) =
        referenceDto.Links
        |> Seq.exists (fun link ->
            match link with
            | ReferenceLinkType.PromotionSetTerminal terminalPromotionSetId -> terminalPromotionSetId = promotionSetId
            | _ -> false)

    /// Allows ordinary promotions or the current terminal output from a durably successful PromotionSet into Branch projections.
    let internal canProjectPromotionReference promotionSetStatus expectedTerminalReferenceId (referenceDto: ReferenceDto) =
        match tryGetPromotionSetId referenceDto with
        | None -> true
        | Some promotionSetId ->
            isPromotionSetTerminalReference promotionSetId referenceDto
            && promotionSetStatus = Some Grace.Types.PromotionSet.PromotionSetStatus.Succeeded
            && expectedTerminalReferenceId = Some referenceDto.ReferenceId

    /// Orders distinct Promotion candidates while allowing direct durable actor state to bridge query visibility lag.
    let internal orderPromotionCandidates
        (queriedPromotions: ReferenceDto array)
        (latestTerminalPromotion: ReferenceDto option)
        (currentDurablePromotion: ReferenceDto option)
        =
        seq {
            match currentDurablePromotion with
            | Some referenceDto -> yield referenceDto
            | None -> ()

            match latestTerminalPromotion with
            | Some referenceDto -> yield referenceDto
            | None -> ()

            yield! queriedPromotions
        }
        |> Seq.filter (fun referenceDto -> referenceDto.ReferenceId <> ReferenceId.Empty)
        |> Seq.distinctBy (fun referenceDto -> referenceDto.ReferenceId)
        |> Seq.sortByDescending (fun referenceDto -> referenceDto.CreatedAt)
        |> Seq.toArray

    /// Selects BasedOn from the newest projectable Promotion or Rebase transition while leaving typed slots independent.
    let internal selectBasedOnProjection
        (durableBase: ReferenceDto)
        (latestRebase: (ReferenceDto * ReferenceDto) option)
        (latestProjectablePromotion: ReferenceDto option)
        =
        match latestRebase, latestProjectablePromotion with
        | Some (rebaseTransition, rebaseTarget), Some promotion when rebaseTransition.CreatedAt > promotion.CreatedAt -> rebaseTarget
        | _, Some promotion -> promotion
        | Some (_, rebaseTarget), None -> rebaseTarget
        | None, None -> durableBase

    /// Builds the public Branch command result without mutating the aggregate a second time for exact Commit retries.
    let private createBranchCommandReturnValue (branchDto: BranchDto) (branchEvent: BranchEvent) =
        let returnValue = GraceReturnValue.Create "Branch command succeeded." branchEvent.Metadata.CorrelationId

        returnValue
            .enhance(nameof RepositoryId, branchDto.RepositoryId)
            .enhance(nameof BranchId, branchDto.BranchId)
            .enhance(nameof BranchName, branchDto.BranchName)
            .enhance(nameof ParentBranchId, branchDto.ParentBranchId)
            .enhance (nameof BranchEventType, getDiscriminatedUnionFullName branchEvent.Event)
        |> ignore

        if branchEvent.Metadata.Properties.ContainsKey(nameof ReferenceId) then
            returnValue.Properties.Add(nameof ReferenceId, Guid.Parse(branchEvent.Metadata.Properties[nameof ReferenceId]))

        if branchEvent.Metadata.Properties.ContainsKey("ChildBranchResults") then
            returnValue.Properties.Add("ChildBranchResults", branchEvent.Metadata.Properties["ChildBranchResults"])

        returnValue

    /// Implements the Orleans grain for branch actor.
    type BranchActor([<PersistentState(StateName.Branch, Constants.GraceActorStorage)>] state: IPersistentState<List<BranchEvent>>) =
        inherit Grain()

        static let actorName = ActorName.Branch

        let log = loggerFactory.CreateLogger("Branch.Actor")

        let mutable branchDto: BranchDto = BranchDto.Default

        let mutable currentCommand = String.Empty

        /// Finds the newest promotion that can truthfully advance the public Branch projection.
        let getLatestProjectablePromotion (branchDto: BranchDto) correlationId =
            task {
                let! promotions = getPromotions branchDto.RepositoryId branchDto.BranchId 500 correlationId
                let! latestTerminalPromotion = getLatestPromotion branchDto.RepositoryId branchDto.BranchId

                let! currentDurablePromotion =
                    if branchDto.LatestPromotion.ReferenceId = ReferenceId.Empty then
                        Task.FromResult<Option<ReferenceDto>> None
                    else
                        task {
                            let referenceActor = Reference.CreateActorProxy branchDto.LatestPromotion.ReferenceId branchDto.RepositoryId correlationId

                            let! referenceDto = referenceActor.Get correlationId

                            if referenceDto.ReferenceId = ReferenceId.Empty then
                                return None
                            else
                                return Some referenceDto
                        }

                let orderedPromotions = orderPromotionCandidates promotions latestTerminalPromotion currentDurablePromotion

                let mutable projectablePromotion: ReferenceDto option = None
                let mutable index = 0

                while index < orderedPromotions.Length
                      && projectablePromotion.IsNone do
                    let referenceDto = orderedPromotions[index]

                    if referenceDto.DeletedAt.IsNone then
                        match tryGetPromotionSetId referenceDto with
                        | None -> projectablePromotion <- Some referenceDto
                        | Some promotionSetId when isPromotionSetTerminalReference promotionSetId referenceDto ->
                            let promotionSetActorProxy =
                                Grace.Actors.Extensions.ActorProxy.PromotionSet.CreateActorProxy promotionSetId branchDto.RepositoryId correlationId

                            let! promotionSetDto = promotionSetActorProxy.Get correlationId
                            let! promotionSetEvents = promotionSetActorProxy.GetEvents correlationId

                            let expectedTerminalReferenceId =
                                promotionSetEvents
                                |> Seq.rev
                                |> Seq.tryPick (fun promotionSetEvent ->
                                    match promotionSetEvent.Event with
                                    | Grace.Types.PromotionSet.PromotionSetEventType.Applied terminalReferenceId -> Some terminalReferenceId
                                    | _ -> None)

                            if canProjectPromotionReference (Some promotionSetDto.Status) expectedTerminalReferenceId referenceDto then
                                projectablePromotion <- Some referenceDto
                        | Some _ -> ()

                    index <- index + 1

                return projectablePromotion
            }

        /// Updates the branchDto with the latest reference of each type from the branch.
        let updateLatestReferences (branchDto: BranchDto) correlationId =
            task {
                let durableBranchDto =
                    state.State
                    |> Seq.fold (fun current branchEvent -> current |> BranchDto.UpdateDto branchEvent) BranchDto.Default

                let mutable newBranchDto = { branchDto with BasedOn = durableBranchDto.BasedOn; LatestPromotion = ReferenceDto.Default }

                let mutable latestRebaseProjection: (ReferenceDto * ReferenceDto) option = None

                let referenceTypes = projectionReconstructionReferenceTypes branchDto

                // Get the latest references.
                let! latestReferences = getLatestReferenceByReferenceTypes referenceTypes branchDto.RepositoryId branchDto.BranchId

                let! latestProjectablePromotion = getLatestProjectablePromotion branchDto correlationId

                // Get the latest reference of any type.
                let latestReference =
                    latestReferences
                        .Values
                        .Where(fun referenceDto ->
                            referenceDto.ReferenceType
                            <> ReferenceType.Promotion)
                        .Concat(
                            match latestProjectablePromotion with
                            | Some referenceDto -> [| referenceDto |]
                            | None -> Array.Empty<ReferenceDto>()
                        )
                        .OrderByDescending(fun referenceDto -> referenceDto.UpdatedAt)
                        .FirstOrDefault(durableBranchDto.BasedOn)

                newBranchDto <- { newBranchDto with LatestReference = latestReference }

                // Get the latest reference of each type.
                for kvp in latestReferences do
                    let referenceDto = kvp.Value

                    match kvp.Key with
                    | Save -> newBranchDto <- { newBranchDto with LatestSave = referenceDto }
                    | Checkpoint -> newBranchDto <- { newBranchDto with LatestCheckpoint = referenceDto }
                    | Commit -> newBranchDto <- { newBranchDto with LatestCommit = referenceDto }
                    | Promotion -> ()
                    | Rebase ->
                        let basedOnReferenceId =
                            kvp.Value.Links
                            |> Seq.tryPick (fun link ->
                                match link with
                                | ReferenceLinkType.BasedOn referenceId -> Some referenceId
                                | _ -> None)

                        match basedOnReferenceId with
                        | Some referenceId ->
                            let basedOnReferenceActorProxy = Reference.CreateActorProxy referenceId branchDto.RepositoryId correlationId
                            let! basedOnReferenceDto = basedOnReferenceActorProxy.Get correlationId
                            latestRebaseProjection <- Some(referenceDto, basedOnReferenceDto)
                        | None -> ()
                    | External -> ()
                    | Tag -> ()

                match latestProjectablePromotion with
                | Some referenceDto -> newBranchDto <- { newBranchDto with LatestPromotion = referenceDto }
                | None -> ()

                return
                    { newBranchDto with
                        BasedOn = selectBasedOnProjection durableBranchDto.BasedOn latestRebaseProjection latestProjectablePromotion
                        ShouldRecomputeLatestReferences = false
                    }
            }

        /// Stores the correlation id used by this actor while reporting timings and errors.
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            branchDto <-
                state.State
                |> Seq.fold (fun branchDto branchEvent -> branchDto |> BranchDto.UpdateDto branchEvent) BranchDto.Default

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            Task.CompletedTask

        /// Applies one persisted Branch event to this activation's in-memory state.
        member private this.ApplyEvent branchEvent =
            task {
                try
                    // If the branchEvent is Created or Rebased, we need to get the reference that the branch is based on for updating the branchDto.
                    match branchEvent.Event with
                    | Created (branchId, branchName, parentBranchId, basedOn, ownerId, organizationId, repositoryId, branchPermissions) ->
                        let! basedOnReferenceDto =
                            if basedOn <> ReferenceId.Empty then
                                task {
                                    let referenceActorProxy = Reference.CreateActorProxy basedOn repositoryId branchEvent.Metadata.CorrelationId
                                    return! referenceActorProxy.Get branchEvent.Metadata.CorrelationId
                                }
                            else
                                ReferenceDto.Default |> returnTask

                        branchEvent.Metadata.Properties[ "basedOnReferenceDto" ] <- serialize basedOnReferenceDto
                    | Rebased basedOn ->
                        let referenceActorProxy = Reference.CreateActorProxy basedOn branchDto.RepositoryId branchEvent.Metadata.CorrelationId
                        let! basedOnReferenceDto = referenceActorProxy.Get branchEvent.Metadata.CorrelationId
                        branchEvent.Metadata.Properties[ "basedOnReferenceDto" ] <- serialize basedOnReferenceDto
                    | _ -> ()

                    // Update the branchDto with the event.
                    branchDto <- branchDto |> BranchDto.UpdateDto branchEvent
                    branchEvent.Metadata.Properties[ nameof RepositoryId ] <- $"{branchDto.RepositoryId}"

                    // Reference actors own publication; eligible Watch transitions also persist here to give each branch one durable replay order.
                    match branchEvent.Event with
                    | Assigned (referenceDto, _, _, _, _)
                    | Promoted (referenceDto, _, _, _, _)
                    | Committed (referenceDto, _, _, _, _)
                    | Checkpointed (referenceDto, _, _, _, _)
                    | Saved (referenceDto, _, _, _, _)
                    | Tagged (referenceDto, _, _, _, _)
                    | ExternalCreated (referenceDto, _, _, _, _) -> branchEvent.Metadata.Properties[ nameof ReferenceId ] <- $"{referenceDto.ReferenceId}"
                    | Rebased referenceId -> applyReferenceIdMetadata branchEvent.Metadata.Properties referenceId
                    | _ -> ()

                    if shouldPersistBranchEvent branchEvent.Event then
                        state.State.Add branchEvent
                        do! state.WriteStateAsync()

                    if shouldPublishBranchEvent branchEvent.Event then
                        let graceEvent = GraceEvent.BranchEvent branchEvent
                        do! publishGraceEvent graceEvent branchEvent.Metadata

                    return Ok(createBranchCommandReturnValue branchDto branchEvent)
                with
                | ex ->
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
            /// Returns the repository id recorded in this Branch actor state.
            member this.GetRepositoryId correlationId = branchDto.RepositoryId |> returnTask

        interface IBranchActor with

            /// Returns the persisted Branch event stream for replay or audit.
            member this.GetEvents correlationId =
                task {
                    this.correlationId <- correlationId
                    return state.State :> IReadOnlyList<BranchEvent>
                }

            /// Reports whether this Branch actor has persisted state.
            member this.Exists correlationId =
                this.correlationId <- correlationId
                branchDto.UpdatedAt.IsSome |> returnTask

            /// Reports whether this Branch actor state is marked logically deleted.
            member this.IsDeleted correlationId =
                this.correlationId <- correlationId
                branchDto.DeletedAt.IsSome |> returnTask

            /// Routes a public actor command to the domain operation that validates and persists it.
            member this.Handle command metadata =
                /// Checks whether command validation succeeded before emitting the domain event.
                let isValid (command: BranchCommand) (metadata: EventMetadata) referenceDisposition =
                    task {
                        let hasDuplicateCorrelation =
                            state.State.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId)
                            && (state.State.Count > 3)

                        let rejectDuplicateCorrelation =
                            match referenceDisposition with
                            | Some disposition -> shouldRejectDuplicateCorrelation hasDuplicateCorrelation disposition
                            | None -> hasDuplicateCorrelation

                        if rejectDuplicateCorrelation then
                            return Error(GraceError.Create (getErrorMessage BranchError.DuplicateCorrelationId) metadata.CorrelationId)
                        else
                            match command with
                            | BranchCommand.Create (_, branchName, _, _, _, _, _, _, _) ->
                                let matchesDurableCreation =
                                    state.State
                                    |> Seq.tryPick (fun branchEvent ->
                                        match branchEvent.Event with
                                        | Created _ -> Some(createCommandMatchesCreationEvent branchEvent.Event command)
                                        | _ -> None)
                                    |> Option.defaultValue false

                                match branchDto.UpdatedAt with
                                | Some _ when
                                    matchesDurableCreation
                                    && (referenceDisposition = Some MatchingRetry
                                        || (branchName = InitialBranchName
                                            && referenceDisposition.IsNone))
                                    ->
                                    return Ok command
                                | Some _ -> return Error(GraceError.Create (BranchError.getErrorMessage BranchAlreadyExists) metadata.CorrelationId)
                                | None -> return Ok command
                            | _ ->
                                match branchDto.UpdatedAt with
                                | Some _ -> return Ok command
                                | None -> return Error(GraceError.Create (getErrorMessage BranchError.BranchDoesNotExist) metadata.CorrelationId)
                    }

                /// Creates a reference DTO for branch promotion, commit, or save-boundary updates.
                let addReferenceWithId
                    referenceId
                    requiresBrokerAcceptance
                    ownerId
                    organizationId
                    repositoryId
                    branchId
                    directoryId
                    sha256Hash
                    blake3Hash
                    referenceText
                    referenceType
                    links
                    =
                    task {
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
                                blake3Hash,
                                referenceType,
                                referenceText,
                                links
                            )

                        metadata.Properties[ nameof (RepositoryId) ] <- $"{repositoryId}"
                        metadata.Properties[ nameof ReferenceId ] <- $"{referenceId}"

                        metadata.Properties[
                            Reference.ReferenceCreatedRequiresBrokerAcceptanceProperty
                        ] <- string requiresBrokerAcceptance

                        return! referenceActor.Handle referenceCommand metadata
                    }

                /// Creates a strict Reference with the identity owned by its producer contract.
                let addReferenceToCurrentBranch referenceId directoryId sha256Hash blake3Hash referenceText referenceType links =
                    addReferenceWithId
                        referenceId
                        true
                        branchDto.OwnerId
                        branchDto.OrganizationId
                        branchDto.RepositoryId
                        branchDto.BranchId
                        directoryId
                        sha256Hash
                        blake3Hash
                        referenceText
                        referenceType
                        links

                /// Maps every Branch Reference producer to the uniform durable Reference Create contract.
                let callerOwnedReferenceCommand command =
                    task {
                        let createFromCurrentBranch referenceId directoryVersionId sha256Hash blake3Hash referenceType referenceText links =
                            referenceId,
                            branchDto.RepositoryId,
                            ReferenceCommand.Create(
                                referenceId,
                                branchDto.OwnerId,
                                branchDto.OrganizationId,
                                branchDto.RepositoryId,
                                branchDto.BranchId,
                                directoryVersionId,
                                sha256Hash,
                                blake3Hash,
                                referenceType,
                                referenceText,
                                links
                            )

                        match command with
                        | BranchCommand.Create (branchId, _, _, basedOn, referenceId, ownerId, organizationId, repositoryId, _) when
                            basedOn <> ReferenceId.Empty
                            ->
                            let basedOnActor = Reference.CreateActorProxy basedOn repositoryId metadata.CorrelationId
                            let! basedOnReference = basedOnActor.Get metadata.CorrelationId

                            return
                                Some(
                                    referenceId,
                                    repositoryId,
                                    ReferenceCommand.Create(
                                        referenceId,
                                        ownerId,
                                        organizationId,
                                        repositoryId,
                                        branchId,
                                        basedOnReference.DirectoryId,
                                        basedOnReference.Sha256Hash,
                                        basedOnReference.Blake3Hash,
                                        ReferenceType.Rebase,
                                        basedOnReference.ReferenceText,
                                        [ ReferenceLinkType.BasedOn basedOn ]
                                    )
                                )
                        | BranchCommand.Rebase (referenceId, basedOn) ->
                            let basedOnActor = Reference.CreateActorProxy basedOn branchDto.RepositoryId metadata.CorrelationId
                            let! basedOnReference = basedOnActor.Get metadata.CorrelationId

                            return
                                Some(
                                    createFromCurrentBranch
                                        referenceId
                                        basedOnReference.DirectoryId
                                        basedOnReference.Sha256Hash
                                        basedOnReference.Blake3Hash
                                        ReferenceType.Rebase
                                        basedOnReference.ReferenceText
                                        [ ReferenceLinkType.BasedOn basedOn ]
                                )
                        | BranchCommand.Assign (referenceId, directoryVersionId, sha256Hash, blake3Hash, referenceText)
                        | BranchCommand.Promote (referenceId, directoryVersionId, sha256Hash, blake3Hash, referenceText) ->
                            return
                                Some(
                                    createFromCurrentBranch
                                        referenceId
                                        directoryVersionId
                                        sha256Hash
                                        blake3Hash
                                        ReferenceType.Promotion
                                        referenceText
                                        List.empty
                                )
                        | BranchCommand.Commit (referenceId, directoryVersionId, sha256Hash, blake3Hash, referenceText) ->
                            return
                                Some(createFromCurrentBranch referenceId directoryVersionId sha256Hash blake3Hash ReferenceType.Commit referenceText List.empty)
                        | BranchCommand.Checkpoint (referenceId, directoryVersionId, sha256Hash, blake3Hash, referenceText) ->
                            return
                                Some(
                                    createFromCurrentBranch
                                        referenceId
                                        directoryVersionId
                                        sha256Hash
                                        blake3Hash
                                        ReferenceType.Checkpoint
                                        referenceText
                                        List.empty
                                )
                        | BranchCommand.Save (referenceId, directoryVersionId, sha256Hash, blake3Hash, referenceText) ->
                            return
                                Some(createFromCurrentBranch referenceId directoryVersionId sha256Hash blake3Hash ReferenceType.Save referenceText List.empty)
                        | BranchCommand.Tag (referenceId, directoryVersionId, sha256Hash, blake3Hash, referenceText) ->
                            return Some(createFromCurrentBranch referenceId directoryVersionId sha256Hash blake3Hash ReferenceType.Tag referenceText List.empty)
                        | BranchCommand.CreateExternal (referenceId, directoryVersionId, sha256Hash, blake3Hash, referenceText) ->
                            return
                                Some(
                                    createFromCurrentBranch referenceId directoryVersionId sha256Hash blake3Hash ReferenceType.External referenceText List.empty
                                )
                        | _ -> return None
                    }

                /// Reads durable Reference state before dispatch so an exact retry avoids a second Branch transition.
                let getReferenceOperationDisposition command =
                    task {
                        match! callerOwnedReferenceCommand command with
                        | None -> return None
                        | Some (referenceId, repositoryId, referenceCommand) ->
                            let referenceActor = Reference.CreateActorProxy referenceId repositoryId metadata.CorrelationId
                            let! exists = referenceActor.Exists metadata.CorrelationId

                            if exists then
                                let! referenceDto = referenceActor.Get metadata.CorrelationId

                                return Some(classifyReferenceOperation referenceCommand (Some referenceDto))
                            else
                                return Some(classifyReferenceOperation referenceCommand None)
                    }

                /// Runs Branch command decisions, applies emitted events, and persists the result.
                let processCommand (command: BranchCommand) (metadata: EventMetadata) referenceDisposition =
                    task {
                        try
                            //logToConsole
                            //    $"In BranchActor.Handle.processCommand: command: {getDiscriminatedUnionFullName command}; metadata: {serialize metadata}."

                            let! event =
                                task {
                                    match command with
                                    | Create (branchId,
                                              branchName,
                                              parentBranchId,
                                              basedOn,
                                              referenceId,
                                              ownerId,
                                              organizationId,
                                              repositoryId,
                                              branchPermissions) ->
                                        let createdEvent =
                                            Created(branchId, branchName, parentBranchId, basedOn, ownerId, organizationId, repositoryId, branchPermissions)

                                        // Add an initial Rebase reference to this branch that points to the BasedOn reference, unless we're creating `main`.
                                        if branchName = InitialBranchName then
                                            memoryCache.CreateBranchNameEntry(repositoryId, branchName, branchId)
                                            return Ok createdEvent
                                        else
                                            // We need to get the reference that we're rebasing on, so we can get the DirectoryId and root hashes.
                                            let referenceActorProxy = Reference.CreateActorProxy basedOn repositoryId this.correlationId
                                            let! promotionDto = referenceActorProxy.Get this.correlationId

                                            match!
                                                addReferenceWithId
                                                    referenceId
                                                    true
                                                    ownerId
                                                    organizationId
                                                    repositoryId
                                                    branchId
                                                    promotionDto.DirectoryId
                                                    promotionDto.Sha256Hash
                                                    promotionDto.Blake3Hash
                                                    promotionDto.ReferenceText
                                                    ReferenceType.Rebase
                                                    [
                                                        ReferenceLinkType.BasedOn promotionDto.ReferenceId
                                                    ]
                                                with
                                            | Ok _ ->
                                                //logToConsole $"In BranchActor.Handle.processCommand: rebaseReferenceDto: {rebaseReferenceDto}."
                                                memoryCache.CreateBranchNameEntry(repositoryId, branchName, branchId)
                                                return Ok createdEvent
                                            | Error error -> return Error error
                                    | BranchCommand.Rebase (referenceId, basedOn) ->
                                        metadata.Properties[ "BasedOn" ] <- $"{basedOn}"
                                        metadata.Properties[ nameof ReferenceId ] <- $"{referenceId}"
                                        metadata.Properties[ nameof RepositoryId ] <- $"{branchDto.RepositoryId}"
                                        metadata.Properties[ nameof BranchId ] <- $"{this.GetGrainId().GetGuidKey()}"
                                        metadata.Properties[ nameof BranchName ] <- $"{branchDto.BranchName}"

                                        // We need to get the reference that we're rebasing on, so we can get the directoryId and root hashes.
                                        let referenceActorProxy = Reference.CreateActorProxy basedOn branchDto.RepositoryId this.correlationId
                                        let! promotionDto = referenceActorProxy.Get metadata.CorrelationId

                                        // Add the Rebase reference to this branch.
                                        match!
                                            addReferenceToCurrentBranch
                                                referenceId
                                                promotionDto.DirectoryId
                                                promotionDto.Sha256Hash
                                                promotionDto.Blake3Hash
                                                promotionDto.ReferenceText
                                                ReferenceType.Rebase
                                                [
                                                    ReferenceLinkType.BasedOn promotionDto.ReferenceId
                                                ]
                                            with
                                        | Ok rebaseReferenceDto ->
                                            //logToConsole $"In BranchActor.Handle.processCommand: rebaseReferenceDto: {rebaseReferenceDto}."
                                            return Ok(Rebased basedOn)
                                        | Error error ->
                                            log.LogError(
                                                "{CurrentInstant}: Error rebasing on referenceId: {referenceId}; promotionDto: {promotionDto}.\n{Error}",
                                                getCurrentInstantExtended (),
                                                basedOn,
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
                                    | BranchCommand.Assign (referenceId, directoryVersionId, sha256Hash, blake3Hash, referenceText) ->
                                        match!
                                            addReferenceToCurrentBranch
                                                referenceId
                                                directoryVersionId
                                                sha256Hash
                                                blake3Hash
                                                referenceText
                                                ReferenceType.Promotion
                                                List.empty
                                            with
                                        | Ok returnValue ->
                                            return Ok(Assigned(returnValue.ReturnValue, directoryVersionId, sha256Hash, blake3Hash, referenceText))
                                        | Error error -> return Error error
                                    | BranchCommand.Promote (referenceId, directoryVersionId, sha256Hash, blake3Hash, referenceText) ->
                                        match!
                                            addReferenceToCurrentBranch
                                                referenceId
                                                directoryVersionId
                                                sha256Hash
                                                blake3Hash
                                                referenceText
                                                ReferenceType.Promotion
                                                List.empty
                                            with
                                        | Ok returnValue ->
                                            return Ok(Promoted(returnValue.ReturnValue, directoryVersionId, sha256Hash, blake3Hash, referenceText))
                                        | Error error -> return Error error
                                    | BranchCommand.Commit (referenceId, directoryVersionId, sha256Hash, blake3Hash, referenceText) ->
                                        match!
                                            addReferenceToCurrentBranch
                                                referenceId
                                                directoryVersionId
                                                sha256Hash
                                                blake3Hash
                                                referenceText
                                                ReferenceType.Commit
                                                List.empty
                                            with
                                        | Ok returnValue ->
                                            return Ok(Committed(returnValue.ReturnValue, directoryVersionId, sha256Hash, blake3Hash, referenceText))
                                        | Error error -> return Error error
                                    | BranchCommand.Checkpoint (referenceId, directoryVersionId, sha256Hash, blake3Hash, referenceText) ->
                                        match!
                                            addReferenceToCurrentBranch
                                                referenceId
                                                directoryVersionId
                                                sha256Hash
                                                blake3Hash
                                                referenceText
                                                ReferenceType.Checkpoint
                                                List.empty
                                            with
                                        | Ok returnValue ->
                                            return Ok(Checkpointed(returnValue.ReturnValue, directoryVersionId, sha256Hash, blake3Hash, referenceText))
                                        | Error error -> return Error error
                                    | BranchCommand.Save (referenceId, directoryVersionId, sha256Hash, blake3Hash, referenceText) ->
                                        match!
                                            addReferenceToCurrentBranch
                                                referenceId
                                                directoryVersionId
                                                sha256Hash
                                                blake3Hash
                                                referenceText
                                                ReferenceType.Save
                                                List.empty
                                            with
                                        | Ok returnValue -> return Ok(Saved(returnValue.ReturnValue, directoryVersionId, sha256Hash, blake3Hash, referenceText))
                                        | Error error -> return Error error
                                    | BranchCommand.Tag (referenceId, directoryVersionId, sha256Hash, blake3Hash, referenceText) ->
                                        match!
                                            addReferenceToCurrentBranch
                                                referenceId
                                                directoryVersionId
                                                sha256Hash
                                                blake3Hash
                                                referenceText
                                                ReferenceType.Tag
                                                List.empty
                                            with
                                        | Ok returnValue ->
                                            return Ok(Tagged(returnValue.ReturnValue, directoryVersionId, sha256Hash, blake3Hash, referenceText))
                                        | Error error -> return Error error
                                    | BranchCommand.CreateExternal (referenceId, directoryVersionId, sha256Hash, blake3Hash, referenceText) ->
                                        match!
                                            addReferenceToCurrentBranch
                                                referenceId
                                                directoryVersionId
                                                sha256Hash
                                                blake3Hash
                                                referenceText
                                                ReferenceType.External
                                                List.empty
                                            with
                                        | Ok returnValue ->
                                            return Ok(ExternalCreated(returnValue.ReturnValue, directoryVersionId, sha256Hash, blake3Hash, referenceText))
                                        | Error error -> return Error error
                                    | RemoveReference referenceId -> return Ok(ReferenceRemoved referenceId)
                                    | DeleteLogical (force, deleteReason, reassignChildBranches, newParentBranchId) ->
                                        // Check for child branches
                                        let! childBranches =
                                            getChildBranches branchDto.RepositoryId branchDto.BranchId Int32.MaxValue false metadata.CorrelationId

                                        if childBranches.Length > 0
                                           && not reassignChildBranches
                                           && not force then
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
                                                                    match!
                                                                        childBranchActorProxy.Handle
                                                                            (DeleteLogical(
                                                                                true,
                                                                                $"Parent branch {branchDto.BranchName} is being deleted.",
                                                                                false,
                                                                                None
                                                                            ))
                                                                            childMetadata
                                                                        with
                                                                    | Ok _ -> childBranchResults.Add($"Deleted child branch: {childBranch.BranchName}")
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

                                                                    match! childBranchActorProxy.Handle (UpdateParentBranch targetParentBranchId) childMetadata
                                                                        with
                                                                    | Ok _ -> childBranchResults.Add($"Reassigned child branch: {childBranch.BranchName}")
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
                                            /// Reads branch logical-delete retention days from event metadata when the caller supplied it.
                                            let tryGetLogicalDeleteDaysFromMetadata () =
                                                match metadata.Properties.TryGetValue("RepositoryLogicalDeleteDays") with
                                                | true, value ->
                                                    let mutable parsed = 0.0f

                                                    if Single.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, &parsed) then
                                                        Some parsed
                                                    else
                                                        None
                                                | _ -> None

                                            let! logicalDeleteDays =
                                                match tryGetLogicalDeleteDaysFromMetadata () with
                                                | Some days -> Task.FromResult days
                                                | None ->
                                                    task {
                                                        let repositoryActorProxy =
                                                            Repository.CreateActorProxy branchDto.OrganizationId branchDto.RepositoryId metadata.CorrelationId

                                                        let! repositoryDto = repositoryActorProxy.Get(metadata.CorrelationId)
                                                        return repositoryDto.LogicalDeleteDays
                                                    }

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
                                                                metadata.Properties[ nameof (RepositoryId) ] <- $"{branchDto.RepositoryId}"

                                                                metadata.Properties[ "RepositoryLogicalDeleteDays" ] <-
                                                                    logicalDeleteDays.ToString("F", CultureInfo.InvariantCulture)

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
                                                {
                                                    RepositoryId = branchDto.RepositoryId
                                                    BranchId = branchDto.BranchId
                                                    BranchName = branchDto.BranchName
                                                    ParentBranchId = branchDto.ParentBranchId
                                                    DeleteReason = deleteReason
                                                    CorrelationId = metadata.CorrelationId
                                                }

                                            do!
                                                (this :> IGraceReminderWithGuidKey)
                                                    .ScheduleReminderAsync
                                                    ReminderTypes.PhysicalDeletion
                                                    (Duration.FromDays(float logicalDeleteDays))
                                                    (ReminderState.BranchPhysicalDeletion physicalDeletionReminderState)
                                                    metadata.CorrelationId

                                            // Add child branch results to metadata for output
                                            if childBranchResults.Count > 0 then
                                                metadata.Properties[ "ChildBranchResults" ] <-
                                                    childBranchResults.ToArray()
                                                    |> String.concat Environment.NewLine

                                            return Ok(LogicalDeleted(force, deleteReason, reassignChildBranches, newParentBranchId))
                                    | DeletePhysical ->
                                        // Delete the state from storage, and deactivate the actor.
                                        do! state.ClearStateAsync()
                                        this.DeactivateOnIdle()
                                        return Ok PhysicalDeleted
                                    | Undelete -> return Ok Undeleted
                                }

                            match event, referenceDisposition with
                            | Ok (Created _ as event), _ when branchDto.UpdatedAt.IsSome ->
                                return Ok(createBranchCommandReturnValue branchDto { Event = event; Metadata = metadata })
                            | Ok event, Some MatchingRetry when branchDto.UpdatedAt.IsNone -> return! this.ApplyEvent { Event = event; Metadata = metadata }
                            | Ok (Rebased basedOn as event), Some MatchingRetry ->
                                match command with
                                | BranchCommand.Rebase (referenceId, _) ->
                                    let referenceActor = Reference.CreateActorProxy referenceId branchDto.RepositoryId metadata.CorrelationId
                                    let basedOnActor = Reference.CreateActorProxy basedOn branchDto.RepositoryId metadata.CorrelationId
                                    let! referenceDto = referenceActor.Get metadata.CorrelationId
                                    let! basedOnReferenceDto = basedOnActor.Get metadata.CorrelationId
                                    let recoveredBranchDto, _ = reconcileRebaseProjection branchDto referenceDto basedOnReferenceDto
                                    branchDto <- recoveredBranchDto
                                    return Ok(createBranchCommandReturnValue branchDto { Event = event; Metadata = metadata })
                                | _ -> return Ok(createBranchCommandReturnValue branchDto { Event = event; Metadata = metadata })
                            | Ok event, Some MatchingRetry ->
                                match tryGetReferenceFromBranchEvent event with
                                | Some referenceDto ->
                                    let recoveredBranchDto, _ = reconcileReferenceProjection branchDto referenceDto
                                    branchDto <- recoveredBranchDto
                                    return Ok(createBranchCommandReturnValue branchDto { Event = event; Metadata = metadata })
                                | None -> return Ok(createBranchCommandReturnValue branchDto { Event = event; Metadata = metadata })
                            | Ok event, Some disposition when not (shouldApplyReferenceEvent disposition) ->
                                return Ok(createBranchCommandReturnValue branchDto { Event = event; Metadata = metadata })
                            | Ok event, _ -> return! this.ApplyEvent { Event = event; Metadata = metadata }
                            | Error error, _ -> return Error error
                        with
                        | ex ->
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

                    let! referenceDisposition = getReferenceOperationDisposition command

                    match! isValid command metadata referenceDisposition with
                    | Ok command -> return! processCommand command metadata referenceDisposition
                    | Error error -> return Error error
                }

            /// Returns the current Branch actor state snapshot.
            member this.Get correlationId =
                task {
                    this.correlationId <- correlationId

                    if branchDto.ShouldRecomputeLatestReferences then
                        let! branchDtoWithLatestReferences = updateLatestReferences branchDto correlationId
                        branchDto <- branchDtoWithLatestReferences

                    if not (BranchDto.IsValidPublicProjection branchDto) then
                        raise (InvalidOperationException $"Branch '{branchDto.BranchId}' cannot be returned with incomplete or mismatched References.")

                    return branchDto
                }

            /// Returns the parent branch when this branch records one without querying the root-parent sentinel.
            member this.GetParentBranch correlationId =
                task {
                    this.correlationId <- correlationId

                    if branchDto.ParentBranchId = Constants.DefaultParentBranchId then
                        return None
                    else
                        let branchActorProxy = Branch.CreateActorProxy branchDto.ParentBranchId branchDto.RepositoryId correlationId
                        let! parentBranch = branchActorProxy.Get correlationId
                        return Some parentBranch
                }

            /// Returns the latest commit reference tracked by this branch.
            member this.GetLatestCommit correlationId =
                this.correlationId <- correlationId
                branchDto.LatestCommit |> returnTask

            /// Returns the latest promotion reference tracked by this branch.
            member this.GetLatestPromotion correlationId =
                this.correlationId <- correlationId
                branchDto.LatestPromotion |> returnTask

            /// Marks branch-derived state so background workers know it must be recomputed.
            member this.MarkForRecompute(correlationId: CorrelationId) : Task =
                this.correlationId <- correlationId
                branchDto <- { branchDto with ShouldRecomputeLatestReferences = true }
                Task.CompletedTask
