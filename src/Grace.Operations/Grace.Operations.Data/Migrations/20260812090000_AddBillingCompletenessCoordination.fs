namespace Grace.Operations.Data.Migrations

open Grace.Operations.Data
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Migrations

/// Adds scoped rejection evidence used by the owner-month completeness coordination boundary.
[<Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof<OperationsDbContext>)>]
[<Migration("20260812090000_AddBillingCompletenessCoordination")>]
type AddBillingCompletenessCoordination() =
    inherit Migration()

    /// Creates active and repaired rejection persistence without creating billing-period state or charge records.
    override _.Up(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(
            """
CREATE TABLE ops.UsageFactRejection
(
    RejectionId uniqueidentifier NOT NULL,
    UsageFactId uniqueidentifier NULL,
    OwnerId uniqueidentifier NULL,
    OrganizationId uniqueidentifier NULL,
    RepositoryId uniqueidentifier NULL,
    MonthStartUtc datetime2(7) NULL,
    Reason nvarchar(400) NOT NULL,
    IsActive bit NOT NULL,
    ResolvedAtUtc datetime2(7) NULL,
    CreatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_ops_UsageFactRejection_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_ops_UsageFactRejection PRIMARY KEY (RejectionId)
);

CREATE UNIQUE INDEX UX_ops_UsageFactRejection_ActiveScopedFact
    ON ops.UsageFactRejection(UsageFactId, OwnerId, OrganizationId, RepositoryId, MonthStartUtc)
    WHERE IsActive = 1
      AND UsageFactId IS NOT NULL
      AND OwnerId IS NOT NULL
      AND OrganizationId IS NOT NULL
      AND RepositoryId IS NOT NULL
      AND MonthStartUtc IS NOT NULL;

CREATE INDEX IX_ops_UsageFactRejection_ActiveScope
    ON ops.UsageFactRejection(OwnerId, OrganizationId, RepositoryId, MonthStartUtc, IsActive);
"""
        )
        |> ignore

    /// Removes only the coordination evidence introduced by this migration.
    override _.Down(migrationBuilder: MigrationBuilder) =
        migrationBuilder.DropTable("UsageFactRejection", OperationsUsageSql.SchemaName)
        |> ignore
