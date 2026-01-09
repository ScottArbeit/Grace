namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open System

module Policy =
    /// Base parameters for policy endpoints.
    type PolicyParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName = String.Empty with get, set
        member val public TargetBranchId = String.Empty with get, set

    /// Parameters for /policy/current.
    type GetPolicyParameters() =
        inherit PolicyParameters()

    /// Parameters for /policy/acknowledge.
    type AcknowledgePolicyParameters() =
        inherit PolicyParameters()
        member val public PolicySnapshotId = String.Empty with get, set
        member val public Note = String.Empty with get, set
