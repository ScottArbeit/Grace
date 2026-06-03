namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.Approval
open Grace.Types.Webhooks
open System.Collections.Generic

type ApprovalPolicyDto = Grace.Types.Webhooks.ApprovalPolicy
type ApprovalRequestDto = Grace.Types.Webhooks.ApprovalRequest

/// The ApprovalPolicy module provides approval policy lifecycle helpers.
type ApprovalPolicy() =
    /// Creates an approval policy.
    static member public Create(parameters: CreateApprovalPolicyParameters) =
        postServer<CreateApprovalPolicyParameters, ApprovalPolicyDto> (parameters |> ensureCorrelationIdIsSet, "approval/policy/create")

    /// Lists approval policies for a repository or branch scope.
    static member public List(parameters: ListApprovalPoliciesParameters) =
        postServer<ListApprovalPoliciesParameters, IReadOnlyList<ApprovalPolicyDto>> (parameters |> ensureCorrelationIdIsSet, "approval/policy/list")

    /// Shows a single approval policy.
    static member public Show(parameters: ShowApprovalPolicyParameters) =
        postServer<ShowApprovalPolicyParameters, ApprovalPolicyDto> (parameters |> ensureCorrelationIdIsSet, "approval/policy/show")

    /// Updates an approval policy and increments its version.
    static member public Update(parameters: UpdateApprovalPolicyParameters) =
        postServer<UpdateApprovalPolicyParameters, ApprovalPolicyDto> (parameters |> ensureCorrelationIdIsSet, "approval/policy/update")

    /// Enables an approval policy.
    static member public Enable(parameters: EnableApprovalPolicyParameters) =
        postServer<EnableApprovalPolicyParameters, ApprovalPolicyDto> (parameters |> ensureCorrelationIdIsSet, "approval/policy/enable")

    /// Disables an approval policy.
    static member public Disable(parameters: DisableApprovalPolicyParameters) =
        postServer<DisableApprovalPolicyParameters, ApprovalPolicyDto> (parameters |> ensureCorrelationIdIsSet, "approval/policy/disable")

    /// Deletes an approval policy.
    static member public Delete(parameters: DeleteApprovalPolicyParameters) =
        postServer<DeleteApprovalPolicyParameters, ApprovalPolicyDto> (parameters |> ensureCorrelationIdIsSet, "approval/policy/delete")

    /// Evaluates enabled approval policies for a subject.
    static member public Evaluate(parameters: EvaluateApprovalPolicyParameters) =
        postServer<EvaluateApprovalPolicyParameters, IReadOnlyList<ApprovalPolicyDto>> (parameters |> ensureCorrelationIdIsSet, "approval/policy/evaluate")

/// The ApprovalRequest module provides read and response helpers for workflow-generated approval requests.
type ApprovalRequest() =
    /// Lists approval requests for a repository or branch scope.
    static member public List(parameters: ListApprovalRequestsParameters) =
        postServer<ListApprovalRequestsParameters, IReadOnlyList<ApprovalRequestDto>> (parameters |> ensureCorrelationIdIsSet, "approval/request/list")

    /// Shows a workflow-generated approval request.
    static member public Show(parameters: ShowApprovalRequestParameters) =
        postServer<ShowApprovalRequestParameters, ApprovalRequestDto> (parameters |> ensureCorrelationIdIsSet, "approval/request/show")

    /// Approves a workflow-generated approval request.
    static member public Approve(parameters: ApproveApprovalRequestParameters) =
        postServer<ApproveApprovalRequestParameters, ApprovalRequestDto> (parameters |> ensureCorrelationIdIsSet, "approval/request/approve")

    /// Rejects a workflow-generated approval request.
    static member public Reject(parameters: RejectApprovalRequestParameters) =
        postServer<RejectApprovalRequestParameters, ApprovalRequestDto> (parameters |> ensureCorrelationIdIsSet, "approval/request/reject")

    /// Gets approval request history.
    static member public History(parameters: ApprovalRequestHistoryParameters) =
        postServer<ApprovalRequestHistoryParameters, IReadOnlyList<ApprovalRequestDto>> (parameters |> ensureCorrelationIdIsSet, "approval/request/history")
