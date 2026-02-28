namespace Grace.Server

open Grace.Shared.Parameters.Review
open Grace.Types.Review
open Grace.Types.Policy
open Microsoft.Extensions.Configuration
open System
open System.Collections.Generic
open System.Threading.Tasks

module ReviewModels =
    type TriageRequest = { PolicySnapshot: PolicySnapshot; RiskProfile: DeterministicRiskProfile; Evidence: EvidenceSet }

    type DeepReviewRequest = { PolicySnapshot: PolicySnapshot; RiskProfile: DeterministicRiskProfile; Evidence: EvidenceSet; WorkItemContext: string option }

    type TriageResult = { Summary: string; RiskLabel: string; Confidence: float; Categories: string list; DeepAnalysisRecommended: bool; Reasons: string list }

    type IReviewModelProvider =
        abstract member TriageModelId: string
        abstract member DeepModelId: string
        abstract member RunTriage: TriageRequest -> Task<TriageResult>
        abstract member RunDeepReview: DeepReviewRequest -> Task<ReviewNotes>

    type OpenRouterSettings = { ApiBase: string; ApiKeyEnvVar: string; TriageModel: string; DeepModel: string; RequestHeaders: Dictionary<string, string> }

    type ReviewModelsSettings = { Provider: string; OpenRouter: OpenRouterSettings }

    let normalizeCandidateId (candidateId: string) = if isNull candidateId then String.Empty else candidateId.Trim()

    let tryParseCandidateId (candidateId: string) =
        let normalizedCandidateId = normalizeCandidateId candidateId
        let isCandidateIdGuid, parsedCandidateId = Guid.TryParse normalizedCandidateId

        if not isCandidateIdGuid
           || parsedCandidateId = Guid.Empty then
            Error normalizedCandidateId
        else
            let canonicalCandidateId = parsedCandidateId.ToString()
            Ok(parsedCandidateId, canonicalCandidateId)

    let createCandidateProjectionScope (ownerId: string) (organizationId: string) (repositoryId: string) =
        let scope = CandidateProjectionScope()
        scope.OwnerId <- ownerId
        scope.OrganizationId <- organizationId
        scope.RepositoryId <- repositoryId
        scope

    let createProjectionSourceStateMetadata (section: string) (sourceState: string) (detail: string) =
        let metadata = ProjectionSourceStateMetadata()
        metadata.Section <- section
        metadata.SourceState <- sourceState
        metadata.Detail <- detail
        metadata

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

    [<RequireQualifiedAccess>]
    module ReviewReportSchema =
        [<Literal>]
        let Version = "1.0"

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

    type ReviewReportEntry() =
        member val public Key = String.Empty with get, set
        member val public Values: string list = [] with get, set

    type ReviewReportSection() =
        member val public Section = String.Empty with get, set
        member val public Title = String.Empty with get, set
        member val public SourceState = ProjectionSourceStates.NotAvailable with get, set
        member val public SourceStates: ProjectionSourceStateMetadata list = [] with get, set
        member val public Entries: ReviewReportEntry list = [] with get, set
        member val public Diagnostics: string list = [] with get, set

    type ReviewReportResult() =
        member val public ReviewReportSchemaVersion = ReviewReportSchema.Version with get, set
        member val public SectionOrder: string list = ReviewReportSections.Ordered with get, set
        member val public Sections: ReviewReportSection list = [] with get, set

    let createReviewReportEntry (key: string) (values: string list) =
        let entry = ReviewReportEntry()
        entry.Key <- key
        entry.Values <- values
        entry

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

    type NullReviewModelProvider() =
        interface IReviewModelProvider with
            member _.TriageModelId = "none"
            member _.DeepModelId = "none"

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

            member _.RunDeepReview _ = Task.FromResult({ ReviewNotes.Default with Summary = "Deep review provider not configured." })

    type OpenRouterReviewModelProvider(settings: OpenRouterSettings) =
        interface IReviewModelProvider with
            member _.TriageModelId = settings.TriageModel
            member _.DeepModelId = settings.DeepModel

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

            member _.RunDeepReview _ = Task.FromResult({ ReviewNotes.Default with Summary = $"OpenRouter deep model configured: {settings.DeepModel}." })

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

    let createProvider (configuration: IConfiguration) =
        match tryGetSettings configuration with
        | Some settings when not <| String.IsNullOrWhiteSpace settings.Provider ->
            match settings.Provider.Trim() with
            | "OpenRouter" -> OpenRouterReviewModelProvider(settings.OpenRouter) :> IReviewModelProvider
            | _ -> NullReviewModelProvider() :> IReviewModelProvider
        | _ -> NullReviewModelProvider() :> IReviewModelProvider
