namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

/// Contains webhooks helpers.
module Webhooks =

    /// Represents webhook rule id.
    type WebhookRuleId = Guid
    /// Represents webhook delivery id.
    type WebhookDeliveryId = Guid
    /// Represents approval policy id.
    type ApprovalPolicyId = Guid
    /// Represents approval request id.
    type ApprovalRequestId = Guid
    /// Represents approval notification delivery id.
    type ApprovalNotificationDeliveryId = Guid
    /// Represents external webhook event name.
    type ExternalWebhookEventName = string
    /// Represents external webhook event version.
    type ExternalWebhookEventVersion = int
    /// Represents external webhook dedupe key.
    type ExternalWebhookDedupeKey = string
    /// Represents webhook signing secret version.
    type WebhookSigningSecretVersion = string
    /// Represents approval policy version.
    type ApprovalPolicyVersion = int
    /// Represents approval subject.
    type ApprovalSubject = string
    /// Represents approval responder selector.
    type ApprovalResponderSelector = string
    /// Represents approval client decision id.
    type ApprovalClientDecisionId = string
    /// Represents approval request attempt.
    type ApprovalRequestAttempt = int option

    /// Represents outbound url safety.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type OutboundUrlSafety =
        | PublicHttps
        | LocalUnsafeDevOnly

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<OutboundUrlSafety>()

    /// Represents webhook rule status.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type WebhookRuleStatus =
        | Enabled
        | Disabled
        | Deleted

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<WebhookRuleStatus>()

    /// Represents webhook delivery status.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type WebhookDeliveryStatus =
        | Pending
        | Succeeded
        | RetryScheduled
        | Failed
        | DeadLettered

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<WebhookDeliveryStatus>()

    /// Represents approval policy status.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ApprovalPolicyStatus =
        | Enabled
        | Disabled
        | Deleted

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ApprovalPolicyStatus>()

    /// Represents approval request status.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ApprovalRequestStatus =
        | Pending
        | Approved
        | Rejected
        | Expired
        | Cancelled
        | Superseded

        /// Indicates whether the approval request state no longer accepts responder decisions.
        member this.IsTerminal =
            match this with
            | Pending -> false
            | Approved
            | Rejected
            | Expired
            | Cancelled
            | Superseded -> true

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ApprovalRequestStatus>()

    /// Represents approval decision.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ApprovalDecision =
        | Approve
        | Reject

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ApprovalDecision>()

    /// Represents approval timeout action.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ApprovalTimeoutAction =
        | Reject
        | Expire

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ApprovalTimeoutAction>()

    /// Represents approval notification delivery status.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ApprovalNotificationDeliveryStatus =
        | Pending
        | Succeeded
        | RetryScheduled
        | Failed
        | DeadLettered

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ApprovalNotificationDeliveryStatus>()

    /// Represents promotion set approval state.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type PromotionSetApprovalState =
        | NotRequired
        | Pending
        | Approved
        | Rejected
        | Expired
        | Stale

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<PromotionSetApprovalState>()

    /// Represents external webhook event source.
    [<CLIMutable; GenerateSerializer>]
    type ExternalWebhookEventSource =
        {
            SourceFamily: string
            SourceCase: string
        }

        /// Identifies the webhook event emitted when a promotion set is applied.
        static member PromotionSetApplied =
            { SourceFamily = nameof PromotionSet.PromotionSetEventType; SourceCase = nameof PromotionSet.PromotionSetEventType.Applied }

        /// Identifies the webhook event emitted when a terminal promotion reference is created.
        static member TerminalPromotionReferenceCreated = { SourceFamily = "TerminalPromotionReference"; SourceCase = "Created" }

    /// Represents scoped outbound url.
    [<CLIMutable; GenerateSerializer>]
    type ScopedOutboundUrl =
        {
            Url: string
            Safety: OutboundUrlSafety
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default = { Url = String.Empty; Safety = OutboundUrlSafety.PublicHttps }

    /// Represents webhook scope.
    [<CLIMutable; GenerateSerializer>]
    type WebhookScope =
        {
            OwnerId: OwnerId
            OrganizationId: OrganizationId
            RepositoryId: RepositoryId
            TargetBranchId: BranchId option
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default = { OwnerId = OwnerId.Empty; OrganizationId = OrganizationId.Empty; RepositoryId = RepositoryId.Empty; TargetBranchId = None }

    /// Represents webhook retry policy.
    [<CLIMutable; GenerateSerializer>]
    type WebhookRetryPolicy =
        {
            MaxAttempts: int
            InitialDelaySeconds: int
            MaxDelaySeconds: int
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default = { MaxAttempts = 8; InitialDelaySeconds = 30; MaxDelaySeconds = 3600 }

    /// Represents webhook rule.
    [<CLIMutable; GenerateSerializer>]
    type WebhookRule =
        {
            Class: string
            WebhookRuleId: WebhookRuleId
            Name: string
            EventName: ExternalWebhookEventName
            EventVersion: ExternalWebhookEventVersion
            Scope: WebhookScope
            Url: ScopedOutboundUrl
            SigningSecretVersion: WebhookSigningSecretVersion
            RetryPolicy: WebhookRetryPolicy
            Status: WebhookRuleStatus
            CreatedBy: UserId
            CreatedAt: Instant
            UpdatedAt: Instant option
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default =
            {
                Class = nameof WebhookRule
                WebhookRuleId = WebhookRuleId.Empty
                Name = String.Empty
                EventName = String.Empty
                EventVersion = 1
                Scope = WebhookScope.Default
                Url = ScopedOutboundUrl.Default
                SigningSecretVersion = String.Empty
                RetryPolicy = WebhookRetryPolicy.Default
                Status = WebhookRuleStatus.Disabled
                CreatedBy = UserId String.Empty
                CreatedAt = Constants.DefaultTimestamp
                UpdatedAt = None
            }

    /// Represents webhook delivery.
    [<CLIMutable; GenerateSerializer>]
    type WebhookDelivery =
        {
            Class: string
            WebhookDeliveryId: WebhookDeliveryId
            WebhookRuleId: WebhookRuleId
            EventName: ExternalWebhookEventName
            EventVersion: ExternalWebhookEventVersion
            DedupeKey: ExternalWebhookDedupeKey
            Status: WebhookDeliveryStatus
            AttemptCount: int
            LastAttemptAt: Instant option
            NextAttemptAt: Instant option
            LastStatusCode: int option
            LastError: string option
            CreatedAt: Instant
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default =
            {
                Class = nameof WebhookDelivery
                WebhookDeliveryId = WebhookDeliveryId.Empty
                WebhookRuleId = WebhookRuleId.Empty
                EventName = String.Empty
                EventVersion = 1
                DedupeKey = String.Empty
                Status = WebhookDeliveryStatus.Pending
                AttemptCount = 0
                LastAttemptAt = None
                NextAttemptAt = None
                LastStatusCode = None
                LastError = None
                CreatedAt = Constants.DefaultTimestamp
            }

    /// Represents approval scope.
    [<CLIMutable; GenerateSerializer>]
    type ApprovalScope =
        {
            OwnerId: OwnerId
            OrganizationId: OrganizationId
            RepositoryId: RepositoryId
            TargetBranchId: BranchId
            PromotionSetId: PromotionSetId option
            StepsComputationAttempt: int option
            ApprovalPolicyId: ApprovalPolicyId option
            ApprovalPolicyVersion: ApprovalPolicyVersion option
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default =
            {
                OwnerId = OwnerId.Empty
                OrganizationId = OrganizationId.Empty
                RepositoryId = RepositoryId.Empty
                TargetBranchId = BranchId.Empty
                PromotionSetId = None
                StepsComputationAttempt = None
                ApprovalPolicyId = None
                ApprovalPolicyVersion = None
            }

    /// Represents approval policy.
    [<CLIMutable; GenerateSerializer>]
    type ApprovalPolicy =
        {
            Class: string
            ApprovalPolicyId: ApprovalPolicyId
            Version: ApprovalPolicyVersion
            Name: string
            Subject: ApprovalSubject
            Scope: ApprovalScope
            RequiredResponder: ApprovalResponderSelector
            NotificationUrl: ScopedOutboundUrl option
            TimeoutSeconds: int option
            OnTimeout: ApprovalTimeoutAction
            Status: ApprovalPolicyStatus
            CreatedBy: UserId
            CreatedAt: Instant
            UpdatedAt: Instant option
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default =
            {
                Class = nameof ApprovalPolicy
                ApprovalPolicyId = ApprovalPolicyId.Empty
                Version = 1
                Name = String.Empty
                Subject = String.Empty
                Scope = ApprovalScope.Default
                RequiredResponder = String.Empty
                NotificationUrl = None
                TimeoutSeconds = None
                OnTimeout = ApprovalTimeoutAction.Reject
                Status = ApprovalPolicyStatus.Disabled
                CreatedBy = UserId String.Empty
                CreatedAt = Constants.DefaultTimestamp
                UpdatedAt = None
            }

    /// Represents approval request decision.
    [<CLIMutable; GenerateSerializer>]
    type ApprovalRequestDecision =
        {
            Decision: ApprovalDecision
            DecidedBy: UserId
            DecidedAt: Instant
            Reason: string option
            ClientDecisionId: ApprovalClientDecisionId
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default =
            {
                Decision = ApprovalDecision.Approve
                DecidedBy = UserId String.Empty
                DecidedAt = Constants.DefaultTimestamp
                Reason = None
                ClientDecisionId = String.Empty
            }

    /// Represents approval request.
    [<CLIMutable; GenerateSerializer>]
    type ApprovalRequest =
        {
            Class: string
            ApprovalRequestId: ApprovalRequestId
            ApprovalPolicyId: ApprovalPolicyId
            ApprovalPolicyVersion: ApprovalPolicyVersion
            Subject: ApprovalSubject
            Scope: ApprovalScope
            RequiredResponder: ApprovalResponderSelector
            Status: ApprovalRequestStatus
            Decision: ApprovalRequestDecision option
            CreatedBy: UserId
            CreatedAt: Instant
            ExpiresAt: Instant option
            UpdatedAt: Instant option
            SupersededByApprovalRequestId: ApprovalRequestId option
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default =
            {
                Class = nameof ApprovalRequest
                ApprovalRequestId = ApprovalRequestId.Empty
                ApprovalPolicyId = ApprovalPolicyId.Empty
                ApprovalPolicyVersion = 1
                Subject = String.Empty
                Scope = ApprovalScope.Default
                RequiredResponder = String.Empty
                Status = ApprovalRequestStatus.Pending
                Decision = None
                CreatedBy = UserId String.Empty
                CreatedAt = Constants.DefaultTimestamp
                ExpiresAt = None
                UpdatedAt = None
                SupersededByApprovalRequestId = None
            }

        /// Creates the DTO shape used to carry partial updates without mutating the persisted aggregate directly.
        static member UpdateDto (approvalRequestEvent: ApprovalRequestEvent) (current: ApprovalRequest) =
            match approvalRequestEvent.Event with
            | ApprovalRequestEventType.Created request -> request
            | ApprovalRequestEventType.DecisionRecorded decision ->
                let status =
                    match decision.Decision with
                    | ApprovalDecision.Approve -> ApprovalRequestStatus.Approved
                    | ApprovalDecision.Reject -> ApprovalRequestStatus.Rejected

                { current with Status = status; Decision = Some decision; UpdatedAt = Some approvalRequestEvent.Metadata.Timestamp }
            | ApprovalRequestEventType.Expired ->
                { current with Status = ApprovalRequestStatus.Expired; UpdatedAt = Some approvalRequestEvent.Metadata.Timestamp }
            | ApprovalRequestEventType.Cancelled ->
                { current with Status = ApprovalRequestStatus.Cancelled; UpdatedAt = Some approvalRequestEvent.Metadata.Timestamp }
            | ApprovalRequestEventType.Superseded supersededByApprovalRequestId ->
                { current with
                    Status = ApprovalRequestStatus.Superseded
                    UpdatedAt = Some approvalRequestEvent.Metadata.Timestamp
                    SupersededByApprovalRequestId = Some supersededByApprovalRequestId
                }

    and [<KnownType("GetKnownTypes"); GenerateSerializer>] ApprovalRequestCommand =
        | Create of request: ApprovalRequest
        | RecordDecision of decision: ApprovalRequestDecision
        | Expire
        | Cancel
        | Supersede of supersededByApprovalRequestId: ApprovalRequestId

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ApprovalRequestCommand>()

    and [<KnownType("GetKnownTypes"); GenerateSerializer>] ApprovalRequestEventType =
        | Created of request: ApprovalRequest
        | DecisionRecorded of decision: ApprovalRequestDecision
        | Expired
        | Cancelled
        | Superseded of supersededByApprovalRequestId: ApprovalRequestId

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ApprovalRequestEventType>()

    and [<CLIMutable; GenerateSerializer>] ApprovalRequestEvent = { Event: ApprovalRequestEventType; Metadata: EventMetadata }

    /// Represents the approval request decision result contract.
    [<GenerateSerializer>]
    type ApprovalRequestDecisionResult = { Request: ApprovalRequest; Events: ApprovalRequestEvent list; WasIdempotentReplay: bool; Message: string }

    /// Represents approval request index command.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ApprovalRequestIndexCommand =
        | AddRequest of approvalRequestId: ApprovalRequestId

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ApprovalRequestIndexCommand>()

    /// Represents approval request index event type.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ApprovalRequestIndexEventType =
        | RequestAdded of approvalRequestId: ApprovalRequestId
        | RequestIndexed of request: ApprovalRequest
        | RequestIndexedJson of requestJson: string

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ApprovalRequestIndexEventType>()

    /// Represents the approval request index event contract.
    [<CLIMutable; GenerateSerializer>]
    type ApprovalRequestIndexEvent = { Event: ApprovalRequestIndexEventType; Metadata: EventMetadata }

    /// Represents approval notification delivery.
    [<CLIMutable; GenerateSerializer>]
    type ApprovalNotificationDelivery =
        {
            Class: string
            ApprovalNotificationDeliveryId: ApprovalNotificationDeliveryId
            ApprovalRequestId: ApprovalRequestId
            ApprovalPolicyId: ApprovalPolicyId
            Url: ScopedOutboundUrl
            Status: ApprovalNotificationDeliveryStatus
            AttemptCount: int
            LastAttemptAt: Instant option
            NextAttemptAt: Instant option
            LastStatusCode: int option
            LastError: string option
            CreatedAt: Instant
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default =
            {
                Class = nameof ApprovalNotificationDelivery
                ApprovalNotificationDeliveryId = ApprovalNotificationDeliveryId.Empty
                ApprovalRequestId = ApprovalRequestId.Empty
                ApprovalPolicyId = ApprovalPolicyId.Empty
                Url = ScopedOutboundUrl.Default
                Status = ApprovalNotificationDeliveryStatus.Pending
                AttemptCount = 0
                LastAttemptAt = None
                NextAttemptAt = None
                LastStatusCode = None
                LastError = None
                CreatedAt = Constants.DefaultTimestamp
            }

    /// Represents promotion set approval summary.
    [<CLIMutable; GenerateSerializer>]
    type PromotionSetApprovalSummary =
        {
            Class: string
            PromotionSetId: PromotionSetId
            TargetBranchId: BranchId
            StepsComputationAttempt: int
            State: PromotionSetApprovalState
            ApprovalRequestId: ApprovalRequestId option
            ApprovalPolicyId: ApprovalPolicyId option
            RequiredResponder: ApprovalResponderSelector option
            LastDecisionAt: Instant option
            ExpiresAt: Instant option
            Reason: string option
        }

        /// Builds an approval summary for a promotion set that did not require an approval policy decision.
        static member NotRequired promotionSetId targetBranchId stepsComputationAttempt =
            {
                Class = nameof PromotionSetApprovalSummary
                PromotionSetId = promotionSetId
                TargetBranchId = targetBranchId
                StepsComputationAttempt = stepsComputationAttempt
                State = PromotionSetApprovalState.NotRequired
                ApprovalRequestId = None
                ApprovalPolicyId = None
                RequiredResponder = None
                LastDecisionAt = None
                ExpiresAt = None
                Reason = None
            }

    /// Represents the external webhook dedupe key shape contract.
    [<CLIMutable; GenerateSerializer>]
    type ExternalWebhookDedupeKeyShape = { Version: ExternalWebhookEventVersion; ScopeFields: string list; EventFields: string list }

    /// Represents external webhook event definition.
    [<CLIMutable; GenerateSerializer>]
    type ExternalWebhookEventDefinition =
        {
            Name: ExternalWebhookEventName
            Version: ExternalWebhookEventVersion
            CanonicalSource: ExternalWebhookEventSource
            DedupeKeyShape: ExternalWebhookDedupeKeyShape
        }

/// Contains external webhook event registry helpers.
module ExternalWebhookEventRegistry =

    open Webhooks

    [<Literal>]
    let PromotionSetAppliedName = "promotion-set.applied"

    let promotionSetApplied =
        {
            Name = PromotionSetAppliedName
            Version = 1
            CanonicalSource = ExternalWebhookEventSource.PromotionSetApplied
            DedupeKeyShape =
                {
                    Version = 1
                    ScopeFields =
                        [
                            "ownerId"
                            "organizationId"
                            "repositoryId"
                            "targetBranchId"
                            "promotionSetId"
                        ]
                    EventFields = [ "terminalPromotionReferenceId" ]
                }
        }

    /// Validates unique canonical sources.
    let validateUniqueCanonicalSources definitions =
        let duplicates =
            definitions
            |> Seq.groupBy (fun definition -> definition.CanonicalSource)
            |> Seq.choose (fun (source, entries) ->
                let entries = entries |> Seq.toList

                if entries.Length > 1 then
                    Some(
                        source,
                        entries
                        |> List.map (fun definition -> definition.Name)
                    )
                else
                    None)
            |> Seq.toList

        if duplicates.IsEmpty then
            Ok()
        else
            let duplicateText =
                duplicates
                |> List.map (fun (source, names) ->
                    let joinedNames = String.Join(", ", names)
                    $"{source}: {joinedNames}")
                |> String.concat "; "

            Error $"External webhook event registry contains duplicate canonical sources: {duplicateText}."

    let private registryDefinitions = [ promotionSetApplied ]

    let All =
        match validateUniqueCanonicalSources registryDefinitions with
        | Ok () -> registryDefinitions
        | Error errorText -> failwith errorText

    /// Attempts to parse.
    let tryParse (eventName: string) =
        if String.IsNullOrWhiteSpace eventName then
            None
        else
            All
            |> List.tryFind (fun definition -> String.Equals(definition.Name, eventName.Trim(), StringComparison.Ordinal))

    /// Parses parse.
    let parse eventName =
        match tryParse eventName with
        | Some definition -> Ok definition
        | None -> Error $"Unknown external webhook event '{eventName}'."
