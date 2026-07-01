namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Types.Webhooks
open System

/// Contains webhook helpers.
module Webhook =

    /// Base parameters for webhook rule endpoints.
    type WebhookRuleParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName = String.Empty with get, set
        member val public TargetBranchId = String.Empty with get, set
        member val public WebhookRuleId = String.Empty with get, set

    /// Parameters for /webhook/rule/create.
    type CreateWebhookRuleParameters() =
        inherit WebhookRuleParameters()
        member val public Name = String.Empty with get, set
        member val public EventName = String.Empty with get, set
        member val public EventVersion = 1 with get, set
        member val public Url = String.Empty with get, set
        member val public UrlSafety = OutboundUrlSafety.PublicHttps with get, set
        member val public AcknowledgeUnsafeLocalDevelopment = false with get, set
        member val public SigningSecretVersion = String.Empty with get, set
        member val public MaxAttempts = WebhookRetryPolicy.Default.MaxAttempts with get, set
        member val public InitialDelaySeconds = WebhookRetryPolicy.Default.InitialDelaySeconds with get, set
        member val public MaxDelaySeconds = WebhookRetryPolicy.Default.MaxDelaySeconds with get, set

    /// Parameters for /webhook/rule/list.
    type ListWebhookRulesParameters() =
        inherit WebhookRuleParameters()
        member val public IncludeDeleted = false with get, set

    /// Parameters for /webhook/rule/show.
    type ShowWebhookRuleParameters() =
        inherit WebhookRuleParameters()

    /// Parameters for /webhook/rule/update.
    type UpdateWebhookRuleParameters() =
        inherit CreateWebhookRuleParameters()

    /// Parameters for /webhook/rule/enable.
    type EnableWebhookRuleParameters() =
        inherit WebhookRuleParameters()

    /// Parameters for /webhook/rule/disable.
    type DisableWebhookRuleParameters() =
        inherit WebhookRuleParameters()

    /// Parameters for /webhook/rule/delete.
    type DeleteWebhookRuleParameters() =
        inherit WebhookRuleParameters()

    /// Parameters for /webhook/rule/test.
    type TestWebhookRuleParameters() =
        inherit WebhookRuleParameters()
        member val public DedupeKey = String.Empty with get, set

    /// Base parameters for webhook delivery endpoints.
    type WebhookDeliveryParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName = String.Empty with get, set
        member val public TargetBranchId = String.Empty with get, set
        member val public WebhookRuleId = String.Empty with get, set
        member val public WebhookDeliveryId = String.Empty with get, set

    /// Parameters for /webhook/delivery/list.
    type ListWebhookDeliveriesParameters() =
        inherit WebhookDeliveryParameters()
        member val public IncludeTerminal = true with get, set

    /// Parameters for /webhook/delivery/show.
    type ShowWebhookDeliveryParameters() =
        inherit WebhookDeliveryParameters()
