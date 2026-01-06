namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open System

module Queue =
    /// Base parameters for queue endpoints.
    type QueueParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName = String.Empty with get, set
        member val public TargetBranchId = String.Empty with get, set
        member val public CandidateId = String.Empty with get, set
        member val public PolicySnapshotId = String.Empty with get, set
        member val public PromotionGroupId = String.Empty with get, set
        member val public WorkItemId = String.Empty with get, set

    /// Parameters for /queue/status.
    type QueueStatusParameters() =
        inherit QueueParameters()

    /// Parameters for /queue/pause and /queue/resume.
    type QueueActionParameters() =
        inherit QueueParameters()

    /// Parameters for /queue/enqueue.
    type EnqueueParameters() =
        inherit QueueParameters()

    /// Parameters for /candidate/get.
    type CandidateParameters() =
        inherit QueueParameters()

    /// Parameters for /candidate/action.
    type CandidateActionParameters() =
        inherit QueueParameters()

    /// Parameters for /candidate/attestations.
    type CandidateAttestationsParameters() =
        inherit QueueParameters()

    /// Parameters for /candidate/gate/rerun.
    type CandidateGateRerunParameters() =
        inherit QueueParameters()
        member val public GateName = String.Empty with get, set
