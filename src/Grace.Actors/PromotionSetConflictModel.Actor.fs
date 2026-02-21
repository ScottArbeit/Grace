namespace Grace.Actors

open System.Threading.Tasks

module PromotionSetConflictModel =

    [<CLIMutable>]
    type ConflictResolutionModelRequest = { FilePath: string; BaseContent: string option; OursContent: string option; TheirsContent: string option }

    [<CLIMutable>]
    type ConflictResolutionModelResponse = { ProposedContent: string option; ShouldDelete: bool; Confidence: float; Explanation: string option }

    type IConflictResolutionModelProvider =
        abstract member ProviderName: string
        abstract member SuggestResolution: ConflictResolutionModelRequest -> Task<Result<ConflictResolutionModelResponse, string>>

    type NullConflictResolutionModelProvider() =
        interface IConflictResolutionModelProvider with
            member _.ProviderName = "none"
            member _.SuggestResolution _ = Task.FromResult(Error "Conflict resolution model provider is not configured.")
