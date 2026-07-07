namespace Grace.Operations.Data.Migrations

open Grace.Operations.Data
open Microsoft.EntityFrameworkCore.Migrations

/// Adds Blob archive authority columns for raw usage fact hot/cold retention.
[<Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof<OperationsDbContext>)>]
[<Migration("20260707130000_AddRawUsageFactArchiveState")>]
type AddRawUsageFactArchiveState() =
    inherit Migration()

    /// Adds SQL archive state and makes the hot payload nullable only after verified Blob authority exists.
    override _.Up(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(
            """
IF COL_LENGTH(N'ops.RawUsageFact', N'ArchiveState') IS NULL
BEGIN
    ALTER TABLE ops.RawUsageFact
        ADD ArchiveState int NOT NULL
            CONSTRAINT DF_ops_RawUsageFact_ArchiveState DEFAULT (0) WITH VALUES;
END;

IF COL_LENGTH(N'ops.RawUsageFact', N'ArchiveBlobName') IS NULL
BEGIN
    ALTER TABLE ops.RawUsageFact
        ADD ArchiveBlobName nvarchar(512) NULL;
END;

IF COL_LENGTH(N'ops.RawUsageFact', N'ArchiveChecksumSha256Hex') IS NULL
BEGIN
    ALTER TABLE ops.RawUsageFact
        ADD ArchiveChecksumSha256Hex char(64) NULL;
END;

IF COL_LENGTH(N'ops.RawUsageFact', N'ArchiveByteLength') IS NULL
BEGIN
    ALTER TABLE ops.RawUsageFact
        ADD ArchiveByteLength bigint NULL;
END;

IF COL_LENGTH(N'ops.RawUsageFact', N'ArchiveVerifiedAtUtc') IS NULL
BEGIN
    ALTER TABLE ops.RawUsageFact
        ADD ArchiveVerifiedAtUtc datetime2(7) NULL;
END;

IF COL_LENGTH(N'ops.RawUsageFact', N'ArchivedAtUtc') IS NULL
BEGIN
    ALTER TABLE ops.RawUsageFact
        ADD ArchivedAtUtc datetime2(7) NULL;
END;

IF EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'ops.RawUsageFact')
    AND name = N'RawPayload'
    AND is_nullable = 0
)
BEGIN
    ALTER TABLE ops.RawUsageFact
        ALTER COLUMN RawPayload varbinary(max) NULL;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'ops.RawUsageFact')
    AND name = N'IX_ops_RawUsageFact_ArchiveStateObservedAt'
)
BEGIN
    IF EXISTS
    (
        SELECT 1
        FROM sys.partition_schemes schemes
        INNER JOIN sys.partition_functions functions
            ON schemes.function_id = functions.function_id
        WHERE schemes.name = N'PS_ops_OperationsUsageMonthUtc'
        AND functions.name = N'PF_ops_OperationsUsageMonthUtc'
    )
    BEGIN
        CREATE INDEX IX_ops_RawUsageFact_ArchiveStateObservedAt
        ON ops.RawUsageFact (ArchiveState, ObservedAtUtc, UsageFactId)
        ON PS_ops_OperationsUsageMonthUtc(ObservedAtUtc);
    END
    ELSE
    BEGIN
        CREATE INDEX IX_ops_RawUsageFact_ArchiveStateObservedAt
        ON ops.RawUsageFact (ArchiveState, ObservedAtUtc, UsageFactId);
    END;
END;
"""
        )
        |> ignore

    /// Removes archive state columns for local rebuild and rollback scenarios.
    override _.Down(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(
            """
IF EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'ops.RawUsageFact')
    AND name = N'IX_ops_RawUsageFact_ArchiveStateObservedAt'
)
BEGIN
    DROP INDEX IX_ops_RawUsageFact_ArchiveStateObservedAt ON ops.RawUsageFact;
END;

IF COL_LENGTH(N'ops.RawUsageFact', N'RawPayload') IS NOT NULL
BEGIN
    UPDATE ops.RawUsageFact
    SET RawPayload = 0x
    WHERE RawPayload IS NULL;

    ALTER TABLE ops.RawUsageFact
        ALTER COLUMN RawPayload varbinary(max) NOT NULL;
END;

IF COL_LENGTH(N'ops.RawUsageFact', N'ArchivedAtUtc') IS NOT NULL
BEGIN
    ALTER TABLE ops.RawUsageFact DROP COLUMN ArchivedAtUtc;
END;

IF COL_LENGTH(N'ops.RawUsageFact', N'ArchiveVerifiedAtUtc') IS NOT NULL
BEGIN
    ALTER TABLE ops.RawUsageFact DROP COLUMN ArchiveVerifiedAtUtc;
END;

IF COL_LENGTH(N'ops.RawUsageFact', N'ArchiveByteLength') IS NOT NULL
BEGIN
    ALTER TABLE ops.RawUsageFact DROP COLUMN ArchiveByteLength;
END;

IF COL_LENGTH(N'ops.RawUsageFact', N'ArchiveChecksumSha256Hex') IS NOT NULL
BEGIN
    ALTER TABLE ops.RawUsageFact DROP COLUMN ArchiveChecksumSha256Hex;
END;

IF COL_LENGTH(N'ops.RawUsageFact', N'ArchiveBlobName') IS NOT NULL
BEGIN
    ALTER TABLE ops.RawUsageFact DROP COLUMN ArchiveBlobName;
END;

IF COL_LENGTH(N'ops.RawUsageFact', N'ArchiveState') IS NOT NULL
BEGIN
    IF OBJECT_ID(N'ops.DF_ops_RawUsageFact_ArchiveState', N'D') IS NOT NULL
    BEGIN
        ALTER TABLE ops.RawUsageFact
            DROP CONSTRAINT DF_ops_RawUsageFact_ArchiveState;
    END;

    ALTER TABLE ops.RawUsageFact DROP COLUMN ArchiveState;
END;
"""
        )
        |> ignore
