namespace Grace.Server

open Giraffe
open Grace.Actors.Constants
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Server.ApplicationContext
open Grace.Server.Services
open Grace.Shared
open Grace.Shared.Extensions
open Grace.Shared.Parameters.Review
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors
open Grace.Shared.Validation.Utilities
open Grace.Types.Policy
open Grace.Types.PromotionSet
open Grace.Types.Queue
open Grace.Types.Review
open Grace.Types.Types
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading.Tasks

module Review =
    type Validations<'T when 'T :> ReviewParameters> = 'T -> ValueTask<Result<unit, ReviewError>> array

    let log = ApplicationContext.loggerFactory.CreateLogger("Review.Server")

    let activitySource = new ActivitySource("Review")

    let private resolveCandidatePromotionSetWith
        (resolvePromotionSet: Guid -> Task<Grace.Types.PromotionSet.PromotionSetDto option>)
        (parameters: CandidateProjectionParameters)
        =
        task {
            let normalizedCandidateId = ReviewModels.normalizeCandidateId parameters.CandidateId

            match ReviewModels.tryParseCandidateId parameters.CandidateId with
            | Error _ ->
                let graceError =
                    (GraceError.Create "CandidateId must be a valid non-empty Guid." parameters.CorrelationId)
                        .enhance("CandidateId", parameters.CandidateId)
                        .enhance("NormalizedCandidateId", normalizedCandidateId)
                        .enhance (nameof RepositoryId, parameters.RepositoryId)

                return Error graceError
            | Ok (candidateGuid, canonicalCandidateId) ->
                let! promotionSet = resolvePromotionSet candidateGuid

                match promotionSet with
                | Option.Some promotionSet -> return Ok(candidateGuid, canonicalCandidateId, promotionSet)
                | Option.None ->
                    let graceError =
                        (GraceError.Create $"Candidate '{canonicalCandidateId}' was not found in repository scope." parameters.CorrelationId)
                            .enhance("CandidateId", parameters.CandidateId)
                            .enhance("NormalizedCandidateId", canonicalCandidateId)
                            .enhance (nameof RepositoryId, parameters.RepositoryId)

                    return Error graceError
        }

    let private resolvePromotionSetById (context: HttpContext) (promotionSetId: Guid) =
        task {
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let actorProxy = PromotionSet.CreateActorProxy promotionSetId graceIds.RepositoryId correlationId
            let! exists = actorProxy.Exists correlationId

            if exists then
                let! promotionSet = actorProxy.Get correlationId
                return Option.Some promotionSet
            else
                return Option.None
        }

    let private resolveCandidateProjectionContext (context: HttpContext) (parameters: CandidateProjectionParameters) =
        task {
            match! resolveCandidatePromotionSetWith (resolvePromotionSetById context) parameters with
            | Error error -> return Error error
            | Ok (_, normalizedCandidateId, promotionSet) ->
                let identity =
                    ReviewModels.createCandidateIdentityProjection
                        normalizedCandidateId
                        promotionSet.PromotionSetId
                        parameters.OwnerId
                        parameters.OrganizationId
                        parameters.RepositoryId

                return Ok(identity, promotionSet)
        }

    let private tryGetQueueForPromotionSet (context: HttpContext) (promotionSet: PromotionSetDto) =
        task {
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let actorProxy = PromotionQueue.CreateActorProxy promotionSet.TargetBranchId graceIds.RepositoryId correlationId
            let! exists = actorProxy.Exists correlationId

            if exists then
                let! queue = actorProxy.Get correlationId
                return Option.Some queue
            else
                return Option.None
        }

    let private getReviewStateForPromotionSet (context: HttpContext) (promotionSetId: PromotionSetId) =
        task {
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let actorProxy = Review.CreateActorProxy promotionSetId graceIds.RepositoryId correlationId
            let! notes = actorProxy.GetNotes correlationId
            let! checkpoints = actorProxy.GetCheckpoints correlationId
            return notes, (checkpoints |> Seq.toList)
        }

    let internal deriveCandidateRequiredActions
        (promotionSetStatus: PromotionSetStatus)
        (stepsComputationStatus: StepsComputationStatus)
        (queueState: QueueState option)
        (unresolvedFindingCount: int)
        (validationSummaryAvailable: bool)
        =
        let requiredActions = ResizeArray<string>()
        let diagnostics = ResizeArray<string>()

        if stepsComputationStatus = StepsComputationStatus.NotComputed
           || stepsComputationStatus = StepsComputationStatus.ComputeFailed then
            requiredActions.Add("RetryComputation")

        if promotionSetStatus = PromotionSetStatus.Blocked then
            requiredActions.Add("ResolveConflicts")

        match queueState with
        | Option.Some QueueState.Paused -> requiredActions.Add("ResumeQueue")
        | Option.Some QueueState.Degraded -> requiredActions.Add("RepairQueue")
        | Option.Some _ -> ()
        | Option.None -> diagnostics.Add("Queue state is unavailable for this candidate.")

        if unresolvedFindingCount > 0 then requiredActions.Add("ResolveFindings")

        if not validationSummaryAvailable then
            requiredActions.Add("ConfirmValidationSummary")

        if requiredActions.Count = 0 then requiredActions.Add("NoActionRequired")

        requiredActions |> Seq.distinct |> Seq.toList, diagnostics |> Seq.toList

    let private buildCandidateProjectionSourceStates (queueExists: bool) (reviewNotesExists: bool) =
        [
            ReviewModels.createProjectionSourceStateMetadata "identity" ProjectionSourceStates.Authoritative "Resolved from candidate identity projection."
            ReviewModels.createProjectionSourceStateMetadata "promotionSet" ProjectionSourceStates.Authoritative "Resolved from PromotionSet.Get."
            if queueExists then
                ReviewModels.createProjectionSourceStateMetadata "queue" ProjectionSourceStates.Authoritative "Resolved from Queue.Get."
            else
                ReviewModels.createProjectionSourceStateMetadata
                    "queue"
                    ProjectionSourceStates.NotAvailable
                    "Queue is not initialized for candidate target branch."
            if reviewNotesExists then
                ReviewModels.createProjectionSourceStateMetadata "review" ProjectionSourceStates.Authoritative "Resolved from Review.GetNotes."
            else
                ReviewModels.createProjectionSourceStateMetadata
                    "review"
                    ProjectionSourceStates.NotAvailable
                    "Review notes are not available for this candidate."
        ]

    let internal buildCandidateProjectionSnapshot
        (identity: CandidateIdentityProjection)
        (promotionSet: PromotionSetDto)
        (queue: PromotionQueue option)
        (reviewNotes: ReviewNotes option)
        =
        let unresolvedFindingCount =
            reviewNotes
            |> Option.map (fun notes ->
                notes.Findings
                |> List.filter (fun finding ->
                    finding.ResolutionState
                    <> FindingResolutionState.Approved)
                |> List.length)
            |> Option.defaultValue 0

        let validationSummaryAvailable =
            reviewNotes
            |> Option.bind (fun notes -> notes.ValidationSummary)
            |> Option.isSome

        let requiredActions, diagnostics =
            deriveCandidateRequiredActions
                promotionSet.Status
                promotionSet.StepsComputationStatus
                (queue |> Option.map (fun value -> value.State))
                unresolvedFindingCount
                validationSummaryAvailable

        let queueDiagnostics =
            if reviewNotes.IsSome then
                diagnostics
            else
                diagnostics
                @ [
                    "Review notes are not available for this candidate."
                ]

        let result = CandidateProjectionSnapshotResult()
        result.Identity <- identity
        result.PromotionSetStatus <- getDiscriminatedUnionCaseName promotionSet.Status
        result.StepsComputationStatus <- getDiscriminatedUnionCaseName promotionSet.StepsComputationStatus

        result.QueueState <-
            queue
            |> Option.map (fun value -> getDiscriminatedUnionCaseName value.State)
            |> Option.defaultValue ProjectionSourceStates.NotAvailable

        result.RunningPromotionSetId <-
            queue
            |> Option.bind (fun value -> value.RunningPromotionSetId)
            |> Option.map string
            |> Option.defaultValue String.Empty

        result.UnresolvedFindingCount <- unresolvedFindingCount
        result.ValidationSummaryAvailable <- validationSummaryAvailable
        result.RequiredActions <- requiredActions
        result.Diagnostics <- queueDiagnostics
        result.SourceStates <- buildCandidateProjectionSourceStates queue.IsSome reviewNotes.IsSome
        result

    let internal buildCandidateAttestationEntries (policySnapshotId: string option) (latestCheckpoint: ReviewCheckpoint option) =
        let policyAttestation = CandidateAttestation(Name = "PolicySnapshot")

        match policySnapshotId with
        | Option.Some snapshotId ->
            policyAttestation.Status <- ProjectionSourceStates.Authoritative
            policyAttestation.Detail <- $"Policy snapshot '{snapshotId}' is available."
        | Option.None ->
            policyAttestation.Status <- ProjectionSourceStates.NotAvailable
            policyAttestation.Detail <- "Policy snapshot context is unavailable for this candidate."

        let checkpointAttestation = CandidateAttestation(Name = "ReviewCheckpoint")

        match latestCheckpoint with
        | Option.Some checkpoint ->
            checkpointAttestation.Status <- ProjectionSourceStates.Authoritative

            checkpointAttestation.Detail <- $"Checkpoint '{checkpoint.ReviewCheckpointId}' by '{checkpoint.Reviewer}' at '{checkpoint.Timestamp}'."
        | Option.None ->
            checkpointAttestation.Status <- ProjectionSourceStates.NotAvailable
            checkpointAttestation.Detail <- "No review checkpoint is available for this candidate."

        let diagnostics =
            [
                if policySnapshotId.IsNone then
                    "Policy snapshot context is unavailable for this candidate."
                if latestCheckpoint.IsNone then
                    "Review checkpoint context is unavailable for this candidate."
            ]

        let sourceStates =
            [
                ReviewModels.createProjectionSourceStateMetadata "identity" ProjectionSourceStates.Authoritative "Resolved from candidate identity projection."
                ReviewModels.createProjectionSourceStateMetadata
                    "policy"
                    (if policySnapshotId.IsSome then
                         ProjectionSourceStates.Authoritative
                     else
                         ProjectionSourceStates.NotAvailable)
                    "Resolved from Policy.GetCurrent."
                ReviewModels.createProjectionSourceStateMetadata
                    "checkpoint"
                    (if latestCheckpoint.IsSome then
                         ProjectionSourceStates.Authoritative
                     else
                         ProjectionSourceStates.NotAvailable)
                    "Resolved from Review.GetCheckpoints."
            ]

        [
            policyAttestation
            checkpointAttestation
        ],
        diagnostics,
        sourceStates

    let private findingSeverityRank (severity: FindingSeverity) =
        match severity with
        | FindingSeverity.Critical -> 0
        | FindingSeverity.High -> 1
        | FindingSeverity.Medium -> 2
        | FindingSeverity.Low -> 3
        | FindingSeverity.Info -> 4

    let private requiredActionRank (action: string) =
        match action with
        | "ResolveConflicts" -> 0
        | "ResolveFindings" -> 1
        | "RetryComputation" -> 2
        | "ResumeQueue" -> 3
        | "RepairQueue" -> 4
        | "ConfirmValidationSummary" -> 5
        | "NoActionRequired" -> 6
        | _ -> 99

    let private blockerSeverityRank (severity: string) =
        match severity with
        | "Critical" -> 0
        | "High" -> 1
        | "Medium" -> 2
        | "Low" -> 3
        | "Info" -> 4
        | _ -> 99

    let private reportSectionRank (section: string) =
        match section with
        | value when value = ReviewModels.ReviewReportSections.CandidateAndPromotionSet -> 0
        | value when value = ReviewModels.ReviewReportSections.QueueAndRequiredActions -> 1
        | value when value = ReviewModels.ReviewReportSections.ValidationAndGateOutcomes -> 2
        | value when value = ReviewModels.ReviewReportSections.ReviewNotesAndCheckpoint -> 3
        | value when value = ReviewModels.ReviewReportSections.WorkItemLinksAndArtifacts -> 4
        | value when value = ReviewModels.ReviewReportSections.BlockingReasonsAndNextActions -> 5
        | _ -> 99

    let private actionCategoryRank (category: string) =
        match category with
        | "candidate" -> 0
        | "queue" -> 1
        | "promotion-set" -> 2
        | "review" -> 3
        | _ -> 99

    let private sortSourceStates (sourceStates: ProjectionSourceStateMetadata list) =
        sourceStates
        |> List.sortBy (fun sourceState -> sourceState.Section, sourceState.SourceState, sourceState.Detail)

    let private mapRequiredActionToBlockerAndSuggestion (candidateId: string) (promotionSetId: string) (targetBranchId: string) (action: string) =
        match action with
        | "ResolveConflicts" ->
            Option.Some(
                "Critical",
                ReviewModels.ReviewReportSections.CandidateAndPromotionSet,
                "Promotion set is blocked by unresolved conflicts.",
                "promotion-set",
                $"grace promotion-set conflicts show --promotion-set {promotionSetId}"
            )
        | "ResolveFindings" ->
            Option.Some(
                "High",
                ReviewModels.ReviewReportSections.ReviewNotesAndCheckpoint,
                "Review findings require resolution before candidate promotion can continue.",
                "review",
                $"grace review open --promotion-set {promotionSetId}"
            )
        | "RetryComputation" ->
            Option.Some(
                "High",
                ReviewModels.ReviewReportSections.CandidateAndPromotionSet,
                "Promotion set computation is stale or failed and requires recomputation.",
                "candidate",
                $"grace candidate retry --candidate {candidateId}"
            )
        | "ResumeQueue" ->
            Option.Some(
                "Medium",
                ReviewModels.ReviewReportSections.QueueAndRequiredActions,
                "Target branch queue is paused.",
                "queue",
                $"grace queue resume --branch {targetBranchId}"
            )
        | "RepairQueue" ->
            Option.Some(
                "Medium",
                ReviewModels.ReviewReportSections.QueueAndRequiredActions,
                "Target branch queue is degraded and needs operator attention.",
                "queue",
                $"grace queue status --branch {targetBranchId}"
            )
        | "ConfirmValidationSummary" ->
            Option.Some(
                "Low",
                ReviewModels.ReviewReportSections.ValidationAndGateOutcomes,
                "Validation summary is unavailable for this candidate.",
                "review",
                $"grace review open --promotion-set {promotionSetId}"
            )
        | "NoActionRequired" -> Option.None
        | _ ->
            Option.Some(
                "Low",
                ReviewModels.ReviewReportSections.BlockingReasonsAndNextActions,
                $"Unknown required action '{action}' was reported.",
                "review",
                $"grace review report show --candidate {candidateId}"
            )

    let internal buildReviewReport
        (identity: CandidateIdentityProjection)
        (promotionSet: PromotionSetDto)
        (snapshot: CandidateProjectionSnapshotResult)
        (reviewNotes: ReviewNotes option)
        (checkpoints: ReviewCheckpoint list)
        =
        let promotionSetId = promotionSet.PromotionSetId.ToString()
        let targetBranchId = promotionSet.TargetBranchId.ToString()

        let requiredActions =
            snapshot.RequiredActions
            |> List.sortBy requiredActionRank

        let queueDiagnostics = snapshot.Diagnostics |> List.sort

        let identitySectionSourceStates =
            snapshot.SourceStates
            |> List.filter (fun sourceState ->
                sourceState.Section = "identity"
                || sourceState.Section = "promotionSet")
            |> sortSourceStates

        let identitySection =
            ReviewModels.createReviewReportSection
                ReviewModels.ReviewReportSections.CandidateAndPromotionSet
                "Candidate and PromotionSet identity or status"
                ProjectionSourceStates.Authoritative
                identitySectionSourceStates
                [
                    ReviewModels.createReviewReportEntry "CandidateId" [ identity.CandidateId ]
                    ReviewModels.createReviewReportEntry "PromotionSetId" [ identity.PromotionSetId ]
                    ReviewModels.createReviewReportEntry "IdentityMode" [ identity.IdentityMode ]
                    ReviewModels.createReviewReportEntry "PromotionSetStatus" [ snapshot.PromotionSetStatus ]
                    ReviewModels.createReviewReportEntry "StepsComputationStatus" [ snapshot.StepsComputationStatus ]
                ]
                []

        let queueSourceState =
            snapshot.SourceStates
            |> List.tryFind (fun sourceState -> sourceState.Section = "queue")
            |> Option.map (fun sourceState -> sourceState.SourceState)
            |> Option.defaultValue ProjectionSourceStates.NotAvailable

        let queueSectionSourceStates =
            snapshot.SourceStates
            |> List.filter (fun sourceState ->
                sourceState.Section = "queue"
                || sourceState.Section = "identity")
            |> sortSourceStates

        let queueSection =
            ReviewModels.createReviewReportSection
                ReviewModels.ReviewReportSections.QueueAndRequiredActions
                "Queue state and required actions"
                queueSourceState
                queueSectionSourceStates
                [
                    ReviewModels.createReviewReportEntry "QueueState" [ snapshot.QueueState ]
                    ReviewModels.createReviewReportEntry
                        "RunningPromotionSetId"
                        [
                            if String.IsNullOrWhiteSpace(snapshot.RunningPromotionSetId) then
                                ProjectionSourceStates.NotAvailable
                            else
                                snapshot.RunningPromotionSetId
                        ]
                    ReviewModels.createReviewReportEntry "RequiredActions" requiredActions
                ]
                queueDiagnostics

        let validationSummary =
            reviewNotes
            |> Option.bind (fun notes -> notes.ValidationSummary)

        let validationResultIds =
            validationSummary
            |> Option.map (fun summary ->
                summary.ValidationResultIds
                |> List.map string
                |> List.sort)
            |> Option.defaultValue [ ProjectionSourceStates.NotAvailable ]

        let gateOutcomes =
            requiredActions
            |> List.filter (fun action -> action <> "NoActionRequired")
            |> List.map (fun action -> $"ActionRequired:{action}")
            |> function
                | [] -> [ "NoGateBlockersDetected" ]
                | values -> values

        let validationSourceState =
            if validationSummary.IsSome then
                ProjectionSourceStates.Authoritative
            else
                ProjectionSourceStates.NotAvailable

        let validationSectionSourceStates =
            [
                ReviewModels.createProjectionSourceStateMetadata "validation" validationSourceState "Resolved from ReviewNotes.ValidationSummary."
            ]
            |> sortSourceStates

        let validationDiagnostics =
            if validationSummary.IsSome then
                []
            else
                [
                    "Validation summary is not available for this candidate."
                ]

        let validationSection =
            ReviewModels.createReviewReportSection
                ReviewModels.ReviewReportSections.ValidationAndGateOutcomes
                "Validation results and gate-like outcomes"
                validationSourceState
                validationSectionSourceStates
                [
                    ReviewModels.createReviewReportEntry
                        "ValidationSummary"
                        [
                            validationSummary
                            |> Option.map (fun summary ->
                                if String.IsNullOrWhiteSpace(summary.Summary) then
                                    ProjectionSourceStates.NotAvailable
                                else
                                    summary.Summary)
                            |> Option.defaultValue ProjectionSourceStates.NotAvailable
                        ]
                    ReviewModels.createReviewReportEntry "ValidationResultIds" validationResultIds
                    ReviewModels.createReviewReportEntry "GateOutcomes" gateOutcomes
                ]
                validationDiagnostics

        let latestCheckpoint =
            checkpoints
            |> List.sortByDescending (fun checkpoint -> checkpoint.Timestamp, checkpoint.ReviewCheckpointId)
            |> List.tryHead

        let reviewSourceState =
            if reviewNotes.IsSome then
                ProjectionSourceStates.Authoritative
            else
                ProjectionSourceStates.NotAvailable

        let findingsBySeverity =
            reviewNotes
            |> Option.map (fun notes ->
                notes.Findings
                |> List.groupBy (fun finding -> finding.Severity)
                |> List.sortBy (fun (severity, _) -> findingSeverityRank severity)
                |> List.map (fun (severity, findings) ->
                    let unresolvedCount =
                        findings
                        |> List.filter (fun finding ->
                            finding.ResolutionState
                            <> FindingResolutionState.Approved)
                        |> List.length

                    $"{getDiscriminatedUnionCaseName severity}: total={findings.Length}; unresolved={unresolvedCount}"))
            |> Option.defaultValue [ ProjectionSourceStates.NotAvailable ]

        let reviewDiagnostics =
            [
                if reviewNotes.IsNone then "Review notes are not available for this candidate."
                if latestCheckpoint.IsNone then
                    "Review checkpoint context is unavailable for this candidate."
            ]

        let reviewSectionSourceStates =
            [
                ReviewModels.createProjectionSourceStateMetadata "review" reviewSourceState "Resolved from Review.GetNotes."
                ReviewModels.createProjectionSourceStateMetadata
                    "checkpoint"
                    (if latestCheckpoint.IsSome then
                         ProjectionSourceStates.Authoritative
                     else
                         ProjectionSourceStates.NotAvailable)
                    "Resolved from Review.GetCheckpoints."
            ]
            |> sortSourceStates

        let reviewSection =
            ReviewModels.createReviewReportSection
                ReviewModels.ReviewReportSections.ReviewNotesAndCheckpoint
                "Review notes, findings, and checkpoint summary"
                reviewSourceState
                reviewSectionSourceStates
                [
                    ReviewModels.createReviewReportEntry
                        "ReviewNotesId"
                        [
                            reviewNotes
                            |> Option.map (fun notes -> notes.ReviewNotesId.ToString())
                            |> Option.defaultValue ProjectionSourceStates.NotAvailable
                        ]
                    ReviewModels.createReviewReportEntry
                        "ReviewSummary"
                        [
                            reviewNotes
                            |> Option.map (fun notes ->
                                if String.IsNullOrWhiteSpace(notes.Summary) then
                                    ProjectionSourceStates.NotAvailable
                                else
                                    notes.Summary)
                            |> Option.defaultValue ProjectionSourceStates.NotAvailable
                        ]
                    ReviewModels.createReviewReportEntry
                        "FindingsTotal"
                        [
                            reviewNotes
                            |> Option.map (fun notes -> notes.Findings.Length.ToString())
                            |> Option.defaultValue "0"
                        ]
                    ReviewModels.createReviewReportEntry
                        "FindingsUnresolved"
                        [
                            snapshot.UnresolvedFindingCount.ToString()
                        ]
                    ReviewModels.createReviewReportEntry "FindingsBySeverity" findingsBySeverity
                    ReviewModels.createReviewReportEntry
                        "LatestCheckpoint"
                        [
                            latestCheckpoint
                            |> Option.map (fun checkpoint ->
                                $"Checkpoint '{checkpoint.ReviewCheckpointId}' by '{checkpoint.Reviewer}' at '{checkpoint.Timestamp}'.")
                            |> Option.defaultValue ProjectionSourceStates.NotAvailable
                        ]
                ]
                reviewDiagnostics

        let workItemDiagnostics =
            [
                "Work item reverse lookup is not available for candidate projections."
                "Artifact summary is unavailable without authoritative work-item links."
            ]

        let workItemSection =
            ReviewModels.createReviewReportSection
                ReviewModels.ReviewReportSections.WorkItemLinksAndArtifacts
                "Work-item links and artifact summary"
                ProjectionSourceStates.NotAvailable
                [
                    ReviewModels.createProjectionSourceStateMetadata
                        "workItems"
                        ProjectionSourceStates.NotAvailable
                        "Candidate to WorkItem reverse lookup endpoint is not available."
                ]
                [
                    ReviewModels.createReviewReportEntry "LinkedWorkItems" [ ProjectionSourceStates.NotAvailable ]
                    ReviewModels.createReviewReportEntry "ArtifactSummary" [ ProjectionSourceStates.NotAvailable ]
                ]
                workItemDiagnostics

        let requiredActionBlockers =
            requiredActions
            |> List.choose (mapRequiredActionToBlockerAndSuggestion identity.CandidateId promotionSetId targetBranchId)
            |> List.distinct
            |> List.sortBy (fun (severity, section, reason, _, _) -> blockerSeverityRank severity, reportSectionRank section, reason)

        let diagnosticBlockers =
            (queueDiagnostics
             @ validationDiagnostics
               @ reviewDiagnostics @ workItemDiagnostics)
            |> List.distinct
            |> List.sort
            |> List.map (fun diagnostic ->
                "Low",
                ReviewModels.ReviewReportSections.BlockingReasonsAndNextActions,
                diagnostic,
                "review",
                $"grace review report show --candidate {identity.CandidateId}")

        let blockers =
            requiredActionBlockers @ diagnosticBlockers
            |> List.distinct
            |> List.sortBy (fun (severity, section, reason, _, _) -> blockerSeverityRank severity, reportSectionRank section, reason)

        let blockerLines =
            blockers
            |> List.map (fun (severity, section, reason, _, _) -> $"{severity}|{section}|{reason}")
            |> function
                | [] ->
                    [
                        "Info|blocking-reasons-and-next-actions|No blockers detected."
                    ]
                | values -> values

        let nextActions =
            blockers
            |> List.map (fun (_, _, _, category, command) -> category, command)
            |> List.distinct
            |> List.sortBy (fun (category, command) -> actionCategoryRank category, command)
            |> List.map snd
            |> function
                | [] -> [ "No action required." ]
                | values -> values

        let blockingSection =
            ReviewModels.createReviewReportSection
                ReviewModels.ReviewReportSections.BlockingReasonsAndNextActions
                "Blocking reasons and next actions"
                ProjectionSourceStates.Inferred
                [
                    ReviewModels.createProjectionSourceStateMetadata
                        "blockers"
                        ProjectionSourceStates.Inferred
                        "Derived from candidate projection, review context, and deterministic required actions."
                ]
                [
                    ReviewModels.createReviewReportEntry "Blockers" blockerLines
                    ReviewModels.createReviewReportEntry "NextActions" nextActions
                ]
                []

        let report = ReviewModels.ReviewReportResult()
        report.ReviewReportSchemaVersion <- ReviewModels.ReviewReportSchema.Version
        report.SectionOrder <- ReviewModels.ReviewReportSections.Ordered

        report.Sections <-
            [
                identitySection
                queueSection
                validationSection
                reviewSection
                workItemSection
                blockingSection
            ]

        report

    let private tryGetCurrentPolicySnapshotId (context: HttpContext) (targetBranchId: BranchId) =
        task {
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let actorProxy = Policy.CreateActorProxy targetBranchId graceIds.RepositoryId correlationId
            let! currentPolicy = actorProxy.GetCurrent correlationId

            return
                currentPolicy
                |> Option.bind (fun policy ->
                    let snapshotId = string policy.PolicySnapshotId

                    if String.IsNullOrWhiteSpace(snapshotId) then
                        Option.None
                    else
                        Option.Some snapshotId)
        }

    let private enrichCandidateReturnValue (context: HttpContext) (parameters: #ReviewParameters) (returnValue: GraceReturnValue<'T>) =
        let graceIds = getGraceIds context

        returnValue
            .enhance(getParametersAsDictionary parameters)
            .enhance(nameof OwnerId, graceIds.OwnerId)
            .enhance(nameof OrganizationId, graceIds.OrganizationId)
            .enhance(nameof RepositoryId, graceIds.RepositoryId)
            .enhance("Command", context.Items["Command"] :?> string)
            .enhance ("Path", context.Request.Path.Value)
        |> ignore

        returnValue

    let private enrichCandidateError (context: HttpContext) (parameters: #ReviewParameters) (error: GraceError) =
        let graceIds = getGraceIds context

        error
            .enhance(getParametersAsDictionary parameters)
            .enhance(nameof OwnerId, graceIds.OwnerId)
            .enhance(nameof OrganizationId, graceIds.OrganizationId)
            .enhance(nameof RepositoryId, graceIds.RepositoryId)
            .enhance("Command", context.Items["Command"] :?> string)
            .enhance ("Path", context.Request.Path.Value)
        |> ignore

        error

    let internal resolveCandidateIdentityProjectionWith
        (resolvePromotionSet: Guid -> Task<Grace.Types.PromotionSet.PromotionSetDto option>)
        (parameters: ResolveCandidateIdentityParameters)
        =
        task {
            match! resolveCandidatePromotionSetWith resolvePromotionSet parameters with
            | Error error -> return Error error
            | Ok (_, normalizedCandidateId, promotionSet) ->
                let projectionResult =
                    ReviewModels.createCandidateIdentityProjectionResult
                        normalizedCandidateId
                        promotionSet
                        parameters.OwnerId
                        parameters.OrganizationId
                        parameters.RepositoryId

                return Ok projectionResult
        }

    let internal resolveCandidateIdentityProjection (context: HttpContext) (parameters: ResolveCandidateIdentityParameters) =
        resolveCandidateIdentityProjectionWith (resolvePromotionSetById context) parameters

    let processCommand<'T when 'T :> ReviewParameters> (context: HttpContext) (validations: Validations<'T>) (command: 'T -> ValueTask<ReviewCommand>) =
        task {
            let commandName = context.Items["Command"] :?> string
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let parameterDictionary = Dictionary<string, obj>()

            try
                use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
                let! parameters = context |> parse<'T>
                parameterDictionary.AddRange(getParametersAsDictionary parameters)

                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let handleCommand promotionSetId cmd =
                    task {
                        let actorProxy = Review.CreateActorProxy promotionSetId graceIds.RepositoryId correlationId
                        let metadata = createMetadata context
                        metadata.Properties[ nameof PromotionSetId ] <- $"{promotionSetId}"
                        metadata.Properties[ "ActorId" ] <- $"{promotionSetId}"

                        match! actorProxy.Handle cmd metadata with
                        | Ok graceReturnValue ->
                            graceReturnValue
                                .enhance(parameterDictionary)
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof PromotionSetId, promotionSetId)
                                .enhance("Command", commandName)
                                .enhance ("Path", context.Request.Path.Value)
                            |> ignore

                            return! context |> result200Ok graceReturnValue
                        | Error graceError ->
                            graceError
                                .enhance(parameterDictionary)
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof PromotionSetId, promotionSetId)
                                .enhance("Command", commandName)
                                .enhance ("Path", context.Request.Path.Value)
                            |> ignore

                            return! context |> result400BadRequest graceError
                    }

                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    let! cmd = command parameters
                    let promotionSetId = Guid.Parse(parameters.PromotionSetId)
                    return! handleCommand promotionSetId cmd
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = ReviewError.getErrorMessage error

                    let graceError =
                        (GraceError.Create errorMessage correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance("Command", commandName)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result400BadRequest graceError
            with
            | ex ->
                log.LogError(
                    ex,
                    "{CurrentInstant}: Exception in Review.Server.processCommand. CorrelationId: {correlationId}.",
                    getCurrentInstantExtended (),
                    correlationId
                )

                let graceError =
                    (GraceError.CreateWithException ex String.Empty correlationId)
                        .enhance(parameterDictionary)
                        .enhance(nameof OwnerId, graceIds.OwnerId)
                        .enhance(nameof OrganizationId, graceIds.OrganizationId)
                        .enhance(nameof RepositoryId, graceIds.RepositoryId)
                        .enhance ("Path", context.Request.Path.Value)

                return! context |> result500ServerError graceError
        }

    let processQuery<'T, 'U when 'T :> ReviewParameters>
        (context: HttpContext)
        (parameters: 'T)
        (validations: Validations<'T>)
        (query: QueryResult<IReviewActor, 'U>)
        =
        task {
            use activity = activitySource.StartActivity("processQuery", ActivityKind.Server)
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let parameterDictionary = getParametersAsDictionary parameters

            try
                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    let promotionSetId = Guid.Parse(parameters.PromotionSetId)
                    let actorProxy = Review.CreateActorProxy promotionSetId graceIds.RepositoryId correlationId
                    let! queryResult = query context 0 actorProxy

                    let graceReturnValue =
                        (GraceReturnValue.Create queryResult correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance(nameof PromotionSetId, promotionSetId)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result200Ok graceReturnValue
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = ReviewError.getErrorMessage error

                    let graceError =
                        (GraceError.Create errorMessage correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result400BadRequest graceError
            with
            | ex ->
                let graceError =
                    (GraceError.CreateWithException ex String.Empty correlationId)
                        .enhance(parameterDictionary)
                        .enhance(nameof OwnerId, graceIds.OwnerId)
                        .enhance(nameof OrganizationId, graceIds.OrganizationId)
                        .enhance(nameof RepositoryId, graceIds.RepositoryId)
                        .enhance ("Path", context.Request.Path.Value)

                return! context |> result500ServerError graceError
        }

    let private createCandidateActionResult
        (identity: CandidateIdentityProjection)
        (actionName: string)
        (appliedOperations: string list)
        (diagnostics: string list)
        =
        let result = CandidateActionResult()
        result.Identity <- identity
        result.Action <- actionName
        result.AppliedOperations <- appliedOperations
        result.Diagnostics <- diagnostics

        result.SourceStates <-
            [
                ReviewModels.createProjectionSourceStateMetadata "identity" ProjectionSourceStates.Authoritative "Resolved from candidate identity projection."
                ReviewModels.createProjectionSourceStateMetadata
                    "operation"
                    ProjectionSourceStates.Authoritative
                    "Mapped to PromotionSet and Queue backend operations."
            ]

        result

    let private executeCandidateRetry (context: HttpContext) (identity: CandidateIdentityProjection) (promotionSet: PromotionSetDto) =
        task {
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let promotionSetMetadata = createMetadata context
            promotionSetMetadata.Properties[ nameof PromotionSetId ] <- $"{promotionSet.PromotionSetId}"
            promotionSetMetadata.Properties[ "ActorId" ] <- $"{promotionSet.PromotionSetId}"

            let queueMetadata = createMetadata context
            queueMetadata.Properties[ nameof BranchId ] <- $"{promotionSet.TargetBranchId}"
            queueMetadata.Properties[ nameof PromotionSetId ] <- $"{promotionSet.PromotionSetId}"
            queueMetadata.Properties[ "ActorId" ] <- $"{promotionSet.TargetBranchId}"

            let promotionSetActorProxy = PromotionSet.CreateActorProxy promotionSet.PromotionSetId graceIds.RepositoryId correlationId

            match! promotionSetActorProxy.Handle (PromotionSetCommand.RecomputeStepsIfStale(Option.Some "candidate retry")) promotionSetMetadata with
            | Error error -> return Error error
            | Ok _ ->
                let queueActorProxy = PromotionQueue.CreateActorProxy promotionSet.TargetBranchId graceIds.RepositoryId correlationId
                let mutable appliedOperations = [ "PromotionSet.RecomputeStepsIfStale" ]

                let! queueExists = queueActorProxy.Exists correlationId

                if not queueExists then
                    let policyActorProxy = Policy.CreateActorProxy promotionSet.TargetBranchId graceIds.RepositoryId correlationId
                    let! policySnapshot = policyActorProxy.GetCurrent correlationId

                    match policySnapshot with
                    | Option.Some snapshot ->
                        let snapshotId = string snapshot.PolicySnapshotId

                        if String.IsNullOrWhiteSpace(snapshotId) then
                            return
                                Error(
                                    GraceError.Create "Candidate retry requires queue initialization, but policy snapshot context is unavailable." correlationId
                                )
                        else
                            match!
                                queueActorProxy.Handle (PromotionQueueCommand.Initialize(promotionSet.TargetBranchId, snapshot.PolicySnapshotId)) queueMetadata
                                with
                            | Error error -> return Error error
                            | Ok _ ->
                                appliedOperations <- appliedOperations @ [ "Queue.Initialize" ]

                                match! queueActorProxy.Handle (PromotionQueueCommand.Enqueue promotionSet.PromotionSetId) queueMetadata with
                                | Ok _ ->
                                    appliedOperations <- appliedOperations @ [ "Queue.Enqueue" ]
                                    return Ok(createCandidateActionResult identity "retry" appliedOperations [])
                                | Error error -> return Error error
                    | Option.None ->
                        return
                            Error(
                                GraceError.Create "Candidate retry requires queue initialization, but no policy snapshot is currently available." correlationId
                            )
                else
                    match! queueActorProxy.Handle (PromotionQueueCommand.Enqueue promotionSet.PromotionSetId) queueMetadata with
                    | Ok _ ->
                        appliedOperations <- appliedOperations @ [ "Queue.Enqueue" ]
                        return Ok(createCandidateActionResult identity "retry" appliedOperations [])
                    | Error error -> return Error error
        }

    let private executeCandidateCancel (context: HttpContext) (identity: CandidateIdentityProjection) (promotionSet: PromotionSetDto) =
        task {
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let metadata = createMetadata context
            metadata.Properties[ nameof BranchId ] <- $"{promotionSet.TargetBranchId}"
            metadata.Properties[ nameof PromotionSetId ] <- $"{promotionSet.PromotionSetId}"
            metadata.Properties[ "ActorId" ] <- $"{promotionSet.TargetBranchId}"
            let queueActorProxy = PromotionQueue.CreateActorProxy promotionSet.TargetBranchId graceIds.RepositoryId correlationId
            let! queueExists = queueActorProxy.Exists correlationId

            if not queueExists then
                return Error(GraceError.Create "Candidate cancel is not supported because the target branch queue is not initialized." correlationId)
            else
                match! queueActorProxy.Handle (PromotionQueueCommand.Dequeue promotionSet.PromotionSetId) metadata with
                | Ok _ -> return Ok(createCandidateActionResult identity "cancel" [ "Queue.Dequeue" ] [])
                | Error error when error.Error = QueueError.getErrorMessage QueueError.PromotionSetNotInQueue ->
                    return Error(GraceError.Create "Candidate cancel is not supported because this candidate is not currently queued." correlationId)
                | Error error -> return Error error
        }

    let private executeCandidateGateRerun (context: HttpContext) (identity: CandidateIdentityProjection) (promotionSet: PromotionSetDto) (gateName: string) =
        task {
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let metadata = createMetadata context
            metadata.Properties[ nameof PromotionSetId ] <- $"{promotionSet.PromotionSetId}"
            metadata.Properties[ "ActorId" ] <- $"{promotionSet.PromotionSetId}"
            let promotionSetActorProxy = PromotionSet.CreateActorProxy promotionSet.PromotionSetId graceIds.RepositoryId correlationId

            let gateReason = $"candidate gate rerun ({gateName.Trim()})"

            match! promotionSetActorProxy.Handle (PromotionSetCommand.RecomputeStepsIfStale(Option.Some gateReason)) metadata with
            | Ok _ ->
                return
                    Ok(
                        createCandidateActionResult
                            identity
                            "gate-rerun"
                            [ "PromotionSet.RecomputeStepsIfStale" ]
                            [
                                "Gate-specific validation trigger is unavailable; requested deterministic promotion-set recompute instead."
                            ]
                    )
            | Error error -> return Error error
        }

    /// Resolves candidate identity mapping to the underlying PromotionSet identity.
    let ResolveCandidateIdentity: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context
                let graceIds = getGraceIds context

                try
                    let! parameters =
                        context
                        |> parse<ResolveCandidateIdentityParameters>

                    parameters.OwnerId <- graceIds.OwnerIdString
                    parameters.OrganizationId <- graceIds.OrganizationIdString
                    parameters.RepositoryId <- graceIds.RepositoryIdString
                    context.Items[ "Command" ] <- "ResolveCandidateIdentity"

                    match! resolveCandidateIdentityProjection context parameters with
                    | Ok projection ->
                        let returnValue =
                            GraceReturnValue.Create projection correlationId
                            |> enrichCandidateReturnValue context parameters

                        return! context |> result200Ok returnValue
                    | Error error ->
                        let candidateError = enrichCandidateError context parameters error
                        return! context |> result400BadRequest candidateError
                with
                | ex ->
                    let candidateError =
                        GraceError.CreateWithException ex String.Empty correlationId
                        |> enrichCandidateError context (ResolveCandidateIdentityParameters())

                    return! context |> result500ServerError candidateError
            }

    /// Gets candidate projection details from PromotionSet, Queue, and Review sources.
    let GetCandidate: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context
                let graceIds = getGraceIds context

                try
                    let! parameters = context |> parse<CandidateProjectionParameters>
                    parameters.OwnerId <- graceIds.OwnerIdString
                    parameters.OrganizationId <- graceIds.OrganizationIdString
                    parameters.RepositoryId <- graceIds.RepositoryIdString
                    context.Items[ "Command" ] <- "GetCandidate"

                    match! resolveCandidateProjectionContext context parameters with
                    | Error error ->
                        let candidateError = enrichCandidateError context parameters error
                        return! context |> result400BadRequest candidateError
                    | Ok (identity, promotionSet) ->
                        let! queue = tryGetQueueForPromotionSet context promotionSet
                        let! reviewNotes, _ = getReviewStateForPromotionSet context promotionSet.PromotionSetId
                        let projection = buildCandidateProjectionSnapshot identity promotionSet queue reviewNotes

                        let returnValue =
                            GraceReturnValue.Create projection correlationId
                            |> enrichCandidateReturnValue context parameters

                        return! context |> result200Ok returnValue
                with
                | ex ->
                    let candidateError =
                        GraceError.CreateWithException ex String.Empty correlationId
                        |> enrichCandidateError context (CandidateProjectionParameters())

                    return! context |> result500ServerError candidateError
            }

    /// Gets deterministic required actions for a candidate.
    let GetCandidateRequiredActions: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context
                let graceIds = getGraceIds context

                try
                    let! parameters = context |> parse<CandidateProjectionParameters>
                    parameters.OwnerId <- graceIds.OwnerIdString
                    parameters.OrganizationId <- graceIds.OrganizationIdString
                    parameters.RepositoryId <- graceIds.RepositoryIdString
                    context.Items[ "Command" ] <- "GetCandidateRequiredActions"

                    match! resolveCandidateProjectionContext context parameters with
                    | Error error ->
                        let candidateError = enrichCandidateError context parameters error
                        return! context |> result400BadRequest candidateError
                    | Ok (identity, promotionSet) ->
                        let! queue = tryGetQueueForPromotionSet context promotionSet
                        let! reviewNotes, _ = getReviewStateForPromotionSet context promotionSet.PromotionSetId
                        let snapshot = buildCandidateProjectionSnapshot identity promotionSet queue reviewNotes

                        let requiredActions = CandidateRequiredActionsResult()
                        requiredActions.Identity <- snapshot.Identity
                        requiredActions.RequiredActions <- snapshot.RequiredActions
                        requiredActions.Diagnostics <- snapshot.Diagnostics
                        requiredActions.SourceStates <- snapshot.SourceStates

                        let returnValue =
                            GraceReturnValue.Create requiredActions correlationId
                            |> enrichCandidateReturnValue context parameters

                        return! context |> result200Ok returnValue
                with
                | ex ->
                    let candidateError =
                        GraceError.CreateWithException ex String.Empty correlationId
                        |> enrichCandidateError context (CandidateProjectionParameters())

                    return! context |> result500ServerError candidateError
            }

    /// Gets candidate attestation state from policy and review checkpoint contexts.
    let GetCandidateAttestations: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context
                let graceIds = getGraceIds context

                try
                    let! parameters = context |> parse<CandidateProjectionParameters>
                    parameters.OwnerId <- graceIds.OwnerIdString
                    parameters.OrganizationId <- graceIds.OrganizationIdString
                    parameters.RepositoryId <- graceIds.RepositoryIdString
                    context.Items[ "Command" ] <- "GetCandidateAttestations"

                    match! resolveCandidateProjectionContext context parameters with
                    | Error error ->
                        let candidateError = enrichCandidateError context parameters error
                        return! context |> result400BadRequest candidateError
                    | Ok (identity, promotionSet) ->
                        let! reviewNotes, checkpoints = getReviewStateForPromotionSet context promotionSet.PromotionSetId

                        let policySnapshotFromNotes =
                            reviewNotes
                            |> Option.map (fun notes -> string notes.PolicySnapshotId)
                            |> Option.filter (fun snapshotId -> not <| String.IsNullOrWhiteSpace snapshotId)

                        let! policySnapshotFromActor = tryGetCurrentPolicySnapshotId context promotionSet.TargetBranchId

                        let policySnapshotId =
                            policySnapshotFromNotes
                            |> Option.orElse policySnapshotFromActor

                        let latestCheckpoint =
                            checkpoints
                            |> List.sortByDescending (fun checkpoint -> checkpoint.Timestamp)
                            |> List.tryHead

                        let attestations, diagnostics, sourceStates = buildCandidateAttestationEntries policySnapshotId latestCheckpoint

                        let result = CandidateAttestationsResult()
                        result.Identity <- identity
                        result.Attestations <- attestations
                        result.Diagnostics <- diagnostics
                        result.SourceStates <- sourceStates

                        let returnValue =
                            GraceReturnValue.Create result correlationId
                            |> enrichCandidateReturnValue context parameters

                        return! context |> result200Ok returnValue
                with
                | ex ->
                    let candidateError =
                        GraceError.CreateWithException ex String.Empty correlationId
                        |> enrichCandidateError context (CandidateProjectionParameters())

                    return! context |> result500ServerError candidateError
            }

    /// Gets a unified review report for candidate-first reviewer workflows.
    let GetReviewReport: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context
                let graceIds = getGraceIds context

                try
                    let! parameters = context |> parse<CandidateProjectionParameters>
                    parameters.OwnerId <- graceIds.OwnerIdString
                    parameters.OrganizationId <- graceIds.OrganizationIdString
                    parameters.RepositoryId <- graceIds.RepositoryIdString
                    context.Items[ "Command" ] <- "GetReviewReport"

                    match! resolveCandidateProjectionContext context parameters with
                    | Error error ->
                        let candidateError = enrichCandidateError context parameters error
                        return! context |> result400BadRequest candidateError
                    | Ok (identity, promotionSet) ->
                        let! queue = tryGetQueueForPromotionSet context promotionSet
                        let! reviewNotes, checkpoints = getReviewStateForPromotionSet context promotionSet.PromotionSetId
                        let snapshot = buildCandidateProjectionSnapshot identity promotionSet queue reviewNotes
                        let report = buildReviewReport identity promotionSet snapshot reviewNotes checkpoints

                        let returnValue =
                            GraceReturnValue.Create report correlationId
                            |> enrichCandidateReturnValue context parameters

                        return! context |> result200Ok returnValue
                with
                | ex ->
                    let candidateError =
                        GraceError.CreateWithException ex String.Empty correlationId
                        |> enrichCandidateError context (CandidateProjectionParameters())

                    return! context |> result500ServerError candidateError
            }

    /// Retries a candidate by recomputing the PromotionSet and re-enqueueing queue work.
    let RetryCandidate: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context
                let graceIds = getGraceIds context

                try
                    let! parameters = context |> parse<CandidateProjectionParameters>
                    parameters.OwnerId <- graceIds.OwnerIdString
                    parameters.OrganizationId <- graceIds.OrganizationIdString
                    parameters.RepositoryId <- graceIds.RepositoryIdString
                    context.Items[ "Command" ] <- "RetryCandidate"

                    match! resolveCandidateProjectionContext context parameters with
                    | Error error ->
                        let candidateError = enrichCandidateError context parameters error
                        return! context |> result400BadRequest candidateError
                    | Ok (identity, promotionSet) ->
                        match! executeCandidateRetry context identity promotionSet with
                        | Ok actionResult ->
                            let returnValue =
                                GraceReturnValue.Create actionResult correlationId
                                |> enrichCandidateReturnValue context parameters

                            return! context |> result200Ok returnValue
                        | Error error ->
                            let candidateError = enrichCandidateError context parameters error
                            return! context |> result400BadRequest candidateError
                with
                | ex ->
                    let candidateError =
                        GraceError.CreateWithException ex String.Empty correlationId
                        |> enrichCandidateError context (CandidateProjectionParameters())

                    return! context |> result500ServerError candidateError
            }

    /// Cancels queued candidate processing through queue dequeue semantics.
    let CancelCandidate: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context
                let graceIds = getGraceIds context

                try
                    let! parameters = context |> parse<CandidateProjectionParameters>
                    parameters.OwnerId <- graceIds.OwnerIdString
                    parameters.OrganizationId <- graceIds.OrganizationIdString
                    parameters.RepositoryId <- graceIds.RepositoryIdString
                    context.Items[ "Command" ] <- "CancelCandidate"

                    match! resolveCandidateProjectionContext context parameters with
                    | Error error ->
                        let candidateError = enrichCandidateError context parameters error
                        return! context |> result400BadRequest candidateError
                    | Ok (identity, promotionSet) ->
                        match! executeCandidateCancel context identity promotionSet with
                        | Ok actionResult ->
                            let returnValue =
                                GraceReturnValue.Create actionResult correlationId
                                |> enrichCandidateReturnValue context parameters

                            return! context |> result200Ok returnValue
                        | Error error ->
                            let candidateError = enrichCandidateError context parameters error
                            return! context |> result400BadRequest candidateError
                with
                | ex ->
                    let candidateError =
                        GraceError.CreateWithException ex String.Empty correlationId
                        |> enrichCandidateError context (CandidateProjectionParameters())

                    return! context |> result500ServerError candidateError
            }

    /// Reruns a candidate gate through deterministic backend recomputation semantics.
    let RerunCandidateGate: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context
                let graceIds = getGraceIds context

                try
                    let! parameters = context |> parse<CandidateGateRerunParameters>
                    parameters.OwnerId <- graceIds.OwnerIdString
                    parameters.OrganizationId <- graceIds.OrganizationIdString
                    parameters.RepositoryId <- graceIds.RepositoryIdString
                    context.Items[ "Command" ] <- "RerunCandidateGate"

                    if String.IsNullOrWhiteSpace parameters.Gate then
                        let candidateError =
                            GraceError.Create "Gate is required for candidate gate rerun." correlationId
                            |> enrichCandidateError context parameters

                        return! context |> result400BadRequest candidateError
                    else
                        match! resolveCandidateProjectionContext context parameters with
                        | Error error ->
                            let candidateError = enrichCandidateError context parameters error
                            return! context |> result400BadRequest candidateError
                        | Ok (identity, promotionSet) ->
                            match! executeCandidateGateRerun context identity promotionSet parameters.Gate with
                            | Ok actionResult ->
                                let returnValue =
                                    GraceReturnValue.Create actionResult correlationId
                                    |> enrichCandidateReturnValue context parameters

                                return! context |> result200Ok returnValue
                            | Error error ->
                                let candidateError = enrichCandidateError context parameters error
                                return! context |> result400BadRequest candidateError
                with
                | ex ->
                    let candidateError =
                        GraceError.CreateWithException ex String.Empty correlationId
                        |> enrichCandidateError context (CandidateGateRerunParameters())

                    return! context |> result500ServerError candidateError
            }

    /// Gets review notes.
    let GetNotes: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                let validations (parameters: GetReviewNotesParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.PromotionSetId ReviewError.InvalidPromotionSetId
                    |]

                let query (context: HttpContext) _ (actorProxy: IReviewActor) = actorProxy.GetNotes(getCorrelationId context)

                let! parameters = context |> parse<GetReviewNotesParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString
                context.Items[ "Command" ] <- "GetNotes"
                return! processQuery context parameters validations query
            }

    /// Records a review checkpoint.
    let Checkpoint: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                let validations (parameters: ReviewCheckpointParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.PromotionSetId ReviewError.InvalidPromotionSetId
                        Guid.isValidAndNotEmptyGuid parameters.ReviewedUpToReferenceId ReviewError.InvalidReferenceId
                        String.isNotEmpty parameters.PolicySnapshotId ReviewError.InvalidPolicySnapshotId
                    |]

                let command (parameters: ReviewCheckpointParameters) =
                    let promotionSetId = Guid.Parse(parameters.PromotionSetId)

                    let principal =
                        if
                            isNull context.User
                            || isNull context.User.Identity
                            || String.IsNullOrEmpty(context.User.Identity.Name)
                        then
                            Constants.GraceSystemUser
                        else
                            context.User.Identity.Name

                    let checkpoint =
                        {
                            ReviewCheckpointId = Guid.NewGuid()
                            PromotionSetId = Option.Some promotionSetId
                            ReviewedUpToReferenceId = Guid.Parse(parameters.ReviewedUpToReferenceId)
                            PolicySnapshotId = PolicySnapshotId parameters.PolicySnapshotId
                            Reviewer = UserId principal
                            Timestamp = getCurrentInstant ()
                        }

                    ReviewCommand.AddCheckpoint checkpoint
                    |> returnValueTask

                context.Items[ "Command" ] <- nameof Checkpoint
                return! processCommand context validations command
            }

    /// Resolves a finding.
    let ResolveFinding: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                let validations (parameters: ResolveFindingParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.PromotionSetId ReviewError.InvalidPromotionSetId
                        Guid.isValidAndNotEmptyGuid parameters.FindingId ReviewError.InvalidFindingId
                        DiscriminatedUnion.isMemberOf<FindingResolutionState, ReviewError> parameters.ResolutionState ReviewError.InvalidResolutionState
                    |]

                let command (parameters: ResolveFindingParameters) =
                    let principal =
                        if
                            isNull context.User
                            || isNull context.User.Identity
                            || String.IsNullOrEmpty(context.User.Identity.Name)
                        then
                            Constants.GraceSystemUser
                        else
                            context.User.Identity.Name

                    let resolutionState =
                        discriminatedUnionFromString<FindingResolutionState> parameters.ResolutionState
                        |> Option.get

                    let note =
                        if String.IsNullOrEmpty(parameters.Note) then
                            Option.None
                        else
                            Option.Some parameters.Note

                    ReviewCommand.ResolveFinding(Guid.Parse(parameters.FindingId), resolutionState, UserId principal, note)
                    |> returnValueTask

                context.Items[ "Command" ] <- nameof ResolveFinding
                return! processCommand context validations command
            }

    /// Requests deeper analysis (placeholder).
    let Deepen: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context
                let graceError = GraceError.Create "Deepen is not implemented yet." correlationId
                return! context |> result400BadRequest graceError
            }
