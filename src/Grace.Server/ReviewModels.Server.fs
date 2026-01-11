namespace Grace.Server

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
        abstract member RunDeepReview: DeepReviewRequest -> Task<ReviewPacket>

    type OpenRouterSettings = { ApiBase: string; ApiKeyEnvVar: string; TriageModel: string; DeepModel: string; RequestHeaders: Dictionary<string, string> }

    type ReviewModelsSettings = { Provider: string; OpenRouter: OpenRouterSettings }

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

            member _.RunDeepReview _ = Task.FromResult({ ReviewPacket.Default with Summary = "Deep review provider not configured." })

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

            member _.RunDeepReview _ = Task.FromResult({ ReviewPacket.Default with Summary = $"OpenRouter deep model configured: {settings.DeepModel}." })

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
