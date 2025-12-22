namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Types.Types
open System

module Stack =

    type StackParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName: OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName: OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName: RepositoryName = String.Empty with get, set
        member val public StackId = String.Empty with get, set

    type CreateStackParameters() =
        inherit StackParameters()
        member val public BaseBranchId: BranchId = BranchId.Empty with get, set

    type AddStackLayerParameters() =
        inherit StackParameters()
        member val public ChangeId: ChangeId = ChangeId.Empty with get, set
        member val public BranchId: BranchId = BranchId.Empty with get, set
        member val public Order = 0 with get, set

    type RestackParameters() =
        inherit StackParameters()

    type ReparentAfterPromotionParameters() =
        inherit StackParameters()

    type GetStackParameters() =
        inherit StackParameters()
