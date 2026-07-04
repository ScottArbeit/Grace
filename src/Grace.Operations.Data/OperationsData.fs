namespace Grace.Operations.Data

open Grace.Types.Common
open Grace.Types.Usage
open NodaTime
open System
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

    /// Creates the operations schema when it is absent.
    [<Literal>]
    let CreateSchema = "IF SCHEMA_ID(N'ops') IS NULL EXEC(N'CREATE SCHEMA ops');"

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
        StoragePoolId nvarchar(256) NOT NULL,
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
        StoragePoolId nvarchar(256) NOT NULL,
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
        @StoragePoolId AS StoragePoolId,
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
