namespace Grace.Operations.Tests

open Grace.Shared
open Grace.Operations.Data
open Grace.Operations.Data.Migrations
open Grace.Types.Common
open Grace.Types.Usage
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Design
open Microsoft.EntityFrameworkCore.Infrastructure
open Microsoft.EntityFrameworkCore.Migrations
open Microsoft.EntityFrameworkCore.Metadata
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.Globalization
open System.Text.Json
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

    /// Serializes the raw usage fact payload that Operations stores for later replay and audit.
    let payloadFor fact = JsonSerializer.SerializeToUtf8Bytes(fact, Constants.JsonSerializerOptions)

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

    /// Returns one committed raw fact by durable usage-fact identity.
    member _.RawFact usageFactId = state.RawFacts[usageFactId]

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

    /// Restores a process environment variable after a design-time configuration test mutates it.
    let restoreEnvironmentVariable name value = Environment.SetEnvironmentVariable(name, value)

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

    /// Generates the Operations migration script without connecting to SQL Server.
    let migrationScript () =
        use context = OperationsDbContextFactory.create "Server=(localdb)\\MSSQLLocalDB;Database=GraceOperationsMigrationScript;Integrated Security=true;"

        let migrator = context.GetService<IMigrator>()
        migrator.GenerateScript(options = MigrationsSqlGenerationOptions.Idempotent)

    /// Generates an Operations migration script between two named migration points without opening SQL Server.
    let migrationScriptFromTo fromMigration toMigration =
        use context = OperationsDbContextFactory.create "Server=(localdb)\\MSSQLLocalDB;Database=GraceOperationsMigrationScript;Integrated Security=true;"

        let migrator = context.GetService<IMigrator>()
        migrator.GenerateScript(fromMigration, toMigration, MigrationsSqlGenerationOptions.Idempotent)

    /// Reads the EF entity metadata that future migrations use for raw fact schema drift.
    let rawFactEntityType () : IEntityType =
        use context = OperationsDbContextFactory.create "Server=(localdb)\\MSSQLLocalDB;Database=GraceOperationsMigrationModel;Integrated Security=true;"

        context.Model.FindEntityType(typeof<RawUsageFactEntity>)

    /// Reads the EF entity metadata that future migrations use for minute aggregate schema drift.
    let aggregateEntityType () : IEntityType =
        use context = OperationsDbContextFactory.create "Server=(localdb)\\MSSQLLocalDB;Database=GraceOperationsMigrationModel;Integrated Security=true;"

        context.Model.FindEntityType(typeof<UsageAggregateMinuteEntity>)

    /// Verifies EF tooling can discover a concrete design-time context factory for reviewed migrations.
    [<Test>]
    member _.OperationsDesignTimeFactoryCreatesSqlServerContext() =
        let factory = OperationsDesignTimeDbContextFactory() :> IDesignTimeDbContextFactory<OperationsDbContext>

        use context =
            factory.CreateDbContext(
                [|
                    "Server=(localdb)\\MSSQLLocalDB;Database=GraceOperationsDesignTimeTests;Integrated Security=true;"
                |]
            )

        Assert.Multiple(
            Action (fun () ->
                Assert.That(context, Is.InstanceOf<OperationsDbContext>())
                Assert.That(context.Database.ProviderName, Is.EqualTo("Microsoft.EntityFrameworkCore.SqlServer")))
        )

    /// Verifies EF tooling honors the documented Operations SQL environment variable before legacy aliases.
    [<Test>]
    [<NonParallelizable>]
    member _.OperationsDesignTimeFactoryPrefersDocumentedSqlEnvironmentVariable() =
        let documentedName = "grace__operations__sql__connectionstring"
        let legacyName = "GRACE_OPERATIONS_SQL_CONNECTION_STRING"
        let documentedConnectionString = "Server=tcp:documented.example.net;Database=GraceOperationsDocumented;"
        let legacyConnectionString = "Server=tcp:legacy.example.net;Database=GraceOperationsLegacy;"
        let previousDocumented = Environment.GetEnvironmentVariable(documentedName)
        let previousLegacy = Environment.GetEnvironmentVariable(legacyName)

        try
            Environment.SetEnvironmentVariable(documentedName, documentedConnectionString)
            Environment.SetEnvironmentVariable(legacyName, legacyConnectionString)

            let actual = OperationsDbContextFactory.designTimeConnectionString [|  |]

            Assert.That(actual, Is.EqualTo(documentedConnectionString))
        finally
            restoreEnvironmentVariable documentedName previousDocumented
            restoreEnvironmentVariable legacyName previousLegacy

    /// Verifies EF tooling still honors the previous design-time SQL environment variable as a fallback.
    [<Test>]
    [<NonParallelizable>]
    member _.OperationsDesignTimeFactoryKeepsLegacySqlEnvironmentVariableFallback() =
        let documentedName = "grace__operations__sql__connectionstring"
        let legacyName = "GRACE_OPERATIONS_SQL_CONNECTION_STRING"
        let legacyConnectionString = "Server=tcp:legacy.example.net;Database=GraceOperationsLegacy;"
        let previousDocumented = Environment.GetEnvironmentVariable(documentedName)
        let previousLegacy = Environment.GetEnvironmentVariable(legacyName)

        try
            Environment.SetEnvironmentVariable(documentedName, null)
            Environment.SetEnvironmentVariable(legacyName, legacyConnectionString)

            let actual = OperationsDbContextFactory.designTimeConnectionString [|  |]

            Assert.That(actual, Is.EqualTo(legacyConnectionString))
        finally
            restoreEnvironmentVariable documentedName previousDocumented
            restoreEnvironmentVariable legacyName previousLegacy

    /// Verifies the EF model keeps raw fact identity as the durable dedupe boundary.
    [<Test>]
    member _.OperationsEfModelUsesUsageFactIdAsRawFactPrimaryKey() =
        let rawFact = rawFactEntityType ()
        let primaryKey = rawFact.FindPrimaryKey()

        let hasScopeIndex =
            rawFact.GetIndexes()
            |> Seq.exists (fun index -> index.GetDatabaseName() = "IX_ops_RawUsageFact_ScopeKindObservedAt")

        Assert.Multiple(
            Action (fun () ->
                Assert.That(rawFact.GetSchema(), Is.EqualTo(OperationsUsageSql.SchemaName))
                Assert.That(rawFact.GetTableName(), Is.EqualTo(OperationsUsageSql.RawUsageFactTableName))
                Assert.That(primaryKey.GetName(), Is.EqualTo("PK_ops_RawUsageFact"))

                Assert.That(
                    primaryKey.Properties
                    |> Seq.map (fun property -> property.Name),
                    Is.EquivalentTo([| "UsageFactId" |])
                )

                Assert.That(
                    rawFact
                        .FindProperty("CorrelationId")
                        .GetMaxLength(),
                    Is.EqualTo(Nullable OperationsUsageSql.CorrelationIdMaxLength)
                )

                Assert.That(rawFact.FindProperty("RawPayload").GetColumnType(), Is.EqualTo("varbinary(max)"))

                Assert.That(
                    rawFact
                        .FindProperty("StoragePoolId")
                        .GetMaxLength(),
                    Is.EqualTo(Nullable OperationsUsageSql.StoragePoolIdMaxLength)
                )

                Assert.That(hasScopeIndex, Is.True)
                Assert.That(OperationsUsageSql.CreateDatabaseIfMissing, Does.Contain("DB_ID(@DatabaseName) IS NULL"))
                Assert.That(OperationsUsageSql.CreateDatabaseIfMissing, Does.Contain("QUOTENAME(@DatabaseName)"))
                Assert.That(OperationsUsageSql.TryInsertRawUsageFact, Does.Contain("WITH (UPDLOCK, HOLDLOCK)"))
                Assert.That(OperationsUsageSql.AddToUsageAggregateMinute, Does.Contain("MERGE ops.UsageAggregateMinute WITH (HOLDLOCK)")))
        )

    /// Verifies the EF model keeps minute aggregates keyed by Grace scope, storage pool, fact kind, and UTC minute.
    [<Test>]
    member _.OperationsEfModelUsesScopedMinuteAggregatePrimaryKey() =
        let aggregate = aggregateEntityType ()
        let primaryKey = aggregate.FindPrimaryKey()

        let expectedKey =
            [|
                "FactKind"
                "OwnerId"
                "OrganizationId"
                "RepositoryId"
                "StoragePoolId"
                "BucketStartUtc"
            |]

        let actualKey =
            primaryKey.Properties
            |> Seq.map (fun property -> property.Name)
            |> fun names -> String.Join("|", names)

        let hasScopeIndex =
            aggregate.GetIndexes()
            |> Seq.exists (fun index -> index.GetDatabaseName() = "IX_ops_UsageAggregateMinute_ScopeKindBucket")

        Assert.Multiple(
            Action (fun () ->
                Assert.That(aggregate.GetSchema(), Is.EqualTo(OperationsUsageSql.SchemaName))
                Assert.That(aggregate.GetTableName(), Is.EqualTo(OperationsUsageSql.UsageAggregateMinuteTableName))
                Assert.That(primaryKey.GetName(), Is.EqualTo("PK_ops_UsageAggregateMinute"))

                Assert.That(actualKey, Is.EqualTo(String.Join("|", expectedKey)))

                Assert.That(hasScopeIndex, Is.True))
        )

    /// Verifies the baseline migration creates the reviewed operations tables, keys, and indexes.
    [<Test>]
    member _.BaselineMigrationScriptContainsExpectedOperationsSchema() =
        let script = migrationScript ()
        let schemaPreambleIndex = script.IndexOf("IF SCHEMA_ID(N'ops') IS NULL", StringComparison.Ordinal)
        let historyTableIndex = script.IndexOf("[ops].[__EFMigrationsHistory]", StringComparison.Ordinal)
        let rawPayloadDefaultIndex = script.IndexOf("CONSTRAINT DF_ops_RawUsageFact_RawPayload DEFAULT (0x) WITH VALUES", StringComparison.Ordinal)
        let rawPayloadDefaultDropIndex = script.IndexOf("DROP CONSTRAINT DF_ops_RawUsageFact_RawPayload", StringComparison.Ordinal)

        Assert.Multiple(
            Action (fun () ->
                Assert.That(script, Does.Contain("CREATE SCHEMA [ops]"))
                Assert.That(schemaPreambleIndex, Is.GreaterThanOrEqualTo(0))
                Assert.That(historyTableIndex, Is.GreaterThan(schemaPreambleIndex))
                Assert.That(script, Does.Contain("IF OBJECT_ID(N'ops.RawUsageFact', N'U') IS NULL"))
                Assert.That(script, Does.Contain("CREATE TABLE ops.RawUsageFact"))
                Assert.That(script, Does.Contain("CONSTRAINT PK_ops_RawUsageFact PRIMARY KEY CLUSTERED (UsageFactId)"))
                Assert.That(script, Does.Contain("ALTER TABLE ops.RawUsageFact"))
                Assert.That(script, Does.Contain("ADD RawPayload varbinary(max) NOT NULL"))
                Assert.That(rawPayloadDefaultIndex, Is.GreaterThanOrEqualTo(0))
                Assert.That(rawPayloadDefaultDropIndex, Is.GreaterThan(rawPayloadDefaultIndex))
                Assert.That(script, Does.Contain("IF OBJECT_ID(N'ops.UsageAggregateMinute', N'U') IS NULL"))
                Assert.That(script, Does.Contain("CREATE TABLE ops.UsageAggregateMinute"))
                Assert.That(script, Does.Contain("CONSTRAINT PK_ops_UsageAggregateMinute PRIMARY KEY CLUSTERED"))
                Assert.That(script, Does.Contain("CREATE INDEX IX_ops_RawUsageFact_ScopeKindObservedAt"))
                Assert.That(script, Does.Contain("CREATE INDEX IX_ops_UsageAggregateMinute_ScopeKindBucket"))
                Assert.That(script, Does.Contain("[ops].[__EFMigrationsHistory]")))
        )

    /// Verifies reviewed partition boundaries are exact UTC month starts for range and retention alignment.
    [<Test>]
    member _.MonthlyPartitionBoundariesUseUtcMonthStarts() =
        let boundaries = OperationsUsagePartitioningSql.InitialMonthlyBoundariesUtc

        let allBoundariesAreMonthStarts =
            boundaries
            |> Array.forall (fun boundary ->
                boundary.Kind = DateTimeKind.Utc
                && boundary.Day = 1
                && boundary.Hour = 0
                && boundary.Minute = 0
                && boundary.Second = 0
                && boundary.Millisecond = 0)

        let allBoundariesAreContiguousMonths =
            boundaries
            |> Seq.pairwise
            |> Seq.forall (fun (previous, current) -> current = previous.AddMonths(1))

        Assert.Multiple(
            Action (fun () ->
                Assert.That(boundaries, Has.Length.EqualTo(37))
                Assert.That(boundaries[0], Is.EqualTo(DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)))
                Assert.That(boundaries[boundaries.Length - 1], Is.EqualTo(DateTime(2029, 1, 1, 0, 0, 0, DateTimeKind.Utc)))
                Assert.That(allBoundariesAreMonthStarts, Is.True)
                Assert.That(allBoundariesAreContiguousMonths, Is.True))
        )

    /// Verifies SQL date literals stay Gregorian even when the ambient thread culture uses another calendar.
    [<Test>]
    member _.MonthlyPartitionBoundaryLiteralUsesInvariantCulture() =
        let originalCulture = Thread.CurrentThread.CurrentCulture
        let originalUiCulture = Thread.CurrentThread.CurrentUICulture

        try
            let thaiCulture = CultureInfo.GetCultureInfo("th-TH")
            Thread.CurrentThread.CurrentCulture <- thaiCulture
            Thread.CurrentThread.CurrentUICulture <- thaiCulture

            let literal = OperationsUsagePartitioningSql.boundaryLiteral (DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc))

            Assert.That(literal, Is.EqualTo("2026-07-01T00:00:00.0000000"))
        finally
            Thread.CurrentThread.CurrentCulture <- originalCulture
            Thread.CurrentThread.CurrentUICulture <- originalUiCulture

    /// Verifies the monthly partitioning migration aligns table and index storage with UTC time columns.
    [<Test>]
    member _.MonthlyPartitioningMigrationScriptAlignsTablesAndIndexes() =
        let script = migrationScript ()
        let partitionFunctionIndex = script.IndexOf("CREATE PARTITION FUNCTION PF_ops_OperationsUsageMonthUtc", StringComparison.Ordinal)
        let rawClusteredIndex = script.IndexOf("CREATE CLUSTERED INDEX CX_ops_RawUsageFact_ObservedAtUtc_UsageFactId", StringComparison.Ordinal)
        let aggregatePartitionPlacement = script.IndexOf("ON PS_ops_OperationsUsageMonthUtc(BucketStartUtc)", StringComparison.Ordinal)

        Assert.Multiple(
            Action (fun () ->
                Assert.That(partitionFunctionIndex, Is.GreaterThanOrEqualTo(0))
                Assert.That(script, Does.Contain("AS RANGE RIGHT FOR VALUES"))
                Assert.That(script, Does.Contain("'2026-07-01T00:00:00.0000000'"))
                Assert.That(script, Does.Contain("'2029-01-01T00:00:00.0000000'"))
                Assert.That(script, Does.Contain("THROW 57105"))
                Assert.That(script, Does.Contain("@ExpectedPartitionBoundaries"))
                Assert.That(script, Does.Contain("boundary_value_on_right = 1"))
                Assert.That(script, Does.Contain("CREATE PARTITION SCHEME PS_ops_OperationsUsageMonthUtc"))
                Assert.That(script, Does.Contain("ALL TO ([PRIMARY])"))
                Assert.That(script, Does.Contain("schemes.function_id = functions.function_id"))
                Assert.That(script, Does.Contain("WHERE schemes.name = N'PS_ops_OperationsUsageMonthUtc'"))
                Assert.That(script, Does.Contain("AND functions.name = N'PF_ops_OperationsUsageMonthUtc'"))
                Assert.That(script, Does.Contain("THROW 57106"))
                Assert.That(rawClusteredIndex, Is.GreaterThan(partitionFunctionIndex))
                Assert.That(script, Does.Contain("ON ops.RawUsageFact (ObservedAtUtc, UsageFactId)"))
                Assert.That(script, Does.Contain("ON PS_ops_OperationsUsageMonthUtc(ObservedAtUtc)"))
                Assert.That(script, Does.Contain("PRIMARY KEY NONCLUSTERED (UsageFactId)"))
                Assert.That(aggregatePartitionPlacement, Is.GreaterThan(partitionFunctionIndex))
                Assert.That(script, Does.Contain("ON ops.RawUsageFact (OwnerId, OrganizationId, RepositoryId, FactKind, ObservedAtUtc)"))
                Assert.That(script, Does.Contain("ON ops.UsageAggregateMinute (OwnerId, OrganizationId, RepositoryId, FactKind, BucketStartUtc)")))
        )

    /// Verifies partitioning is limited to current Operations tables and keeps local SQL Server testability.
    [<Test>]
    member _.MonthlyPartitioningMigrationStaysInsideCurrentUsageTables() =
        let script = migrationScript ()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(script, Does.Contain("OBJECT_ID(N'ops.RawUsageFact', N'U')"))
                Assert.That(script, Does.Contain("OBJECT_ID(N'ops.UsageAggregateMinute', N'U')"))
                Assert.That(script, Does.Contain("THROW 57101"))
                Assert.That(script, Does.Contain("THROW 57102"))
                Assert.That(script, Does.Contain("THROW 57103"))
                Assert.That(script, Does.Contain("THROW 57104"))
                Assert.That(script, Does.Not.Contain("ProviderLog"))
                Assert.That(script, Does.Not.Contain("ChargeLedger"))
                Assert.That(script, Does.Not.Contain("BillingPeriod"))
                Assert.That(script, Does.Not.Contain("CREATE DATABASE"))
                Assert.That(script, Does.Not.Contain("ONLINE = ON")))
        )

    /// Verifies rollback returns to the baseline unpartitioned keys and removes unused partition structures.
    [<Test>]
    member _.MonthlyPartitioningRollbackScriptRestoresBaselineLayout() =
        let script = migrationScriptFromTo "20260707120000_AddOperationsMonthlyPartitioning" "20260707000000_AddRawUsageFactPayload"

        Assert.Multiple(
            Action (fun () ->
                Assert.That(script, Does.Contain("PRIMARY KEY CLUSTERED (UsageFactId)"))
                Assert.That(script, Does.Contain("ON ops.RawUsageFact (OwnerId, OrganizationId, RepositoryId, FactKind, ObservedAtUtc)"))
                Assert.That(script, Does.Contain("ON [PRIMARY]"))
                Assert.That(script, Does.Contain("DROP PARTITION SCHEME PS_ops_OperationsUsageMonthUtc"))
                Assert.That(script, Does.Contain("DROP PARTITION FUNCTION PF_ops_OperationsUsageMonthUtc")))
        )

    /// Verifies the initial migration records the target model used by future migration diffs.
    [<Test>]
    member _.BaselineMigrationTargetModelContainsOperationsEntities() =
        let migration = InitialOperationsSchema()
        let rawFact = migration.TargetModel.FindEntityType(typeof<RawUsageFactEntity>)
        let aggregate = migration.TargetModel.FindEntityType(typeof<UsageAggregateMinuteEntity>)

        Assert.Multiple(
            Action (fun () ->
                Assert.That(rawFact, Is.Not.Null)
                Assert.That(rawFact.GetSchema(), Is.EqualTo(OperationsUsageSql.SchemaName))
                Assert.That(rawFact.GetTableName(), Is.EqualTo(OperationsUsageSql.RawUsageFactTableName))
                Assert.That(aggregate, Is.Not.Null)
                Assert.That(aggregate.GetSchema(), Is.EqualTo(OperationsUsageSql.SchemaName))
                Assert.That(aggregate.GetTableName(), Is.EqualTo(OperationsUsageSql.UsageAggregateMinuteTableName)))
        )

    /// Verifies the checked-in model snapshot carries the reviewed schema shape for future migration diffs.
    [<Test>]
    member _.BaselineModelSnapshotContainsOperationsEntities() =
        let snapshot = OperationsDbContextModelSnapshot()
        let rawFact = snapshot.Model.FindEntityType(typeof<RawUsageFactEntity>)
        let aggregate = snapshot.Model.FindEntityType(typeof<UsageAggregateMinuteEntity>)

        Assert.Multiple(
            Action (fun () ->
                Assert.That(rawFact, Is.Not.Null)
                Assert.That(rawFact.GetSchema(), Is.EqualTo(OperationsUsageSql.SchemaName))
                Assert.That(rawFact.GetTableName(), Is.EqualTo(OperationsUsageSql.RawUsageFactTableName))
                Assert.That(aggregate, Is.Not.Null)
                Assert.That(aggregate.GetSchema(), Is.EqualTo(OperationsUsageSql.SchemaName))
                Assert.That(aggregate.GetTableName(), Is.EqualTo(OperationsUsageSql.UsageAggregateMinuteTableName)))
        )

    /// Verifies schema bootstrap creates the schema before EF creates the schema-scoped history table.
    [<Test>]
    member _.SchemaBootstrapPreCreatesOpsSchemaWithoutCreatingDatabases() =
        Assert.Multiple(
            Action (fun () ->
                Assert.That(OperationsUsageSql.CreateSchemaIfMissing, Does.Contain("SCHEMA_ID(N'ops') IS NULL"))
                Assert.That(OperationsUsageSql.CreateSchemaIfMissing, Does.Contain("CREATE SCHEMA [ops]"))
                Assert.That(OperationsUsageSql.CreateSchemaIfMissing, Does.Not.Contain("CREATE DATABASE")))
        )

    /// Verifies SQL storage-pool keys stay case-sensitive under Azure SQL default collations.
    [<Test>]
    member _.SqlSchemaUsesBinaryCollationForStoragePoolAggregateKeys() =
        let script = migrationScript ()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(OperationsUsageSql.CaseSensitiveStoragePoolIdCollation, Is.EqualTo("Latin1_General_100_BIN2"))
                Assert.That(script, Does.Contain("StoragePoolId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL"))

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
            |> fun fact -> UsageFactPersistencePlan.tryCreate fact (OperationsUsageStorageTestData.payloadFor fact)
            |> requirePlan

        let mixedPoolPlan =
            OperationsUsageStorageTestData.factForStoragePool
                (Guid.Parse("34343434-3434-3434-3434-343434343434"))
                OperationsUsageStorageTestData.storagePoolIdWithDifferentCase
                1L
                observedAt
            |> fun fact -> UsageFactPersistencePlan.tryCreate fact (OperationsUsageStorageTestData.payloadFor fact)
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

        match UsageFactPersistencePlan.tryCreate fact (OperationsUsageStorageTestData.payloadFor fact) with
        | Ok _ -> Assert.Fail("Overlong SQL-bound fact fields should be rejected before persistence.")
        | Error errors ->
            let errorText = String.Join("|", errors)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(errorText, Does.Contain($"CorrelationId must be {OperationsUsageSql.CorrelationIdMaxLength} characters or fewer"))

                    Assert.That(errorText, Does.Contain($"Resource.StoragePoolId must be {OperationsUsageSql.StoragePoolIdMaxLength} characters or fewer")))
            )

    /// Verifies raw payload retention is mandatory for facts accepted into Operations storage.
    [<Test>]
    member _.PersistencePlanRejectsEmptyRawPayload() =
        let fact = OperationsUsageStorageTestData.fact (Guid.Parse("67676767-6767-6767-6767-676767676767")) 1L (Instant.FromUtc(2026, 7, 4, 12, 38, 0))

        match UsageFactPersistencePlan.tryCreate fact Array.empty with
        | Ok _ -> Assert.Fail("Empty raw payloads should be rejected before persistence.")
        | Error errors -> Assert.That(String.Join("|", errors), Does.Contain("Raw UsageFact payload is required"))

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
            let rawPayload = OperationsUsageStorageTestData.payloadFor fact

            let! result = store.StoreUsageFactAsync(fact, rawPayload, CancellationToken.None)
            let stored = requireStored result
            let rawFact = transactionScope.RawFact usageFactId

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(stored.Status, Is.EqualTo(UsageFactPersistenceStatus.Accepted))
                    Assert.That(transactionScope.RawFactCount, Is.EqualTo(1))
                    Assert.That(Convert.ToBase64String(rawFact.RawPayload), Is.EqualTo(Convert.ToBase64String(rawPayload)))
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
            let rawPayload = OperationsUsageStorageTestData.payloadFor fact
            let duplicatePayload = Array.append rawPayload [| byte 0x0A |]

            let! firstResult = store.StoreUsageFactAsync(fact, rawPayload, CancellationToken.None)
            let firstStored = requireStored firstResult

            let! duplicateResult = store.StoreUsageFactAsync(fact, duplicatePayload, CancellationToken.None)
            let duplicateStored = requireStored duplicateResult
            let rawFact = transactionScope.RawFact usageFactId

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(firstStored.Status, Is.EqualTo(UsageFactPersistenceStatus.Accepted))
                    Assert.That(duplicateStored.Status, Is.EqualTo(UsageFactPersistenceStatus.AlreadyProcessed))
                    Assert.That(duplicateStored.Aggregate, Is.EqualTo(None))
                    Assert.That(transactionScope.RawFactCount, Is.EqualTo(1))
                    Assert.That(Convert.ToBase64String(rawFact.RawPayload), Is.EqualTo(Convert.ToBase64String(rawPayload)))
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
            let rawPayload = OperationsUsageStorageTestData.payloadFor fact
            transactionScope.FailNextAggregateUpdate()

            let mutable thrownMessage = None

            try
                let! _ = store.StoreUsageFactAsync(fact, rawPayload, CancellationToken.None)
                Assert.Fail("Aggregate failure should propagate instead of reporting successful storage.")
            with
            | :? InvalidOperationException as ex -> thrownMessage <- Some ex.Message

            Assert.That(thrownMessage, Is.EqualTo(Some "forced aggregate failure"))
            Assert.That(transactionScope.RawFactCount, Is.EqualTo(0))

            let! retryResult = store.StoreUsageFactAsync(fact, rawPayload, CancellationToken.None)
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
            UsageFactPersistencePlan.tryCreate first (OperationsUsageStorageTestData.payloadFor first)
            |> requirePlan

        let secondPlan =
            UsageFactPersistencePlan.tryCreate second (OperationsUsageStorageTestData.payloadFor second)
            |> requirePlan

        let thirdPlan =
            UsageFactPersistencePlan.tryCreate third (OperationsUsageStorageTestData.payloadFor third)
            |> requirePlan

        Assert.Multiple(
            Action (fun () ->
                Assert.That(firstPlan.Aggregate.Key.BucketStart, Is.EqualTo(Instant.FromUtc(2026, 7, 4, 12, 34)))
                Assert.That(secondPlan.Aggregate.Key.BucketStart, Is.EqualTo(firstPlan.Aggregate.Key.BucketStart))
                Assert.That(thirdPlan.Aggregate.Key.BucketStart, Is.EqualTo(Instant.FromUtc(2026, 7, 4, 12, 35))))
        )
