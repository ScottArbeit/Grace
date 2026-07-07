namespace Grace.Operations.Data

open System
open System.Globalization

/// Provides reviewed SQL Server partitioning text for append-heavy Operations usage tables.
[<RequireQualifiedAccess>]
module OperationsUsagePartitioningSql =

    /// Names the shared monthly UTC partition function for raw facts and minute aggregates.
    [<Literal>]
    let PartitionFunctionName = "PF_ops_OperationsUsageMonthUtc"

    /// Names the shared monthly UTC partition scheme for raw facts and minute aggregates.
    [<Literal>]
    let PartitionSchemeName = "PS_ops_OperationsUsageMonthUtc"

    /// Names the partition-aligned clustered index that orders raw facts by UTC observation month.
    [<Literal>]
    let RawUsageFactClusteredIndexName = "CX_ops_RawUsageFact_ObservedAtUtc_UsageFactId"

    /// Provides the first reviewed monthly UTC partition boundaries for Operations usage tables.
    let InitialMonthlyBoundariesUtc =
        [|
            DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2027, 2, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2027, 3, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2027, 4, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2027, 5, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2027, 6, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2027, 7, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2027, 8, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2027, 9, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2027, 10, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2027, 11, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2027, 12, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2028, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2028, 2, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2028, 3, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2028, 4, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2028, 5, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2028, 6, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2028, 7, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2028, 8, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2028, 9, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2028, 10, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2028, 11, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2028, 12, 1, 0, 0, 0, DateTimeKind.Utc)
            DateTime(2029, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        |]

    /// Formats a UTC month boundary as a SQL Server `datetime2(7)` literal.
    let internal boundaryLiteral (boundary: DateTime) = boundary.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture)

    /// Joins the reviewed UTC month boundaries for the partition function definition.
    let private boundaryList () =
        InitialMonthlyBoundariesUtc
        |> Array.map (fun boundary -> $"'{boundaryLiteral boundary}'")
        |> String.concat ", "

    /// Joins the reviewed UTC month boundaries for the partition validation table.
    let private boundaryValidationRows () =
        InitialMonthlyBoundariesUtc
        |> Array.map (fun boundary -> $"    (CONVERT(datetime2(7), N'{boundaryLiteral boundary}'))")
        |> String.concat $",{Environment.NewLine}"

    /// Applies monthly partitioning while preserving raw fact idempotency and aggregate query keys.
    let ApplyMonthlyPartitioning =
        $"""
IF OBJECT_ID(N'ops.RawUsageFact', N'U') IS NULL
BEGIN
    THROW 57101, 'ops.RawUsageFact must exist before applying Operations monthly partitioning.', 1;
END;

IF OBJECT_ID(N'ops.UsageAggregateMinute', N'U') IS NULL
BEGIN
    THROW 57102, 'ops.UsageAggregateMinute must exist before applying Operations monthly partitioning.', 1;
END;

IF COL_LENGTH(N'ops.RawUsageFact', N'ObservedAtUtc') IS NULL
OR COL_LENGTH(N'ops.RawUsageFact', N'UsageFactId') IS NULL
BEGIN
    THROW 57103, 'ops.RawUsageFact must expose ObservedAtUtc and UsageFactId before monthly partitioning.', 1;
END;

IF COL_LENGTH(N'ops.UsageAggregateMinute', N'BucketStartUtc') IS NULL
BEGIN
    THROW 57104, 'ops.UsageAggregateMinute must expose BucketStartUtc before monthly partitioning.', 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.partition_functions
    WHERE name = N'{PartitionFunctionName}'
)
BEGIN
    CREATE PARTITION FUNCTION {PartitionFunctionName} (datetime2(7))
    AS RANGE RIGHT FOR VALUES ({boundaryList ()});
END;

DECLARE @ExpectedPartitionBoundaries TABLE
(
    BoundaryValue datetime2(7) NOT NULL PRIMARY KEY
);

INSERT INTO @ExpectedPartitionBoundaries (BoundaryValue)
VALUES
{boundaryValidationRows ()};

IF NOT EXISTS
(
    SELECT 1
    FROM sys.partition_functions
    WHERE name = N'{PartitionFunctionName}'
      AND boundary_value_on_right = 1
)
OR
(
    SELECT COUNT(*)
    FROM sys.partition_range_values AS boundaryValues
    INNER JOIN sys.partition_functions AS functions
        ON boundaryValues.function_id = functions.function_id
    WHERE functions.name = N'{PartitionFunctionName}'
) <> (SELECT COUNT(*) FROM @ExpectedPartitionBoundaries)
OR EXISTS
(
    SELECT expected.BoundaryValue
    FROM @ExpectedPartitionBoundaries AS expected
    EXCEPT
    SELECT CONVERT(datetime2(7), boundaryValues.value) AS BoundaryValue
    FROM sys.partition_range_values AS boundaryValues
    INNER JOIN sys.partition_functions AS functions
        ON boundaryValues.function_id = functions.function_id
    WHERE functions.name = N'{PartitionFunctionName}'
)
OR EXISTS
(
    SELECT CONVERT(datetime2(7), boundaryValues.value) AS BoundaryValue
    FROM sys.partition_range_values AS boundaryValues
    INNER JOIN sys.partition_functions AS functions
        ON boundaryValues.function_id = functions.function_id
    WHERE functions.name = N'{PartitionFunctionName}'
    EXCEPT
    SELECT expected.BoundaryValue
    FROM @ExpectedPartitionBoundaries AS expected
)
BEGIN
    THROW 57105, 'PF_ops_OperationsUsageMonthUtc boundaries differ from the reviewed monthly UTC set.', 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.partition_schemes
    WHERE name = N'{PartitionSchemeName}'
)
BEGIN
    CREATE PARTITION SCHEME {PartitionSchemeName}
    AS PARTITION {PartitionFunctionName}
    ALL TO ([PRIMARY]);
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.partition_schemes AS schemes
    INNER JOIN sys.partition_functions AS functions
        ON schemes.function_id = functions.function_id
    WHERE schemes.name = N'{PartitionSchemeName}'
      AND functions.name = N'{PartitionFunctionName}'
)
BEGIN
    THROW 57106, 'PS_ops_OperationsUsageMonthUtc must reference PF_ops_OperationsUsageMonthUtc.', 1;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes AS indexes
    INNER JOIN sys.data_spaces AS dataSpaces
        ON indexes.data_space_id = dataSpaces.data_space_id
    WHERE indexes.object_id = OBJECT_ID(N'ops.RawUsageFact')
      AND indexes.name = N'{RawUsageFactClusteredIndexName}'
      AND dataSpaces.name = N'{PartitionSchemeName}'
)
BEGIN
    IF EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE name = N'IX_ops_RawUsageFact_ScopeKindObservedAt'
          AND object_id = OBJECT_ID(N'ops.RawUsageFact')
    )
    BEGIN
        DROP INDEX IX_ops_RawUsageFact_ScopeKindObservedAt ON ops.RawUsageFact;
    END;

    IF OBJECT_ID(N'ops.PK_ops_RawUsageFact', N'PK') IS NOT NULL
    BEGIN
        ALTER TABLE ops.RawUsageFact
            DROP CONSTRAINT PK_ops_RawUsageFact;
    END;

    IF EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE name = N'{RawUsageFactClusteredIndexName}'
          AND object_id = OBJECT_ID(N'ops.RawUsageFact')
    )
    BEGIN
        DROP INDEX {RawUsageFactClusteredIndexName} ON ops.RawUsageFact;
    END;

    CREATE CLUSTERED INDEX {RawUsageFactClusteredIndexName}
    ON ops.RawUsageFact (ObservedAtUtc, UsageFactId)
    ON {PartitionSchemeName}(ObservedAtUtc);

    ALTER TABLE ops.RawUsageFact
        ADD CONSTRAINT PK_ops_RawUsageFact
        PRIMARY KEY NONCLUSTERED (UsageFactId)
        ON [PRIMARY];

    CREATE INDEX IX_ops_RawUsageFact_ScopeKindObservedAt
    ON ops.RawUsageFact (OwnerId, OrganizationId, RepositoryId, FactKind, ObservedAtUtc)
    ON {PartitionSchemeName}(ObservedAtUtc);
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes AS indexes
    INNER JOIN sys.data_spaces AS dataSpaces
        ON indexes.data_space_id = dataSpaces.data_space_id
    WHERE indexes.object_id = OBJECT_ID(N'ops.UsageAggregateMinute')
      AND indexes.name = N'PK_ops_UsageAggregateMinute'
      AND dataSpaces.name = N'{PartitionSchemeName}'
)
BEGIN
    IF EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE name = N'IX_ops_UsageAggregateMinute_ScopeKindBucket'
          AND object_id = OBJECT_ID(N'ops.UsageAggregateMinute')
    )
    BEGIN
        DROP INDEX IX_ops_UsageAggregateMinute_ScopeKindBucket ON ops.UsageAggregateMinute;
    END;

    IF OBJECT_ID(N'ops.PK_ops_UsageAggregateMinute', N'PK') IS NOT NULL
    BEGIN
        ALTER TABLE ops.UsageAggregateMinute
            DROP CONSTRAINT PK_ops_UsageAggregateMinute;
    END;

    ALTER TABLE ops.UsageAggregateMinute
        ADD CONSTRAINT PK_ops_UsageAggregateMinute
        PRIMARY KEY CLUSTERED
        (
            FactKind,
            OwnerId,
            OrganizationId,
            RepositoryId,
            StoragePoolId,
            BucketStartUtc
        )
        ON {PartitionSchemeName}(BucketStartUtc);

    CREATE INDEX IX_ops_UsageAggregateMinute_ScopeKindBucket
    ON ops.UsageAggregateMinute (OwnerId, OrganizationId, RepositoryId, FactKind, BucketStartUtc)
    ON {PartitionSchemeName}(BucketStartUtc);
END;
"""

    /// Reverts monthly partitioning to the baseline unpartitioned Operations table layout.
    let RevertMonthlyPartitioning =
        $"""
IF OBJECT_ID(N'ops.RawUsageFact', N'U') IS NOT NULL
BEGIN
    IF EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE name = N'IX_ops_RawUsageFact_ScopeKindObservedAt'
          AND object_id = OBJECT_ID(N'ops.RawUsageFact')
    )
    BEGIN
        DROP INDEX IX_ops_RawUsageFact_ScopeKindObservedAt ON ops.RawUsageFact;
    END;

    IF OBJECT_ID(N'ops.PK_ops_RawUsageFact', N'PK') IS NOT NULL
    BEGIN
        ALTER TABLE ops.RawUsageFact
            DROP CONSTRAINT PK_ops_RawUsageFact;
    END;

    IF EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE name = N'{RawUsageFactClusteredIndexName}'
          AND object_id = OBJECT_ID(N'ops.RawUsageFact')
    )
    BEGIN
        DROP INDEX {RawUsageFactClusteredIndexName} ON ops.RawUsageFact;
    END;

    ALTER TABLE ops.RawUsageFact
        ADD CONSTRAINT PK_ops_RawUsageFact
        PRIMARY KEY CLUSTERED (UsageFactId)
        ON [PRIMARY];

    CREATE INDEX IX_ops_RawUsageFact_ScopeKindObservedAt
    ON ops.RawUsageFact (OwnerId, OrganizationId, RepositoryId, FactKind, ObservedAtUtc)
    ON [PRIMARY];
END;

IF OBJECT_ID(N'ops.UsageAggregateMinute', N'U') IS NOT NULL
BEGIN
    IF EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE name = N'IX_ops_UsageAggregateMinute_ScopeKindBucket'
          AND object_id = OBJECT_ID(N'ops.UsageAggregateMinute')
    )
    BEGIN
        DROP INDEX IX_ops_UsageAggregateMinute_ScopeKindBucket ON ops.UsageAggregateMinute;
    END;

    IF OBJECT_ID(N'ops.PK_ops_UsageAggregateMinute', N'PK') IS NOT NULL
    BEGIN
        ALTER TABLE ops.UsageAggregateMinute
            DROP CONSTRAINT PK_ops_UsageAggregateMinute;
    END;

    ALTER TABLE ops.UsageAggregateMinute
        ADD CONSTRAINT PK_ops_UsageAggregateMinute
        PRIMARY KEY CLUSTERED
        (
            FactKind,
            OwnerId,
            OrganizationId,
            RepositoryId,
            StoragePoolId,
            BucketStartUtc
        )
        ON [PRIMARY];

    CREATE INDEX IX_ops_UsageAggregateMinute_ScopeKindBucket
    ON ops.UsageAggregateMinute (OwnerId, OrganizationId, RepositoryId, FactKind, BucketStartUtc)
    ON [PRIMARY];
END;

IF EXISTS
(
    SELECT 1
    FROM sys.partition_schemes
    WHERE name = N'{PartitionSchemeName}'
)
AND NOT EXISTS
(
    SELECT 1
    FROM sys.indexes AS indexes
    INNER JOIN sys.data_spaces AS dataSpaces
        ON indexes.data_space_id = dataSpaces.data_space_id
    WHERE dataSpaces.name = N'{PartitionSchemeName}'
)
BEGIN
    DROP PARTITION SCHEME {PartitionSchemeName};
END;

IF EXISTS
(
    SELECT 1
    FROM sys.partition_functions
    WHERE name = N'{PartitionFunctionName}'
)
AND NOT EXISTS
(
    SELECT 1
    FROM sys.partition_schemes AS schemes
    INNER JOIN sys.partition_functions AS functions
        ON schemes.function_id = functions.function_id
    WHERE functions.name = N'{PartitionFunctionName}'
)
BEGIN
    DROP PARTITION FUNCTION {PartitionFunctionName};
END;
"""
