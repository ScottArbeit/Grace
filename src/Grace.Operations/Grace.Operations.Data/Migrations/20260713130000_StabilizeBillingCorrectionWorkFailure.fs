namespace Grace.Operations.Data.Migrations

open Grace.Operations.Data
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Migrations
open System

/// Makes deterministic late-correction calculation failures durable terminal work outcomes and refreshes terminal billing guards.
[<Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof<OperationsDbContext>)>]
[<Migration("20260713130000_StabilizeBillingCorrectionWorkFailure")>]
type StabilizeBillingCorrectionWorkFailure() =
    inherit Migration()

    /// Adds permanent correction-work evidence and refreshes every billing guard affected by the terminal invariant matrix.
    override _.Up(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(
            """
ALTER TABLE ops.BillingCorrectionWork ADD PermanentlyFailedAtUtc datetime2(7) NULL,
    CONSTRAINT CK_ops_BillingCorrectionWork_PermanentFailure CHECK
    (
        PermanentlyFailedAtUtc IS NULL
        OR
        (
            CompletedAtUtc IS NULL
            AND IsAutomaticRetryEligible=0
            AND BlockedCode=N'CalculationOverflow'
            AND BlockedDetail IS NOT NULL
        )
    );
DROP TRIGGER IF EXISTS ops.TR_ops_BillingPeriod_PermanentFailureProtection;
DROP TRIGGER IF EXISTS ops.TR_ops_RawUsageFact_TerminalBillingProtection;
DROP TRIGGER IF EXISTS ops.TR_ops_ChargeLedgerEntry_Immutable;
DROP TRIGGER IF EXISTS ops.TR_ops_PricingRate_HistoricalProtection;
DROP TRIGGER IF EXISTS ops.TR_ops_BillableUsageKindMapping_HistoricalProtection;
DROP TRIGGER IF EXISTS ops.TR_ops_PricingPlan_HistoricalProtection;
DROP TRIGGER IF EXISTS ops.TR_ops_PricingAssignment_HistoricalProtection;
"""
        )
        |> ignore

        migrationBuilder.Sql(OperationsBillingSql.CreateLedgerImmutabilityTrigger)
        |> ignore

        OperationsBillingSql.CreateHistoricalPricingProtectionTriggers.Split([| "GO" |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun statement -> statement.Trim())
        |> Array.filter (fun statement -> not (String.IsNullOrWhiteSpace(statement)))
        |> Array.iter (fun statement -> migrationBuilder.Sql(statement) |> ignore)

        migrationBuilder.Sql(OperationsBillingSql.CreateTerminalRawUsageFactProtectionTrigger)
        |> ignore

        migrationBuilder.Sql(OperationsBillingSql.CreatePermanentBillingFailureProtectionTrigger)
        |> ignore

    /// Removes correction-work terminal evidence while retaining the current terminal billing guards for the preceding schema.
    override _.Down(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(
            """
DROP TRIGGER IF EXISTS ops.TR_ops_BillingPeriod_PermanentFailureProtection;
DROP TRIGGER IF EXISTS ops.TR_ops_RawUsageFact_TerminalBillingProtection;
DROP TRIGGER IF EXISTS ops.TR_ops_ChargeLedgerEntry_Immutable;
DROP TRIGGER IF EXISTS ops.TR_ops_PricingRate_HistoricalProtection;
DROP TRIGGER IF EXISTS ops.TR_ops_BillableUsageKindMapping_HistoricalProtection;
DROP TRIGGER IF EXISTS ops.TR_ops_PricingPlan_HistoricalProtection;
DROP TRIGGER IF EXISTS ops.TR_ops_PricingAssignment_HistoricalProtection;
ALTER TABLE ops.BillingCorrectionWork DROP CONSTRAINT CK_ops_BillingCorrectionWork_PermanentFailure;
ALTER TABLE ops.BillingCorrectionWork DROP COLUMN PermanentlyFailedAtUtc;
"""
        )
        |> ignore

        migrationBuilder.Sql(OperationsBillingSql.CreateLedgerImmutabilityTrigger)
        |> ignore

        OperationsBillingSql.CreateHistoricalPricingProtectionTriggers.Split([| "GO" |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun statement -> statement.Trim())
        |> Array.filter (fun statement -> not (String.IsNullOrWhiteSpace(statement)))
        |> Array.iter (fun statement -> migrationBuilder.Sql(statement) |> ignore)

        migrationBuilder.Sql(OperationsBillingSql.CreateTerminalRawUsageFactProtectionTrigger)
        |> ignore

        migrationBuilder.Sql(OperationsBillingSql.CreatePermanentBillingFailureProtectionTrigger)
        |> ignore

    /// Declares the correction-work terminal failure column and pending-work index literally for migration metadata parity.
    override _.BuildTargetModel(modelBuilder: ModelBuilder) =
        modelBuilder.HasAnnotation("ProductVersion", "10.0.9")
        |> ignore

        modelBuilder.HasAnnotation("Relational:MaxIdentifierLength", 128)
        |> ignore

        let work = modelBuilder.Entity<BillingCorrectionWorkEntity>()

        work.ToTable("BillingCorrectionWork", "ops")
        |> ignore

        work
            .HasKey([| "BillingCorrectionWorkId" |])
            .HasName("PK_ops_BillingCorrectionWork")
        |> ignore

        for name in
            [
                "BillingCorrectionWorkId"
                "BillingPeriodId"
                "UsageFactId"
            ] do
            work
                .Property<Guid>(name)
                .HasColumnType("uniqueidentifier")
                .IsRequired()
            |> ignore

        for name, length in
            [
                "BlockedCode", 64
                "BlockedDetail", 1024
                "ReenabledByPrincipalId", 256
                "ReenabledReasonCode", 64
                "ReenabledReasonText", 1024
                "ReenabledCorrelationId", 200
            ] do
            work.Property<string>(name).HasMaxLength(length)
            |> ignore

        work
            .Property<bool>("IsAutomaticRetryEligible")
            .HasDefaultValue(true)
            .IsRequired()
        |> ignore

        for name in
            [
                "PermanentlyFailedAtUtc"
                "ReenabledAtUtc"
                "CompletedAtUtc"
            ] do
            work
                .Property<Nullable<DateTime>>(name)
                .HasColumnType("datetime2(7)")
            |> ignore

        work
            .Property<DateTime>("CreatedAtUtc")
            .HasDefaultValueSql("SYSUTCDATETIME()")
        |> ignore

        work
            .HasIndex([| "BillingPeriodId"; "UsageFactId" |])
            .HasDatabaseName("UX_ops_BillingCorrectionWork_PeriodFact")
            .IsUnique()
        |> ignore

        work
            .HasIndex(
                [|
                    "CompletedAtUtc"
                    "IsAutomaticRetryEligible"
                    "CreatedAtUtc"
                |]
            )
            .HasDatabaseName("IX_ops_BillingCorrectionWork_Pending")
        |> ignore
