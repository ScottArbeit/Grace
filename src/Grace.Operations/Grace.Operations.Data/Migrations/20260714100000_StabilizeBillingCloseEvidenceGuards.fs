namespace Grace.Operations.Data.Migrations

open Grace.Operations.Data
open Microsoft.EntityFrameworkCore.Migrations

/// Adds database-only proof that final close, correction completion, and failure resolution retain their supported evidence.
[<Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof<OperationsDbContext>)>]
[<Migration("20260714100000_StabilizeBillingCloseEvidenceGuards")>]
type StabilizeBillingCloseEvidenceGuards() =
    inherit Migration()

    /// Refreshes database guards without changing the reviewed EF model shape.
    override _.Up(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(
            """
DROP TRIGGER IF EXISTS ops.TR_ops_BillingPeriod_FinalPreviewSourceProtection;
DROP TRIGGER IF EXISTS ops.TR_ops_BillingCorrectionWork_CompletionProtection;
DROP TRIGGER IF EXISTS ops.TR_ops_BillingIngestionFailure_ResolutionProtection;
"""
        )
        |> ignore

        migrationBuilder.Sql(OperationsBillingSql.CreateFinalPreviewSourceProtectionTrigger)
        |> ignore

        migrationBuilder.Sql(OperationsBillingSql.CreateBillingCorrectionWorkCompletionProtectionTrigger)
        |> ignore

        migrationBuilder.Sql(OperationsBillingSql.CreateBillingIngestionFailureResolutionProtectionTrigger)
        |> ignore

    /// Removes only the database-only evidence guards introduced by this migration.
    override _.Down(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(
            """
DROP TRIGGER IF EXISTS ops.TR_ops_BillingIngestionFailure_ResolutionProtection;
DROP TRIGGER IF EXISTS ops.TR_ops_BillingCorrectionWork_CompletionProtection;
DROP TRIGGER IF EXISTS ops.TR_ops_BillingPeriod_FinalPreviewSourceProtection;
"""
        )
        |> ignore
