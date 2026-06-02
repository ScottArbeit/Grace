namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Types
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

module Webhooks =

    type WebhookRuleId = Guid
    type WebhookDeliveryId = Guid
    type ApprovalPolicyId = Guid
    type ApprovalRequestId = Guid
    type ApprovalNotificationDeliveryId = Guid
    type ExternalWebhookEventName = string
    type ExternalWebhookEventVersion = int
    type ExternalWebhookDedupeKey = string
    type WebhookSigningSecretVersion = string
    type ApprovalPolicyVersion = int
    type ApprovalSubject = string
    type ApprovalResponderSelector = string
    type ApprovalClientDecisionId = string
    type ApprovalRequestAttempt = int option

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type OutboundUrlSafety =
        | PublicHttps
        | LocalUnsafeDevOnly

        static member GetKnownTypes() = GetKnownTypes<OutboundUrlSafety>()

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type WebhookRuleStatus =
        | Enabled
        | Disabled
        | Deleted

        static member GetKnownTypes() = GetKnownTypes<WebhookRuleStatus>()

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type WebhookDeliveryStatus =
        | Pending
        | Succeeded
        | RetryScheduled
        | Failed
        | DeadLettered

        static member GetKnownTypes() = GetKnownTypes<WebhookDeliveryStatus>()

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ApprovalPolicyStatus =
        | Enabled
        | Disabled
        | Deleted

        static member GetKnownTypes() = GetKnownTypes<ApprovalPolicyStatus>()

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ApprovalRequestStatus =
        | Pending
        | Approved
        | Rejected
        | Expired
        | Cancelled
        | Superseded

        member this.IsTerminal =
            match this with
            | Pending -> false
            | Approved
            | Rejected
            | Expired
            | Cancelled
            | Superseded -> true

        static member GetKnownTypes() = GetKnownTypes<ApprovalRequestStatus>()

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ApprovalDecision =
        | Approve
        | Reject

        static member GetKnownTypes() = GetKnownTypes<ApprovalDecision>()

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ApprovalTimeoutAction =
        | Reject
        | Expire

        static member GetKnownTypes() = GetKnownTypes<ApprovalTimeoutAction>()

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ApprovalNotificationDeliveryStatus =
        | Pending
        | Succeeded
        | RetryScheduled
        | Failed
        | DeadLettered

        static member GetKnownTypes() = GetKnownTypes<ApprovalNotificationDeliveryStatus>()

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type PromotionSetApprovalState =
        | NotRequired
        | Pending
        | Approved
        | Rejected
        | Expired
        | Stale

        static member GetKnownTypes() = GetKnownTypes<PromotionSetApprovalState>()

    [<CLIMutable; GenerateSerializer>]
    type ExternalWebhookEventSource =
        {
            SourceFamily: string
            SourceCase: string
        }

        static member PromotionSetApplied =
            { SourceFamily = nameof PromotionSet.PromotionSetEventType; SourceCase = nameof PromotionSet.PromotionSetEventType.Applied }

        static member TerminalPromotionReferenceCreated = { SourceFamily = "TerminalPromotionReference"; SourceCase = "Created" }

    [<CLIMutable; GenerateSerializer>]
    type ScopedOutboundUrl =
        {
            Url: string
            Safety: OutboundUrlSafety
        }

        static member Default = { Url = String.Empty; Safety = OutboundUrlSafety.PublicHttps }

    [<CLIMutable; GenerateSerializer>]
    type WebhookScope =
        {
            OwnerId: OwnerId
            OrganizationId: OrganizationId
            RepositoryId: RepositoryId
            TargetBranchId: BranchId option
        }

        static member Default = { OwnerId = OwnerId.Empty; OrganizationId = OrganizationId.Empty; RepositoryId = RepositoryId.Empty; TargetBranchId = None }

    [<CLIMutable; GenerateSerializer>]
    type WebhookRetryPolicy =
        {
            MaxAttempts: int
            InitialDelaySeconds: int
            MaxDelaySeconds: int
        }

        static member Default = { MaxAttempts = 8; InitialDelaySeconds = 30; MaxDelaySeconds = 3600 }

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

    [<CLIMutable; GenerateSerializer>]
    type ApprovalRequestDecision =
        {
            Decision: ApprovalDecision
            DecidedBy: UserId
            DecidedAt: Instant
            Reason: string option
            ClientDecisionId: ApprovalClientDecisionId
        }

        static member Default =
            {
                Decision = ApprovalDecision.Approve
                DecidedBy = UserId String.Empty
                DecidedAt = Constants.DefaultTimestamp
                Reason = None
                ClientDecisionId = String.Empty
            }

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

        static member GetKnownTypes() = GetKnownTypes<ApprovalRequestCommand>()

    and [<KnownType("GetKnownTypes"); GenerateSerializer>] ApprovalRequestEventType =
        | Created of request: ApprovalRequest
        | DecisionRecorded of decision: ApprovalRequestDecision
        | Expired
        | Cancelled
        | Superseded of supersededByApprovalRequestId: ApprovalRequestId

        static member GetKnownTypes() = GetKnownTypes<ApprovalRequestEventType>()

    and [<CLIMutable; GenerateSerializer>] ApprovalRequestEvent = { Event: ApprovalRequestEventType; Metadata: EventMetadata }

    [<GenerateSerializer>]
    type ApprovalRequestDecisionResult = { Request: ApprovalRequest; Events: ApprovalRequestEvent list; WasIdempotentReplay: bool; Message: string }

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ApprovalRequestIndexCommand =
        | AddRequest of approvalRequestId: ApprovalRequestId

        static member GetKnownTypes() = GetKnownTypes<ApprovalRequestIndexCommand>()

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ApprovalRequestIndexEventType =
        | RequestAdded of approvalRequestId: ApprovalRequestId
        | RequestIndexed of request: ApprovalRequest
        | RequestIndexedJson of requestJson: string

        static member GetKnownTypes() = GetKnownTypes<ApprovalRequestIndexEventType>()

    [<CLIMutable; GenerateSerializer>]
    type ApprovalRequestIndexEvent = { Event: ApprovalRequestIndexEventType; Metadata: EventMetadata }

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
            Reason: string option
        }

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
                Reason = None
            }

    [<CLIMutable; GenerateSerializer>]
    type ExternalWebhookDedupeKeyShape = { Version: ExternalWebhookEventVersion; ScopeFields: string list; EventFields: string list }

    [<CLIMutable; GenerateSerializer>]
    type ExternalWebhookEventDefinition =
        {
            Name: ExternalWebhookEventName
            Version: ExternalWebhookEventVersion
            CanonicalSource: ExternalWebhookEventSource
            DedupeKeyShape: ExternalWebhookDedupeKeyShape
        }

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

    let tryParse (eventName: string) =
        if String.IsNullOrWhiteSpace eventName then
            None
        else
            All
            |> List.tryFind (fun definition -> String.Equals(definition.Name, eventName.Trim(), StringComparison.Ordinal))

    let parse eventName =
        match tryParse eventName with
        | Some definition -> Ok definition
        | None -> Error $"Unknown external webhook event '{eventName}'."
