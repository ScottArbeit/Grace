namespace Grace.Operations.Data.Migrations

open Grace.Operations.Data
open Microsoft.EntityFrameworkCore.Migrations

/// Adds SQL Server monthly partitioning for append-heavy Operations usage tables.
[<Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof<OperationsDbContext>)>]
[<Migration("20260707120000_AddOperationsMonthlyPartitioning")>]
type AddOperationsMonthlyPartitioning() =
    inherit Migration()

    /// Applies reviewed physical partitioning without changing logical ingest or aggregate contracts.
    override _.Up(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(OperationsUsagePartitioningSql.ApplyMonthlyPartitioning)
        |> ignore

    /// Restores the unpartitioned baseline table layout for local rebuild and rollback scenarios.
    override _.Down(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(OperationsUsagePartitioningSql.RevertMonthlyPartitioning)
        |> ignore
