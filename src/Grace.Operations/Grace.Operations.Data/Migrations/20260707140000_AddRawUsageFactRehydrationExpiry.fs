namespace Grace.Operations.Data.Migrations

open Grace.Operations.Data
open Microsoft.EntityFrameworkCore.Migrations

/// Adds the durable expiry marker used for temporarily restored archived payload bytes.
[<Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof<OperationsDbContext>)>]
[<Migration("20260707140000_AddRawUsageFactRehydrationExpiry")>]
type AddRawUsageFactRehydrationExpiry() =
    inherit Migration()

    /// Adds nullable expiry authority for temporarily restored archived payload bytes.
    override _.Up(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(
            """
IF COL_LENGTH(N'ops.RawUsageFact', N'RehydrationExpiresAtUtc') IS NULL
BEGIN
    ALTER TABLE ops.RawUsageFact
        ADD RehydrationExpiresAtUtc datetime2(7) NULL;
END;
"""
        )
        |> ignore

    /// Removes temporary rehydration expiry state for local rebuild and rollback scenarios.
    override _.Down(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(
            """
IF COL_LENGTH(N'ops.RawUsageFact', N'RehydrationExpiresAtUtc') IS NOT NULL
BEGIN
    ALTER TABLE ops.RawUsageFact DROP COLUMN RehydrationExpiresAtUtc;
END;
"""
        )
        |> ignore
