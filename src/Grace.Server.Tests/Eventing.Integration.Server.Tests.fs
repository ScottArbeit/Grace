namespace Grace.Server.Tests

open Grace.Server
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Parameters.Webhook
open Grace.Shared.Utilities
open Grace.Types
open Grace.Types.Common
open Grace.Types.Events
open Grace.Types.PromotionSet
open Grace.Types.Reference
open Grace.Types.Validation
open Grace.Types.Webhooks
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.Net
open System.Threading.Tasks

/// Groups shared helpers for eventing integration helpers.
module private EventingIntegrationHelpers =

    /// Defines host state behavior for the surrounding tests used by the server integration eventing Integration scenario.
    let hostState () =
        match App with
        | Microsoft.FSharp.Core.Option.Some app ->
            {
                App = app
                Client = Client
                GraceServerBaseAddress = graceServerBaseAddress
                ServiceBusConnectionString = serviceBusConnectionString
                ServiceBusTopic = serviceBusTopic
                ServiceBusServerSubscription = serviceBusServerSubscription
                ServiceBusTestSubscription = serviceBusTestSubscription
                OperationalFactsTopic = operationalFactsTopic
                OperationsSqlConnectionString = operationsSqlConnectionString
            }
        | Microsoft.FSharp.Core.Option.None -> failwith "Aspire test host has not started."

    /// Defines metadata behavior for the surrounding tests used by the server integration eventing Integration scenario.
    let metadata ownerId organizationId repositoryId branchId actorId correlationId =
        let properties = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        properties[nameof OwnerId] <- $"{ownerId}"
        properties[nameof OrganizationId] <- $"{organizationId}"
        properties[nameof RepositoryId] <- $"{repositoryId}"
        properties[nameof BranchId] <- $"{branchId}"
        properties[nameof PromotionSetId] <- $"{actorId}"
        properties["ActorId"] <- $"{actorId}"

        {
            Timestamp = Instant.FromUtc(2026, 6, 5, 12, 0)
            CorrelationId = correlationId
            Principal = "eventing-integration-test"
            ClientType = Microsoft.FSharp.Core.Option.None
            Properties = properties
        }

    /// Builds create rule parameters for route calls.
    let createRuleParameters ownerId organizationId repositoryId branchId =
        let parameters = CreateWebhookRuleParameters()
        parameters.OwnerId <- $"{ownerId}"
        parameters.OrganizationId <- $"{organizationId}"
        parameters.RepositoryId <- $"{repositoryId}"
        parameters.TargetBranchId <- $"{branchId}"
        parameters.Name <- $"servicebus-proof-{Guid.NewGuid():N}"
        parameters.EventName <- ExternalWebhookEventRegistry.PromotionSetAppliedName
        parameters.EventVersion <- 1
        parameters.Url <- "http://127.0.0.1:9/grace-webhook?token=secret"
        parameters.UrlSafety <- OutboundUrlSafety.LocalUnsafeDevOnly
        parameters.AcknowledgeUnsafeLocalDevelopment <- true
        parameters.SigningSecretVersion <- "test-secret-v1"
        parameters.MaxAttempts <- 2
        parameters.InitialDelaySeconds <- 1
        parameters.MaxDelaySeconds <- 2
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Defines target branch text behavior for the surrounding tests used by the server integration eventing Integration scenario.
    let private targetBranchText (rule: WebhookRule) =
        rule.Scope.TargetBranchId
        |> Microsoft.FSharp.Core.Option.map string
        |> Microsoft.FSharp.Core.Option.defaultValue String.Empty

    /// Builds enable rule parameters for route calls.
    let enableRuleParameters (rule: WebhookRule) =
        let parameters = EnableWebhookRuleParameters()
        parameters.OwnerId <- $"{rule.Scope.OwnerId}"
        parameters.OrganizationId <- $"{rule.Scope.OrganizationId}"
        parameters.RepositoryId <- $"{rule.Scope.RepositoryId}"
        parameters.TargetBranchId <- targetBranchText rule
        parameters.WebhookRuleId <- $"{rule.WebhookRuleId}"
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Builds list delivery parameters for route calls.
    let listDeliveryParameters (rule: WebhookRule) =
        let parameters = ListWebhookDeliveriesParameters()
        parameters.OwnerId <- $"{rule.Scope.OwnerId}"
        parameters.OrganizationId <- $"{rule.Scope.OrganizationId}"
        parameters.RepositoryId <- $"{rule.Scope.RepositoryId}"
        parameters.TargetBranchId <- targetBranchText rule
        parameters.WebhookRuleId <- $"{rule.WebhookRuleId}"
        parameters.IncludeTerminal <- true
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Posts ok to the running test server.
    let postOkAsync<'T, 'P> (route: string) (parameters: 'P) =
        task {
            let! response = Client.PostAsync(route, createJsonContent parameters)
            let! (body: string) = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            return deserialize<'T> body
        }

    /// Builds a deterministic enabled rule for integration setup fixture for the server integration eventing Integration assertions.
    let createEnabledRuleAsync ownerId organizationId repositoryId branchId =
        task {
            let! created =
                postOkAsync<WebhookRule, CreateWebhookRuleParameters> "/webhook/rule/create" (createRuleParameters ownerId organizationId repositoryId branchId)

            let! enabled = postOkAsync<WebhookRule, EnableWebhookRuleParameters> "/webhook/rule/enable" (enableRuleParameters created)
            Assert.That(enabled.Status, Is.EqualTo(WebhookRuleStatus.Enabled))
            return enabled
        }

    /// Defines promotion set applied event behavior for the surrounding tests used by the server integration eventing Integration scenario.
    let promotionSetAppliedEvent ownerId organizationId repositoryId branchId promotionSetId terminalReferenceId correlationId =
        let eventMetadata = metadata ownerId organizationId repositoryId branchId promotionSetId correlationId

        let promotionSetEvent: PromotionSetEvent = { Event = PromotionSetEventType.Applied terminalReferenceId; Metadata = eventMetadata }

        GraceEvent.PromotionSetEvent promotionSetEvent, eventMetadata

    /// Defines reference created event behavior for the surrounding tests used by the server integration eventing Integration scenario.
    let referenceCreatedEvent referenceType repositoryId branchId correlationId =
        let referenceId = Guid.NewGuid()
        let eventMetadata = metadata (Guid.Parse ownerId) (Guid.Parse organizationId) repositoryId branchId referenceId correlationId

        let referenceEvent: ReferenceEvent =
            {
                Event =
                    ReferenceEventType.Created(
                        referenceId,
                        Guid.Parse ownerId,
                        Guid.Parse organizationId,
                        repositoryId,
                        branchId,
                        Guid.NewGuid(),
                        Sha256Hash String.Empty,
                        Blake3Hash String.Empty,
                        referenceType,
                        $"{referenceType}-reference",
                        Seq.empty
                    )
                Metadata = eventMetadata
            }

        GraceEvent.ReferenceEvent referenceEvent, eventMetadata, referenceId

    /// Waits for for delivery to become observable in the test host.
    let waitForDeliveryAsync rule dedupeKey timeout =
        task {
            let sw = System.Diagnostics.Stopwatch.StartNew()
            let mutable found: WebhookDelivery option = Microsoft.FSharp.Core.Option.None

            while found.IsNone && sw.Elapsed < timeout do
                let! deliveries = postOkAsync<WebhookDelivery array, ListWebhookDeliveriesParameters> "/webhook/delivery/list" (listDeliveryParameters rule)

                found <-
                    deliveries
                    |> Array.tryFind (fun delivery ->
                        delivery.DedupeKey = dedupeKey
                        && delivery.Status <> WebhookDeliveryStatus.Pending)

                if found.IsNone then do! Task.Delay(TimeSpan.FromMilliseconds(250.0))

            match found with
            | Microsoft.FSharp.Core.Option.Some delivery -> return delivery
            | Microsoft.FSharp.Core.Option.None -> return raise (TimeoutException($"Timed out waiting for webhook delivery with dedupe key '{dedupeKey}'."))
        }

    /// Defines delivery count behavior for the surrounding tests used by the server integration eventing Integration scenario.
    let deliveryCountAsync rule =
        task {
            let! deliveries = postOkAsync<WebhookDelivery array, ListWebhookDeliveriesParameters> "/webhook/delivery/list" (listDeliveryParameters rule)

            return deliveries.Length
        }

    /// Asserts delivery count remains for integration responses.
    let assertDeliveryCountRemainsAsync rule expectedCount timeout =
        task {
            let sw = System.Diagnostics.Stopwatch.StartNew()
            let observedCounts = ResizeArray<int>()
            let mutable exceeded = false

            while not exceeded && sw.Elapsed < timeout do
                let! count = deliveryCountAsync rule
                observedCounts.Add(count)

                if count > expectedCount then
                    exceeded <- true
                else
                    do! Task.Delay(TimeSpan.FromMilliseconds(250.0))

            let observedCountsText = String.Join(", ", observedCounts)

            Assert.That(
                exceeded,
                Is.False,
                $"Expected webhook delivery count to remain at {expectedCount} during the bounded duplicate processing window of {timeout.TotalSeconds:n1}s. Observed counts: {observedCountsText}"
            )
        }

    /// Defines validation result matches behavior for the surrounding tests used by the server integration eventing Integration scenario.
    let validationResultMatches correlationId validationName ownerId organizationId repositoryId referenceType referenceId (graceEvent: GraceEvent) =
        match graceEvent with
        | GraceEvent.ValidationResultEvent validationResultEvent ->
            validationResultEvent.Metadata.CorrelationId = correlationId
            && match validationResultEvent.Event with
               | ValidationResultEventType.Recorded result ->
                   result.ValidationName.Equals(validationName, StringComparison.OrdinalIgnoreCase)
                   && result.OwnerId = ownerId
                   && result.OrganizationId = organizationId
                   && result.RepositoryId = repositoryId
                   && result.Output.Status = ValidationStatus.Pass
                   && result.Output.Summary.Contains(getDiscriminatedUnionCaseName referenceType, StringComparison.Ordinal)
                   && result.Output.Summary.Contains($"referenceId={referenceId}", StringComparison.Ordinal)
                   && result.Output.ArtifactIds = []
        | _ -> false

    /// Defines reference event matches behavior for the surrounding tests used by the server integration eventing Integration scenario.
    let referenceEventMatches correlationId (graceEvent: GraceEvent) =
        match graceEvent with
        | GraceEvent.ReferenceEvent referenceEvent -> referenceEvent.Metadata.CorrelationId = correlationId
        | _ -> false

    /// Defines promotion set applied event matches behavior for the surrounding tests used by the server integration eventing Integration scenario.
    let promotionSetAppliedEventMatches correlationId terminalReferenceId (graceEvent: GraceEvent) =
        match graceEvent with
        | GraceEvent.PromotionSetEvent promotionSetEvent ->
            promotionSetEvent.Metadata.CorrelationId = correlationId
            && match promotionSetEvent.Event with
               | PromotionSetEventType.Applied referenceId -> referenceId = terminalReferenceId
               | _ -> false
        | _ -> false

/// Covers service bus eventing scenarios.
[<NonParallelizable>]
type ServiceBusEventingIntegrationTests() =

    /// Verifies the service bus subscriber dispatches matching webhook once and does not block on outbound failure scenario.
    [<Test>]
    member _.ServiceBusSubscriberDispatchesMatchingWebhookOnceAndDoesNotBlockOnOutboundFailure() =
        task {
            let state = EventingIntegrationHelpers.hostState ()
            let repositoryId = Guid.Parse repositoryIds[0]
            let branchId = Guid.Parse repositoryDefaultBranchIds[0]
            let ownerGuid = Guid.Parse ownerId
            let organizationGuid = Guid.Parse organizationId
            let promotionSetId = Guid.NewGuid()
            let terminalReferenceId = Guid.NewGuid()
            let! rule = EventingIntegrationHelpers.createEnabledRuleAsync ownerGuid organizationGuid repositoryId branchId

            /// Defines grace event behavior for the surrounding tests used by the server integration eventing Integration scenario.
            let graceEvent, metadata =
                EventingIntegrationHelpers.promotionSetAppliedEvent
                    ownerGuid
                    organizationGuid
                    repositoryId
                    branchId
                    promotionSetId
                    terminalReferenceId
                    (generateCorrelationId ())

            let dedupeKey =
                String.Join(
                    ":",
                    [|
                        ExternalWebhookEventRegistry.PromotionSetAppliedName
                        "1"
                        $"{ownerGuid}"
                        $"{organizationGuid}"
                        $"{repositoryId}"
                        $"{branchId}"
                        $"{promotionSetId}"
                        $"{terminalReferenceId}"
                    |]
                )

            do! AspireTestHost.sendGraceEventAsync state graceEvent metadata
            let! firstDelivery = EventingIntegrationHelpers.waitForDeliveryAsync rule dedupeKey (TimeSpan.FromSeconds(30.0))

            Assert.That(
                firstDelivery.Status,
                Is
                    .EqualTo(WebhookDeliveryStatus.RetryScheduled)
                    .Or.EqualTo(WebhookDeliveryStatus.DeadLettered)
            )

            Assert.That(firstDelivery.AttemptCount, Is.EqualTo(1), "Local-only webhook failure should be attempted once by the subscriber.")
            Assert.That(firstDelivery.LastError, Is.Not.EqualTo(Microsoft.FSharp.Core.Option.None))
            Assert.That(firstDelivery.LastError.Value, Does.Not.Contain("secret"))

            /// Defines duplicate grace event behavior for the surrounding tests used by the server integration eventing Integration scenario.
            let duplicateGraceEvent, duplicateMetadata =
                EventingIntegrationHelpers.promotionSetAppliedEvent
                    ownerGuid
                    organizationGuid
                    repositoryId
                    branchId
                    promotionSetId
                    terminalReferenceId
                    (generateCorrelationId ())

            do! AspireTestHost.sendGraceEventAsync state duplicateGraceEvent duplicateMetadata

            let! _ =
                AspireTestHost.waitForGraceEventAsync
                    state
                    (TimeSpan.FromSeconds(15.0))
                    $"duplicate PromotionSet Applied echo for dedupe key {dedupeKey}"
                    (EventingIntegrationHelpers.promotionSetAppliedEventMatches duplicateMetadata.CorrelationId terminalReferenceId)

            do! EventingIntegrationHelpers.assertDeliveryCountRemainsAsync rule 1 (TimeSpan.FromSeconds(5.0))
        }

    /// Verifies the malformed service bus message does not block later quick scan work with retry order signal unavailable scenario.
    [<Test>]
    member _.MalformedServiceBusMessageDoesNotBlockLaterQuickScanWorkWithRetryOrderSignalUnavailable() =
        task {
            let state = EventingIntegrationHelpers.hostState ()
            let blockedCorrelationId = generateCorrelationId ()
            let malformedBody = """{ "not": "a GraceEvent" }"""
            let malformedMessageId = $"malformed-{Guid.NewGuid():N}"
            do! AspireTestHost.sendRawServiceBusMessageAsync state malformedBody malformedMessageId blockedCorrelationId

            let repositoryId = Guid.Parse repositoryIds[0]
            let branchId = Guid.Parse repositoryDefaultBranchIds[0]
            let correlationId = generateCorrelationId ()
            /// Defines grace event behavior for the surrounding tests used by the server integration eventing Integration scenario.
            let graceEvent, metadata, referenceId = EventingIntegrationHelpers.referenceCreatedEvent ReferenceType.Commit repositoryId branchId correlationId

            do! AspireTestHost.sendGraceEventAsync state graceEvent metadata

            let! _ =
                AspireTestHost.waitForGraceEventAsync
                    state
                    (TimeSpan.FromSeconds(45.0))
                    $"quick-scan ValidationResultEvent for ReferenceId {referenceId}"
                    (EventingIntegrationHelpers.validationResultMatches
                        correlationId
                        "quick-scan"
                        (Guid.Parse ownerId)
                        (Guid.Parse organizationId)
                        repositoryId
                        ReferenceType.Commit
                        referenceId)

            Assert.Pass(
                "Hosted fixture exposes no server-subscription abandon/retry ordering signal; this proves a malformed payload does not block a later valid quick-scan event."
            )
        }

    /// Verifies the derived quick scan records only supported reference events scenario.
    [<Test>]
    member _.DerivedQuickScanRecordsOnlySupportedReferenceEvents() =
        task {
            let state = EventingIntegrationHelpers.hostState ()
            let repositoryId = Guid.Parse repositoryIds[0]
            let branchId = Guid.Parse repositoryDefaultBranchIds[0]

            let supportedCorrelationId = generateCorrelationId ()

            /// Defines supported event behavior for the surrounding tests used by the server integration eventing Integration scenario.
            let supportedEvent, supportedMetadata, supportedReferenceId =
                EventingIntegrationHelpers.referenceCreatedEvent ReferenceType.Checkpoint repositoryId branchId supportedCorrelationId

            do! AspireTestHost.sendGraceEventAsync state supportedEvent supportedMetadata

            let! _ =
                AspireTestHost.waitForGraceEventAsync
                    state
                    (TimeSpan.FromSeconds(45.0))
                    $"quick-scan ValidationResultEvent for supported ReferenceId {supportedReferenceId}"
                    (EventingIntegrationHelpers.validationResultMatches
                        supportedCorrelationId
                        "quick-scan"
                        (Guid.Parse ownerId)
                        (Guid.Parse organizationId)
                        repositoryId
                        ReferenceType.Checkpoint
                        supportedReferenceId)

            let unsupportedCorrelationId = generateCorrelationId ()

            /// Defines unsupported event behavior for the surrounding tests used by the server integration eventing Integration scenario.
            let unsupportedEvent, unsupportedMetadata, unsupportedReferenceId =
                EventingIntegrationHelpers.referenceCreatedEvent ReferenceType.Save repositoryId branchId unsupportedCorrelationId

            do! AspireTestHost.sendGraceEventAsync state unsupportedEvent unsupportedMetadata

            let! _ =
                AspireTestHost.waitForGraceEventAsync
                    state
                    (TimeSpan.FromSeconds(15.0))
                    "unsupported save ReferenceEvent echo on the Service Bus test subscription"
                    (EventingIntegrationHelpers.referenceEventMatches unsupportedCorrelationId)

            let! unsupportedQuickScan =
                AspireTestHost.tryWaitForGraceEventAsync
                    state
                    (TimeSpan.FromSeconds(5.0))
                    (EventingIntegrationHelpers.validationResultMatches
                        unsupportedCorrelationId
                        "quick-scan"
                        (Guid.Parse ownerId)
                        (Guid.Parse organizationId)
                        repositoryId
                        ReferenceType.Save
                        unsupportedReferenceId)

            Assert.That(unsupportedQuickScan, Is.EqualTo(Microsoft.FSharp.Core.Option.None))
        }
