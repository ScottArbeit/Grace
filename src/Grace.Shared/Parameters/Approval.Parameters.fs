namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Types.Webhooks
open System

module Approval =

    /// Base parameters for approval policy endpoints.
    type ApprovalPolicyParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName = String.Empty with get, set
        member val public TargetBranchId = String.Empty with get, set
        member val public ApprovalPolicyId = String.Empty with get, set

    /// Parameters for /approval/policy/create.
    type CreateApprovalPolicyParameters() =
        inherit ApprovalPolicyParameters()
        member val public Name = String.Empty with get, set
        member val public Subject = String.Empty with get, set
        member val public RequiredResponder = String.Empty with get, set
        member val public NotificationUrl = String.Empty with get, set
        member val public NotificationUrlSafety = OutboundUrlSafety.PublicHttps with get, set
        member val public AcknowledgeUnsafeLocalDevelopment = false with get, set
        member val public TimeoutSeconds: Nullable<int> = Nullable() with get, set
        member val public OnTimeout = ApprovalTimeoutAction.Reject with get, set

    /// Parameters for /approval/policy/list.
    type ListApprovalPoliciesParameters() =
        inherit ApprovalPolicyParameters()
        member val public IncludeDeleted = false with get, set

    /// Parameters for /approval/policy/show.
    type ShowApprovalPolicyParameters() =
        inherit ApprovalPolicyParameters()

    /// Parameters for /approval/policy/update.
    type UpdateApprovalPolicyParameters() =
        inherit CreateApprovalPolicyParameters()

    /// Parameters for /approval/policy/enable.
    type EnableApprovalPolicyParameters() =
        inherit ApprovalPolicyParameters()

    /// Parameters for /approval/policy/disable.
    type DisableApprovalPolicyParameters() =
        inherit ApprovalPolicyParameters()

    /// Parameters for /approval/policy/delete.
    type DeleteApprovalPolicyParameters() =
        inherit ApprovalPolicyParameters()

    /// Parameters for /approval/policy/evaluate.
    type EvaluateApprovalPolicyParameters() =
        inherit ApprovalPolicyParameters()
        member val public Subject = String.Empty with get, set

    /// Base parameters for approval request endpoints.
    type ApprovalRequestParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName = String.Empty with get, set
        member val public TargetBranchId = String.Empty with get, set
        member val public ApprovalRequestId = String.Empty with get, set

    /// Parameters for /approval/request/list.
    type ListApprovalRequestsParameters() =
        inherit ApprovalRequestParameters()
        member val public IncludeTerminal = true with get, set

    /// Parameters for /approval/request/show.
    type ShowApprovalRequestParameters() =
        inherit ApprovalRequestParameters()

    /// Parameters for /approval/request/approve.
    type ApproveApprovalRequestParameters() =
        inherit ApprovalRequestParameters()
        member val public Reason = String.Empty with get, set
        member val public ClientDecisionId = String.Empty with get, set

    /// Parameters for /approval/request/reject.
    type RejectApprovalRequestParameters() =
        inherit ApprovalRequestParameters()
        member val public Reason = String.Empty with get, set
        member val public ClientDecisionId = String.Empty with get, set

    /// Parameters for /approval/request/history.
    type ApprovalRequestHistoryParameters() =
        inherit ApprovalRequestParameters()

    /// Internal workflow/test seam for generated approval requests. Not exposed through SDK create APIs.
    type SeedGeneratedApprovalRequestParameters() =
        inherit ApprovalRequestParameters()
        member val public ApprovalPolicyId = String.Empty with get, set
        member val public ApprovalPolicyVersion = 1 with get, set
        member val public Subject = String.Empty with get, set
        member val public RequiredResponder = String.Empty with get, set
        member val public PromotionSetId = String.Empty with get, set
        member val public StepsComputationAttempt = Nullable<int>() with get, set
        member val public CreatedBy = "workflow" with get, set
