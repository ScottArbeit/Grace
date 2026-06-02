namespace Grace.Server

open Giraffe
open Grace.Server.Security
open Grace.Shared
open Grace.Shared.Parameters.Webhook
open Grace.Shared.Utilities
open Grace.Types
open Grace.Types.Authorization
open Grace.Types.Types
open Grace.Types.Webhooks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open NodaTime
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO

module WebhookStore =

    type WebhookRuleDto = Grace.Types.Webhooks.WebhookRule
    type WebhookDeliveryDto = Grace.Types.Webhooks.WebhookDelivery

    let private rules = ConcurrentDictionary<WebhookRuleId, WebhookRuleDto>()
    let private deliveries = ConcurrentDictionary<WebhookDeliveryId, WebhookDeliveryDto>()
    let private deliveryPayloads = ConcurrentDictionary<WebhookDeliveryId, string>()
    let private deliveryRuleSnapshots = ConcurrentDictionary<WebhookDeliveryId, WebhookRuleDto>()

    let private scopeMatches (expected: WebhookScope) (actual: WebhookScope) =
        actual.OwnerId = expected.OwnerId
        && actual.OrganizationId = expected.OrganizationId
        && actual.RepositoryId = expected.RepositoryId
        && actual.TargetBranchId = expected.TargetBranchId

    let clearForTests () =
        rules.Clear()
        deliveries.Clear()
        deliveryPayloads.Clear()
        deliveryRuleSnapshots.Clear()

    let upsertRule (rule: WebhookRuleDto) =
        rules[rule.WebhookRuleId] <- rule
        rule

    let tryGetRule webhookRuleId =
        match rules.TryGetValue webhookRuleId with
        | true, rule -> Some rule
        | _ -> None

    let listRules scope includeDeleted =
        rules.Values
        |> Seq.filter (fun rule ->
            scopeMatches scope rule.Scope
            && (includeDeleted
                || rule.Status <> WebhookRuleStatus.Deleted))
        |> Seq.toArray
        :> IReadOnlyList<WebhookRuleDto>

    let addDelivery (delivery: WebhookDeliveryDto) =
        deliveries[delivery.WebhookDeliveryId] <- delivery
        delivery

    let addDeliveryPayload webhookDeliveryId payloadJson =
        deliveryPayloads[webhookDeliveryId] <- payloadJson
        payloadJson

    let tryGetDeliveryPayload webhookDeliveryId =
        match deliveryPayloads.TryGetValue webhookDeliveryId with
        | true, payloadJson -> Some payloadJson
        | _ -> None

    let addDeliveryRuleSnapshot webhookDeliveryId (rule: WebhookRuleDto) =
        deliveryRuleSnapshots[webhookDeliveryId] <- rule
        rule

    let tryGetDeliveryRuleSnapshot webhookDeliveryId =
        match deliveryRuleSnapshots.TryGetValue webhookDeliveryId with
        | true, rule -> Some rule
        | _ -> None

    let upsertDelivery (delivery: WebhookDeliveryDto) =
        deliveries[delivery.WebhookDeliveryId] <- delivery
        delivery

    let tryGetDelivery webhookDeliveryId =
        match deliveries.TryGetValue webhookDeliveryId with
        | true, delivery -> Some delivery
        | _ -> None

    let tryGetDeliveryByRuleAndDedupe webhookRuleId dedupeKey =
        deliveries.Values
        |> Seq.tryFind (fun delivery ->
            delivery.WebhookRuleId = webhookRuleId
            && delivery.DedupeKey = dedupeKey)

    let listEnabledRulesForEvent (scope: WebhookScope) eventName eventVersion =
        rules.Values
        |> Seq.filter (fun rule ->
            rule.Status = WebhookRuleStatus.Enabled
            && rule.EventName = eventName
            && rule.EventVersion = eventVersion
            && rule.Scope.OwnerId = scope.OwnerId
            && rule.Scope.OrganizationId = scope.OrganizationId
            && rule.Scope.RepositoryId = scope.RepositoryId
            && (rule.Scope.TargetBranchId.IsNone
                || rule.Scope.TargetBranchId = scope.TargetBranchId))
        |> Seq.toArray
        :> IReadOnlyList<WebhookRuleDto>

    let listDeliveries scope webhookRuleId includeTerminal =
        deliveries.Values
        |> Seq.filter (fun delivery ->
            match tryGetRule delivery.WebhookRuleId with
            | Some rule ->
                scopeMatches scope rule.Scope
                && (webhookRuleId = WebhookRuleId.Empty
                    || delivery.WebhookRuleId = webhookRuleId)
                && (includeTerminal
                    || delivery.Status = WebhookDeliveryStatus.Pending
                    || delivery.Status = WebhookDeliveryStatus.RetryScheduled)
            | None -> false)
        |> Seq.toArray
        :> IReadOnlyList<WebhookDeliveryDto>

    let listScheduledRetries dueAt maxCount =
        deliveries.Values
        |> Seq.filter (fun delivery ->
            delivery.Status = WebhookDeliveryStatus.RetryScheduled
            && match delivery.NextAttemptAt with
               | Some nextAttemptAt -> nextAttemptAt <= dueAt
               | None -> false)
        |> Seq.sortBy (fun delivery -> delivery.NextAttemptAt)
        |> Seq.truncate maxCount
        |> Seq.toArray
        :> IReadOnlyList<WebhookDeliveryDto>

module WebhookCommon =

    let tryParseGuid value =
        let mutable parsed = Guid.Empty

        if String.IsNullOrWhiteSpace value |> not
           && Guid.TryParse(value, &parsed)
           && parsed <> Guid.Empty then
            Some parsed
        else
            None

    let scopeFromRuleParameters (parameters: WebhookRuleParameters) =
        let ownerId =
            tryParseGuid parameters.OwnerId
            |> Option.defaultValue OwnerId.Empty

        let organizationId =
            tryParseGuid parameters.OrganizationId
            |> Option.defaultValue OrganizationId.Empty

        let repositoryId =
            tryParseGuid parameters.RepositoryId
            |> Option.defaultValue RepositoryId.Empty

        let targetBranchId = tryParseGuid parameters.TargetBranchId

        { WebhookScope.Default with OwnerId = ownerId; OrganizationId = organizationId; RepositoryId = repositoryId; TargetBranchId = targetBranchId }

    let scopeFromDeliveryParameters (parameters: WebhookDeliveryParameters) =
        let ownerId =
            tryParseGuid parameters.OwnerId
            |> Option.defaultValue OwnerId.Empty

        let organizationId =
            tryParseGuid parameters.OrganizationId
            |> Option.defaultValue OrganizationId.Empty

        let repositoryId =
            tryParseGuid parameters.RepositoryId
            |> Option.defaultValue RepositoryId.Empty

        let targetBranchId = tryParseGuid parameters.TargetBranchId

        { WebhookScope.Default with OwnerId = ownerId; OrganizationId = organizationId; RepositoryId = repositoryId; TargetBranchId = targetBranchId }

    let resourceFromWebhookScope (scope: WebhookScope) =
        match scope.TargetBranchId with
        | Some branchId -> Resource.Branch(scope.OwnerId, scope.OrganizationId, scope.RepositoryId, branchId)
        | None -> Resource.Repository(scope.OwnerId, scope.OrganizationId, scope.RepositoryId)

    let scopeEquals (left: WebhookScope) (right: WebhookScope) =
        left.OwnerId = right.OwnerId
        && left.OrganizationId = right.OrganizationId
        && left.RepositoryId = right.RepositoryId
        && left.TargetBranchId = right.TargetBranchId

    let currentUserId (context: HttpContext) =
        PrincipalMapper.tryGetUserId context.User
        |> Option.defaultValue "unknown"
        |> UserId

    let error context message = GraceError.Create message (Services.getCorrelationId context)

module WebhookRule =

    open WebhookCommon

    let private validateUrl (context: HttpContext) (parameters: CreateWebhookRuleParameters) =
        let configuration = context.RequestServices.GetRequiredService<IConfiguration>()
        let hostEnvironment = context.RequestServices.GetService<IHostEnvironment>()

        let request: OutboundUrlSafety.ValidationRequest =
            { Url = parameters.Url; RequestedSafety = parameters.UrlSafety; AcknowledgeUnsafeLocalDevelopment = parameters.AcknowledgeUnsafeLocalDevelopment }

        match OutboundUrlSafety.validate hostEnvironment configuration request with
        | Ok validated -> Ok validated.ScopedUrl
        | Error failure -> Error $"Url is not allowed: {failure}."

    let private validateEvent (parameters: CreateWebhookRuleParameters) =
        match ExternalWebhookEventRegistry.parse parameters.EventName with
        | Error message -> Error message
        | Ok definition when definition.Version <> parameters.EventVersion ->
            Error $"Webhook event '{parameters.EventName}' version {parameters.EventVersion} is not registered."
        | Ok _ -> Ok()

    let private retryPolicyFromParameters (parameters: CreateWebhookRuleParameters) =
        if parameters.MaxAttempts < 1 then
            Error "MaxAttempts must be at least 1."
        elif parameters.InitialDelaySeconds < 0 then
            Error "InitialDelaySeconds cannot be negative."
        elif parameters.MaxDelaySeconds < parameters.InitialDelaySeconds then
            Error "MaxDelaySeconds must be greater than or equal to InitialDelaySeconds."
        else
            Ok { MaxAttempts = parameters.MaxAttempts; InitialDelaySeconds = parameters.InitialDelaySeconds; MaxDelaySeconds = parameters.MaxDelaySeconds }

    let private buildRule context webhookRuleId status createdBy createdAt (parameters: CreateWebhookRuleParameters) =
        task {
            match validateEvent parameters, retryPolicyFromParameters parameters, validateUrl context parameters with
            | Error message, _, _
            | _, Error message, _
            | _, _, Error message -> return Error message
            | Ok (), Ok retryPolicy, Ok url ->
                return
                    Ok
                        { Grace.Types.Webhooks.WebhookRule.Default with
                            WebhookRuleId = webhookRuleId
                            Name = parameters.Name
                            EventName = parameters.EventName.Trim()
                            EventVersion = parameters.EventVersion
                            Scope = scopeFromRuleParameters parameters
                            Url = url
                            SigningSecretVersion = parameters.SigningSecretVersion
                            RetryPolicy = retryPolicy
                            Status = status
                            CreatedBy = createdBy
                            CreatedAt = createdAt
                        }
        }

    let private ruleFromContext<'T when 'T :> WebhookRuleParameters> (context: HttpContext) =
        task {
            context.Request.EnableBuffering()
            let! parameters = context.BindJsonAsync<'T>()

            context.Request.Body.Seek(0L, IO.SeekOrigin.Begin)
            |> ignore

            return
                tryParseGuid parameters.WebhookRuleId
                |> Option.bind WebhookStore.tryGetRule
        }

    let resolveStoredRuleForManage<'T when 'T :> WebhookRuleParameters> (context: HttpContext) =
        task {
            let! rule = ruleFromContext<'T> context

            return
                match rule with
                | Some webhookRule -> Ok(Operation.WebhookManage, resourceFromWebhookScope webhookRule.Scope)
                | None -> Error(error context "Webhook rule was not found.")
        }

    let Create: HttpHandler =
        fun _ context ->
            task {
                let! parameters = Services.parse<CreateWebhookRuleParameters> context
                let webhookRuleId = Guid.NewGuid()
                let createdAt = getCurrentInstant ()
                let createdBy = currentUserId context

                match! buildRule context webhookRuleId WebhookRuleStatus.Disabled createdBy createdAt parameters with
                | Error message ->
                    return!
                        context
                        |> Services.result400BadRequest (error context message)
                | Ok rule ->
                    return!
                        context
                        |> Services.result200Ok (WebhookStore.upsertRule rule)
            }

    let List: HttpHandler =
        fun _ context ->
            task {
                let! parameters = Services.parse<ListWebhookRulesParameters> context
                let scope = scopeFromRuleParameters parameters

                return!
                    context
                    |> Services.result200Ok (WebhookStore.listRules scope parameters.IncludeDeleted)
            }

    let Show: HttpHandler =
        fun _ context ->
            task {
                let! parameters = Services.parse<ShowWebhookRuleParameters> context

                match tryParseGuid parameters.WebhookRuleId
                      |> Option.bind WebhookStore.tryGetRule
                    with
                | Some rule -> return! context |> Services.result200Ok rule
                | None -> return! Services.result404NotFound context
            }

    let Update: HttpHandler =
        fun _ context ->
            task {
                let! parameters = Services.parse<UpdateWebhookRuleParameters> context

                match tryParseGuid parameters.WebhookRuleId
                      |> Option.bind WebhookStore.tryGetRule
                    with
                | None -> return! Services.result404NotFound context
                | Some existing ->
                    let requestedScope = scopeFromRuleParameters parameters

                    if scopeEquals requestedScope existing.Scope |> not then
                        return!
                            context
                            |> Services.result400BadRequest (error context "Webhook rule scope cannot be changed.")
                    else
                        match! buildRule context existing.WebhookRuleId existing.Status existing.CreatedBy existing.CreatedAt parameters with
                        | Error message ->
                            return!
                                context
                                |> Services.result400BadRequest (error context message)
                        | Ok rule ->
                            return!
                                context
                                |> Services.result200Ok (WebhookStore.upsertRule { rule with UpdatedAt = Some(getCurrentInstant ()) })
            }

    let private setStatus status : HttpHandler =
        fun _ context ->
            task {
                let! parameters = Services.parse<WebhookRuleParameters> context

                match tryParseGuid parameters.WebhookRuleId
                      |> Option.bind WebhookStore.tryGetRule
                    with
                | None -> return! Services.result404NotFound context
                | Some rule ->
                    return!
                        context
                        |> Services.result200Ok (WebhookStore.upsertRule { rule with Status = status; UpdatedAt = Some(getCurrentInstant ()) })
            }

    let Enable: HttpHandler = setStatus WebhookRuleStatus.Enabled

    let Disable: HttpHandler = setStatus WebhookRuleStatus.Disabled

    let Delete: HttpHandler = setStatus WebhookRuleStatus.Deleted

    let Test: HttpHandler =
        fun _ context ->
            task {
                let! parameters = Services.parse<TestWebhookRuleParameters> context

                match tryParseGuid parameters.WebhookRuleId
                      |> Option.bind WebhookStore.tryGetRule
                    with
                | None -> return! Services.result404NotFound context
                | Some rule ->
                    let delivery =
                        { Grace.Types.Webhooks.WebhookDelivery.Default with
                            WebhookDeliveryId = Guid.NewGuid()
                            WebhookRuleId = rule.WebhookRuleId
                            EventName = rule.EventName
                            EventVersion = rule.EventVersion
                            DedupeKey =
                                if String.IsNullOrWhiteSpace parameters.DedupeKey then
                                    $"test:{Guid.NewGuid():N}"
                                else
                                    parameters.DedupeKey.Trim()
                            Status = WebhookDeliveryStatus.Pending
                            CreatedAt = getCurrentInstant ()
                        }

                    return!
                        context
                        |> Services.result200Ok (WebhookStore.addDelivery delivery)
            }

module WebhookDelivery =

    open WebhookCommon

    let private deliveryFromContext<'T when 'T :> WebhookDeliveryParameters> (context: HttpContext) =
        task {
            context.Request.EnableBuffering()
            let! parameters = context.BindJsonAsync<'T>()

            context.Request.Body.Seek(0L, IO.SeekOrigin.Begin)
            |> ignore

            return
                tryParseGuid parameters.WebhookDeliveryId
                |> Option.bind WebhookStore.tryGetDelivery
        }

    let resolveStoredDeliveryForRead<'T when 'T :> WebhookDeliveryParameters> (context: HttpContext) =
        task {
            let! delivery = deliveryFromContext<'T> context

            return
                match delivery with
                | Some webhookDelivery ->
                    match WebhookStore.tryGetRule webhookDelivery.WebhookRuleId with
                    | Some webhookRule -> Ok(Operation.WebhookDeliveryRead, resourceFromWebhookScope webhookRule.Scope)
                    | None -> Error(error context "Webhook delivery rule was not found.")
                | None -> Error(error context "Webhook delivery was not found.")
        }

    let List: HttpHandler =
        fun _ context ->
            task {
                let! parameters = Services.parse<ListWebhookDeliveriesParameters> context
                let scope = scopeFromDeliveryParameters parameters

                let webhookRuleId =
                    tryParseGuid parameters.WebhookRuleId
                    |> Option.defaultValue WebhookRuleId.Empty

                return!
                    context
                    |> Services.result200Ok (WebhookStore.listDeliveries scope webhookRuleId parameters.IncludeTerminal)
            }

    let Show: HttpHandler =
        fun _ context ->
            task {
                let! parameters = Services.parse<ShowWebhookDeliveryParameters> context

                match tryParseGuid parameters.WebhookDeliveryId
                      |> Option.bind WebhookStore.tryGetDelivery
                    with
                | Some delivery -> return! context |> Services.result200Ok delivery
                | None -> return! Services.result404NotFound context
            }
