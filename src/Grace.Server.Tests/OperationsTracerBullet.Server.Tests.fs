namespace Grace.Server.Tests

open Azure.Messaging.ServiceBus
open Grace.Operations.Data
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

    /// Ensures the dev/test SQL proof database and operations usage tables exist before publishing facts.
    let ensureOperationsSchemaAsync () =
        task {
            let schema = OperationsUsageSchema(operationsSqlConnectionString, OperationsUsageSchemaBootstrapMode.CreateDatabaseIfMissing)

            do! schema.EnsureCreatedAsync(CancellationToken.None)
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
            command.CommandText <- $"SELECT COUNT_BIG(1) FROM {OperationsUsageSql.RawUsageFactTable} WHERE UsageFactId = @UsageFactId;"
            addParameter command "@UsageFactId" SqlDbType.UniqueIdentifier usageFactId
            let! scalar = command.ExecuteScalarAsync()
            return Convert.ToInt64 scalar
        }

    /// Reads the minute aggregate quantity for the fact's repository resource and UTC bucket.
    let aggregateQuantityAsync (fact: UsageFact) =
        task {
            use! connection = openOperationsSqlAsync ()
            use command = connection.CreateCommand()

            command.CommandText <-
                $"""
SELECT COALESCE(SUM(Quantity), 0)
FROM {OperationsUsageSql.UsageAggregateMinuteTable}
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

            let storagePoolParameter = command.Parameters.Add("@StoragePoolId", SqlDbType.NVarChar, OperationsUsageSql.StoragePoolIdMaxLength)

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

            do! ensureOperationsSchemaAsync ()
            do! sendOperationalMessageAsync (usageFactMessage (fact.UsageFactId.ToString("D")) fact)
            do! waitForUsageStateAsync "Initial published fact" 1L 4096L fact

            let duplicateDelivery = usageFactMessage $"{fact.UsageFactId:D}-redelivery" fact

            do! sendOperationalMessageAsync duplicateDelivery
            do! waitForUsageStateAsync "Duplicate UsageFactId delivery" 1L 4096L fact

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
