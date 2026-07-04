namespace Grace.Operations.Tests

open Grace.Operations.Data
open Grace.Types.Common
open Grace.Types.Usage
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

/// Provides deterministic usage facts for operations storage tests.
module OperationsUsageStorageTestData =

    /// Provides the owner used by all test usage facts.
    let ownerId = OwnerId.Parse("11111111-1111-1111-1111-111111111111")

    /// Provides the organization used by all test usage facts.
    let organizationId = OrganizationId.Parse("22222222-2222-2222-2222-222222222222")

    /// Provides the repository used by all test usage facts.
    let repositoryId = RepositoryId.Parse("33333333-3333-3333-3333-333333333333")

    /// Provides the storage pool used by all test usage facts.
    let storagePoolId = StoragePoolId "storage-pool-main"

    /// Provides a second storage pool whose value differs only by case.
    let storagePoolIdWithDifferentCase = StoragePoolId "Storage-Pool-Main"

    /// Creates a valid repository storage usage fact with deterministic scope and resource values.
    let fact usageFactId quantity observedAt =
        UsageFact.RepositoryStorageBytesMinute(
            usageFactId,
            CorrelationId $"corr-{usageFactId}",
            ownerId,
            organizationId,
            repositoryId,
            storagePoolId,
            quantity,
            observedAt
        )

    /// Creates a valid repository storage usage fact with an explicit storage-pool identity.
    let factForStoragePool usageFactId storagePoolId quantity observedAt =
        UsageFact.RepositoryStorageBytesMinute(
            usageFactId,
            CorrelationId $"corr-{usageFactId}",
            ownerId,
            organizationId,
            repositoryId,
            storagePoolId,
            quantity,
            observedAt
        )

/// Stores transaction state for the operations usage test double.
type private InMemoryOperationsUsageState = { RawFacts: Dictionary<UsageFactId, RawUsageFact>; Aggregates: Dictionary<UsageAggregateMinuteKey, int64> }

/// Provides a rollback-capable transaction scope for proving data-layer ordering without SQL Server.
type private InMemoryOperationsUsageTransactionScope() =
    let state = { RawFacts = Dictionary<UsageFactId, RawUsageFact>(); Aggregates = Dictionary<UsageAggregateMinuteKey, int64>() }

    let mutable failNextAggregateUpdate = false

    /// Replaces dictionary contents after a transaction commits.
    let replaceDictionary (target: Dictionary<'TKey, 'TValue>) (source: Dictionary<'TKey, 'TValue>) =
        target.Clear()

        source
        |> Seq.iter (fun pair -> target[pair.Key] <- pair.Value)

    /// Returns the number of committed raw facts.
    member _.RawFactCount = state.RawFacts.Count

    /// Returns the committed quantity for an aggregate row.
    member _.AggregateQuantity(key: UsageAggregateMinuteKey) =
        match state.Aggregates.TryGetValue key with
        | true, quantity -> quantity
        | false, _ -> 0L

    /// Forces the next aggregate update to fail after the raw insert has been staged.
    member _.FailNextAggregateUpdate() = failNextAggregateUpdate <- true

    interface IOperationsUsageTransactionScope with

        member _.ExecuteAsync(operation, cancellationToken) =
            task {
                cancellationToken.ThrowIfCancellationRequested()

                let rawFacts = Dictionary<UsageFactId, RawUsageFact>(state.RawFacts)
                let aggregates = Dictionary<UsageAggregateMinuteKey, int64>(state.Aggregates)

                let transaction =
                    { new IOperationsUsageTransaction with
                        member _.TryInsertRawUsageFactAsync(rawFact, insertCancellationToken) =
                            insertCancellationToken.ThrowIfCancellationRequested()

                            if rawFacts.ContainsKey rawFact.UsageFactId then
                                Task.FromResult false
                            else
                                rawFacts.Add(rawFact.UsageFactId, rawFact)
                                Task.FromResult true

                        member _.AddToUsageAggregateMinuteAsync(aggregate, updateCancellationToken) =
                            updateCancellationToken.ThrowIfCancellationRequested()

                            if failNextAggregateUpdate then
                                failNextAggregateUpdate <- false
                                Task.FromException(InvalidOperationException("forced aggregate failure"))
                            else
                                let current =
                                    match aggregates.TryGetValue aggregate.Key with
                                    | true, quantity -> quantity
                                    | false, _ -> 0L

                                aggregates[aggregate.Key] <- current + aggregate.Quantity
                                Task.CompletedTask
                    }

                let! result = operation transaction cancellationToken

                replaceDictionary state.RawFacts rawFacts
                replaceDictionary state.Aggregates aggregates

                return result
            }

/// Covers operations usage storage idempotency and aggregate projection behavior.
[<TestFixture>]
type OperationsUsageStorageTests() =

    /// Builds a store backed by the rollback-capable test transaction scope.
    let createStore () =
        let transactionScope = InMemoryOperationsUsageTransactionScope()
        OperationsUsageStore transactionScope, transactionScope

    /// Extracts a successful storage result from the data-layer result shape.
    let requireStored (result: Result<UsageFactPersistenceResult, string list>) =
        match result with
        | Ok stored -> stored
        | Error errors ->
            let errorText = String.Join("; ", errors)
            failwith $"Storage failed validation: {errorText}"

    /// Extracts a successful persistence plan from validation.
    let requirePlan (result: Result<UsageFactPersistencePlan, string list>) =
        match result with
        | Ok plan -> plan
        | Error errors ->
            let errorText = String.Join("; ", errors)
            failwith $"Persistence plan failed validation: {errorText}"

    /// Verifies the SQL DDL keeps raw fact identity as the durable dedupe boundary.
    [<Test>]
    member _.SqlSchemaUsesUsageFactIdAsRawFactPrimaryKey() =
        Assert.Multiple(
            Action (fun () ->
                Assert.That(OperationsUsageSql.CreateSchema, Does.Contain("CREATE SCHEMA ops"))
                Assert.That(OperationsUsageSql.CreateDatabaseIfMissing, Does.Contain("DB_ID(@DatabaseName) IS NULL"))
                Assert.That(OperationsUsageSql.CreateDatabaseIfMissing, Does.Contain("QUOTENAME(@DatabaseName)"))
                Assert.That(OperationsUsageSql.CreateRawUsageFactTable, Does.Contain("ops.RawUsageFact"))
                Assert.That(OperationsUsageSql.CreateRawUsageFactTable, Does.Contain("PRIMARY KEY CLUSTERED (UsageFactId)"))
                Assert.That(OperationsUsageSql.TryInsertRawUsageFact, Does.Contain("WITH (UPDLOCK, HOLDLOCK)"))
                Assert.That(OperationsUsageSql.CreateUsageAggregateMinuteTable, Does.Contain("ops.UsageAggregateMinute"))
                Assert.That(OperationsUsageSql.AddToUsageAggregateMinute, Does.Contain("MERGE ops.UsageAggregateMinute WITH (HOLDLOCK)")))
        )

    /// Verifies SQL storage-pool keys stay case-sensitive under Azure SQL default collations.
    [<Test>]
    member _.SqlSchemaUsesBinaryCollationForStoragePoolAggregateKeys() =
        Assert.Multiple(
            Action (fun () ->
                Assert.That(OperationsUsageSql.CaseSensitiveStoragePoolIdCollation, Is.EqualTo("Latin1_General_100_BIN2"))

                Assert.That(OperationsUsageSql.CreateRawUsageFactTable, Does.Contain("StoragePoolId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL"))

                Assert.That(
                    OperationsUsageSql.CreateUsageAggregateMinuteTable,
                    Does.Contain("StoragePoolId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL")
                )

                Assert.That(
                    OperationsUsageSql.AddToUsageAggregateMinute,
                    Does.Contain("CAST(@StoragePoolId AS nvarchar(256)) COLLATE Latin1_General_100_BIN2 AS StoragePoolId")
                ))
        )

    /// Verifies storage-pool identities that differ only by case produce separate aggregate keys before SQL persistence.
    [<Test>]
    member _.StoragePoolIdsDifferingOnlyByCaseProduceDistinctAggregateKeys() =
        let observedAt = Instant.FromUtc(2026, 7, 4, 12, 36, 0)

        let lowerPoolPlan =
            OperationsUsageStorageTestData.factForStoragePool
                (Guid.Parse("12121212-1212-1212-1212-121212121212"))
                OperationsUsageStorageTestData.storagePoolId
                1L
                observedAt
            |> UsageFactPersistencePlan.tryCreate
            |> requirePlan

        let mixedPoolPlan =
            OperationsUsageStorageTestData.factForStoragePool
                (Guid.Parse("34343434-3434-3434-3434-343434343434"))
                OperationsUsageStorageTestData.storagePoolIdWithDifferentCase
                1L
                observedAt
            |> UsageFactPersistencePlan.tryCreate
            |> requirePlan

        Assert.Multiple(
            Action (fun () ->
                Assert.That(lowerPoolPlan.Aggregate.Key.StoragePoolId, Is.Not.EqualTo(mixedPoolPlan.Aggregate.Key.StoragePoolId))
                Assert.That(lowerPoolPlan.Aggregate.Key, Is.Not.EqualTo(mixedPoolPlan.Aggregate.Key)))
        )

    /// Verifies facts that exceed SQL column widths are rejected before SQL Server can truncate them.
    [<Test>]
    member _.PersistencePlanRejectsSqlBoundStringOverflows() =
        let overlongCorrelationId = CorrelationId(String('c', OperationsUsageSql.CorrelationIdMaxLength + 1))

        let overlongStoragePoolId = StoragePoolId(String('s', OperationsUsageSql.StoragePoolIdMaxLength + 1))

        let fact =
            UsageFact.RepositoryStorageBytesMinute(
                Guid.Parse("56565656-5656-5656-5656-565656565656"),
                overlongCorrelationId,
                OperationsUsageStorageTestData.ownerId,
                OperationsUsageStorageTestData.organizationId,
                OperationsUsageStorageTestData.repositoryId,
                overlongStoragePoolId,
                1L,
                Instant.FromUtc(2026, 7, 4, 12, 37, 0)
            )

        match UsageFactPersistencePlan.tryCreate fact with
        | Ok _ -> Assert.Fail("Overlong SQL-bound fact fields should be rejected before persistence.")
        | Error errors ->
            let errorText = String.Join("|", errors)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(errorText, Does.Contain($"CorrelationId must be {OperationsUsageSql.CorrelationIdMaxLength} characters or fewer"))

                    Assert.That(errorText, Does.Contain($"Resource.StoragePoolId must be {OperationsUsageSql.StoragePoolIdMaxLength} characters or fewer")))
            )

    /// Verifies the production SQL transaction scope satisfies the store dependency.
    [<Test>]
    member _.SqlTransactionScopeImplementsOperationsUsageTransactionScope() =
        let transactionScope = SqlOperationsUsageTransactionScope "Server=(localdb)\\MSSQLLocalDB;Database=GraceOperationsTests;Integrated Security=true;"

        Assert.That(transactionScope, Is.InstanceOf<IOperationsUsageTransactionScope>())

    /// Verifies default schema initialization uses the configured target database without requiring `master`.
    [<Test>]
    member _.DefaultSchemaBootstrapUsesTargetDatabaseConnectionOnly() =
        let plan =
            OperationsUsageSchemaBootstrapPlan.create
                "Server=tcp:sql.example.net;Database=GraceOperations;Authentication=Active Directory Default;"
                OperationsUsageSchemaBootstrapMode.TargetDatabaseOnly

        let schemaBuilder = Microsoft.Data.SqlClient.SqlConnectionStringBuilder(plan.SchemaConnectionString)

        Assert.Multiple(
            Action (fun () ->
                Assert.That(plan.TargetDatabaseName, Is.EqualTo(Some "GraceOperations"))
                Assert.That(schemaBuilder.InitialCatalog, Is.EqualTo("GraceOperations"))
                Assert.That(plan.DatabaseCreationConnectionString, Is.EqualTo(None)))
        )

    /// Verifies database creation remains available only when an admin bootstrap mode is explicitly selected.
    [<Test>]
    member _.ExplicitSchemaBootstrapUsesMasterConnectionForDatabaseCreation() =
        let plan =
            OperationsUsageSchemaBootstrapPlan.create
                "Server=tcp:sql.example.net;Database=GraceOperations;Authentication=Active Directory Default;"
                OperationsUsageSchemaBootstrapMode.CreateDatabaseIfMissing

        let creationBuilder = Microsoft.Data.SqlClient.SqlConnectionStringBuilder(plan.DatabaseCreationConnectionString.Value)

        let schemaBuilder = Microsoft.Data.SqlClient.SqlConnectionStringBuilder(plan.SchemaConnectionString)

        Assert.Multiple(
            Action (fun () ->
                Assert.That(plan.TargetDatabaseName, Is.EqualTo(Some "GraceOperations"))
                Assert.That(creationBuilder.InitialCatalog, Is.EqualTo("master"))
                Assert.That(schemaBuilder.InitialCatalog, Is.EqualTo("GraceOperations")))
        )

    /// Verifies a first usage fact inserts a raw row and increments the expected aggregate minute.
    [<Test>]
    member _.FirstFactStoresRawFactAndIncrementsMinuteAggregate() =
        task {
            let store, transactionScope = createStore ()
            let usageFactId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
            let fact = OperationsUsageStorageTestData.fact usageFactId 4096L (Instant.FromUtc(2026, 7, 4, 12, 34, 56))

            let! result = store.StoreUsageFactAsync(fact, CancellationToken.None)
            let stored = requireStored result

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(stored.Status, Is.EqualTo(UsageFactPersistenceStatus.Accepted))
                    Assert.That(transactionScope.RawFactCount, Is.EqualTo(1))
                    Assert.That(stored.Aggregate.IsSome, Is.True)
                    Assert.That(transactionScope.AggregateQuantity(stored.Aggregate.Value.Key), Is.EqualTo(4096L)))
            )
        }

    /// Verifies duplicate `UsageFactId` delivery is acknowledged without changing aggregate totals.
    [<Test>]
    member _.DuplicateUsageFactIdDoesNotDoubleCountAggregate() =
        task {
            let store, transactionScope = createStore ()
            let usageFactId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
            let fact = OperationsUsageStorageTestData.fact usageFactId 2048L (Instant.FromUtc(2026, 7, 4, 12, 34, 0))

            let! firstResult = store.StoreUsageFactAsync(fact, CancellationToken.None)
            let firstStored = requireStored firstResult

            let! duplicateResult = store.StoreUsageFactAsync(fact, CancellationToken.None)
            let duplicateStored = requireStored duplicateResult

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(firstStored.Status, Is.EqualTo(UsageFactPersistenceStatus.Accepted))
                    Assert.That(duplicateStored.Status, Is.EqualTo(UsageFactPersistenceStatus.AlreadyProcessed))
                    Assert.That(duplicateStored.Aggregate, Is.EqualTo(None))
                    Assert.That(transactionScope.RawFactCount, Is.EqualTo(1))
                    Assert.That(transactionScope.AggregateQuantity(firstStored.Aggregate.Value.Key), Is.EqualTo(2048L)))
            )
        }

    /// Verifies raw insertion and aggregate projection roll back together when projection fails.
    [<Test>]
    member _.RawInsertAndAggregateProjectionRollbackTogetherOnFailure() =
        task {
            let store, transactionScope = createStore ()
            let usageFactId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")
            let fact = OperationsUsageStorageTestData.fact usageFactId 1024L (Instant.FromUtc(2026, 7, 4, 12, 35, 0))
            transactionScope.FailNextAggregateUpdate()

            let mutable thrownMessage = None

            try
                let! _ = store.StoreUsageFactAsync(fact, CancellationToken.None)
                Assert.Fail("Aggregate failure should propagate instead of reporting successful storage.")
            with
            | :? InvalidOperationException as ex -> thrownMessage <- Some ex.Message

            Assert.That(thrownMessage, Is.EqualTo(Some "forced aggregate failure"))
            Assert.That(transactionScope.RawFactCount, Is.EqualTo(0))

            let! retryResult = store.StoreUsageFactAsync(fact, CancellationToken.None)
            let retryStored = requireStored retryResult

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(retryStored.Status, Is.EqualTo(UsageFactPersistenceStatus.Accepted))
                    Assert.That(transactionScope.RawFactCount, Is.EqualTo(1))
                    Assert.That(transactionScope.AggregateQuantity(retryStored.Aggregate.Value.Key), Is.EqualTo(1024L)))
            )
        }

    /// Verifies aggregate minute bucketing is deterministic and UTC based.
    [<Test>]
    member _.ObservedAtBucketsAreDeterministicUtcMinuteBoundaries() =
        let first = OperationsUsageStorageTestData.fact (Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd")) 1L (Instant.FromUtc(2026, 7, 4, 12, 34, 59))

        let second = OperationsUsageStorageTestData.fact (Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee")) 1L (Instant.FromUtc(2026, 7, 4, 12, 34, 0))

        let third = OperationsUsageStorageTestData.fact (Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")) 1L (Instant.FromUtc(2026, 7, 4, 12, 35, 0))

        let firstPlan =
            UsageFactPersistencePlan.tryCreate first
            |> requirePlan

        let secondPlan =
            UsageFactPersistencePlan.tryCreate second
            |> requirePlan

        let thirdPlan =
            UsageFactPersistencePlan.tryCreate third
            |> requirePlan

        Assert.Multiple(
            Action (fun () ->
                Assert.That(firstPlan.Aggregate.Key.BucketStart, Is.EqualTo(Instant.FromUtc(2026, 7, 4, 12, 34)))
                Assert.That(secondPlan.Aggregate.Key.BucketStart, Is.EqualTo(firstPlan.Aggregate.Key.BucketStart))
                Assert.That(thirdPlan.Aggregate.Key.BucketStart, Is.EqualTo(Instant.FromUtc(2026, 7, 4, 12, 35))))
        )
