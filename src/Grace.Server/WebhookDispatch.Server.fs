namespace Grace.Server

open Grace.Server.Security
open Grace.Shared.Utilities
open Grace.Types
open Grace.Types.Events
open Grace.Types.PromotionSet
open Grace.Types.Common
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
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks

/// Contains Grace Server webhook dispatch behavior and supporting helpers.
module WebhookDispatch =

    /// Represents outbound webhook request used by Grace Server APIs and background services.
    type OutboundWebhookRequest =
        {
            DeliveryId: WebhookDeliveryId
            Url: Uri
            UrlSafety: OutboundUrlSafety
            RedactedUrl: string
            Headers: IReadOnlyDictionary<string, string>
            PayloadJson: string
        }

    /// Represents outbound webhook result used by Grace Server APIs and background services.
    type OutboundWebhookResult =
        | Succeeded of statusCode: int
        | TransientFailure of statusCode: int option * error: string
        | PermanentFailure of statusCode: int option * error: string

    /// Defines the contract for ioutbound webhook transport.
    type IOutboundWebhookTransport =
        /// Defines the send async operation for implementers.
        abstract member SendAsync: OutboundWebhookRequest * CancellationToken -> Task<OutboundWebhookResult>

    /// Represents http outbound webhook transport used by Grace Server APIs and background services.
    type HttpOutboundWebhookTransport(configuration: IConfiguration, hostEnvironment: IHostEnvironment) =
        let handler = new HttpClientHandler()

        do handler.AllowAutoRedirect <- false

        let client = new HttpClient(handler, disposeHandler = true)

        /// Computes classify status code data used by Grace Server.
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
            /// Releases the HTTP response object after a webhook send completes.
            member _.Dispose() = client.Dispose()

        interface IOutboundWebhookTransport with
            /// Sends an outbound webhook request with the supplied content and cancellation token.
            member _.SendAsync(request, cancellationToken) =
                task {
                    use message = new HttpRequestMessage(HttpMethod.Post, request.Url)
                    use content = new StringContent(request.PayloadJson, Encoding.UTF8, "application/json")
                    message.Content <- content

                    for header in request.Headers do
                        message.Headers.TryAddWithoutValidation(header.Key, header.Value)
                        |> ignore

                    use! response = client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)

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

    /// Represents dispatch result used by Grace Server APIs and background services.
    type DispatchResult =
        {
            DeliveryCount: int
            DeliveredCount: int
            FailedCount: int
            SkippedDuplicateCount: int
        }

        static member Empty = { DeliveryCount = 0; DeliveredCount = 0; FailedCount = 0; SkippedDuplicateCount = 0 }

    /// Gets try get guid from metadata data needed by the server flow.
    let private tryGetGuidFromMetadata propertyName (metadata: EventMetadata) =
        match metadata.Properties.TryGetValue(propertyName) with
        | true, rawValue ->
            match Guid.TryParse rawValue with
            | true, parsed when parsed <> Guid.Empty -> Option.Some parsed
            | _ -> Option.None
        | _ -> Option.None

    /// Gets try get promotion set id data needed by the server flow.
    let private tryGetPromotionSetId (metadata: EventMetadata) =
        match tryGetGuidFromMetadata (nameof PromotionSetId) metadata with
        | Option.Some promotionSetId -> Option.Some promotionSetId
        | Option.None -> tryGetGuidFromMetadata "ActorId" metadata

    /// Gets try get target branch id data needed by the server flow.
    let private tryGetTargetBranchId (metadata: EventMetadata) =
        match tryGetGuidFromMetadata (nameof BranchId) metadata with
        | Option.Some branchId -> Option.Some branchId
        | Option.None -> tryGetGuidFromMetadata "TargetBranchId" metadata

    /// Computes scope from metadata data used by Grace Server.
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

    /// Creates the external webhook dispatch payload and dedupe key for promotion-set-applied events.
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

    /// Selects the external webhook event projection supported for a Grace domain event.
    let private tryCreateDispatchEvent graceEvent =
        match graceEvent with
        | GraceEvent.PromotionSetEvent promotionSetEvent -> tryCreatePromotionSetAppliedDispatchEvent promotionSetEvent
        | _ -> Option.None

    /// Computes hmac sha256 hex data used by Grace Server.
    let private hmacSha256Hex (key: string) (material: byte array) =
        use hmac = new HMACSHA256(Encoding.UTF8.GetBytes key)

        hmac.ComputeHash material
        |> Array.map (fun value -> value.ToString("x2", CultureInfo.InvariantCulture))
        |> String.concat String.Empty

    /// Implements headers for delivery for the server request pipeline.
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

    /// Implements retry delay seconds for the server request pipeline.
    let private retryDelaySeconds attemptCount (policy: WebhookRetryPolicy) =
        if policy.InitialDelaySeconds <= 0 then
            0
        else
            let multiplier = Math.Pow(2.0, float (max 0 (attemptCount - 1)))
            min policy.MaxDelaySeconds (int (float policy.InitialDelaySeconds * multiplier))

    let private sensitiveErrorKeys =
        [|
            "access_token"
            "api_key"
            "apikey"
            "client_secret"
            "code"
            "credential"
            "key"
            "password"
            "secret"
            "sig"
            "signature"
            "signed"
            "token"
        |]

    /// Computes redact sensitive query values data used by Grace Server.
    let private redactSensitiveQueryValues (value: string) =
        Regex.Replace(
            value,
            @"(?i)([?&;](?:access_token|api_key|apikey|client_secret|code|credential|key|password|secret|sig|signature|signed|token)=)([^&#;\s]+)",
            "${1}REDACTED"
        )

    /// Computes redact sensitive assignments data used by Grace Server.
    let private redactSensitiveAssignments (value: string) =
        sensitiveErrorKeys
        |> Array.fold
            (fun current key ->
                let pattern = sprintf @"(?i)\b(%s)(\s*[:=]\s*)(""[^""]*""|'[^']*'|[^&#\s,;]+)" (Regex.Escape key)

                Regex.Replace(current, pattern, "${1}${2}REDACTED"))
            value

    /// Computes sanitize persisted error data used by Grace Server.
    let private sanitizePersistedError (value: string) =
        if String.IsNullOrWhiteSpace value then
            String.Empty
        else
            value
            |> redactSensitiveQueryValues
            |> redactSensitiveAssignments

    /// Computes trim error data used by Grace Server.
    let private trimError (value: string) =
        let sanitized = sanitizePersistedError value

        if sanitized.Length <= 256 then sanitized else sanitized.Substring(0, 256)

    /// Validates validate delivery url inputs before server processing continues.
    let private validateDeliveryUrl hostEnvironment configuration (rule: WebhookRule) =
        OutboundUrlSafety.validate
            hostEnvironment
            configuration
            {
                Url = rule.Url.Url
                RequestedSafety = rule.Url.Safety
                AcknowledgeUnsafeLocalDevelopment = rule.Url.Safety = OutboundUrlSafety.LocalUnsafeDevOnly
            }

    /// Implements terminal delivery status for the server request pipeline.
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

    /// Coordinates record validation failure processing for Grace Server.
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
                LastError = Option.Some(trimError $"Outbound URL rejected: {failure}")
            }
        |> ignore

    /// Coordinates send delivery attempt processing for Grace Server.
    let private sendDeliveryAttempt
        (logger: ILogger)
        (transport: IOutboundWebhookTransport)
        (rule: WebhookRule)
        (validatedUrl: OutboundUrlSafety.ValidatedOutboundUrl)
        (payloadJson: string)
        (delivery: WebhookDelivery)
        cancellationToken
        =
        task {
            let attempt = delivery.AttemptCount + 1
            let attemptAt = getCurrentInstant ()

            let timestamp =
                attemptAt
                    .ToDateTimeUtc()
                    .ToString("O", CultureInfo.InvariantCulture)

            let request =
                {
                    DeliveryId = delivery.WebhookDeliveryId
                    Url = validatedUrl.Uri
                    UrlSafety = rule.Url.Safety
                    RedactedUrl = OutboundUrlSafety.Redaction.redactUri rule.Url.Url
                    Headers = headersForDelivery delivery rule payloadJson timestamp
                    PayloadJson = payloadJson
                }

            logger.LogInformation(
                "Dispatching webhook delivery {WebhookDeliveryId} for {EventName} to {RedactedUrl}, attempt {AttemptCount}.",
                delivery.WebhookDeliveryId,
                rule.EventName,
                request.RedactedUrl,
                attempt
            )

            let! result =
                task {
                    try
                        return! transport.SendAsync(request, cancellationToken)
                    with
                    | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
                    | :? OperationCanceledException as ex ->
                        let message =
                            if String.IsNullOrWhiteSpace ex.Message then
                                "Outbound request timed out or was canceled before completion."
                            else
                                ex.Message

                        return TransientFailure(Option.None, message)
                    | ex -> return TransientFailure(Option.None, ex.Message)
                }

            let status, statusCode, error, nextAttemptAt = terminalDeliveryStatus attempt rule.RetryPolicy result

            let updatedDelivery =
                { delivery with
                    AttemptCount = attempt
                    LastAttemptAt = Some attemptAt
                    LastStatusCode = statusCode
                    LastError = error
                    NextAttemptAt = nextAttemptAt
                    Status = status
                }

            WebhookStore.upsertDelivery updatedDelivery
            |> ignore

            return updatedDelivery
        }

    /// Coordinates deliver rule processing for Grace Server.
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

            if WebhookStore.tryClaimDeliveryByRuleAndDedupe delivery
               |> not then
                return Choice2Of2 true
            else
                WebhookStore.addDeliveryPayload delivery.WebhookDeliveryId payloadJson
                |> ignore

                WebhookStore.addDeliveryRuleSnapshot delivery.WebhookDeliveryId rule
                |> ignore

                match validateDeliveryUrl hostEnvironment configuration rule with
                | Error failure ->
                    recordValidationFailure logger rule delivery failure
                    return Choice1Of2 false
                | Ok validatedUrl ->
                    let! currentDelivery = sendDeliveryAttempt logger transport rule validatedUrl payloadJson delivery cancellationToken

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

    /// Represents scheduled retry result used by Grace Server APIs and background services.
    type ScheduledRetryResult =
        {
            ProcessedCount: int
            DeliveredCount: int
            FailedCount: int
            RescheduledCount: int
            SkippedCount: int
        }

        static member Empty = { ProcessedCount = 0; DeliveredCount = 0; FailedCount = 0; RescheduledCount = 0; SkippedCount = 0 }

    /// Coordinates process scheduled retries async processing for Grace Server.
    let processScheduledRetriesAsync
        (logger: ILogger)
        (configuration: IConfiguration)
        (hostEnvironment: IHostEnvironment)
        (transport: IOutboundWebhookTransport)
        dueAt
        maxDeliveries
        (cancellationToken: CancellationToken)
        =
        task {
            let batchSize = max 0 maxDeliveries
            let dueDeliveries = WebhookStore.listScheduledRetries dueAt batchSize
            let mutable result = ScheduledRetryResult.Empty
            let mutable index = 0

            while index < dueDeliveries.Count do
                cancellationToken.ThrowIfCancellationRequested()

                let delivery = dueDeliveries[index]

                match WebhookStore.tryGetDeliveryRuleSnapshot delivery.WebhookDeliveryId, WebhookStore.tryGetDeliveryPayload delivery.WebhookDeliveryId with
                | Some rule, Some payloadJson ->
                    match validateDeliveryUrl hostEnvironment configuration rule with
                    | Error failure ->
                        recordValidationFailure logger rule delivery failure

                        result <- { result with ProcessedCount = result.ProcessedCount + 1; FailedCount = result.FailedCount + 1 }
                    | Ok validatedUrl ->
                        let! updatedDelivery = sendDeliveryAttempt logger transport rule validatedUrl payloadJson delivery cancellationToken

                        result <-
                            match updatedDelivery.Status with
                            | WebhookDeliveryStatus.Succeeded ->
                                { result with ProcessedCount = result.ProcessedCount + 1; DeliveredCount = result.DeliveredCount + 1 }
                            | WebhookDeliveryStatus.RetryScheduled ->
                                { result with ProcessedCount = result.ProcessedCount + 1; RescheduledCount = result.RescheduledCount + 1 }
                            | _ -> { result with ProcessedCount = result.ProcessedCount + 1; FailedCount = result.FailedCount + 1 }
                | _ ->
                    WebhookStore.upsertDelivery
                        { delivery with
                            Status = WebhookDeliveryStatus.DeadLettered
                            LastAttemptAt = Some(getCurrentInstant ())
                            LastError = Some(trimError "Retry delivery could not be processed because its rule snapshot or payload is no longer available.")
                            NextAttemptAt = Option.None
                        }
                    |> ignore

                    result <- { result with ProcessedCount = result.ProcessedCount + 1; FailedCount = result.FailedCount + 1 }

                index <- index + 1

            return result
        }

    /// Coordinates dispatch committed event async processing for Grace Server.
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
                        cancellationToken.ThrowIfCancellationRequested()

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
            | :? OperationCanceledException as ex when cancellationToken.IsCancellationRequested -> return raise ex
            | ex ->
                logger.LogError(ex, "Webhook dispatch failed after source event commit; source workflow will not be blocked.")
                return DispatchResult.Empty
        }

    /// Coordinates drain scheduled retries once async processing for Grace Server.
    let drainScheduledRetriesOnceAsync
        (logger: ILogger)
        (configuration: IConfiguration)
        (hostEnvironment: IHostEnvironment)
        (transport: IOutboundWebhookTransport)
        maxDeliveries
        (cancellationToken: CancellationToken)
        =
        processScheduledRetriesAsync logger configuration hostEnvironment transport (getCurrentInstant ()) maxDeliveries cancellationToken

/// Represents webhook retry hosted service used by Grace Server APIs and background services.
type WebhookRetryHostedService
    (
        logger: ILogger<WebhookRetryHostedService>,
        configuration: IConfiguration,
        hostEnvironment: IHostEnvironment,
        transport: WebhookDispatch.IOutboundWebhookTransport
    ) =
    inherit BackgroundService()

    let pollIntervalSeconds = max 1 (configuration.GetValue<int>("grace:webhooks:retryProcessor:pollIntervalSeconds", 30))
    let initialDelaySeconds = max 0 (configuration.GetValue<int>("grace:webhooks:retryProcessor:initialDelaySeconds", 5))
    let maxDeliveriesPerTick = max 1 (configuration.GetValue<int>("grace:webhooks:retryProcessor:maxDeliveriesPerTick", 25))
    let pollInterval = TimeSpan.FromSeconds(float pollIntervalSeconds)
    let initialDelay = TimeSpan.FromSeconds(float initialDelaySeconds)

    /// Internal entry point used by tests to execute one webhook dispatch pass with supplied dependencies.
    member internal _.DrainOnceAsync(cancellationToken: CancellationToken) =
        WebhookDispatch.drainScheduledRetriesOnceAsync logger configuration hostEnvironment transport maxDeliveriesPerTick cancellationToken

    /// Runs the hosted webhook dispatcher loop until cancellation or host shutdown.
    override this.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            try
                if initialDelay > TimeSpan.Zero then do! Task.Delay(initialDelay, stoppingToken)

                use periodicTimer = new PeriodicTimer(pollInterval)
                let mutable ticked = true

                while ticked
                      && not stoppingToken.IsCancellationRequested do
                    try
                        let! result = this.DrainOnceAsync(stoppingToken)

                        if result.ProcessedCount > 0 then
                            logger.LogInformation(
                                "Processed {ProcessedCount} scheduled webhook retries: {DeliveredCount} delivered, {FailedCount} failed, {RescheduledCount} rescheduled.",
                                result.ProcessedCount,
                                result.DeliveredCount,
                                result.FailedCount,
                                result.RescheduledCount
                            )
                    with
                    | :? OperationCanceledException when stoppingToken.IsCancellationRequested -> ()
                    | ex -> logger.LogError(ex, "Scheduled webhook retry processing failed; the processor will continue on the next tick.")

                    let! tick = periodicTimer.WaitForNextTickAsync(stoppingToken)
                    ticked <- tick
            with
            | :? OperationCanceledException when stoppingToken.IsCancellationRequested -> ()
        }
        :> Task
