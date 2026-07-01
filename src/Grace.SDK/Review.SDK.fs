namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.Review
open Grace.Types.Review
open System
open System.Threading.Tasks

/// Constants that version the SDK review report payload shape.
[<RequireQualifiedAccess>]
module ReviewReportSchema =
    [<Literal>]
    let Version = "1.0"

/// Stable section identifiers and ordering used when rendering review reports.
[<RequireQualifiedAccess>]
module ReviewReportSections =
    [<Literal>]
    let CandidateAndPromotionSet = "candidate-and-promotion-set"

    [<Literal>]
    let QueueAndRequiredActions = "queue-and-required-actions"

    [<Literal>]
    let ValidationAndGateOutcomes = "validation-and-gate-outcomes"

    [<Literal>]
    let ReviewNotesAndCheckpoint = "review-notes-and-checkpoint"

    [<Literal>]
    let WorkItemLinksAndArtifacts = "work-item-links-and-artifacts"

    [<Literal>]
    let BlockingReasonsAndNextActions = "blocking-reasons-and-next-actions"

    let Ordered =
        [
            CandidateAndPromotionSet
            QueueAndRequiredActions
            ValidationAndGateOutcomes
            ReviewNotesAndCheckpoint
            WorkItemLinksAndArtifacts
            BlockingReasonsAndNextActions
        ]

/// One keyed set of display values inside a review report section.
type ReviewReportEntry() =
    /// Machine-readable label for the entry within its section.
    member val public Key = String.Empty with get, set
    /// Display values associated with the entry key.
    member val public Values: string list = [] with get, set

/// A named review report section with projection state, entries, and diagnostics.
type ReviewReportSection() =
    /// Stable section identifier from ReviewReportSections.
    member val public Section = String.Empty with get, set
    /// Human-readable title for the rendered section.
    member val public Title = String.Empty with get, set
    /// Aggregate projection state for the section.
    member val public SourceState = ProjectionSourceStates.NotAvailable with get, set
    /// Source-specific projection states that explain the aggregate state.
    member val public SourceStates: ProjectionSourceStateMetadata list = [] with get, set
    /// Structured rows rendered under this section.
    member val public Entries: ReviewReportEntry list = [] with get, set
    /// Non-fatal diagnostics collected while preparing the section.
    member val public Diagnostics: string list = [] with get, set

/// Candidate-first review report payload with schema version, section order, and section details.
type ReviewReportResult() =
    /// Schema version used by consumers to parse the review report.
    member val public ReviewReportSchemaVersion = ReviewReportSchema.Version with get, set
    /// Preferred section rendering order for clients that display the report.
    member val public SectionOrder: string list = ReviewReportSections.Ordered with get, set
    /// Sections included in the generated review report.
    member val public Sections: ReviewReportSection list = [] with get, set

/// SDK entry point for creating, updating, and projecting Grace review records.
type Review() =
    /// Gets review notes for a promotion set.
    static member public GetNotes(parameters: GetReviewNotesParameters) =
        postServer<GetReviewNotesParameters, ReviewNotes option> (parameters |> ensureCorrelationIdIsSet, "review/notes")

    /// Records a review checkpoint.
    static member public Checkpoint(parameters: ReviewCheckpointParameters) =
        postServer<ReviewCheckpointParameters, string> (parameters |> ensureCorrelationIdIsSet, "review/checkpoint")

    /// Resolves a review finding.
    static member public ResolveFinding(parameters: ResolveFindingParameters) =
        postServer<ResolveFindingParameters, string> (parameters |> ensureCorrelationIdIsSet, "review/resolve")

    /// Requests deeper analysis for a promotion set.
    static member public Deepen(parameters: DeepenReviewParameters) =
        postServer<DeepenReviewParameters, string> (parameters |> ensureCorrelationIdIsSet, "review/deepen")

    /// Resolves candidate identity to a promotion-set-backed projection identity.
    static member public ResolveCandidateIdentity(parameters: ResolveCandidateIdentityParameters) =
        postServer<ResolveCandidateIdentityParameters, CandidateIdentityProjectionResult> (parameters |> ensureCorrelationIdIsSet, "review/candidate/resolve")

    /// Gets candidate projection details.
    static member public GetCandidate(parameters: CandidateProjectionParameters) =
        postServer<CandidateProjectionParameters, CandidateProjectionSnapshotResult> (parameters |> ensureCorrelationIdIsSet, "review/candidate/get")

    /// Gets candidate required actions.
    static member public GetCandidateRequiredActions(parameters: CandidateProjectionParameters) =
        postServer<CandidateProjectionParameters, CandidateRequiredActionsResult> (parameters |> ensureCorrelationIdIsSet, "review/candidate/required-actions")

    /// Gets candidate attestations.
    static member public GetCandidateAttestations(parameters: CandidateProjectionParameters) =
        postServer<CandidateProjectionParameters, CandidateAttestationsResult> (parameters |> ensureCorrelationIdIsSet, "review/candidate/attestations")

    /// Retries candidate processing through PromotionSet recompute and queue operations.
    static member public RetryCandidate(parameters: CandidateProjectionParameters) =
        postServer<CandidateProjectionParameters, CandidateActionResult> (parameters |> ensureCorrelationIdIsSet, "review/candidate/retry")

    /// Cancels candidate queue processing when queued.
    static member public CancelCandidate(parameters: CandidateProjectionParameters) =
        postServer<CandidateProjectionParameters, CandidateActionResult> (parameters |> ensureCorrelationIdIsSet, "review/candidate/cancel")

    /// Requests candidate gate rerun semantics.
    static member public RerunCandidateGate(parameters: CandidateGateRerunParameters) =
        postServer<CandidateGateRerunParameters, CandidateActionResult> (parameters |> ensureCorrelationIdIsSet, "review/candidate/gate-rerun")

    /// Gets a unified review report for candidate-first reviewer workflows.
    static member public GetReviewReport(parameters: CandidateProjectionParameters) =
        postServer<CandidateProjectionParameters, ReviewReportResult> (parameters |> ensureCorrelationIdIsSet, "review/report/get")
