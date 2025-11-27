namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.PromotionGroup
open Grace.Shared.Utilities
open Grace.Types.PromotionGroup
open System
open System.Collections.Generic
open System.Threading.Tasks

/// The PromotionGroup module provides a set of functions for interacting with promotion groups in the Grace API.
type PromotionGroup() =

    /// Creates a new promotion group.
    static member public Create(parameters: CreatePromotionGroupParameters) =
        postServer<CreatePromotionGroupParameters, string> (parameters |> ensureCorrelationIdIsSet, $"promotionGroup/{nameof (PromotionGroup.Create)}")

    /// Gets a promotion group by ID.
    static member public Get(parameters: GetPromotionGroupParameters) =
        postServer<GetPromotionGroupParameters, PromotionGroupDto> (parameters |> ensureCorrelationIdIsSet, $"promotionGroup/get")

    /// Gets the events for a promotion group.
    static member public GetEvents(parameters: GetPromotionGroupParameters) =
        postServer<GetPromotionGroupParameters, IReadOnlyList<PromotionGroupEvent>> (parameters |> ensureCorrelationIdIsSet, $"promotionGroup/getEvents")

    /// Adds a promotion to the group.
    static member public AddPromotion(parameters: AddPromotionParameters) =
        postServer<AddPromotionParameters, string> (parameters |> ensureCorrelationIdIsSet, $"promotionGroup/addPromotion")

    /// Removes a promotion from the group.
    static member public RemovePromotion(parameters: RemovePromotionParameters) =
        postServer<RemovePromotionParameters, string> (parameters |> ensureCorrelationIdIsSet, $"promotionGroup/removePromotion")

    /// Reorders the promotions in a group.
    static member public ReorderPromotions(parameters: ReorderPromotionsParameters) =
        postServer<ReorderPromotionsParameters, string> (parameters |> ensureCorrelationIdIsSet, $"promotionGroup/reorderPromotions")

    /// Schedules the promotion group.
    static member public Schedule(parameters: ScheduleParameters) =
        postServer<ScheduleParameters, string> (parameters |> ensureCorrelationIdIsSet, $"promotionGroup/schedule")

    /// Marks the group as ready.
    static member public MarkReady(parameters: MarkReadyParameters) =
        postServer<MarkReadyParameters, string> (parameters |> ensureCorrelationIdIsSet, $"promotionGroup/markReady")

    /// Starts the promotion group execution.
    static member public Start(parameters: StartParameters) =
        postServer<StartParameters, string> (parameters |> ensureCorrelationIdIsSet, $"promotionGroup/start")

    /// Completes the promotion group execution.
    static member public Complete(parameters: CompleteParameters) =
        postServer<CompleteParameters, string> (parameters |> ensureCorrelationIdIsSet, $"promotionGroup/complete")

    /// Blocks the promotion group.
    static member public Block(parameters: BlockParameters) =
        postServer<BlockParameters, string> (parameters |> ensureCorrelationIdIsSet, $"promotionGroup/block")

    /// Deletes the promotion group.
    static member public Delete(parameters: DeletePromotionGroupParameters) =
        postServer<DeletePromotionGroupParameters, string> (parameters |> ensureCorrelationIdIsSet, $"promotionGroup/delete")
