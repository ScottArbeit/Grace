namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.PromotionSet
open Grace.Types.PromotionSet

/// The PromotionSet module provides a set of functions for interacting with promotion sets in the Grace API.
type PromotionSet() =
    /// Gets a promotion set by ID.
    static member public Get(parameters: GetPromotionSetParameters) =
        postServer<GetPromotionSetParameters, PromotionSetDto> (parameters |> ensureCorrelationIdIsSet, "promotion-set/get")
