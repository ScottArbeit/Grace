namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.Stack
open Grace.Shared.Utilities
open Grace.Types.Stack
open Grace.Types.Types

type Stack() =

    static member public Create(parameters: CreateStackParameters) =
        postServer<CreateStackParameters, StackDto> (parameters |> ensureCorrelationIdIsSet, $"stack/{nameof (Stack.Create)}")

    static member public AddLayer(parameters: AddStackLayerParameters) =
        postServer<AddStackLayerParameters, StackDto> (parameters |> ensureCorrelationIdIsSet, $"stack/add-layer")

    static member public Get(parameters: GetStackParameters) =
        postServer<GetStackParameters, StackDto> (parameters |> ensureCorrelationIdIsSet, $"stack/{nameof (Stack.Get)}")

    static member public Restack(parameters: RestackParameters) =
        postServer<RestackParameters, OperationId> (parameters |> ensureCorrelationIdIsSet, $"stack/restack")

    static member public ReparentAfterPromotion(parameters: ReparentAfterPromotionParameters) =
        postServer<ReparentAfterPromotionParameters, OperationId> (parameters |> ensureCorrelationIdIsSet, $"stack/reparent-after-promotion")
