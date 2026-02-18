namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Types
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

module PromotionSet =

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type PromotionSetStatus =
        | Ready
        | Running
        | Succeeded
        | Failed
        | Blocked

        static member GetKnownTypes() = GetKnownTypes<PromotionSetStatus>()

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type StepsComputationStatus =
        | NotComputed
        | Computing
        | Computed
        | ComputeFailed

        static member GetKnownTypes() = GetKnownTypes<StepsComputationStatus>()

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type StepConflictStatus =
        | NoConflicts
        | AutoResolved
        | BlockedPendingReview
        | Failed

        static member GetKnownTypes() = GetKnownTypes<StepConflictStatus>()

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ConflictResolutionMethod =
        | None
        | ModelSuggested
        | ManualOverride

        static member GetKnownTypes() = GetKnownTypes<ConflictResolutionMethod>()

    [<GenerateSerializer>]
    type ConflictResolutionOutcome = { ModelResolution: string; Confidence: float; Accepted: bool option }

    [<GenerateSerializer>]
    type ConflictHunk = { StartLine: int; EndLine: int; OursContent: string; TheirsContent: string }

    [<GenerateSerializer>]
    type ConflictAnalysis =
        {
            FilePath: string
            OriginalHunks: ConflictHunk list
            ProposedResolution: ConflictResolutionOutcome option
            ResolutionMethod: ConflictResolutionMethod
        }

    [<GenerateSerializer>]
    type ConflictResolutionDecision = { FilePath: string; Accepted: bool; OverrideContentArtifactId: ArtifactId option }

    [<GenerateSerializer>]
    type PromotionPointer = { BranchId: BranchId; ReferenceId: ReferenceId; DirectoryVersionId: DirectoryVersionId }

    [<GenerateSerializer>]
    type PromotionSetStep =
        {
            StepId: PromotionSetStepId
            Order: int
            OriginalPromotion: PromotionPointer
            OriginalBasePromotionReferenceId: ReferenceId
            OriginalBaseDirectoryVersionId: DirectoryVersionId
            ComputedAgainstBaseDirectoryVersionId: DirectoryVersionId
            AppliedDirectoryVersionId: DirectoryVersionId
            ConflictSummaryArtifactId: ArtifactId option
            ConflictStatus: StepConflictStatus
        }

    [<GenerateSerializer>]
    type PromotionSetDto =
        {
            Class: string
            PromotionSetId: PromotionSetId
            OwnerId: OwnerId
            OrganizationId: OrganizationId
            RepositoryId: RepositoryId
            TargetBranchId: BranchId
            OnBehalfOf: UserId list
            Steps: PromotionSetStep list
            ComputedAgainstParentTerminalPromotionReferenceId: ReferenceId option
            StepsComputationStatus: StepsComputationStatus
            StepsComputationAttempt: int
            StepsComputationError: string option
            StepsComputationUpdatedAt: Instant option
            Status: PromotionSetStatus
            CreatedBy: UserId
            CreatedAt: Instant
            UpdatedAt: Instant option
            DeletedAt: Instant option
            DeleteReason: DeleteReason
        }

        static member Default =
            {
                Class = nameof PromotionSetDto
                PromotionSetId = PromotionSetId.Empty
                OwnerId = OwnerId.Empty
                OrganizationId = OrganizationId.Empty
                RepositoryId = RepositoryId.Empty
                TargetBranchId = BranchId.Empty
                OnBehalfOf = []
                Steps = []
                ComputedAgainstParentTerminalPromotionReferenceId = Option.None
                StepsComputationStatus = StepsComputationStatus.NotComputed
                StepsComputationAttempt = 0
                StepsComputationError = Option.None
                StepsComputationUpdatedAt = Option.None
                Status = PromotionSetStatus.Ready
                CreatedBy = UserId String.Empty
                CreatedAt = Constants.DefaultTimestamp
                UpdatedAt = Option.None
                DeletedAt = Option.None
                DeleteReason = String.Empty
            }

    [<KnownType("GetKnownTypes")>]
    type PromotionSetCommand =
        | CreatePromotionSet of
            promotionSetId: PromotionSetId *
            ownerId: OwnerId *
            organizationId: OrganizationId *
            repositoryId: RepositoryId *
            targetBranchId: BranchId
        | UpdateInputPromotions of promotionPointers: PromotionPointer list
        | RecomputeStepsIfStale of reason: string option
        | ResolveConflicts of stepId: PromotionSetStepId * resolutions: ConflictResolutionDecision list
        | Apply
        | DeleteLogical of force: bool * deleteReason: DeleteReason

        static member GetKnownTypes() = GetKnownTypes<PromotionSetCommand>()

    [<KnownType("GetKnownTypes")>]
    type PromotionSetEventType =
        | Created of promotionSetId: PromotionSetId * ownerId: OwnerId * organizationId: OrganizationId * repositoryId: RepositoryId * targetBranchId: BranchId
        | InputPromotionsUpdated of promotionPointers: PromotionPointer list
        | RecomputeStarted of computedAgainstTerminal: ReferenceId
        | StepsUpdated of steps: PromotionSetStep list * computedAgainstTerminal: ReferenceId
        | RecomputeFailed of reason: string * computedAgainstTerminal: ReferenceId
        | Blocked of reason: string * artifactId: ArtifactId option
        | ApplyStarted
        | Applied of terminalPromotionReferenceId: ReferenceId
        | ApplyFailed of reason: string
        | LogicalDeleted of force: bool * deleteReason: DeleteReason

        static member GetKnownTypes() = GetKnownTypes<PromotionSetEventType>()

    type PromotionSetEvent = { Event: PromotionSetEventType; Metadata: EventMetadata }

    module PromotionSetDto =
        let UpdateDto (promotionSetEvent: PromotionSetEvent) (currentDto: PromotionSetDto) =
            let updatedDto =
                match promotionSetEvent.Event with
                | Created (promotionSetId, ownerId, organizationId, repositoryId, targetBranchId) ->
                    { PromotionSetDto.Default with
                        PromotionSetId = promotionSetId
                        OwnerId = ownerId
                        OrganizationId = organizationId
                        RepositoryId = repositoryId
                        TargetBranchId = targetBranchId
                        CreatedBy = UserId promotionSetEvent.Metadata.Principal
                        CreatedAt = promotionSetEvent.Metadata.Timestamp
                    }
                | InputPromotionsUpdated promotionPointers ->
                    let steps =
                        promotionPointers
                        |> List.mapi (fun index pointer ->
                            {
                                StepId = Guid.NewGuid()
                                Order = index
                                OriginalPromotion = pointer
                                OriginalBasePromotionReferenceId = ReferenceId.Empty
                                OriginalBaseDirectoryVersionId = DirectoryVersionId.Empty
                                ComputedAgainstBaseDirectoryVersionId = DirectoryVersionId.Empty
                                AppliedDirectoryVersionId = DirectoryVersionId.Empty
                                ConflictSummaryArtifactId = Option.None
                                ConflictStatus = StepConflictStatus.NoConflicts
                            })

                    { currentDto with Steps = steps }
                | RecomputeStarted computedAgainstTerminal ->
                    { currentDto with
                        StepsComputationStatus = StepsComputationStatus.Computing
                        ComputedAgainstParentTerminalPromotionReferenceId = Some computedAgainstTerminal
                        StepsComputationError = Option.None
                    }
                | StepsUpdated (steps, computedAgainstTerminal) ->
                    { currentDto with
                        Steps = steps
                        StepsComputationStatus = StepsComputationStatus.Computed
                        ComputedAgainstParentTerminalPromotionReferenceId = Some computedAgainstTerminal
                        StepsComputationAttempt = currentDto.StepsComputationAttempt + 1
                        StepsComputationError = Option.None
                    }
                | RecomputeFailed (reason, computedAgainstTerminal) ->
                    { currentDto with
                        StepsComputationStatus = StepsComputationStatus.ComputeFailed
                        ComputedAgainstParentTerminalPromotionReferenceId = Some computedAgainstTerminal
                        StepsComputationError = Some reason
                    }
                | Blocked (reason, _) ->
                    { currentDto with
                        Status = PromotionSetStatus.Blocked
                        StepsComputationStatus = StepsComputationStatus.ComputeFailed
                        StepsComputationError = Some reason
                    }
                | ApplyStarted -> { currentDto with Status = PromotionSetStatus.Running }
                | Applied _ -> { currentDto with Status = PromotionSetStatus.Succeeded }
                | ApplyFailed reason -> { currentDto with Status = PromotionSetStatus.Failed; StepsComputationError = Some reason }
                | LogicalDeleted (_, deleteReason) -> { currentDto with DeletedAt = Some(getCurrentInstant ()); DeleteReason = deleteReason }

            let onBehalfOf =
                updatedDto.OnBehalfOf
                |> List.append [ UserId promotionSetEvent.Metadata.Principal ]
                |> List.distinct

            { updatedDto with
                OnBehalfOf = onBehalfOf
                UpdatedAt = Some promotionSetEvent.Metadata.Timestamp
                StepsComputationUpdatedAt = Some promotionSetEvent.Metadata.Timestamp
            }
