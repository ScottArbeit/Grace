namespace Grace.Actors

open Grace.Types.PromotionSet
open Grace.Types.Common
open System.Threading.Tasks

/// Defines the contract for approval policy snapshot resolver.
type IApprovalPolicySnapshotResolver =
    /// Resolves the approval policies that must be satisfied before a promotion can be applied.
    abstract member GetCurrentApprovalPoliciesForPromotionApply:
        ownerId: OwnerId * organizationId: OrganizationId * repositoryId: RepositoryId * targetBranchId: BranchId * correlationId: CorrelationId ->
            Task<PromotionSetApprovalPolicySnapshot list>
