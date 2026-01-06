namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open System

module Review =
    /// Base parameters for review endpoints.
    type ReviewParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName = String.Empty with get, set
        member val public CandidateId = String.Empty with get, set
        member val public PromotionGroupId = String.Empty with get, set

    /// Parameters for /review/packet.
    type GetReviewPacketParameters() =
        inherit ReviewParameters()

    /// Parameters for /review/checkpoint.
    type ReviewCheckpointParameters() =
        inherit ReviewParameters()
        member val public ReviewedUpToReferenceId = String.Empty with get, set
        member val public PolicySnapshotId = String.Empty with get, set

    /// Parameters for /review/resolve.
    type ResolveFindingParameters() =
        inherit ReviewParameters()
        member val public FindingId = String.Empty with get, set
        member val public ResolutionState = String.Empty with get, set
        member val public Note = String.Empty with get, set

    /// Parameters for /review/deepen.
    type DeepenReviewParameters() =
        inherit ReviewParameters()
        member val public ChapterId = String.Empty with get, set
