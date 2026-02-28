namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open System

module Review =
    [<RequireQualifiedAccess>]
    module CandidateIdentityModes =
        [<Literal>]
        let DirectPromotionSetProjection = "DirectPromotionSetProjection"

    [<RequireQualifiedAccess>]
    module ProjectionSourceStates =
        [<Literal>]
        let Authoritative = "Authoritative"

        [<Literal>]
        let Inferred = "Inferred"

        [<Literal>]
        let NotAvailable = "NotAvailable"

    /// Base parameters for review endpoints.
    type ReviewParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName = String.Empty with get, set
        member val public PromotionSetId = String.Empty with get, set

    /// Parameters for /review/notes.
    type GetReviewNotesParameters() =
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

    /// Base parameters for candidate projection endpoints.
    type CandidateProjectionParameters() =
        inherit ReviewParameters()
        member val public CandidateId = String.Empty with get, set

    /// Parameters for candidate identity projection.
    type ResolveCandidateIdentityParameters() =
        inherit CandidateProjectionParameters()

    /// Parameters for rerunning a candidate gate.
    type CandidateGateRerunParameters() =
        inherit CandidateProjectionParameters()
        member val public Gate = String.Empty with get, set

    /// Repository scope attached to candidate projection identity.
    type CandidateProjectionScope() =
        member val public OwnerId = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set

    /// Candidate-to-promotion-set identity projection contract.
    type CandidateIdentityProjection() =
        member val public CandidateId = String.Empty with get, set
        member val public PromotionSetId = String.Empty with get, set

        member val public IdentityMode = CandidateIdentityModes.DirectPromotionSetProjection with get, set

        member val public Scope = CandidateProjectionScope() with get, set

    /// Source state metadata for projected sections.
    type ProjectionSourceStateMetadata() =
        member val public Section = String.Empty with get, set
        member val public SourceState = ProjectionSourceStates.NotAvailable with get, set
        member val public Detail = String.Empty with get, set

    /// Candidate identity projection result with section source-state metadata.
    type CandidateIdentityProjectionResult() =
        member val public Identity = CandidateIdentityProjection() with get, set
        member val public SourceStates: ProjectionSourceStateMetadata list = [] with get, set

    /// Candidate snapshot projection result for `candidate get`.
    type CandidateProjectionSnapshotResult() =
        member val public Identity = CandidateIdentityProjection() with get, set
        member val public PromotionSetStatus = String.Empty with get, set
        member val public StepsComputationStatus = String.Empty with get, set
        member val public QueueState = String.Empty with get, set
        member val public RunningPromotionSetId = String.Empty with get, set
        member val public UnresolvedFindingCount = 0 with get, set
        member val public ValidationSummaryAvailable = false with get, set
        member val public RequiredActions: string list = [] with get, set
        member val public Diagnostics: string list = [] with get, set
        member val public SourceStates: ProjectionSourceStateMetadata list = [] with get, set

    /// Candidate required-actions projection result.
    type CandidateRequiredActionsResult() =
        member val public Identity = CandidateIdentityProjection() with get, set
        member val public RequiredActions: string list = [] with get, set
        member val public Diagnostics: string list = [] with get, set
        member val public SourceStates: ProjectionSourceStateMetadata list = [] with get, set

    /// Candidate attestation entry.
    type CandidateAttestation() =
        member val public Name = String.Empty with get, set
        member val public Status = ProjectionSourceStates.NotAvailable with get, set
        member val public Detail = String.Empty with get, set

    /// Candidate attestation projection result.
    type CandidateAttestationsResult() =
        member val public Identity = CandidateIdentityProjection() with get, set
        member val public Attestations: CandidateAttestation list = [] with get, set
        member val public Diagnostics: string list = [] with get, set
        member val public SourceStates: ProjectionSourceStateMetadata list = [] with get, set

    /// Candidate action result for retry, cancel, and gate rerun operations.
    type CandidateActionResult() =
        member val public Identity = CandidateIdentityProjection() with get, set
        member val public Action = String.Empty with get, set
        member val public AppliedOperations: string list = [] with get, set
        member val public Diagnostics: string list = [] with get, set
        member val public SourceStates: ProjectionSourceStateMetadata list = [] with get, set
