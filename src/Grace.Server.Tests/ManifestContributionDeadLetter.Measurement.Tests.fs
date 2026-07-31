namespace Grace.Server.Measurements

open Azure.Messaging.ServiceBus
open Grace.Server.Tests
open Grace.Server.Tests.Measurement
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types
open Grace.Types.Common
open Grace.Types.Events
open Grace.Types.Owner
open NUnit.Framework
open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Implements the broker-only operations for one isolated dead-letter measurement.
module private DeadLetterRuntime =

    let private receiveWindow = TimeSpan.FromSeconds(2.0)

    /// Reports missing selected-process inputs before any host, broker, or evidence side effect begins.
    let missingPrerequisites () =
        [|
            "GRACE_MCA_WORKTREE"
            "GRACE_MCA_HOSTED_COMMAND"
            "GRACE_MCA_EVIDENCE_ROOT"
        |]
        |> Array.filter (fun name -> String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable name))

    /// Sends one valid unrelated Grace event with a caller-owned exact broker identity.
    let sendUnrelatedGraceEventAsync (state: TestHostState) messageId =
        task {
            let correlationId = generateCorrelationId ()
            let metadata = EventMetadata.New (CorrelationId correlationId) "mca-dead-letter-witness"

            let graceEvent = GraceEvent.OwnerEvent { Event = OwnerEventType.Created(Guid.NewGuid(), $"McaDeadLetter{Guid.NewGuid():N}"); Metadata = metadata }

            let payload = JsonSerializer.SerializeToUtf8Bytes(graceEvent, Constants.JsonSerializerOptions)
            let message = ServiceBusMessage(payload)
            message.ContentType <- "application/json"
            message.Subject <- "GraceEvent"
            message.CorrelationId <- correlationId
            message.MessageId <- messageId
            message.ApplicationProperties[ "graceEventType" ] <- getDiscriminatedUnionFullName graceEvent

            use client = new ServiceBusClient(state.ServiceBusConnectionString)
            use sender = client.CreateSender(state.ServiceBusTopic)
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds(10.0))
            do! sender.SendMessageAsync(message, cts.Token)
        }

    /// Peeks a bounded broker snapshot without settling or locking any message.
    let peekMessageIdsAsync (receiver: ServiceBusReceiver) =
        task {
            let! messages = receiver.PeekMessagesAsync(100)

            return
                messages
                |> Seq.map (fun message -> message.MessageId)
                |> Seq.toArray
        }

    /// Receives one exact test-owned message before a bounded deadline and rejects any wrong identity.
    let receiveExactAsync (receiver: ServiceBusReceiver) expectedMessageId description =
        task {
            let timeoutAt = DateTime.UtcNow.AddSeconds(30.0)
            let mutable observed: ServiceBusReceivedMessage option = None

            while observed.IsNone && DateTime.UtcNow < timeoutAt do
                let! message = receiver.ReceiveMessageAsync(receiveWindow)

                if not (isNull message) then
                    if not (DeadLetter.identityMatches expectedMessageId message.MessageId) then
                        invalidOp $"{description} observed wrong message identity '{message.MessageId}'."

                    observed <- Some message

            return
                observed
                |> Option.defaultWith (fun () -> invalidOp $"Timed out waiting for exact {description} identity '{expectedMessageId}'.")
        }

    /// Completes the delivery-limit transition and returns exact active-boundary plus DLQ observations.
    let transitionAsync (state: TestHostState) expectedMessageId =
        task {
            use client = new ServiceBusClient(state.ServiceBusConnectionString)
            let activeOptions = ServiceBusReceiverOptions(ReceiveMode = ServiceBusReceiveMode.PeekLock)
            let deadLetterOptions = ServiceBusReceiverOptions(ReceiveMode = ServiceBusReceiveMode.PeekLock, SubQueue = SubQueue.DeadLetter)
            use activeReceiver = client.CreateReceiver(state.ServiceBusTopic, state.ServiceBusTestSubscription, activeOptions)
            use deadLetterReceiver = client.CreateReceiver(state.ServiceBusTopic, state.ServiceBusTestSubscription, deadLetterOptions)
            let mutable activeIdentityExact = true
            let mutable belowMaximum = false
            let mutable delivery = 1

            while delivery <= DeadLetter.MaximumDeliveryCount do
                let! message = receiveExactAsync activeReceiver expectedMessageId $"active delivery {delivery}"

                activeIdentityExact <-
                    activeIdentityExact
                    && message.DeliveryCount = delivery

                if delivery = DeadLetter.MaximumDeliveryCount then
                    let! deadLetterIds = peekMessageIdsAsync deadLetterReceiver

                    belowMaximum <- DeadLetter.belowMaximumRemainsActive expectedMessageId message.MessageId message.DeliveryCount deadLetterIds

                do! activeReceiver.AbandonMessageAsync(message)
                delivery <- delivery + 1

            let! deadLetterMessage = receiveExactAsync deadLetterReceiver expectedMessageId "dead-letter delivery"
            let deadLetterMessageId = deadLetterMessage.MessageId
            let deadLetterDeliveryCount = deadLetterMessage.DeliveryCount
            let deadLetterReason = deadLetterMessage.DeadLetterReason
            do! deadLetterReceiver.CompleteMessageAsync(deadLetterMessage)
            return activeIdentityExact, belowMaximum, deadLetterMessageId, deadLetterDeliveryCount, deadLetterReason
        }

    /// Peeks both subqueues after terminal settlement so the exact witness cannot obstruct later scenarios.
    let verifyCleanupAsync (state: TestHostState) expectedMessageId =
        task {
            use client = new ServiceBusClient(state.ServiceBusConnectionString)
            let activeOptions = ServiceBusReceiverOptions(ReceiveMode = ServiceBusReceiveMode.PeekLock)
            let deadLetterOptions = ServiceBusReceiverOptions(ReceiveMode = ServiceBusReceiveMode.PeekLock, SubQueue = SubQueue.DeadLetter)
            use activeReceiver = client.CreateReceiver(state.ServiceBusTopic, state.ServiceBusTestSubscription, activeOptions)
            use deadLetterReceiver = client.CreateReceiver(state.ServiceBusTopic, state.ServiceBusTestSubscription, deadLetterOptions)
            let! activeIds = peekMessageIdsAsync activeReceiver
            let! deadLetterIds = peekMessageIdsAsync deadLetterReceiver
            return DeadLetter.cleanupComplete expectedMessageId activeIds deadLetterIds
        }

    /// Settles an exact witness still present after a failed runtime phase without consuming unrelated identities.
    let cleanupWitnessAsync (state: TestHostState) expectedMessageId =
        task {
            use client = new ServiceBusClient(state.ServiceBusConnectionString)

            let cleanupReceiver subQueue =
                task {
                    let options = ServiceBusReceiverOptions(ReceiveMode = ServiceBusReceiveMode.PeekLock, SubQueue = subQueue)
                    use receiver = client.CreateReceiver(state.ServiceBusTopic, state.ServiceBusTestSubscription, options)
                    let timeoutAt = DateTime.UtcNow.AddSeconds(10.0)
                    let mutable settled = false

                    while not settled && DateTime.UtcNow < timeoutAt do
                        let! message = receiver.ReceiveMessageAsync(receiveWindow)

                        if isNull message then
                            settled <- true
                        elif DeadLetter.identityMatches expectedMessageId message.MessageId then
                            do! receiver.CompleteMessageAsync(message)
                            settled <- true
                        else
                            do! receiver.AbandonMessageAsync(message)
                            invalidOp $"Cleanup observed wrong isolated-subscription identity '{message.MessageId}'."
                }

            do! cleanupReceiver SubQueue.None
            do! cleanupReceiver SubQueue.DeadLetter
        }

/// Proves the isolated test-subscription maximum-delivery transition in one explicitly selected Aspire process.
[<NonParallelizable>]
type ManifestContributionDeadLetterMeasurementTests() =

    /// Emits truthful exact-identity, boundary, DLQ, telemetry, cleanup, and evidence results for one unrelated event.
    [<Test; Explicit("Run only through the focused MCA dead-letter measurement selector.")>]
    member _.``isolated broker witness reaches dead-letter delivery eleven``() =
        task {
            let missingPrerequisites = DeadLetterRuntime.missingPrerequisites ()

            if not (Array.isEmpty missingPrerequisites) then
                let missingText = String.Join(", ", missingPrerequisites)
                Assert.Ignore($"Skipped before side effects because selected-process prerequisites are missing: {missingText}")

            let runId = Guid.NewGuid().ToString("N")

            let worktree =
                BaselineRuntime.requireEnvironment "GRACE_MCA_WORKTREE"
                |> Path.GetFullPath

            let command = BaselineRuntime.requireEnvironment "GRACE_MCA_HOSTED_COMMAND"

            let evidenceRoot =
                BaselineRuntime.requireEnvironment "GRACE_MCA_EVIDENCE_ROOT"
                |> Path.GetFullPath

            let evidenceDirectory = Path.Combine(evidenceRoot, runId)
            let! commitSha = BaselineRuntime.runGitAsync worktree [| "rev-parse"; "HEAD" |]

            let! status =
                BaselineRuntime.runGitAsync
                    worktree
                    [|
                        "status"
                        "--porcelain=v1"
                        "--untracked-files=all"
                    |]

            let worktreeState = if String.IsNullOrWhiteSpace status then "clean" else status
            use writer = new EvidenceWriter(evidenceDirectory, BaselineRuntime.MaximumRecordBytes)
            let plan = [| "dead-letter" |]
            writer.Append(MeasurementRun.Create(runId, commitSha, worktree, worktreeState, command, evidenceDirectory, plan))
            let assertions = ResizeArray<MeasurementAssertion>()
            let failures = ResizeArray<string>()
            let mutable host: TestHostState option = None
            let mutable witnessMessageId: string option = None

            let recordAssertion assertionId passed detail =
                let assertion = MeasurementAssertion.Create(runId, "dead-letter", assertionId, passed, detail)
                assertions.Add assertion
                writer.Append assertion

            try
                let bootstrapUserId = Guid.NewGuid().ToString("D")
                let! state = AspireTestHost.startIsolatedAsync bootstrapUserId
                host <- Some state
                state.Client.DefaultRequestHeaders.Add("x-grace-user-id", bootstrapUserId)
                let! _ = AspireTestHost.drainServiceBusAsync state
                let testSubscriptionIsolated = not (state.ServiceBusTestSubscription.Equals(state.ServiceBusServerSubscription, StringComparison.Ordinal))

                recordAssertion
                    "dead-letter.test-subscription-isolated"
                    testSubscriptionIsolated
                    $"testSubscription={state.ServiceBusTestSubscription}; productionSubscriptionDistinct={testSubscriptionIsolated}"

                if not testSubscriptionIsolated then
                    invalidOp "The selected test subscription resolved to the production server subscription."

                let! baselineMetrics = BaselineRuntime.scrapeMetricsAsync state
                let messageId = $"mca-dead-letter-{runId}"
                witnessMessageId <- Some messageId
                do! DeadLetterRuntime.sendUnrelatedGraceEventAsync state messageId

                let! exactActiveIdentity, belowMaximum, deadLetterMessageId, deadLetterDeliveryCount, deadLetterReason =
                    DeadLetterRuntime.transitionAsync state messageId

                recordAssertion "dead-letter.message-identity-exact" exactActiveIdentity $"messageId={messageId}"
                recordAssertion "dead-letter.below-maximum-remains-active" belowMaximum $"delivery={DeadLetter.MaximumDeliveryCount}"
                recordAssertion "dead-letter.dlq-message-observed" true $"messageId={deadLetterMessageId}"

                recordAssertion
                    "dead-letter.delivery-count-eleven"
                    (deadLetterDeliveryCount = DeadLetter.DeadLetterDeliveryCount)
                    $"deliveryCount={deadLetterDeliveryCount}"

                let boundedReason = DeadLetter.boundedBrokerReason deadLetterReason

                recordAssertion
                    "dead-letter.reason-bounded-nonempty"
                    (DeadLetter.deadLetterObservationPasses messageId deadLetterMessageId deadLetterDeliveryCount deadLetterReason)
                    $"reason={boundedReason}"

                let! observedMetrics = BaselineRuntime.scrapeMetricsAsync state

                let telemetryUnchanged, telemetryDetail =
                    match OpenMetrics.evaluateCompletedSettlementUnchanged baselineMetrics observedMetrics with
                    | UnchangedEvaluation.Unchanged (messages, durations) -> true, $"messages={messages}; durations={durations}"
                    | UnchangedEvaluation.Changed reason
                    | UnchangedEvaluation.UnchangedInvalid reason -> false, reason

                recordAssertion "dead-letter.production-manifest-telemetry-unchanged" telemetryUnchanged telemetryDetail
            with
            | ex -> failures.Add(ex.ToString())

            match host, witnessMessageId with
            | Some state, Some messageId ->
                try
                    do! DeadLetterRuntime.cleanupWitnessAsync state messageId
                    let! cleanupComplete = DeadLetterRuntime.verifyCleanupAsync state messageId
                    recordAssertion "dead-letter.cleanup-complete" cleanupComplete $"messageId={messageId}; absent={cleanupComplete}"
                with
                | ex ->
                    failures.Add($"cleanup-witness: {ex}")
                    recordAssertion "dead-letter.cleanup-complete" false ex.Message
            | _ -> ()

            match host with
            | Some state ->
                try
                    do! AspireTestHost.stopIsolatedAsync state
                with
                | ex -> failures.Add($"cleanup: {ex}")
            | None -> ()

            if assertions
               |> Seq.exists (fun assertion -> assertion.AssertionId = "dead-letter.evidence-integrity")
               |> not then
                try
                    let valid = BaselineRuntime.verifyEvidenceIntegrity writer
                    recordAssertion "dead-letter.evidence-integrity" valid $"path={writer.Path}"
                with
                | ex -> recordAssertion "dead-letter.evidence-integrity" false ex.Message

            DeadLetter.requiredAssertionIds
            |> Array.iter (fun assertionId ->
                if assertions
                   |> Seq.exists (fun assertion -> assertion.AssertionId = assertionId)
                   |> not then
                    recordAssertion assertionId false "The runtime failed before this assertion could be evaluated.")

            let summary = ScenarioSummary.derive runId "dead-letter" DeadLetter.requiredAssertionIds (assertions.ToArray()) (failures.ToArray()) false

            writer.Append summary
            TestContext.Progress.WriteLine($"MCA dead-letter evidence directory: {evidenceDirectory}")
            TestContext.Progress.Flush()

            Assert.That(
                summary.Outcome,
                Is.EqualTo("Passed"),
                $"Evidence: {evidenceDirectory}{Environment.NewLine}{String.Join(Environment.NewLine, failures)}"
            )
        }
