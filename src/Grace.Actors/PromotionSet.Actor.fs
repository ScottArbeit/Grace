namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Artifact
open Grace.Types.Events
open Grace.Types.PromotionSet
open Grace.Types.Queue
open Grace.Types.Reference
open Grace.Types.Reminder
open Grace.Types.Types
open Grace.Types.Validation
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Globalization
open System.Threading.Tasks

module PromotionSet =

    type private RecomputeFailure =
        | Blocked of reason: string * artifactId: ArtifactId option
        | Failed of reason: string

    type PromotionSetActor([<PersistentState(StateName.PromotionSet, Constants.GraceActorStorage)>] state: IPersistentState<List<PromotionSetEvent>>) =
        inherit Grain()

        static let actorName = ActorName.PromotionSet
        let log = loggerFactory.CreateLogger("PromotionSet.Actor")

        let mutable currentCommand = String.Empty
        let mutable promotionSetDto = PromotionSetDto.Default

        member val private correlationId: CorrelationId = String.Empty with get, set

        member private this.WithActorMetadata(metadata: EventMetadata) =
            let properties = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

            metadata.Properties
            |> Seq.iter (fun kvp -> properties[kvp.Key] <- kvp.Value)

            if promotionSetDto.RepositoryId <> RepositoryId.Empty then
                properties[nameof RepositoryId] <- $"{promotionSetDto.RepositoryId}"

            let actorId =
                if promotionSetDto.PromotionSetId
                   <> PromotionSetId.Empty then
                    $"{promotionSetDto.PromotionSetId}"
                else
                    $"{this.GetPrimaryKey()}"

            properties["ActorId"] <- actorId

            let principal =
                if String.IsNullOrWhiteSpace metadata.Principal then
                    GraceSystemUser
                else
                    metadata.Principal

            { metadata with Principal = principal; Properties = properties }

        member private this.BuildSuccess(message: string, correlationId: CorrelationId) =
            let graceReturnValue: GraceReturnValue<string> =
                (GraceReturnValue.Create message correlationId)
                    .enhance(nameof RepositoryId, promotionSetDto.RepositoryId)
                    .enhance(nameof PromotionSetId, promotionSetDto.PromotionSetId)
                    .enhance ("Status", getDiscriminatedUnionCaseName promotionSetDto.Status)

            Ok graceReturnValue

        member private this.BuildError(errorMessage: string, correlationId: CorrelationId) =
            Error(
                (GraceError.Create errorMessage correlationId)
                    .enhance(nameof RepositoryId, promotionSetDto.RepositoryId)
                    .enhance (nameof PromotionSetId, promotionSetDto.PromotionSetId)
            )

        member private this.GetCurrentTerminalPromotion() =
            task {
                let! latestPromotion = getLatestPromotion promotionSetDto.RepositoryId promotionSetDto.TargetBranchId

                match latestPromotion with
                | Option.Some promotion -> return promotion.ReferenceId, promotion.DirectoryId
                | Option.None ->
                    let branchActorProxy = Branch.CreateActorProxy promotionSetDto.TargetBranchId promotionSetDto.RepositoryId this.correlationId
                    let! branchDto = branchActorProxy.Get this.correlationId

                    let baseDirectoryVersionId =
                        if branchDto.BasedOn.ReferenceId <> ReferenceId.Empty then
                            branchDto.BasedOn.DirectoryId
                        elif branchDto.LatestPromotion.ReferenceId
                             <> ReferenceId.Empty then
                            branchDto.LatestPromotion.DirectoryId
                        elif branchDto.LatestReference.ReferenceId
                             <> ReferenceId.Empty then
                            branchDto.LatestReference.DirectoryId
                        else
                            DirectoryVersionId.Empty

                    return ReferenceId.Empty, baseDirectoryVersionId
            }

        member private this.GetConflictResolutionPolicy() =
            task {
                let repositoryActorProxy = Repository.CreateActorProxy promotionSetDto.OrganizationId promotionSetDto.RepositoryId this.correlationId
                let! repositoryDto = repositoryActorProxy.Get this.correlationId
                return repositoryDto.ConflictResolutionPolicy
            }

        member private this.HydrateStepProvenance(step: PromotionSetStep) =
            task {
                let referenceActorProxy = Reference.CreateActorProxy step.OriginalPromotion.ReferenceId promotionSetDto.RepositoryId this.correlationId
                let! promotionReferenceDto = referenceActorProxy.Get this.correlationId

                let promotionDirectoryVersionId =
                    if promotionReferenceDto.ReferenceId
                       <> ReferenceId.Empty then
                        promotionReferenceDto.DirectoryId
                    else
                        step.OriginalPromotion.DirectoryVersionId

                if promotionDirectoryVersionId = DirectoryVersionId.Empty then
                    return
                        Error(
                            (GraceError.Create "Original promotion did not include a directory version." this.correlationId)
                                .enhance (nameof PromotionSetStepId, step.StepId)
                        )
                else
                    let basedOnReferenceId =
                        if step.OriginalBasePromotionReferenceId
                           <> ReferenceId.Empty then
                            step.OriginalBasePromotionReferenceId
                        elif promotionReferenceDto.ReferenceId
                             <> ReferenceId.Empty then
                            promotionReferenceDto.Links
                            |> Seq.tryPick (fun link ->
                                match link with
                                | ReferenceLinkType.BasedOn referenceId -> Option.Some referenceId
                                | _ -> Option.None)
                            |> Option.defaultValue ReferenceId.Empty
                        else
                            ReferenceId.Empty

                    let! basedOnDirectoryVersionId =
                        if step.OriginalBaseDirectoryVersionId
                           <> DirectoryVersionId.Empty then
                            Task.FromResult step.OriginalBaseDirectoryVersionId
                        elif basedOnReferenceId <> ReferenceId.Empty then
                            task {
                                let basedOnReferenceActorProxy = Reference.CreateActorProxy basedOnReferenceId promotionSetDto.RepositoryId this.correlationId

                                let! basedOnReferenceDto = basedOnReferenceActorProxy.Get this.correlationId

                                if basedOnReferenceDto.ReferenceId = ReferenceId.Empty then
                                    return promotionDirectoryVersionId
                                else
                                    return basedOnReferenceDto.DirectoryId
                            }
                        else
                            Task.FromResult promotionDirectoryVersionId

                    return
                        Ok
                            { step with
                                OriginalPromotion = { step.OriginalPromotion with DirectoryVersionId = promotionDirectoryVersionId }
                                OriginalBasePromotionReferenceId = basedOnReferenceId
                                OriginalBaseDirectoryVersionId = basedOnDirectoryVersionId
                            }
            }

        member private this.GetModelConfidence(step: PromotionSetStep, computedAgainstBaseDirectoryVersionId: DirectoryVersionId) =
            if step.OriginalBaseDirectoryVersionId = computedAgainstBaseDirectoryVersionId then
                1.0
            elif step.OriginalPromotion.DirectoryVersionId = computedAgainstBaseDirectoryVersionId then
                0.98
            else
                0.5

        member private this.CreateConflictArtifact
            (
                step: PromotionSetStep,
                reason: string,
                confidence: float option,
                computedAgainstBaseDirectoryVersionId: DirectoryVersionId,
                metadata: EventMetadata
            ) =
            task {
                try
                    let report =
                        {|
                            promotionSetId = promotionSetDto.PromotionSetId
                            targetBranchId = promotionSetDto.TargetBranchId
                            stepId = step.StepId
                            order = step.Order
                            reason = reason
                            confidence = confidence
                            computedAgainstBaseDirectoryVersionId = computedAgainstBaseDirectoryVersionId
                            originalBaseDirectoryVersionId = step.OriginalBaseDirectoryVersionId
                            originalPromotionDirectoryVersionId = step.OriginalPromotion.DirectoryVersionId
                            conflicts =
                                [
                                    {|
                                        filePath = "__step__"
                                        hunks =
                                            [
                                                {|
                                                    startLine = 1
                                                    endLine = 1
                                                    oursContent = $"{computedAgainstBaseDirectoryVersionId}"
                                                    theirsContent = $"{step.OriginalPromotion.DirectoryVersionId}"
                                                |}
                                            ]
                                    |}
                                ]
                        |}

                    let reportJson = serialize report
                    let artifactId: ArtifactId = Guid.NewGuid()
                    let nowUtc = getCurrentInstant().InUtc()

                    let blobPath = sprintf "grace-artifacts/%04i/%02i/%02i/%02i/%O" nowUtc.Year nowUtc.Month nowUtc.Day nowUtc.Hour artifactId

                    let artifactMetadata: ArtifactMetadata =
                        { ArtifactMetadata.Default with
                            ArtifactId = artifactId
                            OwnerId = promotionSetDto.OwnerId
                            OrganizationId = promotionSetDto.OrganizationId
                            RepositoryId = promotionSetDto.RepositoryId
                            ArtifactType = ArtifactType.ConflictReport
                            MimeType = "application/json"
                            Size = int64 reportJson.Length
                            BlobPath = blobPath
                            CreatedAt = getCurrentInstant ()
                            CreatedBy = UserId metadata.Principal
                        }

                    let artifactActorProxy = Artifact.CreateActorProxy artifactId promotionSetDto.RepositoryId this.correlationId

                    match! artifactActorProxy.Handle (ArtifactCommand.Create artifactMetadata) (this.WithActorMetadata metadata) with
                    | Ok _ -> return Option.Some artifactId
                    | Error graceError ->
                        log.LogWarning(
                            "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Failed to persist conflict artifact metadata for PromotionSetId {PromotionSetId}. Error: {GraceError}.",
                            getCurrentInstantExtended (),
                            getMachineName,
                            metadata.CorrelationId,
                            promotionSetDto.PromotionSetId,
                            graceError
                        )

                        return Option.None
                with
                | ex ->
                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Exception while creating conflict artifact for PromotionSetId {PromotionSetId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        metadata.CorrelationId,
                        promotionSetDto.PromotionSetId
                    )

                    return Option.None
            }

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            promotionSetDto <-
                state.State
                |> Seq.fold (fun dto event -> PromotionSetDto.UpdateDto event dto) promotionSetDto

            Task.CompletedTask

        interface IGraceReminderWithGuidKey with
            member this.ScheduleReminderAsync reminderType delay reminderState correlationId =
                task {
                    let reminderDto =
                        ReminderDto.Create
                            actorName
                            $"{this.IdentityString}"
                            promotionSetDto.OwnerId
                            promotionSetDto.OrganizationId
                            promotionSetDto.RepositoryId
                            reminderType
                            (getFutureInstant delay)
                            reminderState
                            correlationId

                    do! createReminder reminderDto
                }
                :> Task

            member this.ReceiveReminderAsync(reminder: ReminderDto) : Task<Result<unit, GraceError>> =
                task {
                    this.correlationId <- reminder.CorrelationId

                    return
                        Error(
                            (GraceError.Create
                                $"{actorName} does not process reminder type {getDiscriminatedUnionCaseName reminder.ReminderType} with state {getDiscriminatedUnionCaseName reminder.State}."
                                this.correlationId)
                                .enhance ("IsRetryable", "false")
                        )
                }

        member private this.ApplyEvent(promotionSetEvent: PromotionSetEvent) =
            task {
                let normalizedMetadata = this.WithActorMetadata promotionSetEvent.Metadata
                let normalizedEvent = { promotionSetEvent with Metadata = normalizedMetadata }
                let correlationId = normalizedMetadata.CorrelationId

                try
                    state.State.Add(normalizedEvent)
                    do! state.WriteStateAsync()

                    promotionSetDto <-
                        promotionSetDto
                        |> PromotionSetDto.UpdateDto normalizedEvent

                    let graceEvent = GraceEvent.PromotionSetEvent normalizedEvent
                    do! publishGraceEvent graceEvent normalizedMetadata
                    return this.BuildSuccess("Promotion set command succeeded.", correlationId)
                with
                | ex ->
                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Failed to apply event for PromotionSetId: {PromotionSetId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId,
                        promotionSetDto.PromotionSetId
                    )

                    return
                        Error(
                            (GraceError.CreateWithException ex "Failed while applying PromotionSet event." correlationId)
                                .enhance(nameof RepositoryId, promotionSetDto.RepositoryId)
                                .enhance (nameof PromotionSetId, promotionSetDto.PromotionSetId)
                        )
            }

        member private this.RecomputeSteps
            (
                metadata: EventMetadata,
                reason: string option,
                manualResolution: (PromotionSetStepId * ConflictResolutionDecision list) option
            ) =
            task {
                if promotionSetDto.StepsComputationStatus = StepsComputationStatus.Computing then
                    return this.BuildError("PromotionSet steps are already computing.", metadata.CorrelationId)
                else
                    let! currentTerminalReferenceId, targetBaseDirectoryVersionId = this.GetCurrentTerminalPromotion()

                    if manualResolution.IsNone
                       && promotionSetDto.StepsComputationStatus = StepsComputationStatus.Computed
                       && promotionSetDto.ComputedAgainstParentTerminalPromotionReferenceId = Option.Some currentTerminalReferenceId then
                        return this.BuildSuccess("PromotionSet steps are already computed against the current terminal promotion.", metadata.CorrelationId)
                    else
                        match! this.ApplyEvent { Event = PromotionSetEventType.RecomputeStarted currentTerminalReferenceId; Metadata = metadata } with
                        | Error graceError -> return Error graceError
                        | Ok _ ->
                            let! conflictResolutionPolicy = this.GetConflictResolutionPolicy()

                            let orderedSteps =
                                promotionSetDto.Steps
                                |> List.sortBy (fun step -> step.Order)

                            let computedSteps = ResizeArray<PromotionSetStep>()
                            let mutable recomputeFailure: RecomputeFailure option = Option.None
                            let mutable currentHeadDirectoryVersionId = targetBaseDirectoryVersionId
                            let mutable index = 0

                            while index < orderedSteps.Length
                                  && recomputeFailure.IsNone do
                                let currentStep = orderedSteps[index]
                                let! hydratedStepResult = this.HydrateStepProvenance currentStep

                                match hydratedStepResult with
                                | Error graceError -> recomputeFailure <- Option.Some(Failed graceError.Error)
                                | Ok hydratedStep ->
                                    let computedAgainstBaseDirectoryVersionId = currentHeadDirectoryVersionId

                                    let hasConflict =
                                        computedAgainstBaseDirectoryVersionId
                                        <> DirectoryVersionId.Empty
                                        && hydratedStep.OriginalBaseDirectoryVersionId
                                           <> DirectoryVersionId.Empty
                                        && hydratedStep.OriginalBaseDirectoryVersionId
                                           <> computedAgainstBaseDirectoryVersionId

                                    if not hasConflict then
                                        let computedStep =
                                            { hydratedStep with
                                                ComputedAgainstBaseDirectoryVersionId = computedAgainstBaseDirectoryVersionId
                                                AppliedDirectoryVersionId = hydratedStep.OriginalPromotion.DirectoryVersionId
                                                ConflictSummaryArtifactId = Option.None
                                                ConflictStatus = StepConflictStatus.NoConflicts
                                            }

                                        computedSteps.Add(computedStep)
                                        currentHeadDirectoryVersionId <- computedStep.AppliedDirectoryVersionId
                                    else
                                        let manualAccepted, hasManualOverride =
                                            match manualResolution with
                                            | Option.Some (resolvedStepId, decisions) when resolvedStepId = hydratedStep.StepId ->
                                                let accepted =
                                                    decisions
                                                    |> List.exists (fun decision -> decision.Accepted)

                                                let overrideProvided =
                                                    decisions
                                                    |> List.exists (fun decision ->
                                                        decision.Accepted
                                                        && decision.OverrideContentArtifactId.IsSome)

                                                accepted, overrideProvided
                                            | _ -> false, false

                                        if manualAccepted then
                                            let computedStep =
                                                { hydratedStep with
                                                    ComputedAgainstBaseDirectoryVersionId = computedAgainstBaseDirectoryVersionId
                                                    AppliedDirectoryVersionId = hydratedStep.OriginalPromotion.DirectoryVersionId
                                                    ConflictSummaryArtifactId = Option.None
                                                    ConflictStatus = StepConflictStatus.AutoResolved
                                                }

                                            computedSteps.Add(computedStep)
                                            currentHeadDirectoryVersionId <- computedStep.AppliedDirectoryVersionId
                                        else
                                            match conflictResolutionPolicy with
                                            | ConflictResolutionPolicy.NoConflicts _ ->
                                                let reasonText = $"Conflict detected at step {hydratedStep.StepId}, and repository policy is NoConflicts."

                                                let! artifactId =
                                                    this.CreateConflictArtifact(
                                                        hydratedStep,
                                                        reasonText,
                                                        Option.None,
                                                        computedAgainstBaseDirectoryVersionId,
                                                        metadata
                                                    )

                                                recomputeFailure <- Option.Some(Blocked(reasonText, artifactId))
                                            | ConflictResolutionPolicy.ConflictsAllowed threshold ->
                                                let confidence =
                                                    if hasManualOverride then
                                                        1.0
                                                    else
                                                        this.GetModelConfidence(hydratedStep, computedAgainstBaseDirectoryVersionId)

                                                if confidence >= float threshold then
                                                    let computedStep =
                                                        { hydratedStep with
                                                            ComputedAgainstBaseDirectoryVersionId = computedAgainstBaseDirectoryVersionId
                                                            AppliedDirectoryVersionId = hydratedStep.OriginalPromotion.DirectoryVersionId
                                                            ConflictSummaryArtifactId = Option.None
                                                            ConflictStatus = StepConflictStatus.AutoResolved
                                                        }

                                                    computedSteps.Add(computedStep)
                                                    currentHeadDirectoryVersionId <- computedStep.AppliedDirectoryVersionId
                                                else
                                                    let reasonText =
                                                        sprintf
                                                            "Conflict confidence %.2f is below threshold %.2f at step %O."
                                                            confidence
                                                            (float threshold)
                                                            hydratedStep.StepId

                                                    let! artifactId =
                                                        this.CreateConflictArtifact(
                                                            hydratedStep,
                                                            reasonText,
                                                            Option.Some confidence,
                                                            computedAgainstBaseDirectoryVersionId,
                                                            metadata
                                                        )

                                                    recomputeFailure <- Option.Some(Blocked(reasonText, artifactId))

                                index <- index + 1

                            match recomputeFailure with
                            | Option.None ->
                                match!
                                    this.ApplyEvent
                                        {
                                            Event = PromotionSetEventType.StepsUpdated(computedSteps |> Seq.toList, currentTerminalReferenceId)
                                            Metadata = metadata
                                        }
                                    with
                                | Ok graceReturnValue -> return Ok graceReturnValue
                                | Error graceError -> return Error graceError
                            | Option.Some (Blocked (reasonText, artifactId)) ->
                                match! this.ApplyEvent { Event = PromotionSetEventType.Blocked(reasonText, artifactId); Metadata = metadata } with
                                | Ok _ -> return this.BuildError(reasonText, metadata.CorrelationId)
                                | Error graceError -> return Error graceError
                            | Option.Some (Failed reasonText) ->
                                match!
                                    this.ApplyEvent
                                        { Event = PromotionSetEventType.RecomputeFailed(reasonText, currentTerminalReferenceId); Metadata = metadata }
                                    with
                                | Ok _ -> return this.BuildError(reasonText, metadata.CorrelationId)
                                | Error graceError -> return Error graceError
            }

        member private this.RollbackCreatedPromotions(createdReferenceIds: List<ReferenceId>, rollbackReason: string, metadata: EventMetadata) =
            task {
                let mutable index = 0

                while index < createdReferenceIds.Count do
                    let referenceId = createdReferenceIds[index]
                    let referenceActorProxy = Reference.CreateActorProxy referenceId promotionSetDto.RepositoryId this.correlationId
                    let rollbackMetadata = this.WithActorMetadata metadata
                    rollbackMetadata.Properties[ "ActorId" ] <- $"{referenceId}"

                    match! referenceActorProxy.Handle (ReferenceCommand.DeleteLogical(true, rollbackReason)) rollbackMetadata with
                    | Ok _ -> ()
                    | Error graceError ->
                        log.LogWarning(
                            "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Failed to rollback reference {ReferenceId} for PromotionSetId {PromotionSetId}. Error: {GraceError}",
                            getCurrentInstantExtended (),
                            getMachineName,
                            metadata.CorrelationId,
                            referenceId,
                            promotionSetDto.PromotionSetId,
                            graceError
                        )

                    index <- index + 1

                let branchActorProxy = Branch.CreateActorProxy promotionSetDto.TargetBranchId promotionSetDto.RepositoryId this.correlationId
                do! branchActorProxy.MarkForRecompute metadata.CorrelationId
            }

        member private this.CreatePromotionReference(step: PromotionSetStep, isTerminal: bool, metadata: EventMetadata) =
            task {
                let directoryVersionActorProxy =
                    DirectoryVersion.CreateActorProxy step.AppliedDirectoryVersionId promotionSetDto.RepositoryId this.correlationId

                let! directoryVersionDto = directoryVersionActorProxy.Get this.correlationId

                if directoryVersionDto.DirectoryVersion.DirectoryVersionId = DirectoryVersionId.Empty then
                    return
                        Error(
                            (GraceError.Create "Applied directory version does not exist." metadata.CorrelationId)
                                .enhance(nameof PromotionSetStepId, step.StepId)
                                .enhance (nameof DirectoryVersionId, step.AppliedDirectoryVersionId)
                        )
                else
                    let referenceId: ReferenceId = Guid.NewGuid()
                    let links = ResizeArray<ReferenceLinkType>()
                    links.Add(ReferenceLinkType.IncludedInPromotionSet promotionSetDto.PromotionSetId)

                    if isTerminal then
                        links.Add(ReferenceLinkType.PromotionSetTerminal promotionSetDto.PromotionSetId)

                    let referenceMetadata = this.WithActorMetadata metadata
                    referenceMetadata.Properties[ "ActorId" ] <- $"{referenceId}"
                    referenceMetadata.Properties[ nameof BranchId ] <- $"{promotionSetDto.TargetBranchId}"
                    let referenceActorProxy = Reference.CreateActorProxy referenceId promotionSetDto.RepositoryId this.correlationId
                    let referenceText = ReferenceText $"PromotionSet {promotionSetDto.PromotionSetId} Step {step.Order}"

                    let referenceCommand =
                        ReferenceCommand.Create(
                            referenceId,
                            promotionSetDto.OwnerId,
                            promotionSetDto.OrganizationId,
                            promotionSetDto.RepositoryId,
                            promotionSetDto.TargetBranchId,
                            step.AppliedDirectoryVersionId,
                            directoryVersionDto.DirectoryVersion.Sha256Hash,
                            ReferenceType.Promotion,
                            referenceText,
                            links
                        )

                    match! referenceActorProxy.Handle referenceCommand referenceMetadata with
                    | Ok _ -> return Ok referenceId
                    | Error graceError -> return Error graceError
            }

        member private this.GetRequiredValidationsForApply() =
            task {
                let! validationSets = getValidationSets promotionSetDto.RepositoryId 500 false this.correlationId

                return
                    validationSets
                    |> List.filter (fun validationSet -> validationSet.TargetBranchId = promotionSetDto.TargetBranchId)
                    |> List.collect (fun validationSet -> validationSet.Validations)
                    |> List.filter (fun validation -> validation.RequiredForApply)
                    |> List.distinctBy (fun validation -> $"{validation.Name.Trim().ToLowerInvariant()}::{validation.Version.Trim().ToLowerInvariant()}")
            }

        member private this.EnsureRequiredValidationsPass(metadata: EventMetadata) =
            task {
                let! requiredValidations = this.GetRequiredValidationsForApply()

                if requiredValidations.IsEmpty then
                    return Ok()
                else
                    let! scopedValidationResults =
                        getValidationResultsForPromotionSetAttempt
                            promotionSetDto.RepositoryId
                            promotionSetDto.PromotionSetId
                            promotionSetDto.StepsComputationAttempt
                            5000
                            this.correlationId

                    let hasPass (validationName: string) (validationVersion: string) =
                        scopedValidationResults
                        |> List.exists (fun validationResult ->
                            String.Equals(validationResult.ValidationName, validationName, StringComparison.OrdinalIgnoreCase)
                            && String.Equals(validationResult.ValidationVersion, validationVersion, StringComparison.OrdinalIgnoreCase)
                            && validationResult.Output.Status = ValidationStatus.Pass)

                    let missingOrFailing =
                        requiredValidations
                        |> List.filter (fun validation -> not <| hasPass validation.Name validation.Version)

                    if missingOrFailing.IsEmpty then
                        return Ok()
                    else
                        let details =
                            missingOrFailing
                            |> List.map (fun validation -> $"{validation.Name}:{validation.Version}")
                            |> String.concat ", "

                        return
                            Error(
                                (GraceError.Create
                                    $"Required validations have not passed for StepsComputationAttempt {promotionSetDto.StepsComputationAttempt}: {details}."
                                    metadata.CorrelationId)
                                    .enhance(nameof PromotionSetId, promotionSetDto.PromotionSetId)
                                    .enhance ("StepsComputationAttempt", promotionSetDto.StepsComputationAttempt)
                            )
            }

        member private this.ApplyPromotionSet(metadata: EventMetadata) =
            task {
                if promotionSetDto.Status = PromotionSetStatus.Succeeded then
                    return this.BuildError("PromotionSet has already been applied successfully.", metadata.CorrelationId)
                elif promotionSetDto.DeletedAt.IsSome then
                    return this.BuildError("PromotionSet has been deleted and cannot be applied.", metadata.CorrelationId)
                else
                    let! currentTerminalReferenceId, _ = this.GetCurrentTerminalPromotion()

                    let needsRecompute =
                        promotionSetDto.StepsComputationStatus
                        <> StepsComputationStatus.Computed
                        || promotionSetDto.ComputedAgainstParentTerminalPromotionReferenceId
                           <> Option.Some currentTerminalReferenceId

                    let mutable recomputeError: GraceError option = Option.None

                    if needsRecompute then
                        let! recomputeResult = this.RecomputeSteps(metadata, Option.Some "Apply requested on stale or uncomputed steps.", Option.None)

                        match recomputeResult with
                        | Ok _ -> ()
                        | Error graceError -> recomputeError <- Option.Some graceError

                    let mutable preconditionError: GraceError option = recomputeError

                    if preconditionError.IsNone
                       && promotionSetDto.StepsComputationStatus
                          <> StepsComputationStatus.Computed then
                        preconditionError <- Option.Some(GraceError.Create "PromotionSet steps are not computed." metadata.CorrelationId)

                    if preconditionError.IsNone
                       && promotionSetDto.Steps.IsEmpty then
                        preconditionError <- Option.Some(GraceError.Create "PromotionSet does not have any steps to apply." metadata.CorrelationId)

                    if preconditionError.IsNone then
                        match! this.EnsureRequiredValidationsPass metadata with
                        | Ok _ -> ()
                        | Error graceError -> preconditionError <- Option.Some graceError

                    if preconditionError.IsSome then
                        return Error preconditionError.Value
                    else
                        match! this.ApplyEvent { Event = PromotionSetEventType.ApplyStarted; Metadata = metadata } with
                        | Error graceError -> return Error graceError
                        | Ok _ ->
                            let createdReferenceIds = List<ReferenceId>()

                            let orderedSteps =
                                promotionSetDto.Steps
                                |> List.sortBy (fun step -> step.Order)

                            let mutable applyError: GraceError option = Option.None
                            let mutable index = 0

                            while index < orderedSteps.Length && applyError.IsNone do
                                let step = orderedSteps[index]
                                let isTerminal = index = (orderedSteps.Length - 1)
                                let! createReferenceResult = this.CreatePromotionReference(step, isTerminal, metadata)

                                match createReferenceResult with
                                | Ok referenceId -> createdReferenceIds.Add(referenceId)
                                | Error graceError -> applyError <- Option.Some graceError

                                index <- index + 1

                            match applyError with
                            | Option.None ->
                                let branchActorProxy = Branch.CreateActorProxy promotionSetDto.TargetBranchId promotionSetDto.RepositoryId this.correlationId
                                do! branchActorProxy.MarkForRecompute metadata.CorrelationId
                                let terminalReferenceId = createdReferenceIds[createdReferenceIds.Count - 1]

                                match! this.ApplyEvent { Event = PromotionSetEventType.Applied terminalReferenceId; Metadata = metadata } with
                                | Error graceError -> return Error graceError
                                | Ok graceReturnValue ->
                                    let queueActorProxy =
                                        PromotionQueue.CreateActorProxy promotionSetDto.TargetBranchId promotionSetDto.RepositoryId this.correlationId

                                    let! queueExists = queueActorProxy.Exists metadata.CorrelationId

                                    if queueExists then
                                        let dequeueMetadata = this.WithActorMetadata metadata

                                        match! queueActorProxy.Handle (PromotionQueueCommand.Dequeue promotionSetDto.PromotionSetId) dequeueMetadata with
                                        | Ok _ -> ()
                                        | Error graceError ->
                                            log.LogWarning(
                                                "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Failed to dequeue PromotionSetId {PromotionSetId} after apply. Error: {GraceError}",
                                                getCurrentInstantExtended (),
                                                getMachineName,
                                                metadata.CorrelationId,
                                                promotionSetDto.PromotionSetId,
                                                graceError
                                            )

                                    return Ok graceReturnValue
                            | Option.Some graceError ->
                                do!
                                    this.RollbackCreatedPromotions(
                                        createdReferenceIds,
                                        "PromotionSet apply failed. Rolling back previously created references.",
                                        metadata
                                    )

                                match! this.ApplyEvent { Event = PromotionSetEventType.ApplyFailed graceError.Error; Metadata = metadata } with
                                | Ok _ -> return Error graceError
                                | Error applyFailureError -> return Error applyFailureError
            }

        interface IHasRepositoryId with
            member this.GetRepositoryId correlationId = promotionSetDto.RepositoryId |> returnTask

        interface IPromotionSetActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId

                not
                <| promotionSetDto.PromotionSetId.Equals(PromotionSetId.Empty)
                |> returnTask

            member this.IsDeleted correlationId =
                this.correlationId <- correlationId
                promotionSetDto.DeletedAt.IsSome |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId
                promotionSetDto |> returnTask

            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                state.State :> IReadOnlyList<PromotionSetEvent>
                |> returnTask

            member this.Handle command metadata =
                let isValid (promotionSetCommand: PromotionSetCommand) (eventMetadata: EventMetadata) =
                    task {
                        if state.State.Exists(fun ev -> ev.Metadata.CorrelationId = eventMetadata.CorrelationId) then
                            return Error(GraceError.Create "Duplicate correlation ID for PromotionSet command." eventMetadata.CorrelationId)
                        else
                            match promotionSetCommand with
                            | PromotionSetCommand.CreatePromotionSet _ when
                                promotionSetDto.PromotionSetId
                                <> PromotionSetId.Empty
                                ->
                                return Error(GraceError.Create "PromotionSet already exists." eventMetadata.CorrelationId)
                            | PromotionSetCommand.CreatePromotionSet _ -> return Ok promotionSetCommand
                            | _ when promotionSetDto.PromotionSetId = PromotionSetId.Empty ->
                                return Error(GraceError.Create "PromotionSet does not exist." eventMetadata.CorrelationId)
                            | PromotionSetCommand.Apply when promotionSetDto.Status = PromotionSetStatus.Succeeded ->
                                return Error(GraceError.Create "PromotionSet has already been applied successfully." eventMetadata.CorrelationId)
                            | PromotionSetCommand.Apply when promotionSetDto.Status = PromotionSetStatus.Running ->
                                return Error(GraceError.Create "PromotionSet is already running." eventMetadata.CorrelationId)
                            | PromotionSetCommand.RecomputeStepsIfStale _ when promotionSetDto.StepsComputationStatus = StepsComputationStatus.Computing ->
                                return Error(GraceError.Create "PromotionSet steps are already computing." eventMetadata.CorrelationId)
                            | PromotionSetCommand.ResolveConflicts _ when
                                promotionSetDto.Status
                                <> PromotionSetStatus.Blocked
                                ->
                                return Error(GraceError.Create "PromotionSet is not blocked for conflict review." eventMetadata.CorrelationId)
                            | PromotionSetCommand.UpdateInputPromotions _ when promotionSetDto.Status = PromotionSetStatus.Succeeded ->
                                return Error(GraceError.Create "PromotionSet has already succeeded and cannot be edited." eventMetadata.CorrelationId)
                            | _ -> return Ok promotionSetCommand
                    }

                let processCommand (promotionSetCommand: PromotionSetCommand) (eventMetadata: EventMetadata) =
                    task {
                        match promotionSetCommand with
                        | PromotionSetCommand.CreatePromotionSet (promotionSetId, ownerId, organizationId, repositoryId, targetBranchId) ->
                            return!
                                this.ApplyEvent
                                    {
                                        Event = PromotionSetEventType.Created(promotionSetId, ownerId, organizationId, repositoryId, targetBranchId)
                                        Metadata = eventMetadata
                                    }
                        | PromotionSetCommand.UpdateInputPromotions promotionPointers ->
                            match! this.ApplyEvent { Event = PromotionSetEventType.InputPromotionsUpdated promotionPointers; Metadata = eventMetadata } with
                            | Error graceError -> return Error graceError
                            | Ok _ -> return! this.RecomputeSteps(eventMetadata, Option.Some "Input promotions changed.", Option.None)
                        | PromotionSetCommand.RecomputeStepsIfStale reason -> return! this.RecomputeSteps(eventMetadata, reason, Option.None)
                        | PromotionSetCommand.ResolveConflicts (stepId, resolutions) ->
                            return! this.RecomputeSteps(eventMetadata, Option.Some "Manual conflict resolutions submitted.", Option.Some(stepId, resolutions))
                        | PromotionSetCommand.Apply -> return! this.ApplyPromotionSet eventMetadata
                        | PromotionSetCommand.DeleteLogical (force, deleteReason) ->
                            return! this.ApplyEvent { Event = PromotionSetEventType.LogicalDeleted(force, deleteReason); Metadata = eventMetadata }
                    }

                task {
                    currentCommand <- getDiscriminatedUnionCaseName command
                    this.correlationId <- metadata.CorrelationId
                    RequestContext.Set(Constants.CurrentCommandProperty, getDiscriminatedUnionCaseName command)

                    match! isValid command metadata with
                    | Ok validCommand -> return! processCommand validCommand metadata
                    | Error validationError -> return Error validationError
                }
