namespace Grace.Server.Tests

open Grace.Server
open Grace.Server.WebhookDispatch
open Grace.Shared.Utilities
open Grace.Types
open Grace.Types.Events
open Grace.Types.PromotionSet
open Grace.Types.Reference
open Grace.Types.Common
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
open System.Threading
open System.Threading.Tasks
open System.Text.Json

module private WebhookDispatchTestHelpers =

    type TestHostEnvironment() =
        interface IHostEnvironment with
            member val ApplicationName = "Grace.Server.Tests" with get, set
            member val EnvironmentName = Environments.Production with get, set
            member val ContentRootPath = Environment.CurrentDirectory with get, set
            member val ContentRootFileProvider = NullFileProvider() :> IFileProvider with get, set

    type RecordingTransport(results: OutboundWebhookResult list) =
        let requests = List<OutboundWebhookRequest>()
        let mutable remainingResults = results

        member _.Requests = requests |> Seq.toArray

        interface IOutboundWebhookTransport with
            member _.SendAsync(request, _) =
                task {
                    requests.Add request

                    match remainingResults with
                    | result :: tail ->
                        remainingResults <- tail
                        return result
                    | [] -> return OutboundWebhookResult.Succeeded 200
                }

    type ThrowingTransport() =
        interface IOutboundWebhookTransport with
            member _.SendAsync(_, _) =
                task {
                    return
                        raise (
                            InvalidOperationException(
                                "simulated outbound failure token=super-secret-token url=https://example.test/hook?signature=signed-secret"
                            )
                        )
                }

    type ScriptedTransport(steps: (OutboundWebhookRequest -> CancellationToken -> Task<OutboundWebhookResult>) list) =
        let requests = List<OutboundWebhookRequest>()
        let mutable remainingSteps = steps

        member _.Requests = requests |> Seq.toArray

        interface IOutboundWebhookTransport with
            member _.SendAsync(request, cancellationToken) =
                task {
                    requests.Add request

                    match remainingSteps with
                    | step :: tail ->
                        remainingSteps <- tail
                        return! step request cancellationToken
                    | [] -> return OutboundWebhookResult.Succeeded 200
                }

    type CancelingTransport(cancellationTokenSource: CancellationTokenSource) =
        let requests = List<OutboundWebhookRequest>()

        member _.Requests = requests |> Seq.toArray

        interface IOutboundWebhookTransport with
            member _.SendAsync(request, cancellationToken) =
                task {
                    requests.Add request
                    cancellationTokenSource.Cancel()
                    return raise (OperationCanceledException(cancellationToken))
                }

    type CancelAfterSuccessTransport(cancellationTokenSource: CancellationTokenSource) =
        let requests = List<OutboundWebhookRequest>()

        member _.Requests = requests |> Seq.toArray

        interface IOutboundWebhookTransport with
            member _.SendAsync(request, _) =
                task {
                    requests.Add request
                    cancellationTokenSource.Cancel()
                    return OutboundWebhookResult.Succeeded 200
                }

    type BlockingSuccessTransport() =
        let requests = ConcurrentBag<OutboundWebhookRequest>()
        let firstRequest = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        member _.Requests = requests.ToArray()

        member _.WaitForFirstRequestAsync() = firstRequest.Task

        member _.Release() = release.TrySetResult(()) |> ignore

        interface IOutboundWebhookTransport with
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

    let appliedEvent ownerId organizationId repositoryId targetBranchId promotionSetId terminalReferenceId =
        let promotionSetEvent: PromotionSetEvent =
            {
                Event = PromotionSetEventType.Applied terminalReferenceId
                Metadata = metadata ownerId organizationId repositoryId targetBranchId promotionSetId "corr-webhook"
            }

        GraceEvent.PromotionSetEvent promotionSetEvent

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

    let dispatch transport graceEvent =
        WebhookDispatch.dispatchCommittedEventAsync logger configuration hostEnvironment transport graceEvent CancellationToken.None

[<NonParallelizable>]
type WebhookDispatchUnitTests() =

    [<SetUp>]
    member _.SetUp() = WebhookStore.clearForTests ()

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
