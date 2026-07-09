namespace Grace.Operations.Data.Migrations

open Grace.Operations.Data
open Microsoft.EntityFrameworkCore.Migrations

/// Adds the filtered expiry index used by periodic temporary-hot archive cleanup.
[<Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof<OperationsDbContext>)>]
[<Migration("20260709090000_AddRawUsageFactRehydrationExpiryIndex")>]
type AddRawUsageFactRehydrationExpiryIndex() =
    inherit Migration()

    /// Adds a filtered path for locating expired temporary-hot payloads without scanning archived rows.
    override _.Up(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(
            $"""
IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'{OperationsUsageSql.RawUsageFactTable}')
    AND name = N'{OperationsUsageSql.TemporaryHotCleanupExpiryIndexName}'
)
BEGIN
    CREATE INDEX {OperationsUsageSql.TemporaryHotCleanupExpiryIndexName}
        ON {OperationsUsageSql.RawUsageFactTable}(RehydrationExpiresAtUtc)
        WHERE RehydrationExpiresAtUtc IS NOT NULL;
END;
"""
        )
        |> ignore

    /// Removes the filtered expiry index for local rebuild and rollback scenarios.
    override _.Down(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(
            $"""
IF EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'{OperationsUsageSql.RawUsageFactTable}')
    AND name = N'{OperationsUsageSql.TemporaryHotCleanupExpiryIndexName}'
)
BEGIN
    DROP INDEX {OperationsUsageSql.TemporaryHotCleanupExpiryIndexName}
        ON {OperationsUsageSql.RawUsageFactTable};
END;
"""
        )
        |> ignore
