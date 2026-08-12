namespace Grace.Server.Tests

open Azure.Messaging.ServiceBus
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Types.Common
open Grace.Types.Usage
open Microsoft.Data.SqlClient
open NodaTime
open NUnit.Framework
open System
open System.Data
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Proves the dev/test operational usage tracer bullet through Service Bus, worker ingestion, SQL raw storage, and aggregate projection.
[<TestFixture>]
type OperationsTracerBulletServerTests() =

    /// Durable operations worker subscription created by AppHost for local tracer-bullet runs.
    [<Literal>]
    let operationsProcessorSubscriptionName = "operational-facts-processor"

    /// JSON media type used for operational usage fact Service Bus messages.
    [<Literal>]
    let operationalJsonContentType = "application/json"

    /// Service Bus subject used by operational usage fact messages.
    [<Literal>]
    let usageFactSubject = "GraceOperationalUsageFact"

    /// Application property that identifies the operational fact contract carried by the message.
    [<Literal>]
    let usageFactMessageTypeProperty = "graceMessageType"

    /// Application property value for operational usage fact messages.
    [<Literal>]
    let usageFactMessageType = "UsageFact"

    /// Application property that records the specific usage fact kind without parsing the payload.
    [<Literal>]
    let usageFactKindProperty = "usageFactKind"

    /// External operations raw-fact table verified by the server integration tracer bullet.
    [<Literal>]
    let operationsRawUsageFactTable = "ops.RawUsageFact"

    /// External operations aggregate table verified by the server integration tracer bullet.
    [<Literal>]
    let operationsUsageAggregateMinuteTable = "ops.UsageAggregateMinute"

    /// Operations journal table verified when proving dispatch recovery from durable Pending state.
    [<Literal>]
    let operationsUsageFactJournalTable = "ops.UsageFactJournal"

    /// Storage pool identifier width enforced by the operations database schema.
    [<Literal>]
    let operationsStoragePoolIdMaxLength = 256

    /// Bounded wait used while the worker consumes Service Bus messages and commits SQL rows.
    let proofTimeout = TimeSpan.FromSeconds(45.0)

    /// Builds a deterministic repository storage usage fact for the tracer-bullet proof.
    let usageFact usageFactId correlationId =
        UsageFact.RepositoryStorageBytesMinute(
            usageFactId,
            correlationId,
            Guid.Parse(ownerId),
            Guid.Parse(organizationId),
            Guid.Parse(repositoryIds[0]),
            StoragePoolId Constants.DefaultStoragePoolId,
            4096L,
            Instant.FromUtc(2026, 7, 4, 12, 34, 56)
        )

    /// Creates a Service Bus message that carries a usage fact through the same operational envelope consumed by the worker.
    let usageFactMessage messageId (fact: UsageFact) =
        let payload = JsonSerializer.SerializeToUtf8Bytes(fact, Constants.JsonSerializerOptions)
        let message = ServiceBusMessage(payload)
        message.ContentType <- operationalJsonContentType
        message.Subject <- usageFactSubject
        message.MessageId <- messageId
        message.CorrelationId <- fact.CorrelationId

        message.ApplicationProperties[ usageFactMessageTypeProperty ] <- usageFactMessageType

        message.ApplicationProperties[ usageFactKindProperty ] <- fact.FactKind.ToString()

        message

    /// Invokes the Operations-owned test executable so the AppHost tracer enters canonical production append without a root-to-Operations project reference.
    let appendJournalFactAsync (fact: UsageFact) =
        task {
            let projectPath =
                Path.GetFullPath(
                    Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Operations", "Grace.Operations.ProofHost", "Grace.Operations.ProofHost.fsproj")
                )

            let startInfo = ProcessStartInfo("dotnet")
            startInfo.UseShellExecute <- false
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.ArgumentList.Add("run")
            startInfo.ArgumentList.Add("--configuration")
            startInfo.ArgumentList.Add("Release")
            startInfo.ArgumentList.Add("--project")
            startInfo.ArgumentList.Add(projectPath)
            startInfo.ArgumentList.Add("--no-launch-profile")
            startInfo.ArgumentList.Add("--")
            startInfo.ArgumentList.Add(operationsSqlConnectionString)
            startInfo.ArgumentList.Add(Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(fact, Constants.JsonSerializerOptions)))

            use child = Process.Start(startInfo)

            if isNull child then
                failwith "The Operations append proof executable did not start."

            use timeout = new CancellationTokenSource(proofTimeout)
            let! output = child.StandardOutput.ReadToEndAsync(timeout.Token)
            let! error = child.StandardError.ReadToEndAsync(timeout.Token)
            do! child.WaitForExitAsync(timeout.Token)

            if
                child.ExitCode <> 0
                || not (output.Contains("appended-pending", StringComparison.Ordinal))
            then
                failwith $"The Operations append proof executable failed with exit code {child.ExitCode}. Output: {output}. Error: {error}"
        }

    /// Sends one raw message directly to the operational facts topic for duplicate and invalid-payload proof.
    let sendOperationalMessageAsync (message: ServiceBusMessage) =
        task {
            let client = ServiceBusClient(serviceBusConnectionString)
            use _client = client
            let sender = client.CreateSender(operationalFactsTopic)

            try
                use cts = new CancellationTokenSource(TimeSpan.FromSeconds(10.0))
                do! sender.SendMessageAsync(message, cts.Token)
            finally
                sender
                    .DisposeAsync()
                    .AsTask()
                    .GetAwaiter()
                    .GetResult()
        }

    /// Describes a peeked Service Bus message without exposing the raw usage payload.
    let describePeekedMessage (message: ServiceBusReceivedMessage) =
        let usageKind =
            match message.ApplicationProperties.TryGetValue usageFactKindProperty with
            | true, value when not (isNull value) -> string value
            | _ -> "<missing>"

        let deadLetterReason =
            if String.IsNullOrWhiteSpace message.DeadLetterReason then
                "<none>"
            else
                message.DeadLetterReason

        $"MessageId={message.MessageId}; CorrelationId={message.CorrelationId}; Subject={message.Subject}; UsageKind={usageKind}; DeliveryCount={message.DeliveryCount}; DeadLetterReason={deadLetterReason}"

    /// Peeks messages from the operations worker subscription without changing delivery state.
    let peekOperationalMessagesAsync subQueue =
        task {
            let client = ServiceBusClient(serviceBusConnectionString)
            use _client = client

            let receiverOptions =
                match subQueue with
                | Some queue -> ServiceBusReceiverOptions(SubQueue = queue, ReceiveMode = ServiceBusReceiveMode.PeekLock)
                | None -> ServiceBusReceiverOptions(ReceiveMode = ServiceBusReceiveMode.PeekLock)

            let receiver = client.CreateReceiver(operationalFactsTopic, operationsProcessorSubscriptionName, receiverOptions)

            use _receiver = receiver
            let! messages = receiver.PeekMessagesAsync(5)

            return
                messages
                |> Seq.map describePeekedMessage
                |> Seq.toList
        }

    /// Peeks message identities from the operations worker subscription without changing delivery state.
    let peekOperationalMessageIdsAsync subQueue =
        task {
            let client = ServiceBusClient(serviceBusConnectionString)
            use _client = client

            let receiverOptions =
                match subQueue with
                | Some queue -> ServiceBusReceiverOptions(SubQueue = queue, ReceiveMode = ServiceBusReceiveMode.PeekLock)
                | None -> ServiceBusReceiverOptions(ReceiveMode = ServiceBusReceiveMode.PeekLock)

            let receiver = client.CreateReceiver(operationalFactsTopic, operationsProcessorSubscriptionName, receiverOptions)

            use _receiver = receiver
            let! messages = receiver.PeekMessagesAsync(50)

            return
                messages
                |> Seq.map (fun message -> message.MessageId)
                |> Seq.toList
        }

    /// Captures Service Bus delivery evidence for a failed operations SQL wait.
    let operationsServiceBusDiagnosticsAsync () =
        task {
            try
                let! activeMessages = peekOperationalMessagesAsync None
                let! deadLetterMessages = peekOperationalMessagesAsync (Some SubQueue.DeadLetter)

                let format label messages =
                    if List.isEmpty messages then
                        $"{label}: <none>"
                    else
                        $"{label}:{Environment.NewLine}{String.Join(Environment.NewLine, messages)}"

                return
                    String.Join(
                        Environment.NewLine,
                        [
                            format "Active operational messages" activeMessages
                            format "Dead-letter operational messages" deadLetterMessages
                        ]
                    )
            with
            | ex -> return $"Service Bus diagnostics unavailable: {ex.GetType().FullName}: {ex.Message}"
        }

    /// Opens a SQL connection to the operations database configured for the AppHost worker.
    let openOperationsSqlAsync () =
        task {
            let connection = new SqlConnection(operationsSqlConnectionString)
            do! connection.OpenAsync()
            return connection
        }

    /// Adds a SQL parameter to a command without logging the value.
    let addParameter (command: SqlCommand) name sqlDbType value =
        let parameter = command.Parameters.Add(name, sqlDbType)
        parameter.Value <- value

    /// Reads the raw fact count for one durable usage fact identity.
    let rawFactCountAsync usageFactId =
        task {
            use! connection = openOperationsSqlAsync ()
            use command = connection.CreateCommand()
            command.CommandText <- $"SELECT COUNT_BIG(1) FROM {operationsRawUsageFactTable} WHERE UsageFactId = @UsageFactId;"
            addParameter command "@UsageFactId" SqlDbType.UniqueIdentifier usageFactId
            let! scalar = command.ExecuteScalarAsync()
            return Convert.ToInt64 scalar
        }

    /// Reads the durable journal state for one usage-fact identity.
    let journalStateAsync usageFactId =
        task {
            use! connection = openOperationsSqlAsync ()
            use command = connection.CreateCommand()
            command.CommandText <- $"SELECT State FROM {operationsUsageFactJournalTable} WHERE UsageFactId = @UsageFactId;"
            addParameter command "@UsageFactId" SqlDbType.UniqueIdentifier usageFactId
            let! scalar = command.ExecuteScalarAsync()

            return
                match scalar with
                | :? DBNull -> None
                | value -> Some(Convert.ToInt32 value)
        }

    /// Reads the minute aggregate quantity for the fact's repository resource and UTC bucket.
    let aggregateQuantityAsync (fact: UsageFact) =
        task {
            use! connection = openOperationsSqlAsync ()
            use command = connection.CreateCommand()

            command.CommandText <-
                $"""
SELECT COALESCE(SUM(Quantity), 0)
FROM {operationsUsageAggregateMinuteTable}
WHERE FactKind = @FactKind
  AND OwnerId = @OwnerId
  AND OrganizationId = @OrganizationId
  AND RepositoryId = @RepositoryId
  AND StoragePoolId = @StoragePoolId
  AND BucketStartUtc = @BucketStartUtc;
"""

            addParameter command "@FactKind" SqlDbType.Int (int fact.FactKind)
            addParameter command "@OwnerId" SqlDbType.UniqueIdentifier fact.Scope.OwnerId
            addParameter command "@OrganizationId" SqlDbType.UniqueIdentifier fact.Scope.OrganizationId
            addParameter command "@RepositoryId" SqlDbType.UniqueIdentifier fact.Scope.RepositoryId

            let storagePoolParameter = command.Parameters.Add("@StoragePoolId", SqlDbType.NVarChar, operationsStoragePoolIdMaxLength)

            storagePoolParameter.Value <- fact.Resource.StoragePoolId
            addParameter command "@BucketStartUtc" SqlDbType.DateTime2 (fact.ObservedAt.ToDateTimeUtc())

            let! scalar = command.ExecuteScalarAsync()
            return Convert.ToInt64 scalar
        }

    /// Waits until SQL shows the expected raw count and aggregate quantity for the tracer-bullet fact.
    let waitForUsageStateAsync description expectedRawCount expectedAggregateQuantity fact =
        task {
            let stopwatch = Stopwatch.StartNew()
            let mutable matched = false
            let mutable lastRawCount = -1L
            let mutable lastAggregateQuantity = -1L
            let mutable lastError = String.Empty

            while not matched && stopwatch.Elapsed < proofTimeout do
                try
                    let! rawCount = rawFactCountAsync fact.UsageFactId
                    let! aggregateQuantity = aggregateQuantityAsync fact
                    lastRawCount <- rawCount
                    lastAggregateQuantity <- aggregateQuantity
                    lastError <- String.Empty

                    if lastRawCount = expectedRawCount
                       && lastAggregateQuantity = expectedAggregateQuantity then
                        matched <- true
                    else
                        do! Task.Delay(TimeSpan.FromSeconds(1.0))
                with
                | ex ->
                    lastError <- ex.Message
                    do! Task.Delay(TimeSpan.FromSeconds(1.0))

            if not matched then
                let! serviceBusDiagnostics = operationsServiceBusDiagnosticsAsync ()

                let errorSuffix =
                    if String.IsNullOrWhiteSpace lastError then
                        ""
                    else
                        $" Last SQL error: {lastError}"

                raise (
                    TimeoutException(
                        $"{description} did not reach expected operations SQL state. Expected raw={expectedRawCount}, aggregate={expectedAggregateQuantity}; actual raw={lastRawCount}, aggregate={lastAggregateQuantity}.{errorSuffix}{Environment.NewLine}{serviceBusDiagnostics}"
                    )
                )
        }

    /// Waits for the requested durable journal state while retaining SQL and broker diagnostics on timeout.
    let waitForJournalStateAsync description expectedState fact =
        task {
            let stopwatch = Stopwatch.StartNew()
            let mutable matched = false
            let mutable lastState = None
            let mutable lastError = String.Empty

            while not matched && stopwatch.Elapsed < proofTimeout do
                try
                    let! state = journalStateAsync fact.UsageFactId
                    lastState <- state
                    lastError <- String.Empty

                    if state = Some expectedState then
                        matched <- true
                    else
                        do! Task.Delay(TimeSpan.FromSeconds(1.0))
                with
                | ex ->
                    lastError <- ex.Message
                    do! Task.Delay(TimeSpan.FromSeconds(1.0))

            if not matched then
                let! serviceBusDiagnostics = operationsServiceBusDiagnosticsAsync ()

                let errorSuffix =
                    if String.IsNullOrWhiteSpace lastError then
                        ""
                    else
                        $" Last SQL error: {lastError}"

                raise (
                    TimeoutException(
                        $"{description} did not reach journal state {expectedState}. Actual state={lastState}.{errorSuffix}{Environment.NewLine}{serviceBusDiagnostics}"
                    )
                )
        }

    /// Waits until the operations worker has removed one expected message from its durable subscription.
    let waitForOperationalMessageSettledAsync description messageId =
        task {
            let stopwatch = Stopwatch.StartNew()
            let mutable settled = false
            let mutable lastActiveMessages = List.empty
            let mutable lastDeadLetterMessages = List.empty

            while not settled && stopwatch.Elapsed < proofTimeout do
                let! activeMessageIds = peekOperationalMessageIdsAsync None
                let! deadLetterMessageIds = peekOperationalMessageIdsAsync (Some SubQueue.DeadLetter)

                lastActiveMessages <- activeMessageIds
                lastDeadLetterMessages <- deadLetterMessageIds

                let hasMatchingMessage =
                    activeMessageIds
                    |> List.append deadLetterMessageIds
                    |> List.exists (fun activeMessageId -> String.Equals(activeMessageId, messageId, StringComparison.Ordinal))

                if hasMatchingMessage then
                    do! Task.Delay(TimeSpan.FromSeconds(1.0))
                else
                    settled <- true

            if not settled then
                let format label messages =
                    if List.isEmpty messages then
                        $"{label}: <none>"
                    else
                        $"{label}:{Environment.NewLine}{String.Join(Environment.NewLine, messages)}"

                let activeDiagnostics = format "Active operational messages" lastActiveMessages
                let deadLetterDiagnostics = format "Dead-letter operational messages" lastDeadLetterMessages

                raise (
                    TimeoutException(
                        $"{description} message '{messageId}' was not settled by the operations worker before timeout.{Environment.NewLine}{activeDiagnostics}{Environment.NewLine}{deadLetterDiagnostics}"
                    )
                )
        }

    /// Abandons one locked signal until the emulator applies its configured final-delivery movement.
    let abandonOperationalMessageThroughFinalDeliveryAsync messageId =
        task {
            let client = ServiceBusClient(serviceBusConnectionString)
            use _client = client

            let receiver =
                client.CreateReceiver(
                    operationalFactsTopic,
                    operationsProcessorSubscriptionName,
                    ServiceBusReceiverOptions(ReceiveMode = ServiceBusReceiveMode.PeekLock)
                )

            use _receiver = receiver
            let mutable delivery = 0

            while delivery < 10 do
                let! message = receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5.0))

                if isNull message then
                    raise (TimeoutException($"Timed out receiving final-delivery signal '{messageId}' at delivery {delivery + 1}."))

                if not (String.Equals(message.MessageId, messageId, StringComparison.Ordinal)) then
                    raise (InvalidOperationException($"Expected final-delivery signal '{messageId}', received '{message.MessageId}'."))

                do! receiver.AbandonMessageAsync(message)
                delivery <- delivery + 1
        }

    /// Receives the expected broker-terminal signal and returns the broker's terminal reason.
    let receiveTerminalOperationalMessageReasonAsync messageId =
        task {
            let client = ServiceBusClient(serviceBusConnectionString)
            use _client = client

            let receiver =
                client.CreateReceiver(
                    operationalFactsTopic,
                    operationsProcessorSubscriptionName,
                    ServiceBusReceiverOptions(SubQueue = SubQueue.DeadLetter, ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete)
                )

            use _receiver = receiver
            let stopwatch = Stopwatch.StartNew()
            let mutable terminalReason = None

            while terminalReason.IsNone
                  && stopwatch.Elapsed < proofTimeout do
                let! message = receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(1.0))

                if
                    not (isNull message)
                    && String.Equals(message.MessageId, messageId, StringComparison.Ordinal)
                then
                    terminalReason <- Some message.DeadLetterReason

            return
                terminalReason
                |> Option.defaultWith (fun () -> raise (TimeoutException($"Timed out waiting for terminal signal '{messageId}' in the dead-letter subqueue.")))
        }

    /// Waits for an invalid operational message to reach the worker's dead-letter queue.
    let waitForDeadLetterReasonAsync expectedReason =
        task {
            let client = ServiceBusClient(serviceBusConnectionString)
            use _client = client

            let receiver =
                client.CreateReceiver(
                    operationalFactsTopic,
                    operationsProcessorSubscriptionName,
                    ServiceBusReceiverOptions(SubQueue = SubQueue.DeadLetter, ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete)
                )

            use _receiver = receiver
            let stopwatch = Stopwatch.StartNew()
            let mutable found = false
            let reasons = ResizeArray<string>()

            while not found && stopwatch.Elapsed < proofTimeout do
                let! message = receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(1.0))

                if not (isNull message) then
                    reasons.Add(message.DeadLetterReason)

                    if message.DeadLetterReason = expectedReason then found <- true

            if not found then
                let reasonText = if reasons.Count = 0 then "<none>" else String.Join(", ", reasons)

                raise (TimeoutException($"Timed out waiting for operations dead-letter reason '{expectedReason}'. Observed reasons: {reasonText}."))
        }

    /// Builds unsupported future-kind JSON while preserving the usage fact identity for SQL absence checks.
    let futureKindJson (fact: UsageFact) =
        JsonSerializer
            .Serialize(fact, Constants.JsonSerializerOptions)
            .Replace("repositoryStorageBytesMinute", "futureUsageFactKind")

    /// Verifies the local dev/test source exercises publisher, Service Bus, worker, raw SQL, duplicate, and invalid payload behavior.
    [<Test>]
    member _.DevTestUsageFactPublishesThroughWorkerToRawSqlAndAggregateOnce() =
        task {
            let usageFactId = Guid.Parse("53153153-1531-4531-8531-531531531531")
            let correlationId = CorrelationId "ops6-correlation-distinct-from-usage-fact-id"
            let fact = usageFact usageFactId correlationId

            Assert.That(fact.CorrelationId, Is.Not.EqualTo(fact.UsageFactId.ToString("D")))

            do! appendJournalFactAsync fact
            do! waitForUsageStateAsync "Initial published fact" 1L 4096L fact
            do! waitForJournalStateAsync "Initial published fact" 1 fact

            let restartFact = usageFact (Guid.Parse("53153153-3531-4531-8531-531531531531")) (CorrelationId "ops6-worker-restart-pending-correlation")

            let finalDeliveryFact = usageFact (Guid.Parse("53153153-4531-4531-8531-531531531531")) (CorrelationId "ops6-final-delivery-pending-correlation")

            let app =
                match App with
                | Some runningApp -> runningApp
                | None -> failwith "The shared Aspire test host was not available for Operations worker recovery proof."

            do! AspireTestHost.stopOperationsWorkerAsync app
            do! appendJournalFactAsync restartFact
            do! waitForJournalStateAsync "Stopped worker leaves journal append pending" 0 restartFact

            do! appendJournalFactAsync finalDeliveryFact
            do! waitForJournalStateAsync "Final-delivery journal append remains pending" 0 finalDeliveryFact

            let finalDeliveryMessageId = $"{finalDeliveryFact.UsageFactId:D}-terminal"
            do! sendOperationalMessageAsync (usageFactMessage finalDeliveryMessageId finalDeliveryFact)
            do! abandonOperationalMessageThroughFinalDeliveryAsync finalDeliveryMessageId
            let! finalDeliveryReason = receiveTerminalOperationalMessageReasonAsync finalDeliveryMessageId

            Assert.That(finalDeliveryReason, Is.Not.Empty)
            do! waitForJournalStateAsync "Broker terminal movement leaves journal fact pending" 0 finalDeliveryFact

            do! AspireTestHost.startOperationsWorkerAsync app
            do! waitForUsageStateAsync "Restarted worker dispatches pending journal fact" 1L 12288L restartFact
            do! waitForJournalStateAsync "Restarted worker accepts pending journal fact" 1 restartFact
            do! waitForUsageStateAsync "Terminal broker movement is redispatched from SQL journal" 1L 12288L finalDeliveryFact
            do! waitForJournalStateAsync "Terminal broker movement journal fact is accepted" 1 finalDeliveryFact

            let duplicateMessageId = $"{fact.UsageFactId:D}-redelivery"
            let duplicateDelivery = usageFactMessage duplicateMessageId fact

            do! sendOperationalMessageAsync duplicateDelivery
            do! waitForOperationalMessageSettledAsync "Duplicate UsageFactId delivery" duplicateMessageId
            do! waitForUsageStateAsync "Duplicate UsageFactId delivery" 1L 12288L fact

            let futureFact = usageFact (Guid.Parse("53153153-2531-4531-8531-531531531531")) (CorrelationId "ops6-future-kind-correlation")

            let futureMessage = ServiceBusMessage(BinaryData.FromString(futureKindJson futureFact))
            futureMessage.ContentType <- operationalJsonContentType
            futureMessage.Subject <- usageFactSubject
            futureMessage.MessageId <- $"{futureFact.UsageFactId:D}-future-kind"
            futureMessage.CorrelationId <- futureFact.CorrelationId

            futureMessage.ApplicationProperties[ usageFactMessageTypeProperty ] <- usageFactMessageType

            futureMessage.ApplicationProperties[ usageFactKindProperty ] <- "futureUsageFactKind"

            do! sendOperationalMessageAsync futureMessage
            do! waitForDeadLetterReasonAsync "MalformedUsageFactJson"

            let malformedMessage = ServiceBusMessage(BinaryData.FromBytes(Encoding.UTF8.GetBytes("{ not valid json")))
            malformedMessage.ContentType <- operationalJsonContentType
            malformedMessage.Subject <- usageFactSubject
            malformedMessage.MessageId <- "ops6-malformed-usage-fact"
            malformedMessage.CorrelationId <- "ops6-malformed-correlation"

            malformedMessage.ApplicationProperties[ usageFactMessageTypeProperty ] <- usageFactMessageType

            do! sendOperationalMessageAsync malformedMessage
            do! waitForDeadLetterReasonAsync "MalformedUsageFactJson"

            let! rejectedRawCount = rawFactCountAsync futureFact.UsageFactId

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(rejectedRawCount, Is.EqualTo(0L))
                    Assert.That(fact.ObservedAt, Is.EqualTo(Instant.FromUtc(2026, 7, 4, 12, 34))))
            )
        }
