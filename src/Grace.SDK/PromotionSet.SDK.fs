namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.PromotionSet
open Grace.Types.PromotionSet
open System.Collections.Generic

/// The PromotionSet module provides a set of functions for interacting with promotion sets in the Grace API.
type PromotionSet() =
    /// Creates a promotion set.
    static member public Create(parameters: CreatePromotionSetParameters) =
        postServer<CreatePromotionSetParameters, string> (parameters |> ensureCorrelationIdIsSet, "promotion-set/create")

    /// Gets a promotion set by ID.
    static member public Get(parameters: GetPromotionSetParameters) =
        postServer<GetPromotionSetParameters, PromotionSetDto> (parameters |> ensureCorrelationIdIsSet, "promotion-set/get")

    /// Gets promotion set events by ID.
    static member public GetEvents(parameters: GetPromotionSetEventsParameters) =
        postServer<GetPromotionSetEventsParameters, IReadOnlyList<PromotionSetEvent>> (parameters |> ensureCorrelationIdIsSet, "promotion-set/get-events")

    /// Updates the input promotion pointers for a promotion set.
    static member public UpdateInputPromotions(parameters: UpdatePromotionSetInputPromotionsParameters) =
        postServer<UpdatePromotionSetInputPromotionsParameters, string> (parameters |> ensureCorrelationIdIsSet, "promotion-set/update-input-promotions")

    /// Requests server-side step recomputation for a promotion set.
    static member public Recompute(parameters: RecomputePromotionSetParameters) =
        postServer<RecomputePromotionSetParameters, string> (parameters |> ensureCorrelationIdIsSet, "promotion-set/recompute")

    /// Applies a promotion set.
    static member public Apply(parameters: ApplyPromotionSetParameters) =
        postServer<ApplyPromotionSetParameters, string> (parameters |> ensureCorrelationIdIsSet, "promotion-set/apply")

    /// Resolves blocked promotion set conflicts.
    static member public ResolveConflicts(parameters: ResolvePromotionSetConflictsParameters) =
        postServer<ResolvePromotionSetConflictsParameters, string> (parameters |> ensureCorrelationIdIsSet, "promotion-set/resolve-conflicts")

    /// Logically deletes a promotion set.
    static member public Delete(parameters: DeletePromotionSetParameters) =
        postServer<DeletePromotionSetParameters, string> (parameters |> ensureCorrelationIdIsSet, "promotion-set/delete")
