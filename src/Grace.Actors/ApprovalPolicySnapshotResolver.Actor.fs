namespace Grace.Actors

open Grace.Types.PromotionSet
open Grace.Types.Common
open System.Threading.Tasks

type IApprovalPolicySnapshotResolver =
    abstract member GetCurrentApprovalPoliciesForPromotionApply:
        ownerId: OwnerId * organizationId: OrganizationId * repositoryId: RepositoryId * targetBranchId: BranchId * correlationId: CorrelationId ->
            Task<PromotionSetApprovalPolicySnapshot list>
