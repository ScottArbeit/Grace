namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Types.Types
open Grace.Types.PromotionGroup
open System
open System.Collections.Generic

module PromotionGroup =

    /// Parameters for many endpoints in the /promotionGroup path.
    type PromotionGroupParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName = String.Empty with get, set
        member val public PromotionGroupId = String.Empty with get, set

    /// Parameters for the /promotionGroup/create endpoint.
    type CreatePromotionGroupParameters() =
        inherit PromotionGroupParameters()
        member val public TargetBranchId = String.Empty with get, set
        member val public TargetBranchName = String.Empty with get, set
        member val public Description = String.Empty with get, set
        member val public ScheduledAt = String.Empty with get, set

    /// Parameters for the /promotionGroup/get endpoint.
    type GetPromotionGroupParameters() =
        inherit PromotionGroupParameters()

    /// Parameters for the /promotionGroup/addPromotion endpoint.
    type AddPromotionParameters() =
        inherit PromotionGroupParameters()
        member val public PromotionId = String.Empty with get, set

    /// Parameters for the /promotionGroup/removePromotion endpoint.
    type RemovePromotionParameters() =
        inherit PromotionGroupParameters()
        member val public PromotionId = String.Empty with get, set

    /// Parameters for the /promotionGroup/reorderPromotions endpoint.
    type ReorderPromotionsParameters() =
        inherit PromotionGroupParameters()
        member val public PromotionIds: IEnumerable<string> = Array.Empty<string>() with get, set

    /// Parameters for the /promotionGroup/schedule endpoint.
    type ScheduleParameters() =
        inherit PromotionGroupParameters()
        member val public ScheduledAt = String.Empty with get, set

    /// Parameters for the /promotionGroup/markReady endpoint.
    type MarkReadyParameters() =
        inherit PromotionGroupParameters()

    /// Parameters for the /promotionGroup/start endpoint.
    type StartParameters() =
        inherit PromotionGroupParameters()

    /// Parameters for the /promotionGroup/complete endpoint.
    type CompleteParameters() =
        inherit PromotionGroupParameters()
        member val public Success = false with get, set

    /// Parameters for the /promotionGroup/block endpoint.
    type BlockParameters() =
        inherit PromotionGroupParameters()
        member val public Reason = String.Empty with get, set

    /// Parameters for the /promotionGroup/delete endpoint.
    type DeletePromotionGroupParameters() =
        inherit PromotionGroupParameters()
        member val public Force = false with get, set
        member val public DeleteReason = String.Empty with get, set

    /// Parameters for the /promotionGroup/list endpoint.
    type ListPromotionGroupsParameters() =
        inherit PromotionGroupParameters()
        member val public TargetBranchId = String.Empty with get, set
        member val public TargetBranchName = String.Empty with get, set
        member val public Status = String.Empty with get, set
        member val public MaxCount: int = 30 with get, set
