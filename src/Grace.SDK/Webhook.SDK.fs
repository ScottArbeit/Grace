namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.Webhook
open Grace.Types.Webhooks
open System.Collections.Generic

/// The WebhookRule module provides webhook rule lifecycle helpers.
type WebhookRule() =
    /// Creates a webhook rule with its destination URL.
    static member public Create(parameters: CreateWebhookRuleParameters) =
        postServer<CreateWebhookRuleParameters, WebhookRule> (parameters |> ensureCorrelationIdIsSet, "webhook/rule/create")

    /// Lists webhook rules for a repository or branch scope.
    static member public List(parameters: ListWebhookRulesParameters) =
        postServer<ListWebhookRulesParameters, IReadOnlyList<WebhookRule>> (parameters |> ensureCorrelationIdIsSet, "webhook/rule/list")

    /// Shows a single webhook rule.
    static member public Show(parameters: ShowWebhookRuleParameters) =
        postServer<ShowWebhookRuleParameters, WebhookRule> (parameters |> ensureCorrelationIdIsSet, "webhook/rule/show")

    /// Updates a webhook rule.
    static member public Update(parameters: UpdateWebhookRuleParameters) =
        postServer<UpdateWebhookRuleParameters, WebhookRule> (parameters |> ensureCorrelationIdIsSet, "webhook/rule/update")

    /// Enables a webhook rule.
    static member public Enable(parameters: EnableWebhookRuleParameters) =
        postServer<EnableWebhookRuleParameters, WebhookRule> (parameters |> ensureCorrelationIdIsSet, "webhook/rule/enable")

    /// Disables a webhook rule.
    static member public Disable(parameters: DisableWebhookRuleParameters) =
        postServer<DisableWebhookRuleParameters, WebhookRule> (parameters |> ensureCorrelationIdIsSet, "webhook/rule/disable")

    /// Deletes a webhook rule.
    static member public Delete(parameters: DeleteWebhookRuleParameters) =
        postServer<DeleteWebhookRuleParameters, WebhookRule> (parameters |> ensureCorrelationIdIsSet, "webhook/rule/delete")

    /// Records a pending test delivery for the webhook rule without dispatching it.
    static member public Test(parameters: TestWebhookRuleParameters) =
        postServer<TestWebhookRuleParameters, WebhookDelivery> (parameters |> ensureCorrelationIdIsSet, "webhook/rule/test")

/// The WebhookDelivery module provides read helpers for webhook delivery observability.
type WebhookDelivery() =
    /// Lists webhook deliveries for a repository or branch scope.
    static member public List(parameters: ListWebhookDeliveriesParameters) =
        postServer<ListWebhookDeliveriesParameters, IReadOnlyList<WebhookDelivery>> (parameters |> ensureCorrelationIdIsSet, "webhook/delivery/list")

    /// Shows a single webhook delivery.
    static member public Show(parameters: ShowWebhookDeliveryParameters) =
        postServer<ShowWebhookDeliveryParameters, WebhookDelivery> (parameters |> ensureCorrelationIdIsSet, "webhook/delivery/show")
