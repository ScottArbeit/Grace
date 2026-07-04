namespace Grace.Operations.Tests

open Grace.Operations.Data
open Grace.Operations.Worker
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.Usage
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging.Abstractions
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Provides deterministic usage facts and envelopes for operations worker ingestion tests.
module OperationsWorkerIngestionTestData =

    /// Provides the owner used by worker ingestion facts.
    let ownerId = OwnerId.Parse("11111111-1111-1111-1111-111111111111")

    /// Provides the organization used by worker ingestion facts.
    let organizationId = OrganizationId.Parse("22222222-2222-2222-2222-222222222222")

    /// Provides the repository used by worker ingestion facts.
    let repositoryId = RepositoryId.Parse("33333333-3333-3333-3333-333333333333")

    /// Provides the storage pool used by worker ingestion facts.
    let storagePoolId = StoragePoolId "storage-pool-main"

    /// Creates a valid repository storage usage fact for ingestion tests.
    let usageFact usageFactId =
        UsageFact.RepositoryStorageBytesMinute(
            usageFactId,
            CorrelationId $"corr-{usageFactId}",
            ownerId,
            organizationId,
            repositoryId,
            storagePoolId,
            4096L,
            Instant.FromUtc(2026, 7, 4, 12, 34, 0)
        )

    /// Serializes a usage fact through the shared Grace JSON options.
    let serializeFact fact = JsonSerializer.SerializeToUtf8Bytes(fact, Constants.JsonSerializerOptions)

    /// Builds the Service Bus application properties expected by the operations worker.
    let applicationProperties () =
        let properties = Dictionary<string, obj>()
        properties[OperationalFactEnvelope.UsageFactMessageTypeProperty] <- box OperationalFactEnvelope.UsageFactMessageType
        properties[OperationalFactEnvelope.UsageFactKindProperty] <- box "RepositoryStorageBytesMinute"
        properties :> IReadOnlyDictionary<string, obj>

    /// Creates a worker message for one operational fact payload.
    let message body =
        {
            MessageId = "message-1"
            CorrelationId = "message-correlation"
            DeliveryCount = 2
            Subject = OperationalFactEnvelope.UsageFactSubject
            ApplicationProperties = applicationProperties ()
            Body = body
        }

    /// Builds the aggregate projected by the data layer for a valid fact.
    let aggregateFor fact =
        match UsageFactPersistencePlan.tryCreate fact with
        | Ok plan -> plan.Aggregate
        | Error errors -> failwith (String.Join("; ", errors))

/// Records message settlement chosen by the ingestion processor.
type private RecordingMessageActions(events: List<string>) =
    let settlements = ResizeArray<string * string option>()

    /// Returns the settlements recorded by the fake actions.
    member _.Settlements = settlements |> Seq.toList

    interface IOperationsUsageMessageActions with

        member _.CompleteAsync(_cancellationToken) =
            events.Add("complete")
            settlements.Add("complete", None)
            Task.CompletedTask

        member _.AbandonAsync(_cancellationToken) =
            events.Add("abandon")
            settlements.Add("abandon", None)
            Task.CompletedTask

        member _.DeadLetterAsync(reason, _description, _cancellationToken) =
            events.Add("dead-letter")
            settlements.Add("dead-letter", Some reason)
            Task.CompletedTask

/// Fakes the operations data-layer boundary for deterministic processor tests.
type private StubUsageFactStore(storeAsync: UsageFact -> CancellationToken -> Task<Result<UsageFactPersistenceResult, string list>>, events: List<string>) =
    let storedFacts = ResizeArray<UsageFact>()

    /// Returns the facts sent to the fake store.
    member _.StoredFacts = storedFacts |> Seq.toList

    interface IOperationsUsageFactStore with

        member _.StoreUsageFactAsync(fact, cancellationToken) =
            storedFacts.Add fact
            events.Add("store")
            storeAsync fact cancellationToken

/// Covers the Grace operations worker usage fact ingestion loop.
[<TestFixture>]
type OperationsWorkerIngestionTests() =

    /// Builds host configuration from explicit keys so tests can model normalized environment variables.
    let configurationFromPairs pairs =
        ConfigurationBuilder()
            .AddInMemoryCollection(pairs)
            .Build()

    /// Creates a processor with deterministic fake dependencies.
    let createProcessor storeAsync =
        let events = List<string>()
        let store = StubUsageFactStore(storeAsync, events)

        let logger =
            NullLogger<OperationsUsageIngestionProcessor>
                .Instance

        OperationsUsageIngestionProcessor(store, logger), store, RecordingMessageActions(events), events

    /// Formats ordered fake events for overload-free assertions.
    let eventText (events: seq<string>) = String.Join("|", events)

    /// Formats ordered settlement outcomes for overload-free assertions.
    let settlementText settlements =
        settlements
        |> Seq.map (fun (action, reason) ->
            match reason with
            | Some value -> $"{action}:{value}"
            | None -> action)
        |> fun values -> String.Join("|", values)

    /// Creates an accepted persistence result for a fact.
    let accepted fact =
        { Status = UsageFactPersistenceStatus.Accepted; UsageFactId = fact.UsageFactId; Aggregate = Some(OperationsWorkerIngestionTestData.aggregateFor fact) }

    /// Creates an already-processed persistence result for a fact.
    let alreadyProcessed fact = { Status = UsageFactPersistenceStatus.AlreadyProcessed; UsageFactId = fact.UsageFactId; Aggregate = None }

    /// Verifies AppHost-style environment names resolve after Host configuration normalizes `__` to `:`.
    [<Test>]
    member _.WorkerSettingsReadNormalizedConfigurationKeys() =
        let configuration =
            configurationFromPairs [ KeyValuePair<string, string>(
                                         getConfigKey Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic,
                                         "operations-topic"
                                     )
                                     KeyValuePair<string, string>(
                                         getConfigKey OperationsWorkerSettings.ProcessorSubscriptionEnvironmentVariable,
                                         OperationsWorkerSettings.DefaultProcessorSubscriptionName
                                     )
                                     KeyValuePair<string, string>(
                                         getConfigKey OperationsWorkerSettings.SqlConnectionStringEnvironmentVariable,
                                         "Server=tcp:sql.example.net;Database=GraceOperations;Authentication=Active Directory Default;"
                                     )
                                     KeyValuePair<string, string>(
                                         getConfigKey Constants.EnvironmentVariables.AzureServiceBusConnectionString,
                                         "Endpoint=sb://operations.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=fake"
                                     )
                                     KeyValuePair<string, string>(getConfigKey OperationsWorkerSettings.MaxConcurrentCallsEnvironmentVariable, "7")
                                     KeyValuePair<string, string>(getConfigKey OperationsWorkerSettings.PrefetchCountEnvironmentVariable, "29") ]

        let settings = OperationsWorkerSettings.fromConfiguration configuration

        match settings with
        | Ok value ->
            Assert.Multiple(
                Action (fun () ->
                    Assert.That(value.TopicName, Is.EqualTo("operations-topic"))
                    Assert.That(value.SubscriptionName, Is.EqualTo(OperationsWorkerSettings.DefaultProcessorSubscriptionName))

                    Assert.That(
                        value.SqlConnectionString,
                        Is.EqualTo("Server=tcp:sql.example.net;Database=GraceOperations;Authentication=Active Directory Default;")
                    )

                    Assert.That(value.MaxConcurrentCalls, Is.EqualTo(7))
                    Assert.That(value.PrefetchCount, Is.EqualTo(29)))
            )
        | Error errors -> Assert.Fail(String.Join("; ", errors))

    /// Verifies valid messages complete only after the data layer reports durable persistence.
    [<Test>]
    member _.ValidMessageIsPersistedBeforeCompletion() =
        task {
            let fact = OperationsWorkerIngestionTestData.usageFact (Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"))

            let processor, store, actions, events = createProcessor (fun storedFact _ -> Task.FromResult(Ok(accepted storedFact)))

            let message =
                fact
                |> OperationsWorkerIngestionTestData.serializeFact
                |> OperationsWorkerIngestionTestData.message

            do! processor.ProcessMessageAsync(message, actions, CancellationToken.None)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(List.length store.StoredFacts, Is.EqualTo(1))
                    Assert.That(store.StoredFacts[0].UsageFactId, Is.EqualTo(fact.UsageFactId))
                    Assert.That(eventText events, Is.EqualTo("store|complete"))
                    Assert.That(settlementText actions.Settlements, Is.EqualTo("complete")))
            )
        }

    /// Verifies duplicate durable processing still completes the Service Bus message.
    [<Test>]
    member _.DuplicateMessageCompletesAfterIdempotentStorageResult() =
        task {
            let fact = OperationsWorkerIngestionTestData.usageFact (Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"))

            let processor, _store, actions, events = createProcessor (fun storedFact _ -> Task.FromResult(Ok(alreadyProcessed storedFact)))

            let message =
                fact
                |> OperationsWorkerIngestionTestData.serializeFact
                |> OperationsWorkerIngestionTestData.message

            do! processor.ProcessMessageAsync(message, actions, CancellationToken.None)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(eventText events, Is.EqualTo("store|complete"))
                    Assert.That(settlementText actions.Settlements, Is.EqualTo("complete")))
            )
        }

    /// Verifies malformed JSON dead-letters without calling the data layer.
    [<Test>]
    member _.MalformedJsonIsDeadLetteredWithoutStorage() =
        task {
            let processor, store, actions, events = createProcessor (fun storedFact _ -> Task.FromResult(Ok(accepted storedFact)))

            let message =
                "{ not valid json"
                |> Encoding.UTF8.GetBytes
                |> OperationsWorkerIngestionTestData.message

            do! processor.ProcessMessageAsync(message, actions, CancellationToken.None)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(store.StoredFacts, Is.Empty)
                    Assert.That(eventText events, Is.EqualTo("dead-letter"))
                    Assert.That(settlementText actions.Settlements, Is.EqualTo("dead-letter:MalformedUsageFactJson")))
            )
        }

    /// Verifies future usage fact kinds dead-letter instead of being silently completed.
    [<Test>]
    member _.FutureUsageFactKindIsDeadLetteredWithoutStorage() =
        task {
            let fact = OperationsWorkerIngestionTestData.usageFact (Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"))

            let processor, store, actions, events = createProcessor (fun storedFact _ -> Task.FromResult(Ok(accepted storedFact)))

            let unknownKindJson =
                JsonSerializer
                    .Serialize(fact, Constants.JsonSerializerOptions)
                    .Replace("repositoryStorageBytesMinute", "futureUsageFactKind")

            let message =
                unknownKindJson
                |> Encoding.UTF8.GetBytes
                |> OperationsWorkerIngestionTestData.message

            do! processor.ProcessMessageAsync(message, actions, CancellationToken.None)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(store.StoredFacts, Is.Empty)
                    Assert.That(eventText events, Is.EqualTo("dead-letter"))
                    Assert.That(settlementText actions.Settlements, Is.EqualTo("dead-letter:MalformedUsageFactJson")))
            )
        }

    /// Verifies transient storage failures abandon the message for retry.
    [<Test>]
    member _.StorageFailureAbandonsMessageForRetry() =
        task {
            let fact = OperationsWorkerIngestionTestData.usageFact (Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"))

            let processor, store, actions, events =
                createProcessor (fun _ _ -> Task.FromException<Result<UsageFactPersistenceResult, string list>>(InvalidOperationException("forced SQL failure")))

            let message =
                fact
                |> OperationsWorkerIngestionTestData.serializeFact
                |> OperationsWorkerIngestionTestData.message

            do! processor.ProcessMessageAsync(message, actions, CancellationToken.None)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(List.length store.StoredFacts, Is.EqualTo(1))
                    Assert.That(eventText events, Is.EqualTo("store|abandon"))
                    Assert.That(settlementText actions.Settlements, Is.EqualTo("abandon")))
            )
        }

    /// Verifies cancellation during storage leaves the message unsettled so Service Bus can redeliver it.
    [<Test>]
    member _.CancellationDuringStorageLeavesMessageUnsettled() =
        task {
            use cancellation = new CancellationTokenSource()
            let fact = OperationsWorkerIngestionTestData.usageFact (Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"))

            let processor, store, actions, events =
                createProcessor (fun _ cancellationToken ->
                    cancellation.Cancel()
                    Task.FromCanceled<Result<UsageFactPersistenceResult, string list>>(cancellationToken))

            let message =
                fact
                |> OperationsWorkerIngestionTestData.serializeFact
                |> OperationsWorkerIngestionTestData.message

            do! processor.ProcessMessageAsync(message, actions, cancellation.Token)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(List.length store.StoredFacts, Is.EqualTo(1))
                    Assert.That(eventText events, Is.EqualTo("store"))
                    Assert.That(actions.Settlements, Is.Empty))
            )
        }
