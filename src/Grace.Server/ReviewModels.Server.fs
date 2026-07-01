namespace Grace.Server

open Grace.Shared.Parameters.Review
open Grace.Types.Review
open Grace.Types.Policy
open Microsoft.Extensions.Configuration
open System
open System.Collections.Generic
open System.Threading.Tasks

/// Contains Grace Server review models behavior and supporting helpers.
module ReviewModels =
    /// Represents triage request used by Grace Server APIs and background services.
    type TriageRequest = { PolicySnapshot: PolicySnapshot; RiskProfile: DeterministicRiskProfile; Evidence: EvidenceSet }

    /// Represents deep review request used by Grace Server APIs and background services.
    type DeepReviewRequest = { PolicySnapshot: PolicySnapshot; RiskProfile: DeterministicRiskProfile; Evidence: EvidenceSet; WorkItemContext: string option }

    /// Represents triage result used by Grace Server APIs and background services.
    type TriageResult = { Summary: string; RiskLabel: string; Confidence: float; Categories: string list; DeepAnalysisRecommended: bool; Reasons: string list }

    /// Defines the contract for ireview model provider.
    type IReviewModelProvider =
        /// Defines the triage model id operation for implementers.
        abstract member TriageModelId: string
        /// Defines the deep model id operation for implementers.
        abstract member DeepModelId: string
        /// Defines the run triage operation for implementers.
        abstract member RunTriage: TriageRequest -> Task<TriageResult>
        /// Defines the run deep review operation for implementers.
        abstract member RunDeepReview: DeepReviewRequest -> Task<ReviewNotes>

    /// Represents open router settings used by Grace Server APIs and background services.
    type OpenRouterSettings = { ApiBase: string; ApiKeyEnvVar: string; TriageModel: string; DeepModel: string; RequestHeaders: Dictionary<string, string> }

    /// Represents review models settings used by Grace Server APIs and background services.
    type ReviewModelsSettings = { Provider: string; OpenRouter: OpenRouterSettings }

    /// Normalizes normalize candidate id data for stable server comparisons.
    let normalizeCandidateId (candidateId: string) = if isNull candidateId then String.Empty else candidateId.Trim()

    /// Accepts a candidate id only when it normalizes to a non-empty GUID and returns both parsed and canonical forms.
    let tryParseCandidateId (candidateId: string) =
        let normalizedCandidateId = normalizeCandidateId candidateId
        let isCandidateIdGuid, parsedCandidateId = Guid.TryParse normalizedCandidateId

        if not isCandidateIdGuid
           || parsedCandidateId = Guid.Empty then
            Error normalizedCandidateId
        else
            let canonicalCandidateId = parsedCandidateId.ToString()
            Ok(parsedCandidateId, canonicalCandidateId)

    /// Copies owner, organization, and repository identifiers into the candidate projection scope DTO.
    let createCandidateProjectionScope (ownerId: string) (organizationId: string) (repositoryId: string) =
        let scope = CandidateProjectionScope()
        scope.OwnerId <- ownerId
        scope.OrganizationId <- organizationId
        scope.RepositoryId <- repositoryId
        scope

    /// Describes one source used by a candidate projection, including whether that source was authoritative or unavailable.
    let createProjectionSourceStateMetadata (section: string) (sourceState: string) (detail: string) =
        let metadata = ProjectionSourceStateMetadata()
        metadata.Section <- section
        metadata.SourceState <- sourceState
        metadata.Detail <- detail
        metadata

    /// Builds the direct promotion-set identity projection used before queue and review state are layered in.
    let createCandidateIdentityProjection
        (normalizedCandidateId: string)
        (promotionSetId: Guid)
        (ownerId: string)
        (organizationId: string)
        (repositoryId: string)
        =
        let projection = CandidateIdentityProjection()
        projection.CandidateId <- normalizedCandidateId
        projection.PromotionSetId <- promotionSetId.ToString()
        projection.IdentityMode <- CandidateIdentityModes.DirectPromotionSetProjection
        projection.Scope <- createCandidateProjectionScope ownerId organizationId repositoryId
        projection

    /// Returns the candidate identity projection with source metadata proving it came from promotion-set state.
    let createCandidateIdentityProjectionResult
        (normalizedCandidateId: string)
        (promotionSet: Grace.Types.PromotionSet.PromotionSetDto)
        (ownerId: string)
        (organizationId: string)
        (repositoryId: string)
        =
        let projection = createCandidateIdentityProjection normalizedCandidateId promotionSet.PromotionSetId ownerId organizationId repositoryId

        let sourceState = createProjectionSourceStateMetadata "identity" ProjectionSourceStates.Authoritative "Resolved from PromotionSet.Get."

        let result = CandidateIdentityProjectionResult()
        result.Identity <- projection
        result.SourceStates <- [ sourceState ]
        result

    /// Defines the review-report schema version published by the server.
    [<RequireQualifiedAccess>]
    module ReviewReportSchema =
        [<Literal>]
        let Version = "1.0"

    /// Defines stable review-report section identifiers and ordering.
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

    /// Represents review report entry used by Grace Server APIs and background services.
    type ReviewReportEntry() =
        /// Machine-readable key for one row in a review report section.
        member val public Key = String.Empty with get, set
        /// Values displayed for the report row.
        member val public Values: string list = [] with get, set

    /// Represents review report section used by Grace Server APIs and background services.
    type ReviewReportSection() =
        /// Stable section identifier used by clients to order and render report content.
        member val public Section = String.Empty with get, set
        /// Human-readable heading for the review report section.
        member val public Title = String.Empty with get, set
        /// Aggregate source-state label for the section.
        member val public SourceState = ProjectionSourceStates.NotAvailable with get, set
        /// Per-source provenance details used to explain how the section was projected.
        member val public SourceStates: ProjectionSourceStateMetadata list = [] with get, set
        /// Ordered report rows included in the section.
        member val public Entries: ReviewReportEntry list = [] with get, set
        /// Section-specific warnings or missing-context notes.
        member val public Diagnostics: string list = [] with get, set

    /// Represents review report result used by Grace Server APIs and background services.
    type ReviewReportResult() =
        /// Schema version clients can use to interpret report sections.
        member val public ReviewReportSchemaVersion = ReviewReportSchema.Version with get, set
        /// Canonical section ordering expected by review report clients.
        member val public SectionOrder: string list = ReviewReportSections.Ordered with get, set
        /// Materialized sections that make up the review report.
        member val public Sections: ReviewReportSection list = [] with get, set

    /// Creates a key/value row for a review report section.
    let createReviewReportEntry (key: string) (values: string list) =
        let entry = ReviewReportEntry()
        entry.Key <- key
        entry.Values <- values
        entry

    /// Builds a review report section with ordered entries, source-state annotations, and diagnostics.
    let createReviewReportSection
        (section: string)
        (title: string)
        (sourceState: string)
        (sourceStates: ProjectionSourceStateMetadata list)
        (entries: ReviewReportEntry list)
        (diagnostics: string list)
        =
        let reportSection = ReviewReportSection()
        reportSection.Section <- section
        reportSection.Title <- title
        reportSection.SourceState <- sourceState
        reportSection.SourceStates <- sourceStates
        reportSection.Entries <- entries
        reportSection.Diagnostics <- diagnostics
        reportSection

    /// Represents null review model provider used by Grace Server APIs and background services.
    type NullReviewModelProvider() =
        interface IReviewModelProvider with
            /// Reports that no triage model is active when review models are unconfigured.
            member _.TriageModelId = "none"
            /// Reports that no deep-review model is active when review models are unconfigured.
            member _.DeepModelId = "none"

            /// Returns a deterministic no-provider triage result without calling an external model.
            member _.RunTriage _ =
                Task.FromResult(
                    {
                        Summary = "Triage provider not configured."
                        RiskLabel = "Green"
                        Confidence = 0.0
                        Categories = []
                        DeepAnalysisRecommended = false
                        Reasons = [ "ProviderMissing" ]
                    }
                )

            /// Returns default review notes that explain deep review is not configured.
            member _.RunDeepReview _ = Task.FromResult({ ReviewNotes.Default with Summary = "Deep review provider not configured." })

    /// Represents open router review model provider used by Grace Server APIs and background services.
    type OpenRouterReviewModelProvider(settings: OpenRouterSettings) =
        interface IReviewModelProvider with
            /// Exposes the configured OpenRouter triage model id.
            member _.TriageModelId = settings.TriageModel
            /// Exposes the configured OpenRouter deep-review model id.
            member _.DeepModelId = settings.DeepModel

            /// Returns the current OpenRouter triage stub response while preserving configured model metadata.
            member _.RunTriage _ =
                Task.FromResult(
                    {
                        Summary = $"OpenRouter triage model configured: {settings.TriageModel}."
                        RiskLabel = "Yellow"
                        Confidence = 0.0
                        Categories = []
                        DeepAnalysisRecommended = false
                        Reasons = [ "ProviderStub" ]
                    }
                )

            /// Returns the current OpenRouter deep-review stub response while preserving configured model metadata.
            member _.RunDeepReview _ = Task.FromResult({ ReviewNotes.Default with Summary = $"OpenRouter deep model configured: {settings.DeepModel}." })

    /// Gets try get settings data needed by the server flow.
    let private tryGetSettings (configuration: IConfiguration) =
        if isNull configuration then
            None
        else
            let section = configuration.GetSection("Grace:ReviewModels")

            if isNull section then
                None
            else
                let settings = section.Get<ReviewModelsSettings>()
                if isNull (box settings) then None else Some settings

    /// Selects the configured review-model provider, falling back to the null provider for absent or unknown settings.
    let createProvider (configuration: IConfiguration) =
        match tryGetSettings configuration with
        | Some settings when not <| String.IsNullOrWhiteSpace settings.Provider ->
            match settings.Provider.Trim() with
            | "OpenRouter" -> OpenRouterReviewModelProvider(settings.OpenRouter) :> IReviewModelProvider
            | _ -> NullReviewModelProvider() :> IReviewModelProvider
        | _ -> NullReviewModelProvider() :> IReviewModelProvider
