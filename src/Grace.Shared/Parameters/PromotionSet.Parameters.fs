namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Types.PromotionSet
open System

module PromotionSet =
    /// Base parameters for promotion-set endpoints.
    type PromotionSetParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName = String.Empty with get, set
        member val public PromotionSetId = String.Empty with get, set

    /// Parameters for /promotion-set/get.
    type GetPromotionSetParameters() =
        inherit PromotionSetParameters()

    /// Parameters for /promotion-set/get-events.
    type GetPromotionSetEventsParameters() =
        inherit PromotionSetParameters()

    /// Parameters for /promotion-set/create.
    type CreatePromotionSetParameters() =
        inherit PromotionSetParameters()
        member val public TargetBranchId = String.Empty with get, set

    /// Parameters for /promotion-set/update-input-promotions.
    type UpdatePromotionSetInputPromotionsParameters() =
        inherit PromotionSetParameters()
        member val public PromotionPointers: PromotionPointer list = [] with get, set

    /// Parameters for /promotion-set/recompute.
    type RecomputePromotionSetParameters() =
        inherit PromotionSetParameters()
        member val public Reason = String.Empty with get, set

    /// Parameters for /promotion-set/apply.
    type ApplyPromotionSetParameters() =
        inherit PromotionSetParameters()

    /// Parameters for /promotion-set/{promotionSetId}/resolve-conflicts.
    type ResolvePromotionSetConflictsParameters() =
        inherit PromotionSetParameters()
        member val public StepId = String.Empty with get, set
        member val public Decisions: ConflictResolutionDecision list = [] with get, set
        member val public StepsComputationAttempt = 0 with get, set

    /// Parameters for /promotion-set/delete.
    type DeletePromotionSetParameters() =
        inherit PromotionSetParameters()
        member val public Force = false with get, set
        member val public DeleteReason = String.Empty with get, set
