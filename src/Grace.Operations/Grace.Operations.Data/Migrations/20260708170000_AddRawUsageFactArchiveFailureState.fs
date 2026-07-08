namespace Grace.Operations.Data.Migrations

open Grace.Operations.Data
open Microsoft.EntityFrameworkCore.Migrations

/// Adds row-scoped archive failure evidence so retention can continue past corrupt rows without hiding repair work.
[<Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof<OperationsDbContext>)>]
[<Migration("20260708170000_AddRawUsageFactArchiveFailureState")>]
type AddRawUsageFactArchiveFailureState() =
    inherit Migration()

    /// Adds durable archive failure metadata with guarded DDL for idempotent local repair runs.
    override _.Up(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(
            """
IF COL_LENGTH(N'ops.RawUsageFact', N'LastArchiveFailureReason') IS NULL
BEGIN
    ALTER TABLE ops.RawUsageFact
        ADD LastArchiveFailureReason nvarchar(400) NULL;
END;

IF COL_LENGTH(N'ops.RawUsageFact', N'LastArchiveFailureAtUtc') IS NULL
BEGIN
    ALTER TABLE ops.RawUsageFact
        ADD LastArchiveFailureAtUtc datetime2(7) NULL;
END;

IF COL_LENGTH(N'ops.RawUsageFact', N'ArchiveFailureCount') IS NULL
BEGIN
    ALTER TABLE ops.RawUsageFact
        ADD ArchiveFailureCount int NOT NULL
            CONSTRAINT DF_ops_RawUsageFact_ArchiveFailureCount DEFAULT (0) WITH VALUES;
END;

IF COL_LENGTH(N'ops.RawUsageFact', N'ArchiveRetiredAtUtc') IS NULL
BEGIN
    ALTER TABLE ops.RawUsageFact
        ADD ArchiveRetiredAtUtc datetime2(7) NULL;
END;
"""
        )
        |> ignore

    /// Removes row-scoped archive failure metadata for local rebuild and rollback scenarios.
    override _.Down(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(
            """
IF COL_LENGTH(N'ops.RawUsageFact', N'ArchiveRetiredAtUtc') IS NOT NULL
BEGIN
    ALTER TABLE ops.RawUsageFact DROP COLUMN ArchiveRetiredAtUtc;
END;

IF COL_LENGTH(N'ops.RawUsageFact', N'ArchiveFailureCount') IS NOT NULL
BEGIN
    IF OBJECT_ID(N'ops.DF_ops_RawUsageFact_ArchiveFailureCount', N'D') IS NOT NULL
    BEGIN
        ALTER TABLE ops.RawUsageFact
            DROP CONSTRAINT DF_ops_RawUsageFact_ArchiveFailureCount;
    END;

    ALTER TABLE ops.RawUsageFact DROP COLUMN ArchiveFailureCount;
END;

IF COL_LENGTH(N'ops.RawUsageFact', N'LastArchiveFailureAtUtc') IS NOT NULL
BEGIN
    ALTER TABLE ops.RawUsageFact DROP COLUMN LastArchiveFailureAtUtc;
END;

IF COL_LENGTH(N'ops.RawUsageFact', N'LastArchiveFailureReason') IS NOT NULL
BEGIN
    ALTER TABLE ops.RawUsageFact DROP COLUMN LastArchiveFailureReason;
END;
"""
        )
        |> ignore
