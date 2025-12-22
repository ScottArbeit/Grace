namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.Operation
open Grace.Shared.Utilities
open Grace.Types.Operation
open Grace.Types.Types

type Operation() =

    static member public Get(parameters: GetOperationParameters) =
        postServer<GetOperationParameters, OperationDto> (parameters |> ensureCorrelationIdIsSet, $"operation/{nameof (Operation.Get)}")

    static member public Cancel(parameters: CancelOperationParameters) =
        postServer<CancelOperationParameters, OperationDto> (parameters |> ensureCorrelationIdIsSet, $"operation/cancel")

    static member public ApproveConflict(parameters: ApproveConflictParameters) =
        postServer<ApproveConflictParameters, OperationDto> (parameters |> ensureCorrelationIdIsSet, $"operation/approve-conflict")
