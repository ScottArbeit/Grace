namespace Grace.Server.Tests

open Grace.Server
open Grace.Server.WebhookDispatch
open Grace.Shared.Utilities
open Grace.Types
open Grace.Types.Events
open Grace.Types.PromotionSet
open Grace.Types.Reference
open Grace.Types.Common
open Grace.Types.Visibility
open Grace.Types.Webhooks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.FileProviders
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging.Abstractions
open NodaTime
open NUnit.Framework
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open System.Text.Json

/// Groups server unit test helpers for webhook Dispatch Test Helpers.
module private WebhookDispatchTestHelpers =

    /// Covers test Host Environment behavior in no-Aspire server unit tests.
    type TestHostEnvironment() =
        interface IHostEnvironment with
            /// Stores the test application Name value used by validation scenarios.
            member val ApplicationName = "Grace.Server.Tests" with get, set
            /// Stores the test environment Name value used by validation scenarios.
            member val EnvironmentName = Environments.Production with get, set
            /// Stores the test content Root Path value used by validation scenarios.
            member val ContentRootPath = Environment.CurrentDirectory with get, set
            /// Stores the test content Root File Provider value used by validation scenarios.
            member val ContentRootFileProvider = NullFileProvider() :> IFileProvider with get, set

    /// Covers recording Transport behavior in no-Aspire server unit tests.
    type RecordingTransport(results: OutboundWebhookResult list) =
        let requests = List<OutboundWebhookRequest>()
        let mutable remainingResults = results

        /// Exposes recorded outbound webhook requests for assertions.
        member _.Requests = requests |> Seq.toArray

        interface IOutboundWebhookTransport with
            /// Sends an outbound webhook request through the scripted test transport.
            member _.SendAsync(request, _) =
                task {
                    requests.Add request

                    match remainingResults with
                    | result :: tail ->
                        remainingResults <- tail
                        return result
                    | [] -> return OutboundWebhookResult.Succeeded 200
                }

    /// Covers throwing Transport behavior in no-Aspire server unit tests.
    type ThrowingTransport() =
        interface IOutboundWebhookTransport with
            /// Sends an outbound webhook request through the scripted test transport.
            member _.SendAsync(_, _) =
                task {
                    return
                        raise (
                            InvalidOperationException(
                                "simulated outbound failure token=super-secret-token url=https://example.test/hook?signature=signed-secret"
                            )
                        )
                }

    /// Covers scripted Transport behavior in no-Aspire server unit tests.
    type ScriptedTransport(steps: (OutboundWebhookRequest -> CancellationToken -> Task<OutboundWebhookResult>) list) =
        let requests = List<OutboundWebhookRequest>()
        let mutable remainingSteps = steps

        /// Exposes recorded outbound webhook requests for assertions.
        member _.Requests = requests |> Seq.toArray

        interface IOutboundWebhookTransport with
            /// Sends an outbound webhook request through the scripted test transport.
            member _.SendAsync(request, cancellationToken) =
                task {
                    requests.Add request

                    match remainingSteps with
                    | step :: tail ->
                        remainingSteps <- tail
                        return! step request cancellationToken
                    | [] -> return OutboundWebhookResult.Succeeded 200
                }

    /// Covers canceling Transport behavior in no-Aspire server unit tests.
    type CancelingTransport(cancellationTokenSource: CancellationTokenSource) =
        let requests = List<OutboundWebhookRequest>()

        /// Exposes recorded outbound webhook requests for assertions.
        member _.Requests = requests |> Seq.toArray

        interface IOutboundWebhookTransport with
            /// Sends an outbound webhook request through the scripted test transport.
            member _.SendAsync(request, cancellationToken) =
                task {
                    requests.Add request
                    cancellationTokenSource.Cancel()
                    return raise (OperationCanceledException(cancellationToken))
                }

    /// Covers cancel After Success Transport behavior in no-Aspire server unit tests.
    type CancelAfterSuccessTransport(cancellationTokenSource: CancellationTokenSource) =
        let requests = List<OutboundWebhookRequest>()

        /// Exposes recorded outbound webhook requests for assertions.
        member _.Requests = requests |> Seq.toArray

        interface IOutboundWebhookTransport with
            /// Sends an outbound webhook request through the scripted test transport.
            member _.SendAsync(request, _) =
                task {
                    requests.Add request
                    cancellationTokenSource.Cancel()
                    return OutboundWebhookResult.Succeeded 200
                }

    /// Covers blocking Success Transport behavior in no-Aspire server unit tests.
    type BlockingSuccessTransport() =
        let requests = ConcurrentBag<OutboundWebhookRequest>()
        let firstRequest = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        /// Exposes recorded outbound webhook requests for assertions.
        member _.Requests = requests.ToArray()

        /// Waits until the blocking transport records its first outbound request.
        member _.WaitForFirstRequestAsync() = firstRequest.Task

        /// Releases the blocking transport so the pending send can finish.
        member _.Release() = release.TrySetResult(()) |> ignore

        interface IOutboundWebhookTransport with
            /// Sends an outbound webhook request through the scripted test transport.
            member _.SendAsync(request, _) =
                task {
                    requests.Add request
                    firstRequest.TrySetResult(()) |> ignore

                    do! release.Task
                    return OutboundWebhookResult.Succeeded 200
                }

    let configuration: IConfiguration = ConfigurationBuilder().Build()

    let hostEnvironment = TestHostEnvironment() :> IHostEnvironment

    let logger = NullLogger.Instance

    /// Constructs metadata fixtures used by the server unit webhook Dispatch assertions.
    let metadata ownerId organizationId repositoryId targetBranchId promotionSetId correlationId =
        let properties = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        properties[nameof OwnerId] <- $"{ownerId}"
        properties[nameof OrganizationId] <- $"{organizationId}"
        properties[nameof RepositoryId] <- $"{repositoryId}"
        properties[nameof BranchId] <- $"{targetBranchId}"
        properties[nameof PromotionSetId] <- $"{promotionSetId}"
        properties["ActorId"] <- $"{promotionSetId}"

        {
            Timestamp = Instant.FromUtc(2026, 6, 2, 12, 0)
            CorrelationId = correlationId
            Principal = "tester"
            ClientType = Option.None
            Properties = properties
        }

    /// Builds applied Event test data for the server unit webhook Dispatch scenarios in this file.
    let appliedEvent ownerId organizationId repositoryId targetBranchId promotionSetId terminalReferenceId =
        let promotionSetEvent: PromotionSetEvent =
            {
                Event = PromotionSetEventType.Applied terminalReferenceId
                Metadata = metadata ownerId organizationId repositoryId targetBranchId promotionSetId "corr-webhook"
            }

        GraceEvent.PromotionSetEvent promotionSetEvent

    /// Builds a private applied event that must not reach external webhook fanout before reference reveal.
    let privateAppliedEvent ownerId organizationId repositoryId targetBranchId promotionSetId terminalReferenceId =
        let eventMetadata = metadata ownerId organizationId repositoryId targetBranchId promotionSetId "corr-webhook-private"
        eventMetadata.Properties[ "Visibility" ] <- $"{ResourceVisibility.Private}"
        eventMetadata.Properties[ "Ownership" ] <- $"{ResourceOwnership.ContributorOwned}"

        GraceEvent.PromotionSetEvent { Event = PromotionSetEventType.Applied terminalReferenceId; Metadata = eventMetadata }

    /// Builds a reveal event that is the first public publication point for a private PromotionSet terminal reference.
    let terminalReferenceRevealEvent ownerId organizationId repositoryId targetBranchId promotionSetId terminalReferenceId =
        let eventMetadata = metadata ownerId organizationId repositoryId targetBranchId promotionSetId "corr-webhook-reveal"
        eventMetadata.Properties[ nameof ReferenceId ] <- $"{terminalReferenceId}"
        eventMetadata.Properties[ "TerminalPromotionReferenceId" ] <- $"{terminalReferenceId}"
        eventMetadata.Properties[ "ReferenceRevealOperationId" ] <- "reveal-op-1"

        let referenceEvent: ReferenceEvent =
            {
                Event = ReferenceEventType.Revealed("reveal-op-1", "reviewer@example.test", "accepted", ResourceVisibility.Private, ResourceVisibility.Public)
                Metadata = eventMetadata
            }

        GraceEvent.ReferenceEvent referenceEvent

    /// Builds a non-terminal reference reveal event that must not publish PromotionSet apply.
    let genericReferenceRevealEvent ownerId organizationId repositoryId targetBranchId promotionSetId terminalReferenceId =
        let eventMetadata = metadata ownerId organizationId repositoryId targetBranchId promotionSetId "corr-webhook-generic-reveal"
        eventMetadata.Properties[ nameof ReferenceId ] <- $"{terminalReferenceId}"

        let referenceEvent: ReferenceEvent =
            {
                Event = ReferenceEventType.Revealed("reveal-op-1", "reviewer@example.test", "accepted", ResourceVisibility.Private, ResourceVisibility.Public)
                Metadata = eventMetadata
            }

        GraceEvent.ReferenceEvent referenceEvent

    /// Builds rule test data for the server unit webhook Dispatch scenarios in this file.
    let rule ruleId ownerId organizationId repositoryId targetBranchId =
        { WebhookRule.Default with
            WebhookRuleId = ruleId
            EventName = ExternalWebhookEventRegistry.PromotionSetAppliedName
            EventVersion = 1
            Scope = { OwnerId = ownerId; OrganizationId = organizationId; RepositoryId = repositoryId; TargetBranchId = targetBranchId }
            Url = { Url = "https://8.8.8.8/grace?token=secret"; Safety = OutboundUrlSafety.PublicHttps }
            SigningSecretVersion = "test-secret-v1"
            RetryPolicy = { MaxAttempts = 3; InitialDelaySeconds = 1; MaxDelaySeconds = 8 }
            Status = WebhookRuleStatus.Enabled
            CreatedBy = UserId "tester"
            CreatedAt = Instant.FromUtc(2026, 6, 2, 11, 0)
        }

    /// Builds dispatch test data for the server unit webhook Dispatch scenarios in this file.
    let dispatch transport graceEvent =
        WebhookDispatch.dispatchCommittedEventAsync logger configuration hostEnvironment transport graceEvent CancellationToken.None

/// Covers webhook Dispatch Unit behavior in no-Aspire server unit tests.
[<NonParallelizable>]
type WebhookDispatchUnitTests() =

    /// Verifies that set Up.
    [<SetUp>]
    member _.SetUp() = WebhookStore.clearForTests ()

    /// Verifies that committed Promotion Set Applied Creates Signed Versioned Delivery For Matching Rule.
    [<Test>]
    member _.CommittedPromotionSetAppliedCreatesSignedVersionedDeliveryForMatchingRule() =
        task {
            let ownerId = Guid.NewGuid()
            let organizationId = Guid.NewGuid()
            let repositoryId = Guid.NewGuid()
            let targetBranchId = Guid.NewGuid()
            let promotionSetId = Guid.NewGuid()
            let terminalReferenceId = Guid.NewGuid()
            let rule = WebhookDispatchTestHelpers.rule (Guid.NewGuid()) ownerId organizationId repositoryId (Option.Some targetBranchId)
            WebhookStore.upsertRule rule |> ignore

            let transport = WebhookDispatchTestHelpers.RecordingTransport([ OutboundWebhookResult.Succeeded 202 ])

            let! result =
                WebhookDispatchTestHelpers.dispatch
                    transport
                    (WebhookDispatchTestHelpers.appliedEvent ownerId organizationId repositoryId targetBranchId promotionSetId terminalReferenceId)

            Assert.That(result.DeliveryCount, Is.EqualTo(1))
            Assert.That(result.DeliveredCount, Is.EqualTo(1))
            Assert.That(transport.Requests, Has.Length.EqualTo(1))

            let request = transport.Requests[0]
            Assert.That(request.Headers["x-grace-webhook-event-name"], Is.EqualTo(ExternalWebhookEventRegistry.PromotionSetAppliedName))
            Assert.That(request.Headers["x-grace-webhook-event-version"], Is.EqualTo("1"))
            Assert.That(request.Headers["x-grace-webhook-signature"], Does.StartWith("sha256="))
            Assert.That(request.Headers["x-grace-webhook-signature"], Does.Not.Contain(rule.SigningSecretVersion))
            Assert.That(request.RedactedUrl, Does.Contain("token=REDACTED"))
            Assert.That(request.RedactedUrl, Does.Not.Contain("secret"))

            use payload = JsonDocument.Parse(request.PayloadJson)

            Assert.That(
                payload
                    .RootElement
                    .GetProperty("eventName")
                    .GetString(),
                Is.EqualTo(ExternalWebhookEventRegistry.PromotionSetAppliedName)
            )

            Assert.That(
                payload
                    .RootElement
                    .GetProperty("eventVersion")
                    .GetInt32(),
                Is.EqualTo(1)
            )

            Assert.That(
                payload
                    .RootElement
                    .GetProperty("promotionSetId")
                    .GetGuid(),
                Is.EqualTo(promotionSetId)
            )

            Assert.That(
                payload
                    .RootElement
                    .GetProperty("terminalPromotionReferenceId")
                    .GetGuid(),
                Is.EqualTo(terminalReferenceId)
            )

            Assert.That(request.PayloadJson, Does.Not.Contain("DataJson"))
            Assert.That(request.PayloadJson, Does.Not.Contain("AutomationEventEnvelope"))

            let deliveries = WebhookStore.listDeliveries rule.Scope rule.WebhookRuleId true
            Assert.That(deliveries, Has.Count.EqualTo(1))
            Assert.That(deliveries[0].Status, Is.EqualTo(WebhookDeliveryStatus.Succeeded))
            Assert.That(deliveries[0].AttemptCount, Is.EqualTo(1))
            Assert.That(deliveries[0].LastStatusCode, Is.EqualTo(Option.Some 202))
        }

    /// Verifies that private PromotionSet apply is suppressed before webhook dispatch fanout.
    [<Test>]
    member _.PrivatePromotionSetAppliedDoesNotCreateWebhookDelivery() =
        task {
            let ownerId = Guid.NewGuid()
            let organizationId = Guid.NewGuid()
            let repositoryId = Guid.NewGuid()
            let targetBranchId = Guid.NewGuid()
            let promotionSetId = Guid.NewGuid()
            let terminalReferenceId = Guid.NewGuid()
            let rule = WebhookDispatchTestHelpers.rule (Guid.NewGuid()) ownerId organizationId repositoryId (Option.Some targetBranchId)
            WebhookStore.upsertRule rule |> ignore

            let transport = WebhookDispatchTestHelpers.RecordingTransport([ OutboundWebhookResult.Succeeded 202 ])

            let! result =
                WebhookDispatchTestHelpers.dispatch
                    transport
                    (WebhookDispatchTestHelpers.privateAppliedEvent ownerId organizationId repositoryId targetBranchId promotionSetId terminalReferenceId)

            Assert.That(result.DeliveryCount, Is.EqualTo(0))
            Assert.That(result.DeliveredCount, Is.EqualTo(0))
            Assert.That(transport.Requests, Is.Empty)

            let deliveries = WebhookStore.listDeliveries rule.Scope rule.WebhookRuleId true
            Assert.That(deliveries, Is.Empty)
        }

    /// Verifies that terminal reference reveal publishes private PromotionSet apply exactly once.
    [<Test>]
    member _.TerminalReferenceRevealPublishesPrivatePromotionSetAppliedOnce() =
        task {
            let ownerId = Guid.NewGuid()
            let organizationId = Guid.NewGuid()
            let repositoryId = Guid.NewGuid()
            let targetBranchId = Guid.NewGuid()
            let promotionSetId = Guid.NewGuid()
            let terminalReferenceId = Guid.NewGuid()
            let rule = WebhookDispatchTestHelpers.rule (Guid.NewGuid()) ownerId organizationId repositoryId (Option.Some targetBranchId)
            WebhookStore.upsertRule rule |> ignore

            let transport =
                WebhookDispatchTestHelpers.RecordingTransport(
                    [
                        OutboundWebhookResult.Succeeded 202
                        OutboundWebhookResult.Succeeded 202
                    ]
                )

            let graceEvent =
                WebhookDispatchTestHelpers.terminalReferenceRevealEvent ownerId organizationId repositoryId targetBranchId promotionSetId terminalReferenceId

            let! first = WebhookDispatchTestHelpers.dispatch transport graceEvent
            let! second = WebhookDispatchTestHelpers.dispatch transport graceEvent

            Assert.That(first.DeliveryCount, Is.EqualTo(1))
            Assert.That(first.DeliveredCount, Is.EqualTo(1))
            Assert.That(second.SkippedDuplicateCount, Is.EqualTo(1))
            Assert.That(transport.Requests, Has.Length.EqualTo(1))

            use payload = JsonDocument.Parse(transport.Requests[0].PayloadJson)
            let root = payload.RootElement
            Assert.That(root.GetProperty("eventName").GetString(), Is.EqualTo(ExternalWebhookEventRegistry.PromotionSetAppliedName))
            Assert.That(root.GetProperty("promotionSetId").GetGuid(), Is.EqualTo(promotionSetId))

            Assert.That(
                root
                    .GetProperty("terminalPromotionReferenceId")
                    .GetGuid(),
                Is.EqualTo(terminalReferenceId)
            )
        }

    /// Verifies that generic reference reveal does not replay private PromotionSet history into webhooks.
    [<Test>]
    member _.GenericReferenceRevealDoesNotPublishPromotionSetApplied() =
        task {
            let ownerId = Guid.NewGuid()
            let organizationId = Guid.NewGuid()
            let repositoryId = Guid.NewGuid()
            let targetBranchId = Guid.NewGuid()
            let promotionSetId = Guid.NewGuid()
            let referenceId = Guid.NewGuid()
            let rule = WebhookDispatchTestHelpers.rule (Guid.NewGuid()) ownerId organizationId repositoryId (Option.Some targetBranchId)
            WebhookStore.upsertRule rule |> ignore

            let transport = WebhookDispatchTestHelpers.RecordingTransport([ OutboundWebhookResult.Succeeded 202 ])

            let! result =
                WebhookDispatchTestHelpers.dispatch
                    transport
                    (WebhookDispatchTestHelpers.genericReferenceRevealEvent ownerId organizationId repositoryId targetBranchId promotionSetId referenceId)

            Assert.That(result.DeliveryCount, Is.EqualTo(0))
            Assert.That(transport.Requests, Is.Empty)
        }

    /// Verifies that committed Delivery Headers Use Stable Sha256 Payload Digest And Hmac Preimage.
    [<Test>]
    member _.CommittedDeliveryHeadersUseStableSha256PayloadDigestAndHmacPreimage() =
        task {
            let ownerId = Guid.Parse("11111111-1111-1111-1111-111111111111")
            let organizationId = Guid.Parse("22222222-2222-2222-2222-222222222222")
            let repositoryId = Guid.Parse("33333333-3333-3333-3333-333333333333")
            let targetBranchId = Guid.Parse("44444444-4444-4444-4444-444444444444")
            let promotionSetId = Guid.Parse("55555555-5555-5555-5555-555555555555")
            let terminalReferenceId = Guid.Parse("66666666-6666-6666-6666-666666666666")

            let rule =
                WebhookDispatchTestHelpers.rule
                    (Guid.Parse("77777777-7777-7777-7777-777777777777"))
                    ownerId
                    organizationId
                    repositoryId
                    (Option.Some targetBranchId)

            WebhookStore.upsertRule rule |> ignore

            let transport = WebhookDispatchTestHelpers.RecordingTransport([ OutboundWebhookResult.Succeeded 202 ])

            let! result =
                WebhookDispatchTestHelpers.dispatch
                    transport
                    (WebhookDispatchTestHelpers.appliedEvent ownerId organizationId repositoryId targetBranchId promotionSetId terminalReferenceId)

            Assert.That(result.DeliveredCount, Is.EqualTo(1))
            let request = transport.Requests |> Array.exactlyOne
            let payloadBytes = Encoding.UTF8.GetBytes request.PayloadJson

            let payloadSha256 =
                SHA256.HashData payloadBytes
                |> Array.map (fun value -> value.ToString("x2", Globalization.CultureInfo.InvariantCulture))
                |> String.concat String.Empty

            Assert.That(request.Headers["x-grace-webhook-payload-sha256"], Is.EqualTo(payloadSha256))
            Assert.That(request.Headers["x-grace-webhook-payload-sha256"], Does.Match("^[a-f0-9]{64}$"))

            let deliveryId = request.Headers["x-grace-webhook-delivery-id"]
            let timestamp = request.Headers["x-grace-webhook-timestamp"]
            let keyId = request.Headers["x-grace-webhook-signature-key-id"]
            let signedMaterial = Encoding.UTF8.GetBytes($"{deliveryId}.{timestamp}.{keyId}.{payloadSha256}")

            use hmac = new HMACSHA256(Encoding.UTF8.GetBytes rule.SigningSecretVersion)

            let expectedSignature =
                hmac.ComputeHash signedMaterial
                |> Array.map (fun value -> value.ToString("x2", Globalization.CultureInfo.InvariantCulture))
                |> String.concat String.Empty

            Assert.That(keyId, Is.EqualTo(rule.SigningSecretVersion))
            Assert.That(request.Headers["x-grace-webhook-signature"], Is.EqualTo($"sha256={expectedSignature}"))

            let tamperedPayloadSha256 =
                SHA256.HashData(Encoding.UTF8.GetBytes(request.PayloadJson + " "))
                |> Array.map (fun value -> value.ToString("x2", Globalization.CultureInfo.InvariantCulture))
                |> String.concat String.Empty

            Assert.That(tamperedPayloadSha256, Is.Not.EqualTo(payloadSha256))
            Assert.That(Encoding.UTF8.GetString(signedMaterial), Is.EqualTo($"{deliveryId}.{timestamp}.{rule.SigningSecretVersion}.{payloadSha256}"))
        }

    /// Verifies that non Matching Rule And Non Canonical Reference Source Do Not Create Delivery.
    [<Test>]
    member _.NonMatchingRuleAndNonCanonicalReferenceSourceDoNotCreateDelivery() =
        task {
            let ownerId = Guid.NewGuid()
            let organizationId = Guid.NewGuid()
            let repositoryId = Guid.NewGuid()
            let matchingBranchId = Guid.NewGuid()
            let otherBranchId = Guid.NewGuid()
            let promotionSetId = Guid.NewGuid()
            let rule = WebhookDispatchTestHelpers.rule (Guid.NewGuid()) ownerId organizationId repositoryId (Option.Some otherBranchId)
            WebhookStore.upsertRule rule |> ignore

            let transport = WebhookDispatchTestHelpers.RecordingTransport([])

            let! result =
                WebhookDispatchTestHelpers.dispatch
                    transport
                    (WebhookDispatchTestHelpers.appliedEvent ownerId organizationId repositoryId matchingBranchId promotionSetId (Guid.NewGuid()))

            Assert.That(result.DeliveryCount, Is.EqualTo(0))
            Assert.That(transport.Requests, Is.Empty)

            let referenceEvent: ReferenceEvent =
                {
                    Event =
                        ReferenceEventType.Created(
                            Guid.NewGuid(),
                            ownerId,
                            organizationId,
                            repositoryId,
                            otherBranchId,
                            Guid.NewGuid(),
                            Sha256Hash String.Empty,
                            Blake3Hash String.Empty,
                            ReferenceType.Promotion,
                            "promotion",
                            [
                                ReferenceLinkType.PromotionSetTerminal promotionSetId
                            ]
                        )
                    Metadata = WebhookDispatchTestHelpers.metadata ownerId organizationId repositoryId otherBranchId promotionSetId "corr-reference"
                }

            let! referenceResult = WebhookDispatchTestHelpers.dispatch transport (GraceEvent.ReferenceEvent referenceEvent)
            Assert.That(referenceResult.DeliveryCount, Is.EqualTo(0))
            Assert.That(transport.Requests, Is.Empty)
        }

    /// Verifies that duplicate Committed Event Skips Existing Dedupe Key.
    [<Test>]
    member _.DuplicateCommittedEventSkipsExistingDedupeKey() =
        task {
            let ownerId = Guid.NewGuid()
            let organizationId = Guid.NewGuid()
            let repositoryId = Guid.NewGuid()
            let targetBranchId = Guid.NewGuid()
            let promotionSetId = Guid.NewGuid()
            let terminalReferenceId = Guid.NewGuid()
            let rule = WebhookDispatchTestHelpers.rule (Guid.NewGuid()) ownerId organizationId repositoryId Option.None
            WebhookStore.upsertRule rule |> ignore

            let transport =
                WebhookDispatchTestHelpers.RecordingTransport(
                    [
                        OutboundWebhookResult.Succeeded 200
                        OutboundWebhookResult.Succeeded 200
                    ]
                )

            let graceEvent = WebhookDispatchTestHelpers.appliedEvent ownerId organizationId repositoryId targetBranchId promotionSetId terminalReferenceId
            let! first = WebhookDispatchTestHelpers.dispatch transport graceEvent
            let! second = WebhookDispatchTestHelpers.dispatch transport graceEvent

            Assert.That(first.DeliveryCount, Is.EqualTo(1))
            Assert.That(second.SkippedDuplicateCount, Is.EqualTo(1))
            Assert.That(transport.Requests, Has.Length.EqualTo(1))

            let deliveries = WebhookStore.listDeliveries rule.Scope rule.WebhookRuleId true
            Assert.That(deliveries, Has.Count.EqualTo(1))
        }

    /// Verifies that concurrent Duplicate Committed Event Only Sends Once For Dedupe Key.
    [<Test>]
    member _.ConcurrentDuplicateCommittedEventOnlySendsOnceForDedupeKey() =
        task {
            let ownerId = Guid.NewGuid()
            let organizationId = Guid.NewGuid()
            let repositoryId = Guid.NewGuid()
            let targetBranchId = Guid.NewGuid()
            let promotionSetId = Guid.NewGuid()
            let terminalReferenceId = Guid.NewGuid()
            let rule = WebhookDispatchTestHelpers.rule (Guid.NewGuid()) ownerId organizationId repositoryId Option.None
            WebhookStore.upsertRule rule |> ignore

            let transport = WebhookDispatchTestHelpers.BlockingSuccessTransport()
            let graceEvent = WebhookDispatchTestHelpers.appliedEvent ownerId organizationId repositoryId targetBranchId promotionSetId terminalReferenceId

            let dispatches =
                [|
                    for _ in 1..16 -> WebhookDispatchTestHelpers.dispatch transport graceEvent
                |]

            do! transport.WaitForFirstRequestAsync()
            transport.Release()

            let! results = Task.WhenAll dispatches

            Assert.That(
                results
                |> Array.sumBy (fun result -> result.DeliveryCount),
                Is.EqualTo(1)
            )

            Assert.That(
                results
                |> Array.sumBy (fun result -> result.DeliveredCount),
                Is.EqualTo(1)
            )

            Assert.That(
                results
                |> Array.sumBy (fun result -> result.SkippedDuplicateCount),
                Is.EqualTo(15)
            )

            Assert.That(transport.Requests, Has.Length.EqualTo(1))

            let deliveries = WebhookStore.listDeliveries rule.Scope rule.WebhookRuleId true
            Assert.That(deliveries, Has.Count.EqualTo(1))
            Assert.That(deliveries[0].Status, Is.EqualTo(WebhookDeliveryStatus.Succeeded))
        }

    /// Verifies that transient Failure Schedules Retry Without Blocking Committed Event Dispatch.
    [<Test>]
    member _.TransientFailureSchedulesRetryWithoutBlockingCommittedEventDispatch() =
        task {
            let ownerId = Guid.NewGuid()
            let organizationId = Guid.NewGuid()
            let repositoryId = Guid.NewGuid()
            let targetBranchId = Guid.NewGuid()
            let promotionSetId = Guid.NewGuid()

            let transientRule =
                { WebhookDispatchTestHelpers.rule (Guid.NewGuid()) ownerId organizationId repositoryId (Option.Some targetBranchId) with
                    RetryPolicy = { MaxAttempts = 3; InitialDelaySeconds = 1; MaxDelaySeconds = 8 }
                }

            WebhookStore.upsertRule transientRule |> ignore

            let transport =
                WebhookDispatchTestHelpers.RecordingTransport(
                    [
                        OutboundWebhookResult.TransientFailure(Option.Some 503, "service unavailable token=response-secret")
                        OutboundWebhookResult.Succeeded 200
                    ]
                )

            let! result =
                WebhookDispatchTestHelpers.dispatch
                    transport
                    (WebhookDispatchTestHelpers.appliedEvent ownerId organizationId repositoryId targetBranchId promotionSetId (Guid.NewGuid()))

            Assert.That(result.DeliveredCount, Is.EqualTo(0))
            Assert.That(result.FailedCount, Is.EqualTo(1))
            Assert.That(transport.Requests, Has.Length.EqualTo(1))
            let deliveries = WebhookStore.listDeliveries transientRule.Scope transientRule.WebhookRuleId true
            let delivery = deliveries[0]
            Assert.That(delivery.Status, Is.EqualTo(WebhookDeliveryStatus.RetryScheduled))
            Assert.That(delivery.AttemptCount, Is.EqualTo(1))
            Assert.That(delivery.NextAttemptAt, Is.Not.EqualTo(Option.None))
            Assert.That(delivery.LastError.Value, Does.Not.Contain("response-secret"))

            let! retryResult =
                WebhookDispatch.processScheduledRetriesAsync
                    WebhookDispatchTestHelpers.logger
                    WebhookDispatchTestHelpers.configuration
                    WebhookDispatchTestHelpers.hostEnvironment
                    transport
                    (delivery.NextAttemptAt.Value.Plus(Duration.FromSeconds(1.0)))
                    10
                    CancellationToken.None

            Assert.That(retryResult.DeliveredCount, Is.EqualTo(1))
            Assert.That(transport.Requests, Has.Length.EqualTo(2))
            let updatedDeliveries = WebhookStore.listDeliveries transientRule.Scope transientRule.WebhookRuleId true
            Assert.That(updatedDeliveries[0].Status, Is.EqualTo(WebhookDeliveryStatus.Succeeded))
            Assert.That(updatedDeliveries[0].AttemptCount, Is.EqualTo(2))
        }

    /// Verifies that timeout Like Cancellation Schedules Retry And Does Not Abort Committed Event Batch.
    [<Test>]
    member _.TimeoutLikeCancellationSchedulesRetryAndDoesNotAbortCommittedEventBatch() =
        task {
            let ownerId = Guid.NewGuid()
            let organizationId = Guid.NewGuid()
            let repositoryId = Guid.NewGuid()
            let targetBranchId = Guid.NewGuid()
            let promotionSetId = Guid.NewGuid()

            let timeoutRule =
                { WebhookDispatchTestHelpers.rule (Guid.NewGuid()) ownerId organizationId repositoryId (Option.Some targetBranchId) with
                    RetryPolicy = { MaxAttempts = 3; InitialDelaySeconds = 1; MaxDelaySeconds = 8 }
                }

            let successRule = WebhookDispatchTestHelpers.rule (Guid.NewGuid()) ownerId organizationId repositoryId (Option.Some targetBranchId)

            WebhookStore.upsertRule timeoutRule |> ignore
            WebhookStore.upsertRule successRule |> ignore

            let timeoutStep _ _ = task { return raise (TaskCanceledException("simulated request timeout")) }

            let successStep _ _ = task { return OutboundWebhookResult.Succeeded 200 }

            let transport = WebhookDispatchTestHelpers.ScriptedTransport([ timeoutStep; successStep ])

            let! result =
                WebhookDispatchTestHelpers.dispatch
                    transport
                    (WebhookDispatchTestHelpers.appliedEvent ownerId organizationId repositoryId targetBranchId promotionSetId (Guid.NewGuid()))

            Assert.That(result.DeliveryCount, Is.EqualTo(2))
            Assert.That(result.DeliveredCount, Is.EqualTo(1))
            Assert.That(result.FailedCount, Is.EqualTo(1))
            Assert.That(transport.Requests, Has.Length.EqualTo(2))

            let deliveries =
                WebhookStore.listDeliveries timeoutRule.Scope WebhookRuleId.Empty true
                |> Seq.toArray

            Assert.That(deliveries, Has.Length.EqualTo(2))

            Assert.That(
                deliveries
                |> Seq.filter (fun delivery -> delivery.Status = WebhookDeliveryStatus.Succeeded),
                Has.Exactly(1).Items
            )

            let retryDelivery =
                deliveries
                |> Seq.find (fun delivery -> delivery.Status = WebhookDeliveryStatus.RetryScheduled)

            Assert.That(retryDelivery.AttemptCount, Is.EqualTo(1))
            Assert.That(retryDelivery.LastError.Value, Does.Contain("simulated request timeout"))
            Assert.That(retryDelivery.NextAttemptAt, Is.Not.EqualTo(Option.None))
        }

    /// Verifies that committed Event Cancellation Between Rules Does Not Claim Or Send Next Rule.
    [<Test>]
    member _.CommittedEventCancellationBetweenRulesDoesNotClaimOrSendNextRule() =
        task {
            let ownerId = Guid.NewGuid()
            let organizationId = Guid.NewGuid()
            let repositoryId = Guid.NewGuid()
            let targetBranchId = Guid.NewGuid()
            let promotionSetId = Guid.NewGuid()

            let firstRule = WebhookDispatchTestHelpers.rule (Guid.NewGuid()) ownerId organizationId repositoryId (Option.Some targetBranchId)
            let secondRule = WebhookDispatchTestHelpers.rule (Guid.NewGuid()) ownerId organizationId repositoryId (Option.Some targetBranchId)

            WebhookStore.upsertRule firstRule |> ignore
            WebhookStore.upsertRule secondRule |> ignore

            use cancellationTokenSource = new CancellationTokenSource()
            let transport = WebhookDispatchTestHelpers.CancelAfterSuccessTransport(cancellationTokenSource)

            let operation =
                Func<Task> (fun () ->
                    task {
                        let! _ =
                            WebhookDispatch.dispatchCommittedEventAsync
                                WebhookDispatchTestHelpers.logger
                                WebhookDispatchTestHelpers.configuration
                                WebhookDispatchTestHelpers.hostEnvironment
                                transport
                                (WebhookDispatchTestHelpers.appliedEvent ownerId organizationId repositoryId targetBranchId promotionSetId (Guid.NewGuid()))
                                cancellationTokenSource.Token

                        return ()
                    }
                    :> Task)

            Assert.ThrowsAsync<OperationCanceledException>(operation)
            |> ignore

            Assert.That(transport.Requests, Has.Length.EqualTo(1))

            let deliveries =
                WebhookStore.listDeliveries firstRule.Scope WebhookRuleId.Empty true
                |> Seq.toArray

            Assert.That(deliveries, Has.Length.EqualTo(1))
            Assert.That(deliveries[0].Status, Is.EqualTo(WebhookDeliveryStatus.Succeeded))
            Assert.That(deliveries[0].AttemptCount, Is.EqualTo(1))
        }

    /// Verifies that scheduled Retry Uses Original Rule Snapshot After Rule Url And Signing Update.
    [<Test>]
    member _.ScheduledRetryUsesOriginalRuleSnapshotAfterRuleUrlAndSigningUpdate() =
        task {
            let ownerId = Guid.NewGuid()
            let organizationId = Guid.NewGuid()
            let repositoryId = Guid.NewGuid()
            let targetBranchId = Guid.NewGuid()
            let promotionSetId = Guid.NewGuid()

            let originalRule =
                { WebhookDispatchTestHelpers.rule (Guid.NewGuid()) ownerId organizationId repositoryId (Option.Some targetBranchId) with
                    Url = { Url = "https://8.8.8.8/original?token=original-secret"; Safety = OutboundUrlSafety.PublicHttps }
                    SigningSecretVersion = "original-secret-version"
                    RetryPolicy = { MaxAttempts = 3; InitialDelaySeconds = 1; MaxDelaySeconds = 8 }
                }

            WebhookStore.upsertRule originalRule |> ignore

            let transport =
                WebhookDispatchTestHelpers.RecordingTransport(
                    [
                        OutboundWebhookResult.TransientFailure(Option.Some 503, "service unavailable")
                        OutboundWebhookResult.Succeeded 200
                    ]
                )

            let! result =
                WebhookDispatchTestHelpers.dispatch
                    transport
                    (WebhookDispatchTestHelpers.appliedEvent ownerId organizationId repositoryId targetBranchId promotionSetId (Guid.NewGuid()))

            Assert.That(result.FailedCount, Is.EqualTo(1))
            let delivery = (WebhookStore.listDeliveries originalRule.Scope originalRule.WebhookRuleId true)[0]

            let updatedRule =
                { originalRule with
                    Url = { Url = "https://8.8.4.4/updated?token=updated-secret"; Safety = OutboundUrlSafety.PublicHttps }
                    SigningSecretVersion = "updated-secret-version"
                }

            WebhookStore.upsertRule updatedRule |> ignore

            let! retryResult =
                WebhookDispatch.processScheduledRetriesAsync
                    WebhookDispatchTestHelpers.logger
                    WebhookDispatchTestHelpers.configuration
                    WebhookDispatchTestHelpers.hostEnvironment
                    transport
                    (delivery.NextAttemptAt.Value.Plus(Duration.FromSeconds(1.0)))
                    10
                    CancellationToken.None

            Assert.That(retryResult.DeliveredCount, Is.EqualTo(1))
            Assert.That(transport.Requests, Has.Length.EqualTo(2))
            Assert.That(transport.Requests[1].Url.AbsoluteUri, Is.EqualTo("https://8.8.8.8/original?token=original-secret"))
            Assert.That(transport.Requests[1].Headers["x-grace-webhook-signature-key-id"], Is.EqualTo("original-secret-version"))
        }

    /// Verifies that scheduled Retry Uses Original Rule Snapshot After Live Rule Is Disabled Or Deleted.
    [<Test>]
    member _.ScheduledRetryUsesOriginalRuleSnapshotAfterLiveRuleIsDisabledOrDeleted() =
        task {
            let ownerId = Guid.NewGuid()
            let organizationId = Guid.NewGuid()
            let repositoryId = Guid.NewGuid()
            let targetBranchId = Guid.NewGuid()
            let promotionSetId = Guid.NewGuid()

            let originalRule =
                { WebhookDispatchTestHelpers.rule (Guid.NewGuid()) ownerId organizationId repositoryId (Option.Some targetBranchId) with
                    RetryPolicy = { MaxAttempts = 4; InitialDelaySeconds = 1; MaxDelaySeconds = 8 }
                }

            WebhookStore.upsertRule originalRule |> ignore

            let transport =
                WebhookDispatchTestHelpers.RecordingTransport(
                    [
                        OutboundWebhookResult.TransientFailure(Option.Some 503, "service unavailable")
                        OutboundWebhookResult.TransientFailure(Option.Some 503, "service unavailable")
                        OutboundWebhookResult.Succeeded 200
                    ]
                )

            let! result =
                WebhookDispatchTestHelpers.dispatch
                    transport
                    (WebhookDispatchTestHelpers.appliedEvent ownerId organizationId repositoryId targetBranchId promotionSetId (Guid.NewGuid()))

            Assert.That(result.FailedCount, Is.EqualTo(1))
            let firstDelivery = (WebhookStore.listDeliveries originalRule.Scope originalRule.WebhookRuleId true)[0]

            WebhookStore.upsertRule { originalRule with Status = WebhookRuleStatus.Disabled }
            |> ignore

            let! disabledRetryResult =
                WebhookDispatch.processScheduledRetriesAsync
                    WebhookDispatchTestHelpers.logger
                    WebhookDispatchTestHelpers.configuration
                    WebhookDispatchTestHelpers.hostEnvironment
                    transport
                    (firstDelivery.NextAttemptAt.Value.Plus(Duration.FromSeconds(1.0)))
                    10
                    CancellationToken.None

            Assert.That(disabledRetryResult.RescheduledCount, Is.EqualTo(1))
            let secondDelivery = (WebhookStore.listDeliveries originalRule.Scope originalRule.WebhookRuleId true)[0]
            Assert.That(secondDelivery.Status, Is.EqualTo(WebhookDeliveryStatus.RetryScheduled))
            Assert.That(secondDelivery.AttemptCount, Is.EqualTo(2))

            WebhookStore.upsertRule { originalRule with Status = WebhookRuleStatus.Deleted }
            |> ignore

            let! deletedRetryResult =
                WebhookDispatch.processScheduledRetriesAsync
                    WebhookDispatchTestHelpers.logger
                    WebhookDispatchTestHelpers.configuration
                    WebhookDispatchTestHelpers.hostEnvironment
                    transport
                    (secondDelivery.NextAttemptAt.Value.Plus(Duration.FromSeconds(1.0)))
                    10
                    CancellationToken.None

            Assert.That(deletedRetryResult.DeliveredCount, Is.EqualTo(1))
            Assert.That(transport.Requests, Has.Length.EqualTo(3))
            Assert.That(transport.Requests[1].Url.AbsoluteUri, Is.EqualTo(originalRule.Url.Url))
            Assert.That(transport.Requests[2].Url.AbsoluteUri, Is.EqualTo(originalRule.Url.Url))
        }

    /// Verifies that drain Scheduled Retries Once Processes Due Retry Batch.
    [<Test>]
    member _.DrainScheduledRetriesOnceProcessesDueRetryBatch() =
        task {
            let ownerId = Guid.NewGuid()
            let organizationId = Guid.NewGuid()
            let repositoryId = Guid.NewGuid()
            let targetBranchId = Guid.NewGuid()
            let promotionSetId = Guid.NewGuid()

            let rule = WebhookDispatchTestHelpers.rule (Guid.NewGuid()) ownerId organizationId repositoryId (Option.Some targetBranchId)
            WebhookStore.upsertRule rule |> ignore

            let transport =
                WebhookDispatchTestHelpers.RecordingTransport(
                    [
                        OutboundWebhookResult.TransientFailure(Option.Some 503, "service unavailable")
                        OutboundWebhookResult.Succeeded 200
                    ]
                )

            let! dispatchResult =
                WebhookDispatchTestHelpers.dispatch
                    transport
                    (WebhookDispatchTestHelpers.appliedEvent ownerId organizationId repositoryId targetBranchId promotionSetId (Guid.NewGuid()))

            Assert.That(dispatchResult.FailedCount, Is.EqualTo(1))
            let delivery = (WebhookStore.listDeliveries rule.Scope rule.WebhookRuleId true)[0]

            WebhookStore.upsertDelivery
                { delivery with
                    NextAttemptAt =
                        Some(
                            getCurrentInstant()
                                .Minus(Duration.FromSeconds(1.0))
                        )
                }
            |> ignore

            let! retryResult =
                WebhookDispatch.drainScheduledRetriesOnceAsync
                    WebhookDispatchTestHelpers.logger
                    WebhookDispatchTestHelpers.configuration
                    WebhookDispatchTestHelpers.hostEnvironment
                    transport
                    10
                    CancellationToken.None

            Assert.That(retryResult.DeliveredCount, Is.EqualTo(1))
            Assert.That(transport.Requests, Has.Length.EqualTo(2))
        }

    /// Verifies that scheduled Retry Exhaustion Dead Letters Without Blocking Source Workflow.
    [<Test>]
    member _.ScheduledRetryExhaustionDeadLettersWithoutBlockingSourceWorkflow() =
        task {
            let ownerId = Guid.NewGuid()
            let organizationId = Guid.NewGuid()
            let repositoryId = Guid.NewGuid()
            let targetBranchId = Guid.NewGuid()
            let promotionSetId = Guid.NewGuid()

            let transientRule =
                { WebhookDispatchTestHelpers.rule (Guid.NewGuid()) ownerId organizationId repositoryId (Option.Some targetBranchId) with
                    RetryPolicy = { MaxAttempts = 2; InitialDelaySeconds = 1; MaxDelaySeconds = 8 }
                }

            let exhaustedRule = { transientRule with WebhookRuleId = Guid.NewGuid() }

            WebhookStore.upsertRule exhaustedRule |> ignore

            let exhaustedTransport =
                WebhookDispatchTestHelpers.RecordingTransport(
                    [
                        OutboundWebhookResult.TransientFailure(Option.Some 503, "service unavailable")
                        OutboundWebhookResult.TransientFailure(Option.Some 503, "service unavailable")
                    ]
                )

            let! exhaustedResult =
                WebhookDispatchTestHelpers.dispatch
                    exhaustedTransport
                    (WebhookDispatchTestHelpers.appliedEvent ownerId organizationId repositoryId targetBranchId promotionSetId (Guid.NewGuid()))

            Assert.That(exhaustedResult.FailedCount, Is.EqualTo(1))
            Assert.That(exhaustedTransport.Requests, Has.Length.EqualTo(1))
            let exhaustedDeliveries = WebhookStore.listDeliveries exhaustedRule.Scope exhaustedRule.WebhookRuleId true
            let exhaustedDelivery = exhaustedDeliveries[0]
            Assert.That(exhaustedDelivery.Status, Is.EqualTo(WebhookDeliveryStatus.RetryScheduled))
            Assert.That(exhaustedDelivery.AttemptCount, Is.EqualTo(1))

            let! retryResult =
                WebhookDispatch.processScheduledRetriesAsync
                    WebhookDispatchTestHelpers.logger
                    WebhookDispatchTestHelpers.configuration
                    WebhookDispatchTestHelpers.hostEnvironment
                    exhaustedTransport
                    (exhaustedDelivery.NextAttemptAt.Value.Plus(Duration.FromSeconds(1.0)))
                    10
                    CancellationToken.None

            Assert.That(retryResult.FailedCount, Is.EqualTo(1))
            Assert.That(exhaustedTransport.Requests, Has.Length.EqualTo(2))
            let retriedDeliveries = WebhookStore.listDeliveries exhaustedRule.Scope exhaustedRule.WebhookRuleId true
            let retriedDelivery = retriedDeliveries[0]
            Assert.That(retriedDelivery.Status, Is.EqualTo(WebhookDeliveryStatus.DeadLettered))
            Assert.That(retriedDelivery.AttemptCount, Is.EqualTo(2))
        }

    /// Verifies that scheduled Retry Cancellation Propagates Without Mutating Due Delivery.
    [<Test>]
    member _.ScheduledRetryCancellationPropagatesWithoutMutatingDueDelivery() =
        task {
            let ownerId = Guid.NewGuid()
            let organizationId = Guid.NewGuid()
            let repositoryId = Guid.NewGuid()
            let targetBranchId = Guid.NewGuid()
            let promotionSetId = Guid.NewGuid()

            let rule =
                { WebhookDispatchTestHelpers.rule (Guid.NewGuid()) ownerId organizationId repositoryId (Option.Some targetBranchId) with
                    RetryPolicy = { MaxAttempts = 3; InitialDelaySeconds = 1; MaxDelaySeconds = 8 }
                }

            WebhookStore.upsertRule rule |> ignore

            let! dispatchResult =
                WebhookDispatchTestHelpers.dispatch
                    (WebhookDispatchTestHelpers.RecordingTransport(
                        [
                            OutboundWebhookResult.TransientFailure(Option.Some 503, "service unavailable")
                        ]
                    ))
                    (WebhookDispatchTestHelpers.appliedEvent ownerId organizationId repositoryId targetBranchId promotionSetId (Guid.NewGuid()))

            Assert.That(dispatchResult.FailedCount, Is.EqualTo(1))

            let scheduledDelivery = (WebhookStore.listDeliveries rule.Scope rule.WebhookRuleId true)[0]

            let dueDelivery =
                { scheduledDelivery with
                    NextAttemptAt =
                        Some(
                            getCurrentInstant()
                                .Minus(Duration.FromSeconds(1.0))
                        )
                }

            WebhookStore.upsertDelivery dueDelivery |> ignore

            use cancellationTokenSource = new CancellationTokenSource()
            let transport = WebhookDispatchTestHelpers.CancelingTransport(cancellationTokenSource)

            let operation =
                Func<Task> (fun () ->
                    task {
                        let! _ =
                            WebhookDispatch.processScheduledRetriesAsync
                                WebhookDispatchTestHelpers.logger
                                WebhookDispatchTestHelpers.configuration
                                WebhookDispatchTestHelpers.hostEnvironment
                                transport
                                (getCurrentInstant ())
                                10
                                cancellationTokenSource.Token

                        return ()
                    }
                    :> Task)

            Assert.ThrowsAsync<OperationCanceledException>(operation)
            |> ignore

            Assert.That(transport.Requests, Has.Length.EqualTo(1))

            let unchangedDelivery =
                WebhookStore.tryGetDelivery dueDelivery.WebhookDeliveryId
                |> Option.get

            Assert.That(unchangedDelivery.Status, Is.EqualTo(dueDelivery.Status))
            Assert.That(unchangedDelivery.AttemptCount, Is.EqualTo(dueDelivery.AttemptCount))
            Assert.That(unchangedDelivery.LastAttemptAt, Is.EqualTo(dueDelivery.LastAttemptAt))
            Assert.That(unchangedDelivery.LastStatusCode, Is.EqualTo(dueDelivery.LastStatusCode))
            Assert.That(unchangedDelivery.LastError, Is.EqualTo(dueDelivery.LastError))
            Assert.That(unchangedDelivery.NextAttemptAt, Is.EqualTo(dueDelivery.NextAttemptAt))
        }

    /// Verifies that scheduled Retry Cancellation Between Items Does Not Mutate Next Due Delivery.
    [<Test>]
    member _.ScheduledRetryCancellationBetweenItemsDoesNotMutateNextDueDelivery() =
        task {
            let ownerId = Guid.NewGuid()
            let organizationId = Guid.NewGuid()
            let repositoryId = Guid.NewGuid()
            let firstTargetBranchId = Guid.NewGuid()
            let secondTargetBranchId = Guid.NewGuid()
            let promotionSetId = Guid.NewGuid()

            let firstRule = WebhookDispatchTestHelpers.rule (Guid.NewGuid()) ownerId organizationId repositoryId (Option.Some firstTargetBranchId)

            let secondRule = WebhookDispatchTestHelpers.rule (Guid.NewGuid()) ownerId organizationId repositoryId (Option.Some secondTargetBranchId)

            WebhookStore.upsertRule firstRule |> ignore
            WebhookStore.upsertRule secondRule |> ignore

            let! firstDispatchResult =
                WebhookDispatchTestHelpers.dispatch
                    (WebhookDispatchTestHelpers.RecordingTransport(
                        [
                            OutboundWebhookResult.TransientFailure(Option.Some 503, "service unavailable")
                        ]
                    ))
                    (WebhookDispatchTestHelpers.appliedEvent ownerId organizationId repositoryId firstTargetBranchId promotionSetId (Guid.NewGuid()))

            let! secondDispatchResult =
                WebhookDispatchTestHelpers.dispatch
                    (WebhookDispatchTestHelpers.RecordingTransport(
                        [
                            OutboundWebhookResult.TransientFailure(Option.Some 503, "service unavailable")
                        ]
                    ))
                    (WebhookDispatchTestHelpers.appliedEvent ownerId organizationId repositoryId secondTargetBranchId promotionSetId (Guid.NewGuid()))

            Assert.That(firstDispatchResult.FailedCount, Is.EqualTo(1))
            Assert.That(secondDispatchResult.FailedCount, Is.EqualTo(1))

            let now = getCurrentInstant ()

            let firstDueDelivery =
                { (WebhookStore.listDeliveries firstRule.Scope firstRule.WebhookRuleId true)[0] with
                    NextAttemptAt = Some(now.Minus(Duration.FromSeconds(2.0)))
                }

            let secondDueDelivery =
                { (WebhookStore.listDeliveries secondRule.Scope secondRule.WebhookRuleId true)[0] with
                    NextAttemptAt = Some(now.Minus(Duration.FromSeconds(1.0)))
                }

            WebhookStore.upsertDelivery firstDueDelivery
            |> ignore

            WebhookStore.upsertDelivery secondDueDelivery
            |> ignore

            use cancellationTokenSource = new CancellationTokenSource()
            let transport = WebhookDispatchTestHelpers.CancelAfterSuccessTransport(cancellationTokenSource)

            let operation =
                Func<Task> (fun () ->
                    task {
                        let! _ =
                            WebhookDispatch.processScheduledRetriesAsync
                                WebhookDispatchTestHelpers.logger
                                WebhookDispatchTestHelpers.configuration
                                WebhookDispatchTestHelpers.hostEnvironment
                                transport
                                now
                                10
                                cancellationTokenSource.Token

                        return ()
                    }
                    :> Task)

            Assert.ThrowsAsync<OperationCanceledException>(operation)
            |> ignore

            Assert.That(transport.Requests, Has.Length.EqualTo(1))

            let firstRetriedDelivery =
                WebhookStore.tryGetDelivery firstDueDelivery.WebhookDeliveryId
                |> Option.get

            Assert.That(firstRetriedDelivery.Status, Is.EqualTo(WebhookDeliveryStatus.Succeeded))
            Assert.That(firstRetriedDelivery.AttemptCount, Is.EqualTo(2))

            let unchangedSecondDelivery =
                WebhookStore.tryGetDelivery secondDueDelivery.WebhookDeliveryId
                |> Option.get

            Assert.That(unchangedSecondDelivery.Status, Is.EqualTo(secondDueDelivery.Status))
            Assert.That(unchangedSecondDelivery.AttemptCount, Is.EqualTo(secondDueDelivery.AttemptCount))
            Assert.That(unchangedSecondDelivery.LastAttemptAt, Is.EqualTo(secondDueDelivery.LastAttemptAt))
            Assert.That(unchangedSecondDelivery.LastStatusCode, Is.EqualTo(secondDueDelivery.LastStatusCode))
            Assert.That(unchangedSecondDelivery.LastError, Is.EqualTo(secondDueDelivery.LastError))
            Assert.That(unchangedSecondDelivery.NextAttemptAt, Is.EqualTo(secondDueDelivery.NextAttemptAt))
        }

    /// Verifies that scheduled Retry Transport Exception Is Redacted And Dead Lettered At Max Attempts.
    [<Test>]
    member _.ScheduledRetryTransportExceptionIsRedactedAndDeadLetteredAtMaxAttempts() =
        task {
            let ownerId = Guid.NewGuid()
            let organizationId = Guid.NewGuid()
            let repositoryId = Guid.NewGuid()
            let targetBranchId = Guid.NewGuid()
            let promotionSetId = Guid.NewGuid()

            let rule =
                { WebhookDispatchTestHelpers.rule (Guid.NewGuid()) ownerId organizationId repositoryId (Option.Some targetBranchId) with
                    RetryPolicy = { MaxAttempts = 2; InitialDelaySeconds = 1; MaxDelaySeconds = 8 }
                }

            WebhookStore.upsertRule rule |> ignore

            let! dispatchResult =
                WebhookDispatchTestHelpers.dispatch
                    (WebhookDispatchTestHelpers.RecordingTransport(
                        [
                            OutboundWebhookResult.TransientFailure(Option.Some 503, "service unavailable")
                        ]
                    ))
                    (WebhookDispatchTestHelpers.appliedEvent ownerId organizationId repositoryId targetBranchId promotionSetId (Guid.NewGuid()))

            Assert.That(dispatchResult.FailedCount, Is.EqualTo(1))

            let scheduledDelivery = (WebhookStore.listDeliveries rule.Scope rule.WebhookRuleId true)[0]

            WebhookStore.upsertDelivery
                { scheduledDelivery with
                    NextAttemptAt =
                        Some(
                            getCurrentInstant()
                                .Minus(Duration.FromSeconds(1.0))
                        )
                }
            |> ignore

            let! retryResult =
                WebhookDispatch.processScheduledRetriesAsync
                    WebhookDispatchTestHelpers.logger
                    WebhookDispatchTestHelpers.configuration
                    WebhookDispatchTestHelpers.hostEnvironment
                    (WebhookDispatchTestHelpers.ThrowingTransport())
                    (getCurrentInstant ())
                    10
                    CancellationToken.None

            Assert.That(retryResult.FailedCount, Is.EqualTo(1))

            let retriedDelivery = (WebhookStore.listDeliveries rule.Scope rule.WebhookRuleId true)[0]
            Assert.That(retriedDelivery.Status, Is.EqualTo(WebhookDeliveryStatus.DeadLettered))
            Assert.That(retriedDelivery.AttemptCount, Is.EqualTo(2))
            Assert.That(retriedDelivery.LastError.Value, Does.Not.Contain("super-secret-token"))
            Assert.That(retriedDelivery.LastError.Value, Does.Not.Contain("signed-secret"))
            Assert.That(retriedDelivery.LastError.Value, Does.Contain("token=REDACTED"))
            Assert.That(retriedDelivery.LastError.Value, Does.Contain("signature=REDACTED"))
        }

    /// Verifies that delivery Rejects Unsafe Url Without Calling Transport Or Leaking Secret Url.
    [<Test>]
    member _.DeliveryRejectsUnsafeUrlWithoutCallingTransportOrLeakingSecretUrl() =
        task {
            let ownerId = Guid.NewGuid()
            let organizationId = Guid.NewGuid()
            let repositoryId = Guid.NewGuid()
            let targetBranchId = Guid.NewGuid()
            let promotionSetId = Guid.NewGuid()

            let rule =
                { WebhookDispatchTestHelpers.rule (Guid.NewGuid()) ownerId organizationId repositoryId (Option.Some targetBranchId) with
                    Url = { Url = "https://127.0.0.1/hook?token=secret"; Safety = OutboundUrlSafety.PublicHttps }
                }

            WebhookStore.upsertRule rule |> ignore

            let transport = WebhookDispatchTestHelpers.RecordingTransport([])

            let! result =
                WebhookDispatchTestHelpers.dispatch
                    transport
                    (WebhookDispatchTestHelpers.appliedEvent ownerId organizationId repositoryId targetBranchId promotionSetId (Guid.NewGuid()))

            Assert.That(result.FailedCount, Is.EqualTo(1))
            Assert.That(transport.Requests, Is.Empty)
            let deliveries = WebhookStore.listDeliveries rule.Scope rule.WebhookRuleId true
            let delivery = deliveries[0]
            Assert.That(delivery.Status, Is.EqualTo(WebhookDeliveryStatus.DeadLettered))
            Assert.That(delivery.LastError.Value, Does.Contain("Outbound URL rejected"))
            Assert.That(delivery.LastError.Value, Does.Not.Contain("secret"))
        }

    /// Verifies that outbound Transport Exception Does Not Escape Committed Event Dispatch.
    [<Test>]
    member _.OutboundTransportExceptionDoesNotEscapeCommittedEventDispatch() =
        task {
            let ownerId = Guid.NewGuid()
            let organizationId = Guid.NewGuid()
            let repositoryId = Guid.NewGuid()
            let targetBranchId = Guid.NewGuid()
            let promotionSetId = Guid.NewGuid()

            let rule = WebhookDispatchTestHelpers.rule (Guid.NewGuid()) ownerId organizationId repositoryId (Option.Some targetBranchId)

            WebhookStore.upsertRule rule |> ignore

            let! result =
                WebhookDispatchTestHelpers.dispatch
                    (WebhookDispatchTestHelpers.ThrowingTransport())
                    (WebhookDispatchTestHelpers.appliedEvent ownerId organizationId repositoryId targetBranchId promotionSetId (Guid.NewGuid()))

            Assert.That(result.FailedCount, Is.EqualTo(1))

            let deliveries = WebhookStore.listDeliveries rule.Scope rule.WebhookRuleId true
            Assert.That(deliveries, Has.Count.EqualTo(1))
            Assert.That(deliveries[0].Status, Is.EqualTo(WebhookDeliveryStatus.RetryScheduled))
            Assert.That(deliveries[0].AttemptCount, Is.EqualTo(1))
            Assert.That(deliveries[0].LastError.Value, Does.Not.Contain("super-secret-token"))
            Assert.That(deliveries[0].LastError.Value, Does.Not.Contain("signed-secret"))
            Assert.That(deliveries[0].LastError.Value, Does.Contain("token=REDACTED"))
            Assert.That(deliveries[0].LastError.Value, Does.Contain("signature=REDACTED"))
        }

    /// Verifies that failed Response Error Is Redacted Before Persistence.
    [<Test>]
    member _.FailedResponseErrorIsRedactedBeforePersistence() =
        task {
            let ownerId = Guid.NewGuid()
            let organizationId = Guid.NewGuid()
            let repositoryId = Guid.NewGuid()
            let targetBranchId = Guid.NewGuid()
            let promotionSetId = Guid.NewGuid()

            let rule = WebhookDispatchTestHelpers.rule (Guid.NewGuid()) ownerId organizationId repositoryId (Option.Some targetBranchId)
            WebhookStore.upsertRule rule |> ignore

            let transport =
                WebhookDispatchTestHelpers.RecordingTransport(
                    [
                        OutboundWebhookResult.PermanentFailure(
                            Option.Some 400,
                            "bad response from https://example.test/hook?token=response-token&signature=response-signature secret=response-secret"
                        )
                    ]
                )

            let! result =
                WebhookDispatchTestHelpers.dispatch
                    transport
                    (WebhookDispatchTestHelpers.appliedEvent ownerId organizationId repositoryId targetBranchId promotionSetId (Guid.NewGuid()))

            Assert.That(result.FailedCount, Is.EqualTo(1))

            let deliveries = WebhookStore.listDeliveries rule.Scope rule.WebhookRuleId true
            Assert.That(deliveries, Has.Count.EqualTo(1))
            Assert.That(deliveries[0].Status, Is.EqualTo(WebhookDeliveryStatus.Failed))
            Assert.That(deliveries[0].LastError.Value, Does.Not.Contain("response-token"))
            Assert.That(deliveries[0].LastError.Value, Does.Not.Contain("response-signature"))
            Assert.That(deliveries[0].LastError.Value, Does.Not.Contain("response-secret"))
            Assert.That(deliveries[0].LastError.Value, Does.Contain("token=REDACTED"))
            Assert.That(deliveries[0].LastError.Value, Does.Contain("signature=REDACTED"))
            Assert.That(deliveries[0].LastError.Value, Does.Contain("secret=REDACTED"))
        }
