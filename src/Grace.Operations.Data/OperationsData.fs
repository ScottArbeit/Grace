namespace Grace.Operations.Data

open Grace.Types.Common
open Grace.Types.Usage
open Microsoft.Data.SqlClient
open NodaTime
open System
open System.Data
open System.Threading
open System.Threading.Tasks

/// Identifies the Grace operations data assembly while persistence contracts remain owned by later slices.
[<AbstractClass; Sealed>]
type internal OperationsDataAssembly =
    class
    end

/// Describes a raw immutable usage fact row in `ops.RawUsageFact`.
type RawUsageFact =
    {
        UsageFactId: UsageFactId
        CorrelationId: CorrelationId
        FactKind: UsageFactKind
        OwnerId: OwnerId
        OrganizationId: OrganizationId
        RepositoryId: RepositoryId
        StoragePoolId: StoragePoolId
        Quantity: int64
        ObservedAt: Instant
    }

/// Identifies one UTC minute aggregate row in `ops.UsageAggregateMinute`.
type UsageAggregateMinuteKey =
    {
        FactKind: UsageFactKind
        OwnerId: OwnerId
        OrganizationId: OrganizationId
        RepositoryId: RepositoryId
        StoragePoolId: StoragePoolId
        BucketStart: Instant
    }

/// Describes the aggregate quantity projected for one Grace repository resource and UTC minute.
type UsageAggregateMinute = { Key: UsageAggregateMinuteKey; Quantity: int64 }

/// Carries the raw insert and aggregate update that must commit or roll back together.
type UsageFactPersistencePlan = { RawFact: RawUsageFact; Aggregate: UsageAggregateMinute }

/// Reports whether a usage fact changed storage state or was already present.
type UsageFactPersistenceStatus =
    | Accepted = 1
    | AlreadyProcessed = 2

/// Reports the outcome of transactionally storing a usage fact.
type UsageFactPersistenceResult = { Status: UsageFactPersistenceStatus; UsageFactId: UsageFactId; Aggregate: UsageAggregateMinute option }

/// Chooses whether schema initialization may connect to `master` to create the operations database.
type OperationsUsageSchemaBootstrapMode =

    /// Opens only the configured target database, which supports least-privilege users without `master` access.
    | TargetDatabaseOnly = 1

    /// Opens `master` first to create the configured target database when an admin bootstrap caller opts in.
    | CreateDatabaseIfMissing = 2

/// Describes the SQL connections needed for an operations usage schema initialization pass.
type internal OperationsUsageSchemaBootstrapPlan =
    {
        TargetDatabaseName: string option
        SchemaConnectionString: string
        DatabaseCreationConnectionString: string option
    }

/// Builds schema initialization plans without opening SQL connections.
[<RequireQualifiedAccess>]
module internal OperationsUsageSchemaBootstrapPlan =

    /// Creates a plan that uses the target database by default and connects to `master` only for explicit bootstrap.
    let create connectionString mode =
        let builder = SqlConnectionStringBuilder(connectionString)

        let targetDatabaseName =
            if String.IsNullOrWhiteSpace builder.InitialCatalog then
                None
            else
                Some builder.InitialCatalog

        let databaseCreationConnectionString =
            match mode, targetDatabaseName with
            | OperationsUsageSchemaBootstrapMode.CreateDatabaseIfMissing, Some _ ->
                let masterBuilder = SqlConnectionStringBuilder(connectionString)
                masterBuilder.InitialCatalog <- "master"
                Some masterBuilder.ConnectionString
            | _ -> None

        {
            TargetDatabaseName = targetDatabaseName
            SchemaConnectionString = connectionString
            DatabaseCreationConnectionString = databaseCreationConnectionString
        }

/// Provides SQL Server schema and command text for the operations usage fact tables.
[<RequireQualifiedAccess>]
module OperationsUsageSql =

    /// Names the SQL schema used by Grace operations data.
    [<Literal>]
    let SchemaName = "ops"

    /// Names the raw immutable fact table.
    [<Literal>]
    let RawUsageFactTable = "ops.RawUsageFact"

    /// Names the minute aggregate table derived from accepted raw facts.
    [<Literal>]
    let UsageAggregateMinuteTable = "ops.UsageAggregateMinute"

    /// Keeps storage-pool identities case-sensitive even when the database default collation is case-insensitive.
    [<Literal>]
    let CaseSensitiveStoragePoolIdCollation = "Latin1_General_100_BIN2"

    /// Creates the operations schema when it is absent.
    [<Literal>]
    let CreateSchema = "IF SCHEMA_ID(N'ops') IS NULL EXEC(N'CREATE SCHEMA ops');"

    /// Creates the configured operations database when SQL Server does not already contain it.
    [<Literal>]
    let CreateDatabaseIfMissing =
        """
IF DB_ID(@DatabaseName) IS NULL
BEGIN
    DECLARE @CreateDatabaseSql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@DatabaseName);
    EXEC(@CreateDatabaseSql);
END;
"""

    /// Creates `ops.RawUsageFact` with `UsageFactId` as the durable dedupe key.
    [<Literal>]
    let CreateRawUsageFactTable =
        """
IF OBJECT_ID(N'ops.RawUsageFact', N'U') IS NULL
BEGIN
    CREATE TABLE ops.RawUsageFact
    (
        UsageFactId uniqueidentifier NOT NULL,
        CorrelationId nvarchar(200) NOT NULL,
        FactKind int NOT NULL,
        OwnerId uniqueidentifier NOT NULL,
        OrganizationId uniqueidentifier NOT NULL,
        RepositoryId uniqueidentifier NOT NULL,
        StoragePoolId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
        Quantity bigint NOT NULL,
        ObservedAtUtc datetime2(7) NOT NULL,
        CreatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_ops_RawUsageFact_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_ops_RawUsageFact PRIMARY KEY CLUSTERED (UsageFactId)
    );
END;
"""

    /// Creates `ops.UsageAggregateMinute` with one row per repository resource and UTC minute.
    [<Literal>]
    let CreateUsageAggregateMinuteTable =
        """
IF OBJECT_ID(N'ops.UsageAggregateMinute', N'U') IS NULL
BEGIN
    CREATE TABLE ops.UsageAggregateMinute
    (
        FactKind int NOT NULL,
        OwnerId uniqueidentifier NOT NULL,
        OrganizationId uniqueidentifier NOT NULL,
        RepositoryId uniqueidentifier NOT NULL,
        StoragePoolId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
        BucketStartUtc datetime2(7) NOT NULL,
        Quantity bigint NOT NULL,
        UpdatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_ops_UsageAggregateMinute_UpdatedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_ops_UsageAggregateMinute PRIMARY KEY CLUSTERED
        (
            FactKind,
            OwnerId,
            OrganizationId,
            RepositoryId,
            StoragePoolId,
            BucketStartUtc
        )
    );
END;
"""

    /// Inserts one raw usage fact only when its durable identity has not already been accepted.
    [<Literal>]
    let TryInsertRawUsageFact =
        """
INSERT INTO ops.RawUsageFact
(
    UsageFactId,
    CorrelationId,
    FactKind,
    OwnerId,
    OrganizationId,
    RepositoryId,
    StoragePoolId,
    Quantity,
    ObservedAtUtc
)
SELECT
    @UsageFactId,
    @CorrelationId,
    @FactKind,
    @OwnerId,
    @OrganizationId,
    @RepositoryId,
    @StoragePoolId,
    @Quantity,
    @ObservedAtUtc
WHERE NOT EXISTS
(
    SELECT 1
    FROM ops.RawUsageFact WITH (UPDLOCK, HOLDLOCK)
    WHERE UsageFactId = @UsageFactId
);
"""

    /// Adds a quantity to the minute aggregate row associated with a newly accepted raw fact.
    [<Literal>]
    let AddToUsageAggregateMinute =
        """
MERGE ops.UsageAggregateMinute WITH (HOLDLOCK) AS target
USING
(
    SELECT
        @FactKind AS FactKind,
        @OwnerId AS OwnerId,
        @OrganizationId AS OrganizationId,
        @RepositoryId AS RepositoryId,
        CAST(@StoragePoolId AS nvarchar(256)) COLLATE Latin1_General_100_BIN2 AS StoragePoolId,
        @BucketStartUtc AS BucketStartUtc,
        @Quantity AS Quantity
) AS source
ON
    target.FactKind = source.FactKind
    AND target.OwnerId = source.OwnerId
    AND target.OrganizationId = source.OrganizationId
    AND target.RepositoryId = source.RepositoryId
    AND target.StoragePoolId = source.StoragePoolId
    AND target.BucketStartUtc = source.BucketStartUtc
WHEN MATCHED THEN
    UPDATE SET
        Quantity = target.Quantity + source.Quantity,
        UpdatedAtUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT
    (
        FactKind,
        OwnerId,
        OrganizationId,
        RepositoryId,
        StoragePoolId,
        BucketStartUtc,
        Quantity
    )
    VALUES
    (
        source.FactKind,
        source.OwnerId,
        source.OrganizationId,
        source.RepositoryId,
        source.StoragePoolId,
        source.BucketStartUtc,
        source.Quantity
    );
"""

/// Builds deterministic raw fact and aggregate projection plans from the shared usage contract.
[<RequireQualifiedAccess>]
module UsageFactPersistencePlan =

    /// Rounds a fact timestamp to the UTC minute bucket used for operations aggregates.
    let bucketObservedAt (observedAt: Instant) = UsageFact.NormalizeObservedAtToMinute observedAt

    /// Validates a usage fact and converts it into the transaction plan required by operations storage.
    let tryCreate (fact: UsageFact) =
        match UsageFact.Validate fact with
        | Error errors -> Error errors
        | Ok () ->
            let bucketStart = bucketObservedAt fact.ObservedAt

            let rawFact =
                {
                    UsageFactId = fact.UsageFactId
                    CorrelationId = fact.CorrelationId
                    FactKind = fact.FactKind
                    OwnerId = fact.Scope.OwnerId
                    OrganizationId = fact.Scope.OrganizationId
                    RepositoryId = fact.Scope.RepositoryId
                    StoragePoolId = fact.Resource.StoragePoolId
                    Quantity = fact.Quantity
                    ObservedAt = bucketStart
                }

            let aggregate =
                {
                    Key =
                        {
                            FactKind = fact.FactKind
                            OwnerId = fact.Scope.OwnerId
                            OrganizationId = fact.Scope.OrganizationId
                            RepositoryId = fact.Scope.RepositoryId
                            StoragePoolId = fact.Resource.StoragePoolId
                            BucketStart = bucketStart
                        }
                    Quantity = fact.Quantity
                }

            Ok { RawFact = rawFact; Aggregate = aggregate }

/// Represents the commands available inside one durable operations usage transaction.
type IOperationsUsageTransaction =

    /// Attempts to insert the raw fact, returning `false` when `UsageFactId` already exists.
    abstract TryInsertRawUsageFactAsync: rawFact: RawUsageFact * cancellationToken: CancellationToken -> Task<bool>

    /// Adds the accepted raw fact quantity to the derived minute aggregate.
    abstract AddToUsageAggregateMinuteAsync: aggregate: UsageAggregateMinute * cancellationToken: CancellationToken -> Task

/// Runs operations usage mutations inside one storage transaction boundary.
type IOperationsUsageTransactionScope =

    /// Executes the supplied operation and commits only when it completes successfully.
    abstract ExecuteAsync<'T> : operation: (IOperationsUsageTransaction -> CancellationToken -> Task<'T>) * cancellationToken: CancellationToken -> Task<'T>

/// Executes operations usage SQL commands through an already-open SQL connection and transaction.
type private SqlOperationsUsageTransaction(connection: SqlConnection, transaction: SqlTransaction) =

    /// Adds a SQL parameter and assigns either the supplied value or database null.
    let addParameter (command: SqlCommand) name sqlDbType value =
        let parameter = command.Parameters.Add(name, sqlDbType)
        parameter.Value <- value

    /// Adds a SQL parameter for a Grace string alias value.
    let addStringParameter (command: SqlCommand) name length (value: string) =
        let parameter = command.Parameters.Add(name, SqlDbType.NVarChar, length)
        parameter.Value <- value

    /// Creates a command that participates in the current operations usage transaction.
    let createCommand commandText =
        let command = connection.CreateCommand()
        command.Transaction <- transaction
        command.CommandType <- CommandType.Text
        command.CommandText <- commandText
        command

    /// Converts a NodaTime instant to the UTC SQL timestamp shape used by operations usage tables.
    let toUtcDateTime (instant: Instant) = instant.ToDateTimeUtc()

    /// Adds the raw usage fact parameters expected by `OperationsUsageSql.TryInsertRawUsageFact`.
    let addRawUsageFactParameters (command: SqlCommand) (rawFact: RawUsageFact) =
        addParameter command "@UsageFactId" SqlDbType.UniqueIdentifier rawFact.UsageFactId
        addStringParameter command "@CorrelationId" 200 rawFact.CorrelationId
        addParameter command "@FactKind" SqlDbType.Int (int rawFact.FactKind)
        addParameter command "@OwnerId" SqlDbType.UniqueIdentifier rawFact.OwnerId
        addParameter command "@OrganizationId" SqlDbType.UniqueIdentifier rawFact.OrganizationId
        addParameter command "@RepositoryId" SqlDbType.UniqueIdentifier rawFact.RepositoryId
        addStringParameter command "@StoragePoolId" 256 rawFact.StoragePoolId
        addParameter command "@Quantity" SqlDbType.BigInt rawFact.Quantity
        addParameter command "@ObservedAtUtc" SqlDbType.DateTime2 (toUtcDateTime rawFact.ObservedAt)

    /// Adds the aggregate parameters expected by `OperationsUsageSql.AddToUsageAggregateMinute`.
    let addUsageAggregateMinuteParameters (command: SqlCommand) (aggregate: UsageAggregateMinute) =
        addParameter command "@FactKind" SqlDbType.Int (int aggregate.Key.FactKind)
        addParameter command "@OwnerId" SqlDbType.UniqueIdentifier aggregate.Key.OwnerId
        addParameter command "@OrganizationId" SqlDbType.UniqueIdentifier aggregate.Key.OrganizationId
        addParameter command "@RepositoryId" SqlDbType.UniqueIdentifier aggregate.Key.RepositoryId
        addStringParameter command "@StoragePoolId" 256 aggregate.Key.StoragePoolId
        addParameter command "@BucketStartUtc" SqlDbType.DateTime2 (toUtcDateTime aggregate.Key.BucketStart)
        addParameter command "@Quantity" SqlDbType.BigInt aggregate.Quantity

    interface IOperationsUsageTransaction with

        member _.TryInsertRawUsageFactAsync(rawFact, cancellationToken) =
            task {
                use command = createCommand OperationsUsageSql.TryInsertRawUsageFact
                addRawUsageFactParameters command rawFact
                let! rowsAffected = command.ExecuteNonQueryAsync cancellationToken
                return rowsAffected = 1
            }

        member _.AddToUsageAggregateMinuteAsync(aggregate, cancellationToken) =
            task {
                use command = createCommand OperationsUsageSql.AddToUsageAggregateMinute
                addUsageAggregateMinuteParameters command aggregate
                let! _ = command.ExecuteNonQueryAsync cancellationToken
                return ()
            }

/// Runs operations usage mutations inside a concrete Azure SQL transaction boundary.
type SqlOperationsUsageTransactionScope(connectionString: string) =

    /// Opens a SQL connection for one operations usage transaction.
    let openConnectionAsync cancellationToken =
        task {
            let connection = new SqlConnection(connectionString)
            do! connection.OpenAsync cancellationToken
            return connection
        }

    /// Rolls back a failed transaction while preserving the original failure when rollback also fails.
    let rollbackIgnoringFailuresAsync (transaction: SqlTransaction) =
        task {
            try
                do! transaction.RollbackAsync CancellationToken.None
            with
            | _ -> ()
        }

    interface IOperationsUsageTransactionScope with

        member _.ExecuteAsync(operation, cancellationToken) =
            task {
                use! connection = openConnectionAsync cancellationToken
                let! dbTransaction = connection.BeginTransactionAsync cancellationToken
                use transaction = dbTransaction :?> SqlTransaction
                let operationsTransaction = SqlOperationsUsageTransaction(connection, transaction)

                try
                    let! result = operation operationsTransaction cancellationToken
                    do! transaction.CommitAsync cancellationToken
                    return result
                with
                | ex ->
                    do! rollbackIgnoringFailuresAsync transaction
                    return raise ex
            }

/// Ensures the operations usage SQL schema exists before ingestion starts consuming durable messages.
type OperationsUsageSchema(connectionString: string, ?bootstrapMode: OperationsUsageSchemaBootstrapMode) =

    /// Describes whether this schema pass is target-only or explicit admin bootstrap.
    let bootstrapMode = defaultArg bootstrapMode OperationsUsageSchemaBootstrapMode.TargetDatabaseOnly

    /// Captures the connection strings used by this schema pass without opening SQL connections.
    let bootstrapPlan = OperationsUsageSchemaBootstrapPlan.create connectionString bootstrapMode

    /// Opens the SQL connection used for one schema initialization pass.
    let openConnectionAsync databaseConnectionString cancellationToken =
        task {
            let connection = new SqlConnection(databaseConnectionString)
            do! connection.OpenAsync cancellationToken
            return connection
        }

    /// Executes a schema command against the operations database.
    let executeCommandAsync (connection: SqlConnection) commandText cancellationToken =
        task {
            use command = connection.CreateCommand()
            command.CommandType <- CommandType.Text
            command.CommandText <- commandText
            let! _ = command.ExecuteNonQueryAsync cancellationToken
            return ()
        }

    /// Creates the configured operations database when the connection string points at a database that is absent.
    let ensureDatabaseCreatedAsync cancellationToken =
        task {
            match bootstrapPlan.DatabaseCreationConnectionString, bootstrapPlan.TargetDatabaseName with
            | Some databaseCreationConnectionString, Some databaseName ->
                use! connection = openConnectionAsync databaseCreationConnectionString cancellationToken
                use command = connection.CreateCommand()
                command.CommandType <- CommandType.Text
                command.CommandText <- OperationsUsageSql.CreateDatabaseIfMissing

                let parameter = command.Parameters.Add("@DatabaseName", SqlDbType.NVarChar, 128)
                parameter.Value <- databaseName
                let! _ = command.ExecuteNonQueryAsync cancellationToken
                return ()
            | _ -> return ()
        }

    /// Creates the operations usage schema, raw fact table, and minute aggregate table when they are absent.
    member _.EnsureCreatedAsync(cancellationToken: CancellationToken) =
        task {
            do! ensureDatabaseCreatedAsync cancellationToken
            use! connection = openConnectionAsync bootstrapPlan.SchemaConnectionString cancellationToken
            do! executeCommandAsync connection OperationsUsageSql.CreateSchema cancellationToken
            do! executeCommandAsync connection OperationsUsageSql.CreateRawUsageFactTable cancellationToken
            do! executeCommandAsync connection OperationsUsageSql.CreateUsageAggregateMinuteTable cancellationToken
        }

/// Persists usage facts through a transaction-scoped raw insert and aggregate projection.
type OperationsUsageStore(transactionScope: IOperationsUsageTransactionScope) =

    /// Stores a usage fact exactly once by durable `UsageFactId` and projects aggregates only for newly accepted facts.
    member _.StoreUsageFactAsync(fact: UsageFact, cancellationToken: CancellationToken) =
        task {
            match UsageFactPersistencePlan.tryCreate fact with
            | Error errors -> return Error errors
            | Ok plan ->
                let operation (transaction: IOperationsUsageTransaction) (operationCancellationToken: CancellationToken) =
                    task {
                        let! accepted = transaction.TryInsertRawUsageFactAsync(plan.RawFact, operationCancellationToken)

                        if accepted then
                            do! transaction.AddToUsageAggregateMinuteAsync(plan.Aggregate, operationCancellationToken)

                            return { Status = UsageFactPersistenceStatus.Accepted; UsageFactId = plan.RawFact.UsageFactId; Aggregate = Some plan.Aggregate }
                        else
                            return { Status = UsageFactPersistenceStatus.AlreadyProcessed; UsageFactId = plan.RawFact.UsageFactId; Aggregate = None }
                    }

                let! result = transactionScope.ExecuteAsync(operation, cancellationToken)
                return Ok result
        }
