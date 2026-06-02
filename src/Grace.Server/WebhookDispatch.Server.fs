namespace Grace.Server

open Grace.Server.Security
open Grace.Shared.Utilities
open Grace.Types
open Grace.Types.Events
open Grace.Types.PromotionSet
open Grace.Types.Types
open Grace.Types.Webhooks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Generic
open System.Globalization
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

module WebhookDispatch =

    type OutboundWebhookRequest =
        {
            DeliveryId: WebhookDeliveryId
            Url: Uri
            UrlSafety: OutboundUrlSafety
            RedactedUrl: string
            Headers: IReadOnlyDictionary<string, string>
            PayloadJson: string
        }

    type OutboundWebhookResult =
        | Succeeded of statusCode: int
        | TransientFailure of statusCode: int option * error: string
        | PermanentFailure of statusCode: int option * error: string

    type IOutboundWebhookTransport =
        abstract member SendAsync: OutboundWebhookRequest * CancellationToken -> Task<OutboundWebhookResult>

    type HttpOutboundWebhookTransport(configuration: IConfiguration, hostEnvironment: IHostEnvironment) =
        let handler = new HttpClientHandler()

        do handler.AllowAutoRedirect <- false

        let client = new HttpClient(handler, disposeHandler = true)

        let classifyStatusCode (statusCode: HttpStatusCode) =
            let numericStatusCode = int statusCode

            if numericStatusCode >= 200
               && numericStatusCode <= 299 then
                Succeeded numericStatusCode
            elif numericStatusCode = 408
                 || numericStatusCode = 409
                 || numericStatusCode = 425
                 || numericStatusCode = 429
                 || numericStatusCode >= 500 then
                TransientFailure(Some numericStatusCode, $"HTTP {numericStatusCode}")
            else
                PermanentFailure(Some numericStatusCode, $"HTTP {numericStatusCode}")

        interface IDisposable with
            member _.Dispose() = client.Dispose()

        interface IOutboundWebhookTransport with
            member _.SendAsync(request, cancellationToken) =
                task {
                    use message = new HttpRequestMessage(HttpMethod.Post, request.Url)
                    use content = new StringContent(request.PayloadJson, Encoding.UTF8, "application/json")
                    message.Content <- content

                    for header in request.Headers do
                        message.Headers.TryAddWithoutValidation(header.Key, header.Value)
                        |> ignore

                    let! response = client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)

                    match int response.StatusCode with
                    | statusCode when
                        statusCode >= 300
                        && statusCode <= 399
                        && response.Headers.Location <> null
                        ->
                        let original: OutboundUrlSafety.ValidatedOutboundUrl =
                            {
                                Uri = request.Url
                                ScopedUrl = { Url = request.Url.AbsoluteUri; Safety = request.UrlSafety }
                                RedirectPolicy = OutboundUrlSafety.RedirectPolicy.RevalidateEveryRedirect
                                ResolvedAddresses = Array.empty
                            }

                        match OutboundUrlSafety.validateRedirect hostEnvironment configuration original response.Headers.Location with
                        | Error failure -> return PermanentFailure(Some statusCode, $"Redirect rejected: {failure}")
                        | Ok _ -> return TransientFailure(Some statusCode, "Redirected delivery will be retried.")
                    | _ -> return classifyStatusCode response.StatusCode
                }

    type DispatchResult =
        {
            DeliveryCount: int
            DeliveredCount: int
            FailedCount: int
            SkippedDuplicateCount: int
        }

        static member Empty = { DeliveryCount = 0; DeliveredCount = 0; FailedCount = 0; SkippedDuplicateCount = 0 }

    let private tryGetGuidFromMetadata propertyName (metadata: EventMetadata) =
        match metadata.Properties.TryGetValue(propertyName) with
        | true, rawValue ->
            match Guid.TryParse rawValue with
            | true, parsed when parsed <> Guid.Empty -> Option.Some parsed
            | _ -> Option.None
        | _ -> Option.None

    let private tryGetPromotionSetId (metadata: EventMetadata) =
        match tryGetGuidFromMetadata (nameof PromotionSetId) metadata with
        | Option.Some promotionSetId -> Option.Some promotionSetId
        | Option.None -> tryGetGuidFromMetadata "ActorId" metadata

    let private tryGetTargetBranchId (metadata: EventMetadata) =
        match tryGetGuidFromMetadata (nameof BranchId) metadata with
        | Option.Some branchId -> Option.Some branchId
        | Option.None -> tryGetGuidFromMetadata "TargetBranchId" metadata

    let private scopeFromMetadata metadata =
        {
            OwnerId =
                tryGetGuidFromMetadata (nameof OwnerId) metadata
                |> Option.defaultValue OwnerId.Empty
            OrganizationId =
                tryGetGuidFromMetadata (nameof OrganizationId) metadata
                |> Option.defaultValue OrganizationId.Empty
            RepositoryId =
                tryGetGuidFromMetadata (nameof RepositoryId) metadata
                |> Option.defaultValue RepositoryId.Empty
            TargetBranchId = tryGetTargetBranchId metadata
        }

    let private tryCreatePromotionSetAppliedDispatchEvent (promotionSetEvent: PromotionSetEvent) =
        match promotionSetEvent.Event with
        | PromotionSetEventType.Applied terminalPromotionReferenceId ->
            let definition = ExternalWebhookEventRegistry.promotionSetApplied
            let scope = scopeFromMetadata promotionSetEvent.Metadata

            let promotionSetId =
                tryGetPromotionSetId promotionSetEvent.Metadata
                |> Option.defaultValue PromotionSetId.Empty

            let dedupeKey =
                String.Join(
                    ":",
                    [|
                        definition.Name
                        $"{definition.Version}"
                        $"{scope.OwnerId}"
                        $"{scope.OrganizationId}"
                        $"{scope.RepositoryId}"
                        $"{scope.TargetBranchId
                           |> Option.defaultValue BranchId.Empty}"
                        $"{promotionSetId}"
                        $"{terminalPromotionReferenceId}"
                    |]
                )

            let payload =
                {|
                    eventName = definition.Name
                    eventVersion = definition.Version
                    eventTime = promotionSetEvent.Metadata.Timestamp
                    correlationId = promotionSetEvent.Metadata.CorrelationId
                    ownerId = scope.OwnerId
                    organizationId = scope.OrganizationId
                    repositoryId = scope.RepositoryId
                    targetBranchId = scope.TargetBranchId
                    promotionSetId = promotionSetId
                    terminalPromotionReferenceId = terminalPromotionReferenceId
                |}

            Option.Some(definition, scope, dedupeKey, serialize payload)
        | _ -> Option.None

    let private tryCreateDispatchEvent graceEvent =
        match graceEvent with
        | GraceEvent.PromotionSetEvent promotionSetEvent -> tryCreatePromotionSetAppliedDispatchEvent promotionSetEvent
        | _ -> Option.None

    let private hmacSha256Hex (key: string) (material: byte array) =
        use hmac = new HMACSHA256(Encoding.UTF8.GetBytes key)

        hmac.ComputeHash material
        |> Array.map (fun value -> value.ToString("x2", CultureInfo.InvariantCulture))
        |> String.concat String.Empty

    let private headersForDelivery (delivery: WebhookDelivery) (rule: WebhookRule) (payloadJson: string) (timestamp: string) =
        let payload = Encoding.UTF8.GetBytes payloadJson
        let signingInput = OutboundUrlSafety.Signing.createSigningInput $"{delivery.WebhookDeliveryId}" timestamp rule.SigningSecretVersion payload
        let signature = hmacSha256Hex rule.SigningSecretVersion signingInput.SignedMaterial

        Dictionary<string, string>(
            dict [ "x-grace-webhook-delivery-id", $"{delivery.WebhookDeliveryId}"
                   "x-grace-webhook-event-name", rule.EventName
                   "x-grace-webhook-event-version", $"{rule.EventVersion}"
                   "x-grace-webhook-timestamp", signingInput.Timestamp
                   "x-grace-webhook-signature-key-id", signingInput.KeyId
                   "x-grace-webhook-payload-sha256", signingInput.PayloadSha256Hex
                   "x-grace-webhook-signature", $"sha256={signature}" ]
        )
        :> IReadOnlyDictionary<string, string>

    let private retryDelaySeconds attemptCount (policy: WebhookRetryPolicy) =
        if policy.InitialDelaySeconds <= 0 then
            0
        else
            let multiplier = Math.Pow(2.0, float (max 0 (attemptCount - 1)))
            min policy.MaxDelaySeconds (int (float policy.InitialDelaySeconds * multiplier))

    let private trimError (value: string) =
        if String.IsNullOrWhiteSpace value then String.Empty
        elif value.Length <= 256 then value
        else value.Substring(0, 256)

    let private validateDeliveryUrl hostEnvironment configuration (rule: WebhookRule) =
        OutboundUrlSafety.validate
            hostEnvironment
            configuration
            {
                Url = rule.Url.Url
                RequestedSafety = rule.Url.Safety
                AcknowledgeUnsafeLocalDevelopment = rule.Url.Safety = OutboundUrlSafety.LocalUnsafeDevOnly
            }

    let private terminalDeliveryStatus attempts (policy: WebhookRetryPolicy) result =
        match result with
        | Succeeded statusCode -> WebhookDeliveryStatus.Succeeded, Option.Some statusCode, Option.None, Option.None
        | PermanentFailure (statusCode, error) -> WebhookDeliveryStatus.Failed, statusCode, Option.Some(trimError error), Option.None
        | TransientFailure (statusCode, error) when attempts >= policy.MaxAttempts ->
            WebhookDeliveryStatus.DeadLettered, statusCode, Option.Some(trimError error), Option.None
        | TransientFailure (statusCode, error) ->
            let nextAttemptAt =
                getCurrentInstant()
                    .Plus(Duration.FromSeconds(float (retryDelaySeconds attempts policy)))

            WebhookDeliveryStatus.RetryScheduled, statusCode, Option.Some(trimError error), Option.Some nextAttemptAt

    let private recordValidationFailure (logger: ILogger) (rule: WebhookRule) (delivery: WebhookDelivery) failure =
        let redactedUrl = OutboundUrlSafety.Redaction.redactUri rule.Url.Url

        logger.LogWarning(
            "Rejected webhook delivery {WebhookDeliveryId} for {EventName} to {RedactedUrl}: {Failure}.",
            delivery.WebhookDeliveryId,
            rule.EventName,
            redactedUrl,
            failure
        )

        WebhookStore.upsertDelivery
            { delivery with
                Status = WebhookDeliveryStatus.DeadLettered
                LastAttemptAt = Option.Some(getCurrentInstant ())
                LastError = Option.Some $"Outbound URL rejected: {failure}"
            }
        |> ignore

    let private deliverRule
        (logger: ILogger)
        (configuration: IConfiguration)
        (hostEnvironment: IHostEnvironment)
        (transport: IOutboundWebhookTransport)
        (definition: ExternalWebhookEventDefinition)
        dedupeKey
        (payloadJson: string)
        (rule: WebhookRule)
        cancellationToken
        =
        task {
            match WebhookStore.tryGetDeliveryByRuleAndDedupe rule.WebhookRuleId dedupeKey with
            | Option.Some _ -> return Choice2Of2 true
            | Option.None ->
                let delivery =
                    { WebhookDelivery.Default with
                        WebhookDeliveryId = Guid.NewGuid()
                        WebhookRuleId = rule.WebhookRuleId
                        EventName = definition.Name
                        EventVersion = definition.Version
                        DedupeKey = dedupeKey
                        Status = WebhookDeliveryStatus.Pending
                        CreatedAt = getCurrentInstant ()
                    }

                WebhookStore.addDelivery delivery |> ignore

                match validateDeliveryUrl hostEnvironment configuration rule with
                | Error failure ->
                    recordValidationFailure logger rule delivery failure
                    return Choice1Of2 false
                | Ok validatedUrl ->
                    let mutable currentDelivery = delivery
                    let mutable shouldContinue = true

                    while shouldContinue
                          && currentDelivery.AttemptCount < rule.RetryPolicy.MaxAttempts do
                        let attempt = currentDelivery.AttemptCount + 1
                        let attemptAt = getCurrentInstant ()

                        let timestamp =
                            attemptAt
                                .ToDateTimeUtc()
                                .ToString("O", CultureInfo.InvariantCulture)

                        let request =
                            {
                                DeliveryId = currentDelivery.WebhookDeliveryId
                                Url = validatedUrl.Uri
                                UrlSafety = rule.Url.Safety
                                RedactedUrl = OutboundUrlSafety.Redaction.redactUri rule.Url.Url
                                Headers = headersForDelivery currentDelivery rule payloadJson timestamp
                                PayloadJson = payloadJson
                            }

                        logger.LogInformation(
                            "Dispatching webhook delivery {WebhookDeliveryId} for {EventName} to {RedactedUrl}, attempt {AttemptCount}.",
                            currentDelivery.WebhookDeliveryId,
                            rule.EventName,
                            request.RedactedUrl,
                            attempt
                        )

                        let! result =
                            task {
                                try
                                    return! transport.SendAsync(request, cancellationToken)
                                with
                                | ex -> return TransientFailure(Option.None, trimError ex.Message)
                            }

                        let status, statusCode, error, nextAttemptAt = terminalDeliveryStatus attempt rule.RetryPolicy result

                        currentDelivery <-
                            { currentDelivery with
                                AttemptCount = attempt
                                LastAttemptAt = Some attemptAt
                                LastStatusCode = statusCode
                                LastError = error
                                NextAttemptAt = nextAttemptAt
                                Status = status
                            }

                        WebhookStore.upsertDelivery currentDelivery
                        |> ignore

                        match result with
                        | TransientFailure _ when attempt < rule.RetryPolicy.MaxAttempts -> ()
                        | _ -> shouldContinue <- false

                    match currentDelivery.Status with
                    | WebhookDeliveryStatus.Succeeded -> return Choice1Of2 true
                    | _ ->
                        logger.LogWarning(
                            "Webhook delivery {WebhookDeliveryId} for {EventName} finished with {WebhookDeliveryStatus}.",
                            currentDelivery.WebhookDeliveryId,
                            currentDelivery.EventName,
                            currentDelivery.Status
                        )

                        return Choice1Of2 false
        }

    let dispatchCommittedEventAsync
        (logger: ILogger)
        (configuration: IConfiguration)
        (hostEnvironment: IHostEnvironment)
        (transport: IOutboundWebhookTransport)
        (graceEvent: GraceEvent)
        (cancellationToken: CancellationToken)
        =
        task {
            try
                match tryCreateDispatchEvent graceEvent with
                | Option.None -> return DispatchResult.Empty
                | Option.Some (definition, scope, dedupeKey, payloadJson) ->
                    let rules = WebhookStore.listEnabledRulesForEvent scope definition.Name definition.Version
                    let mutable deliveredCount = 0
                    let mutable failedCount = 0
                    let mutable duplicateCount = 0
                    let mutable index = 0

                    while index < rules.Count do
                        let! result = deliverRule logger configuration hostEnvironment transport definition dedupeKey payloadJson rules[index] cancellationToken

                        match result with
                        | Choice1Of2 true -> deliveredCount <- deliveredCount + 1
                        | Choice1Of2 false -> failedCount <- failedCount + 1
                        | Choice2Of2 true -> duplicateCount <- duplicateCount + 1
                        | Choice2Of2 false -> ()

                        index <- index + 1

                    return
                        {
                            DeliveryCount = rules.Count - duplicateCount
                            DeliveredCount = deliveredCount
                            FailedCount = failedCount
                            SkippedDuplicateCount = duplicateCount
                        }
            with
            | ex ->
                logger.LogError(ex, "Webhook dispatch failed after source event commit; source workflow will not be blocked.")
                return DispatchResult.Empty
        }
