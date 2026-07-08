namespace Grace.Operations.Data.Migrations

open Grace.Operations.Data
open Microsoft.EntityFrameworkCore.Migrations

/// Adds a request-owned lease that prevents overlapping support rehydration cleanup from clearing another request.
[<Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof<OperationsDbContext>)>]
[<Migration("20260707140000_AddRawUsageFactRehydrationLease")>]
type AddRawUsageFactRehydrationLease() =
    inherit Migration()

    /// Adds nullable lease authority for temporarily restored archived payload bytes.
    override _.Up(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(
            """
IF COL_LENGTH(N'ops.RawUsageFact', N'RehydrationLeaseId') IS NULL
BEGIN
    ALTER TABLE ops.RawUsageFact
        ADD RehydrationLeaseId uniqueidentifier NULL;
END;
"""
        )
        |> ignore

    /// Removes temporary rehydration lease authority for local rebuild and rollback scenarios.
    override _.Down(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(
            """
IF COL_LENGTH(N'ops.RawUsageFact', N'RehydrationLeaseId') IS NOT NULL
BEGIN
    ALTER TABLE ops.RawUsageFact DROP COLUMN RehydrationLeaseId;
END;
"""
        )
        |> ignore
