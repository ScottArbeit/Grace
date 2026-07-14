namespace Grace.Operations.Data.Migrations

open Grace.Operations.Data
open Microsoft.EntityFrameworkCore.Migrations

/// Strengthens terminal billing INSERT guards, freezes final preview evidence, and keeps Adjustment as the sole correction kind.
[<Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof<OperationsDbContext>)>]
[<Migration("20260713140000_StabilizeBillingTerminalInsertProtection")>]
type StabilizeBillingTerminalInsertProtection() =
    inherit Migration()

    /// Replaces the correction-kind constraints and refreshes every terminal evidence guard for upgraded development databases.
    override _.Up(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(
            """
ALTER TABLE ops.ChargeLedgerEntry DROP CONSTRAINT CK_ops_ChargeLedgerEntry_Source;
ALTER TABLE ops.ChargeLedgerEntry DROP CONSTRAINT CK_ops_ChargeLedgerEntry_Kind;
ALTER TABLE ops.ChargeLedgerEntry ADD
    CONSTRAINT CK_ops_ChargeLedgerEntry_Kind CHECK (EntryKind BETWEEN 0 AND 1),
    CONSTRAINT CK_ops_ChargeLedgerEntry_Source CHECK ((EntryKind=0 AND SourceChargePreviewLineId IS NOT NULL AND PriorChargeLedgerEntryId IS NULL) OR (EntryKind=1 AND SourceChargePreviewLineId IS NULL));
DROP TRIGGER IF EXISTS ops.TR_ops_ChargePreviewFreshness_TerminalBillingProtection;
DROP TRIGGER IF EXISTS ops.TR_ops_ChargePreviewLine_TerminalBillingProtection;
DROP TRIGGER IF EXISTS ops.TR_ops_BillingPeriod_PermanentFailureProtection;
DROP TRIGGER IF EXISTS ops.TR_ops_RawUsageFact_TerminalBillingProtection;
DROP TRIGGER IF EXISTS ops.TR_ops_ChargeLedgerEntry_Immutable;
"""
        )
        |> ignore

        migrationBuilder.Sql(OperationsBillingSql.CreateLedgerImmutabilityTrigger)
        |> ignore

        migrationBuilder.Sql(OperationsBillingSql.CreateTerminalRawUsageFactProtectionTrigger)
        |> ignore

        migrationBuilder.Sql(OperationsBillingSql.CreatePermanentBillingFailureProtectionTrigger)
        |> ignore

        migrationBuilder.Sql(OperationsBillingSql.CreateTerminalChargePreviewLineProtectionTrigger)
        |> ignore

        migrationBuilder.Sql(OperationsBillingSql.CreateTerminalChargePreviewFreshnessProtectionTrigger)
        |> ignore

    /// Removes only the newly introduced preview evidence guards while retaining the current Adjustment-only persisted contract.
    override _.Down(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(
            """
DROP TRIGGER IF EXISTS ops.TR_ops_ChargePreviewFreshness_TerminalBillingProtection;
DROP TRIGGER IF EXISTS ops.TR_ops_ChargePreviewLine_TerminalBillingProtection;
"""
        )
        |> ignore
