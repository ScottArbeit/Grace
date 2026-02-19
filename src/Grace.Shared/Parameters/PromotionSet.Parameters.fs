namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
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
