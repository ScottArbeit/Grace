namespace Grace.Operations.Tests

open Grace.Operations.Data
open Grace.Operations.Worker
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.Usage
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Diagnostics.HealthChecks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
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

/// Blocks message completion so tests can interleave another receive after durable storage succeeds.
type private BlockingCompleteMessageActions(events: List<string>) =
    let completeStarted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let completeRelease = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let settlements = ResizeArray<string * string option>()

    /// Completes when the processor reaches message completion after durable storage succeeded.
    member _.CompleteStarted = completeStarted.Task

    /// Allows the blocked completion to finish.
    member _.ReleaseComplete() = completeRelease.SetResult(())

    /// Returns the settlements recorded by the fake actions.
    member _.Settlements = settlements |> Seq.toList

    interface IOperationsUsageMessageActions with

        member _.CompleteAsync(_cancellationToken) =
            task {
                events.Add("complete-waiting")
                completeStarted.SetResult(())
                do! completeRelease.Task
                events.Add("complete")
                settlements.Add("complete", None)
            }
            :> Task

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

/// Records formatted log output from worker infrastructure tests.
type private RecordingLogger<'T>() =
    let messages = ResizeArray<string>()

    /// Returns formatted log messages written through this logger.
    member _.Messages = messages |> Seq.toList

    interface ILogger<'T> with

        member _.BeginScope<'TState>(_state: 'TState) =
            { new IDisposable with
                member _.Dispose() = ()
            }

        member _.IsEnabled(_logLevel) = true

        member _.Log<'TState>(_logLevel, _eventId, state: 'TState, ex: exn, formatter: Func<'TState, exn, string>) = messages.Add(formatter.Invoke(state, ex))

/// Fails if deterministic validation lets an invalid fact reach a storage transaction.
type private FailingOperationsUsageTransactionScope() =

    interface IOperationsUsageTransactionScope with

        member _.ExecuteAsync<'T>(_operation, _cancellationToken) =
            Task.FromException<'T>(InvalidOperationException("Invalid usage facts must be rejected before opening a storage transaction."))

/// Covers the Grace operations worker usage fact ingestion loop.
[<TestFixture>]
type OperationsWorkerIngestionTests() =

    /// Builds host configuration from explicit keys so tests can model normalized environment variables.
    let configurationFromPairs pairs =
        ConfigurationBuilder()
            .AddInMemoryCollection(pairs)
            .Build()

    /// Creates a processor with deterministic fake dependencies and an inspectable readiness state.
    let createProcessorWithReadiness storeAsync =
        let events = List<string>()
        let store = StubUsageFactStore(storeAsync, events)
        let readiness = OperationsUsageReadinessState()

        let logger =
            NullLogger<OperationsUsageIngestionProcessor>
                .Instance

        OperationsUsageIngestionProcessor(store, logger, readiness :> IOperationsUsageReadinessRecorder),
        store,
        RecordingMessageActions(events),
        events,
        readiness

    /// Creates a processor with deterministic fake dependencies.
    let createProcessor storeAsync =
        let processor, store, actions, events, _readiness = createProcessorWithReadiness storeAsync
        processor, store, actions, events

    /// Creates a processor backed by the real data-layer validation path.
    let createProcessorWithRealStore () =
        let events = List<string>()

        let store =
            OperationsUsageStore(FailingOperationsUsageTransactionScope())
            |> OperationsUsageFactStoreAdapter

        let logger =
            NullLogger<OperationsUsageIngestionProcessor>
                .Instance

        OperationsUsageIngestionProcessor(store, logger), RecordingMessageActions(events), events

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

    /// Returns the latest unsupported contract readiness note for focused worker assertions.
    let lastUnsupportedContract (readiness: OperationsUsageReadinessState) =
        let snapshot =
            (readiness :> IOperationsUsageReadinessProbe)
                .GetSnapshot()

        snapshot.LastUnsupportedContract
        |> Option.defaultValue String.Empty

    /// Returns the current readiness snapshot for state-transition assertions.
    let readinessSnapshot (readiness: OperationsUsageReadinessState) =
        (readiness :> IOperationsUsageReadinessProbe)
            .GetSnapshot()

    /// Creates an accepted persistence result for a fact.
    let accepted fact =
        { Status = UsageFactPersistenceStatus.Accepted; UsageFactId = fact.UsageFactId; Aggregate = Some(OperationsWorkerIngestionTestData.aggregateFor fact) }

    /// Creates an already-processed persistence result for a fact.
    let alreadyProcessed fact = { Status = UsageFactPersistenceStatus.AlreadyProcessed; UsageFactId = fact.UsageFactId; Aggregate = None }

    /// Verifies AppHost SQL Server data sources use SqlClient comma-port syntax and IPv4 loopback for local endpoints.
    [<Test>]
    member _.AppHostFormatsLocalSqlDataSourceWithCommaPort() =
        Assert.Multiple(
            Action (fun () ->
                Assert.That(global.Program.BuildSqlTcpDataSource("localhost", 21433), Is.EqualTo("tcp:127.0.0.1,21433"))
                Assert.That(global.Program.BuildSqlTcpDataSource("tcp:localhost", "21433"), Is.EqualTo("tcp:127.0.0.1,21433")))
        )

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

    /// Verifies DebugLocal opts into admin bootstrap so fresh local SQL containers get the operations database.
    [<Test>]
    member _.WorkerSettingsUseDatabaseCreationBootstrapForDebugLocal() =
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
                                         "Server=tcp:localhost,21433;Database=GraceOperations;User ID=sa;Password=fake;TrustServerCertificate=True;Encrypt=False;"
                                     )
                                     KeyValuePair<string, string>(
                                         getConfigKey Constants.EnvironmentVariables.AzureServiceBusConnectionString,
                                         "Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=fake;UseDevelopmentEmulator=true;"
                                     )
                                     KeyValuePair<string, string>(getConfigKey Constants.EnvironmentVariables.DebugEnvironment, "Local") ]

        match OperationsWorkerSettings.fromConfiguration configuration with
        | Ok value -> Assert.That(value.SchemaBootstrapMode, Is.EqualTo(OperationsUsageSchemaBootstrapMode.CreateDatabaseIfMissing))
        | Error errors -> Assert.Fail(String.Join("; ", errors))

    /// Verifies non-local worker settings keep least-privilege target-only schema initialization.
    [<Test>]
    member _.WorkerSettingsKeepTargetOnlyBootstrapForDebugAzure() =
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
                                     KeyValuePair<string, string>(getConfigKey Constants.EnvironmentVariables.AzureServiceBusNamespace, "operations-bus")
                                     KeyValuePair<string, string>(getConfigKey Constants.EnvironmentVariables.DebugEnvironment, "Azure") ]

        match OperationsWorkerSettings.fromConfiguration configuration with
        | Ok value -> Assert.That(value.SchemaBootstrapMode, Is.EqualTo(OperationsUsageSchemaBootstrapMode.TargetDatabaseOnly))
        | Error errors -> Assert.Fail(String.Join("; ", errors))

    /// Verifies worker-only managed identity settings accept short Service Bus namespace names.
    [<Test>]
    member _.WorkerSettingsNormalizeManagedIdentityNamespace() =
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
                                     KeyValuePair<string, string>(getConfigKey Constants.EnvironmentVariables.AzureServiceBusNamespace, "mybus") ]

        match OperationsWorkerSettings.fromConfiguration configuration with
        | Ok value ->
            Assert.Multiple(
                Action (fun () ->
                    Assert.That(value.ServiceBusConnectionString, Is.EqualTo(None))
                    Assert.That(value.ServiceBusFullyQualifiedNamespace, Is.EqualTo(Some "mybus.servicebus.windows.net")))
            )
        | Error errors -> Assert.Fail(String.Join("; ", errors))

    /// Verifies readiness reports the supported UsageFact contract and dependency startup state.
    [<Test>]
    member _.ReadinessSnapshotReportsSupportedContractAndDependencyFailure() =
        let readiness = OperationsUsageReadinessState()
        let recorder = readiness :> IOperationsUsageReadinessRecorder

        recorder.MarkDependencyFailure("Dependency startup failed (SqlException).")
        recorder.MarkUnsupportedContract("UsageFact SchemaVersion '2' is not supported. Expected '1'.")

        let snapshot =
            (readiness :> IOperationsUsageReadinessProbe)
                .GetSnapshot()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(snapshot.Status, Is.EqualTo(OperationsUsageReadinessStatus.NotReady))
                Assert.That(snapshot.SupportedUsageFactSchemaVersion, Is.EqualTo(UsageFactSchemaVersion))
                Assert.That(snapshot.DependencyFailure, Is.EqualTo(Some "Dependency startup failed (SqlException)."))
                Assert.That(snapshot.LastUnsupportedContract, Is.EqualTo(Some "UsageFact SchemaVersion '2' is not supported. Expected '1'.")))
        )

    /// Verifies readiness clears dependency failure after the worker proves startup dependencies.
    [<Test>]
    member _.ReadinessSnapshotMarksWorkerReadyAfterDependencyStartup() =
        let readiness = OperationsUsageReadinessState()
        let recorder = readiness :> IOperationsUsageReadinessRecorder

        recorder.MarkDependencyFailure("Dependency startup failed (ServiceBusException).")
        recorder.MarkReady()

        let snapshot =
            (readiness :> IOperationsUsageReadinessProbe)
                .GetSnapshot()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(snapshot.Status, Is.EqualTo(OperationsUsageReadinessStatus.Ready))
                Assert.That(snapshot.SupportedUsageFactSchemaVersion, Is.EqualTo(UsageFactSchemaVersion))
                Assert.That(snapshot.DependencyFailure, Is.EqualTo(None)))
        )

    /// Verifies post-start Service Bus processor faults downgrade readiness through the shared worker state.
    [<Test>]
    member _.ServiceBusProcessorReceiveFaultDowngradesReadinessAfterStartup() =
        let readiness = OperationsUsageReadinessState()
        let recorder = readiness :> IOperationsUsageReadinessRecorder

        recorder.MarkReady()

        OperationsUsageReadinessTransitions.recordServiceBusProcessorFault
            recorder
            Azure.Messaging.ServiceBus.ServiceBusErrorSource.Receive
            (InvalidOperationException("receive-link-secret"))

        let snapshot = readinessSnapshot readiness

        let dependencyFailure =
            snapshot.DependencyFailure
            |> Option.defaultValue String.Empty

        Assert.Multiple(
            Action (fun () ->
                Assert.That(snapshot.Status, Is.EqualTo(OperationsUsageReadinessStatus.NotReady))
                Assert.That(snapshot.DependencyFailure, Is.EqualTo(Some "Service Bus processor fault (Receive, InvalidOperationException)."))
                Assert.That(dependencyFailure, Does.Not.Contain("receive-link-secret")))
        )

    /// Verifies the standard health-check adapter exposes redacted readiness data.
    [<Test>]
    member _.ReadinessHealthCheckReportsDependencyFailureAndSupportedContract() =
        task {
            let readiness = OperationsUsageReadinessState()
            let recorder = readiness :> IOperationsUsageReadinessRecorder

            recorder.MarkDependencyFailure("Dependency startup failed (SqlException).")
            recorder.MarkUnsupportedContract("UsageFact SchemaVersion '2' is not supported. Expected '1'.")

            let healthCheck = OperationsUsageReadinessHealthCheck(readiness :> IOperationsUsageReadinessProbe)

            let! result =
                (healthCheck :> IHealthCheck)
                    .CheckHealthAsync(HealthCheckContext(), CancellationToken.None)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(result.Status, Is.EqualTo(HealthStatus.Unhealthy))
                    Assert.That(result.Description, Is.EqualTo("Dependency startup failed (SqlException)."))
                    Assert.That(result.Data["supportedUsageFactSchemaVersion"], Is.EqualTo(UsageFactSchemaVersion))
                    Assert.That(result.Data["dependencyFailure"], Is.EqualTo("Dependency startup failed (SqlException)."))
                    Assert.That(result.Data["lastUnsupportedContract"], Is.EqualTo("UsageFact SchemaVersion '2' is not supported. Expected '1'.")))
            )
        }

    /// Verifies the worker health publisher makes readiness log-visible without publishing payloads or secrets.
    [<Test>]
    member _.ReadinessHealthPublisherLogsRedactedStatus() =
        task {
            let data = Dictionary<string, obj>()
            data["supportedUsageFactSchemaVersion"] <- box UsageFactSchemaVersion
            data["dependencyFailure"] <- box "Dependency startup failed (SqlException)."
            data["lastUnsupportedContract"] <- box "UsageFact SchemaVersion '2' is not supported. Expected '1'."
            data["messageBody"] <- box "message-body-secret"
            data["connectionString"] <- box "Endpoint=sb://example/;SharedAccessKey=secret"

            let entry = HealthReportEntry(HealthStatus.Unhealthy, "Dependency startup failed (SqlException).", TimeSpan.Zero, null, data)

            let entries = Dictionary<string, HealthReportEntry>()
            entries["operations-usage-ingestion"] <- entry
            let report = HealthReport(entries, TimeSpan.Zero)

            let logger = RecordingLogger<OperationsUsageReadinessHealthCheckPublisher>()
            let publisher = OperationsUsageReadinessHealthCheckPublisher(logger)

            do!
                (publisher :> IHealthCheckPublisher)
                    .PublishAsync(report, CancellationToken.None)

            let loggedText = String.Join("\n", logger.Messages)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(loggedText, Does.Contain("Operations usage readiness published"))
                    Assert.That(loggedText, Does.Contain("Unhealthy"))
                    Assert.That(loggedText, Does.Contain("Dependency startup failed (SqlException)."))
                    Assert.That(loggedText, Does.Contain($"UsageFact SchemaVersion '2' is not supported. Expected '{UsageFactSchemaVersion}'."))
                    Assert.That(loggedText, Does.Not.Contain("message-body-secret"))
                    Assert.That(loggedText, Does.Not.Contain("SharedAccessKey=secret")))
            )
        }

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

    /// Verifies unsupported Service Bus envelopes dead-letter before the worker reads the body.
    [<Test>]
    member _.UnsupportedEnvelopeIsDeadLetteredWithoutStorage() =
        task {
            let fact = OperationsWorkerIngestionTestData.usageFact (Guid.Parse("12121212-1212-4212-8212-121212121212"))

            let processor, store, actions, events, readiness = createProcessorWithReadiness (fun storedFact _ -> Task.FromResult(Ok(accepted storedFact)))

            let message =
                fact
                |> OperationsWorkerIngestionTestData.serializeFact
                |> OperationsWorkerIngestionTestData.message
                |> fun value -> { value with Subject = "FutureOperationalFact" }

            do! processor.ProcessMessageAsync(message, actions, CancellationToken.None)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(store.StoredFacts, Is.Empty)
                    Assert.That(eventText events, Is.EqualTo("dead-letter"))
                    Assert.That(settlementText actions.Settlements, Is.EqualTo("dead-letter:UnsupportedOperationalFactEnvelope"))
                    Assert.That(lastUnsupportedContract readiness, Does.Contain("Unsupported Service Bus envelope")))
            )
        }

    /// Verifies unsupported message type properties dead-letter as unsupported envelopes.
    [<Test>]
    member _.UnsupportedMessageTypeIsDeadLetteredWithoutStorage() =
        task {
            let fact = OperationsWorkerIngestionTestData.usageFact (Guid.Parse("16161616-1616-4616-8616-161616161616"))

            let processor, store, actions, events, readiness = createProcessorWithReadiness (fun storedFact _ -> Task.FromResult(Ok(accepted storedFact)))

            let applicationProperties = Dictionary<string, obj>()
            applicationProperties[OperationalFactEnvelope.UsageFactMessageTypeProperty] <- box "FutureUsageFact"
            applicationProperties[OperationalFactEnvelope.UsageFactKindProperty] <- box "RepositoryStorageBytesMinute"

            let message =
                fact
                |> OperationsWorkerIngestionTestData.serializeFact
                |> OperationsWorkerIngestionTestData.message
                |> fun value -> { value with ApplicationProperties = applicationProperties }

            do! processor.ProcessMessageAsync(message, actions, CancellationToken.None)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(store.StoredFacts, Is.Empty)
                    Assert.That(eventText events, Is.EqualTo("dead-letter"))
                    Assert.That(settlementText actions.Settlements, Is.EqualTo("dead-letter:UnsupportedOperationalFactEnvelope"))
                    Assert.That(lastUnsupportedContract readiness, Does.Contain("Unsupported Service Bus envelope")))
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

    /// Verifies future UsageFact schemas dead-letter with a deterministic reason before storage.
    [<Test>]
    member _.UnsupportedUsageFactSchemaIsDeadLetteredWithoutStorage() =
        task {
            let fact =
                { OperationsWorkerIngestionTestData.usageFact (Guid.Parse("13131313-1313-4313-8313-131313131313")) with
                    SchemaVersion = UsageFactSchemaVersion + 1
                }

            let processor, store, actions, events, readiness = createProcessorWithReadiness (fun storedFact _ -> Task.FromResult(Ok(accepted storedFact)))

            let message =
                fact
                |> OperationsWorkerIngestionTestData.serializeFact
                |> OperationsWorkerIngestionTestData.message

            do! processor.ProcessMessageAsync(message, actions, CancellationToken.None)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(store.StoredFacts, Is.Empty)
                    Assert.That(eventText events, Is.EqualTo("dead-letter"))
                    Assert.That(settlementText actions.Settlements, Is.EqualTo("dead-letter:UnsupportedUsageFactSchema"))
                    Assert.That(lastUnsupportedContract readiness, Does.Contain("SchemaVersion '2' is not supported")))
            )
        }

    /// Verifies future schemas with lenient JSON classify as unsupported schema before v1 enum deserialization.
    [<Test>]
    member _.FutureSchemaWithTrailingCommaAndUnknownEnumsIsDeadLetteredAsUnsupportedSchema() =
        task {
            let fact = OperationsWorkerIngestionTestData.usageFact (Guid.Parse("17171717-1717-4717-8717-171717171717"))

            let processor, store, actions, events, readiness = createProcessorWithReadiness (fun storedFact _ -> Task.FromResult(Ok(accepted storedFact)))

            let futureSchemaJson =
                let document =
                    JsonNode
                        .Parse(JsonSerializer.Serialize(fact, Constants.JsonSerializerOptions))
                        .AsObject()

                document["SchemaVersion"] <- JsonValue.Create(UsageFactSchemaVersion + 1)
                document["FactKind"] <- JsonValue.Create("futureUsageFactKind")
                document["Confidence"] <- JsonValue.Create("futureConfidence")

                let json =
                    document
                        .ToJsonString(Constants.JsonSerializerOptions)
                        .TrimEnd()

                json.Substring(0, json.Length - 1)
                + $",{Environment.NewLine}  // future producers may send JSON accepted by Grace reader options{Environment.NewLine}}}"

            let message =
                futureSchemaJson
                |> Encoding.UTF8.GetBytes
                |> OperationsWorkerIngestionTestData.message

            do! processor.ProcessMessageAsync(message, actions, CancellationToken.None)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(store.StoredFacts, Is.Empty)
                    Assert.That(eventText events, Is.EqualTo("dead-letter"))
                    Assert.That(settlementText actions.Settlements, Is.EqualTo("dead-letter:UnsupportedUsageFactSchema"))
                    Assert.That(lastUnsupportedContract readiness, Does.Contain("SchemaVersion '2' is not supported")))
            )
        }

    /// Verifies missing UsageFact identity is treated as impossible poison input.
    [<Test>]
    member _.MissingUsageFactIdIsDeadLetteredWithoutStorage() =
        task {
            let fact = { OperationsWorkerIngestionTestData.usageFact (Guid.Parse("14141414-1414-4414-8414-141414141414")) with UsageFactId = Guid.Empty }

            let processor, store, actions, events = createProcessor (fun storedFact _ -> Task.FromResult(Ok(accepted storedFact)))

            let message =
                fact
                |> OperationsWorkerIngestionTestData.serializeFact
                |> OperationsWorkerIngestionTestData.message

            do! processor.ProcessMessageAsync(message, actions, CancellationToken.None)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(store.StoredFacts, Is.Empty)
                    Assert.That(eventText events, Is.EqualTo("dead-letter"))
                    Assert.That(settlementText actions.Settlements, Is.EqualTo("dead-letter:InvalidUsageFact")))
            )
        }

    /// Verifies non-positive quantities are treated as impossible poison input.
    [<Test>]
    member _.InvalidQuantityIsDeadLetteredWithoutStorage() =
        task {
            let fact = { OperationsWorkerIngestionTestData.usageFact (Guid.Parse("15151515-1515-4515-8515-151515151515")) with Quantity = 0L }

            let processor, store, actions, events = createProcessor (fun storedFact _ -> Task.FromResult(Ok(accepted storedFact)))

            let message =
                fact
                |> OperationsWorkerIngestionTestData.serializeFact
                |> OperationsWorkerIngestionTestData.message

            do! processor.ProcessMessageAsync(message, actions, CancellationToken.None)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(store.StoredFacts, Is.Empty)
                    Assert.That(eventText events, Is.EqualTo("dead-letter"))
                    Assert.That(settlementText actions.Settlements, Is.EqualTo("dead-letter:InvalidUsageFact")))
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

            let processor, store, actions, events, readiness =
                createProcessorWithReadiness (fun _ _ ->
                    Task.FromException<Result<UsageFactPersistenceResult, string list>>(
                        InvalidOperationException("forced SQL failure with SharedAccessKey=secret")
                    ))

            (readiness :> IOperationsUsageReadinessRecorder)
                .MarkReady()

            let message =
                fact
                |> OperationsWorkerIngestionTestData.serializeFact
                |> OperationsWorkerIngestionTestData.message

            do! processor.ProcessMessageAsync(message, actions, CancellationToken.None)

            let snapshot = readinessSnapshot readiness

            let dependencyFailure =
                snapshot.DependencyFailure
                |> Option.defaultValue String.Empty

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(List.length store.StoredFacts, Is.EqualTo(1))
                    Assert.That(eventText events, Is.EqualTo("store|abandon"))
                    Assert.That(settlementText actions.Settlements, Is.EqualTo("abandon"))
                    Assert.That(snapshot.Status, Is.EqualTo(OperationsUsageReadinessStatus.NotReady))
                    Assert.That(snapshot.DependencyFailure, Is.EqualTo(Some "Runtime processing dependency failed (InvalidOperationException)."))
                    Assert.That(dependencyFailure, Does.Not.Contain("SharedAccessKey=secret")))
            )
        }

    /// Verifies a later durable success clears a runtime dependency failure recorded by an earlier storage exception.
    [<Test>]
    member _.StorageFailureReadinessClearsAfterLaterSuccess() =
        task {
            let firstFact = OperationsWorkerIngestionTestData.usageFact (Guid.Parse("18181818-1818-4818-8818-181818181818"))

            let secondFact = OperationsWorkerIngestionTestData.usageFact (Guid.Parse("19191919-1919-4919-8919-191919191919"))

            let mutable shouldFail = true

            let processor, _store, actions, events, readiness =
                createProcessorWithReadiness (fun storedFact _ ->
                    if shouldFail then
                        shouldFail <- false
                        Task.FromException<Result<UsageFactPersistenceResult, string list>>(InvalidOperationException("forced SQL failure"))
                    else
                        Task.FromResult(Ok(accepted storedFact)))

            (readiness :> IOperationsUsageReadinessRecorder)
                .MarkReady()

            let firstMessage =
                firstFact
                |> OperationsWorkerIngestionTestData.serializeFact
                |> OperationsWorkerIngestionTestData.message

            let secondMessage =
                secondFact
                |> OperationsWorkerIngestionTestData.serializeFact
                |> OperationsWorkerIngestionTestData.message

            do! processor.ProcessMessageAsync(firstMessage, actions, CancellationToken.None)

            let failedSnapshot = readinessSnapshot readiness

            do! processor.ProcessMessageAsync(secondMessage, actions, CancellationToken.None)

            let recoveredSnapshot = readinessSnapshot readiness

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(eventText events, Is.EqualTo("store|abandon|store|complete"))
                    Assert.That(settlementText actions.Settlements, Is.EqualTo("abandon|complete"))
                    Assert.That(failedSnapshot.Status, Is.EqualTo(OperationsUsageReadinessStatus.NotReady))
                    Assert.That(failedSnapshot.DependencyFailure, Is.EqualTo(Some "Runtime processing dependency failed (InvalidOperationException)."))
                    Assert.That(recoveredSnapshot.Status, Is.EqualTo(OperationsUsageReadinessStatus.Ready))
                    Assert.That(recoveredSnapshot.DependencyFailure, Is.EqualTo(None)))
            )
        }

    /// Verifies in-flight message success cannot clear a Service Bus receive/link readiness failure.
    [<Test>]
    member _.InFlightSuccessDoesNotClearServiceBusReceiveFaultReadiness() =
        task {
            let fact = OperationsWorkerIngestionTestData.usageFact (Guid.Parse("1d1d1d1d-1d1d-4d1d-8d1d-1d1d1d1d1d1d"))

            let storeStarted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let durableSuccess = TaskCompletionSource<Result<UsageFactPersistenceResult, string list>>(TaskCreationOptions.RunContinuationsAsynchronously)

            let processor, _store, actions, events, readiness =
                createProcessorWithReadiness (fun storedFact _ ->
                    storeStarted.SetResult(())
                    durableSuccess.Task)

            let recorder = readiness :> IOperationsUsageReadinessRecorder
            recorder.MarkReady()

            let message =
                fact
                |> OperationsWorkerIngestionTestData.serializeFact
                |> OperationsWorkerIngestionTestData.message

            let processing = processor.ProcessMessageAsync(message, actions, CancellationToken.None)

            do! storeStarted.Task

            OperationsUsageReadinessTransitions.recordServiceBusProcessorFault
                recorder
                Azure.Messaging.ServiceBus.ServiceBusErrorSource.Receive
                (InvalidOperationException("receive-link-secret"))

            durableSuccess.SetResult(Ok(accepted fact))
            do! processing

            let snapshot = readinessSnapshot readiness

            let dependencyFailure =
                snapshot.DependencyFailure
                |> Option.defaultValue String.Empty

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(eventText events, Is.EqualTo("store|complete"))
                    Assert.That(settlementText actions.Settlements, Is.EqualTo("complete"))
                    Assert.That(snapshot.Status, Is.EqualTo(OperationsUsageReadinessStatus.NotReady))
                    Assert.That(snapshot.DependencyFailure, Is.EqualTo(Some "Service Bus processor fault (Receive, InvalidOperationException)."))
                    Assert.That(dependencyFailure, Does.Not.Contain("receive-link-secret")))
            )
        }

    /// Verifies an older in-flight success cannot clear a newer runtime dependency failure.
    [<Test>]
    member _.StaleInFlightSuccessDoesNotClearNewerDependencyFailure() =
        task {
            let olderSuccessFact = OperationsWorkerIngestionTestData.usageFact (Guid.Parse("1a1a1a1a-1a1a-4a1a-8a1a-1a1a1a1a1a1a"))

            let newerFailureFact = OperationsWorkerIngestionTestData.usageFact (Guid.Parse("1b1b1b1b-1b1b-4b1b-8b1b-1b1b1b1b1b1b"))

            let newerSuccessFact = OperationsWorkerIngestionTestData.usageFact (Guid.Parse("1c1c1c1c-1c1c-4c1c-8c1c-1c1c1c1c1c1c"))

            let processor, _store, _actions, events, readiness =
                createProcessorWithReadiness (fun storedFact _ ->
                    if storedFact.UsageFactId = newerFailureFact.UsageFactId then
                        Task.FromException<Result<UsageFactPersistenceResult, string list>>(InvalidOperationException("newer SQL failure"))
                    else
                        Task.FromResult(Ok(accepted storedFact)))

            (readiness :> IOperationsUsageReadinessRecorder)
                .MarkReady()

            let olderActions = BlockingCompleteMessageActions(events)
            let newerFailureActions = RecordingMessageActions(events)
            let newerSuccessActions = RecordingMessageActions(events)

            let olderMessage =
                olderSuccessFact
                |> OperationsWorkerIngestionTestData.serializeFact
                |> OperationsWorkerIngestionTestData.message

            let newerFailureMessage =
                newerFailureFact
                |> OperationsWorkerIngestionTestData.serializeFact
                |> OperationsWorkerIngestionTestData.message

            let newerSuccessMessage =
                newerSuccessFact
                |> OperationsWorkerIngestionTestData.serializeFact
                |> OperationsWorkerIngestionTestData.message

            let olderProcessing = processor.ProcessMessageAsync(olderMessage, olderActions, CancellationToken.None)

            do! olderActions.CompleteStarted

            do! processor.ProcessMessageAsync(newerFailureMessage, newerFailureActions, CancellationToken.None)

            let failedSnapshot = readinessSnapshot readiness

            olderActions.ReleaseComplete()
            do! olderProcessing

            let staleCompletionSnapshot = readinessSnapshot readiness

            do! processor.ProcessMessageAsync(newerSuccessMessage, newerSuccessActions, CancellationToken.None)

            let recoveredSnapshot = readinessSnapshot readiness

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(eventText events, Is.EqualTo("store|complete-waiting|store|abandon|complete|store|complete"))
                    Assert.That(settlementText olderActions.Settlements, Is.EqualTo("complete"))
                    Assert.That(settlementText newerFailureActions.Settlements, Is.EqualTo("abandon"))
                    Assert.That(settlementText newerSuccessActions.Settlements, Is.EqualTo("complete"))
                    Assert.That(failedSnapshot.Status, Is.EqualTo(OperationsUsageReadinessStatus.NotReady))
                    Assert.That(failedSnapshot.DependencyFailure, Is.EqualTo(Some "Runtime processing dependency failed (InvalidOperationException)."))
                    Assert.That(staleCompletionSnapshot.Status, Is.EqualTo(OperationsUsageReadinessStatus.NotReady))
                    Assert.That(staleCompletionSnapshot.DependencyFailure, Is.EqualTo(Some "Runtime processing dependency failed (InvalidOperationException)."))
                    Assert.That(recoveredSnapshot.Status, Is.EqualTo(OperationsUsageReadinessStatus.Ready))
                    Assert.That(recoveredSnapshot.DependencyFailure, Is.EqualTo(None)))
            )
        }

    /// Verifies usage facts that violate SQL-bound shape limits dead-letter instead of being retried.
    [<Test>]
    member _.SqlShapeValidationFailureIsDeadLettered() =
        task {
            let overlongCorrelationId = CorrelationId(String('c', OperationsUsageSql.CorrelationIdMaxLength + 1))

            let overlongStoragePoolId = StoragePoolId(String('s', OperationsUsageSql.StoragePoolIdMaxLength + 1))

            let fact =
                UsageFact.RepositoryStorageBytesMinute(
                    Guid.Parse("99999999-9999-9999-9999-999999999999"),
                    overlongCorrelationId,
                    OperationsWorkerIngestionTestData.ownerId,
                    OperationsWorkerIngestionTestData.organizationId,
                    OperationsWorkerIngestionTestData.repositoryId,
                    overlongStoragePoolId,
                    4096L,
                    Instant.FromUtc(2026, 7, 4, 12, 39, 0)
                )

            let processor, actions, events = createProcessorWithRealStore ()

            let message =
                fact
                |> OperationsWorkerIngestionTestData.serializeFact
                |> OperationsWorkerIngestionTestData.message

            do! processor.ProcessMessageAsync(message, actions, CancellationToken.None)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(eventText events, Is.EqualTo("dead-letter"))
                    Assert.That(settlementText actions.Settlements, Is.EqualTo("dead-letter:InvalidUsageFact")))
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
