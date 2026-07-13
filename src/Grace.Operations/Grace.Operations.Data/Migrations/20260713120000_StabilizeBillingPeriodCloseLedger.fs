namespace Grace.Operations.Data.Migrations

open Grace.Operations.Data
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Migrations
open System

/// Freezes terminal billing source evidence and records permanent deterministic calculation failures without reopening history.
[<Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof<OperationsDbContext>)>]
[<Migration("20260713120000_StabilizeBillingPeriodCloseLedger")>]
type StabilizeBillingPeriodCloseLedger() =
    inherit Migration()

    /// Adds permanent-failure evidence and the database guards that keep terminal billing evidence stable.
    override _.Up(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(
            """
ALTER TABLE ops.BillingPeriod DROP CONSTRAINT CK_ops_BillingPeriod_State;
ALTER TABLE ops.BillingPeriod ADD
    PermanentFailureCode nvarchar(64) NULL,
    PermanentFailureDetail nvarchar(1024) NULL,
    PermanentlyFailedAtUtc datetime2(7) NULL,
    PermanentFailureInitiatedByPrincipalId nvarchar(256) NULL,
    PermanentFailureReasonCode nvarchar(64) NULL,
    PermanentFailureReasonText nvarchar(1024) NULL,
    PermanentFailureCorrelationId nvarchar(200) NULL,
    CONSTRAINT CK_ops_BillingPeriod_State CHECK (State BETWEEN 0 AND 4),
    CONSTRAINT CK_ops_BillingPeriod_PermanentFailureEvidence CHECK
    (
        (State<>4)
        OR
        (
            PermanentFailureCode IS NOT NULL
            AND PermanentFailureDetail IS NOT NULL
            AND PermanentlyFailedAtUtc IS NOT NULL
            AND PermanentFailureInitiatedByPrincipalId IS NOT NULL
            AND PermanentFailureReasonCode IS NOT NULL
            AND PermanentFailureReasonText IS NOT NULL
            AND PermanentFailureCorrelationId IS NOT NULL
        )
    );
"""
        )
        |> ignore

        migrationBuilder.Sql(OperationsBillingSql.CreateTerminalRawUsageFactProtectionTrigger)
        |> ignore

        migrationBuilder.Sql(OperationsBillingSql.CreatePermanentBillingFailureProtectionTrigger)
        |> ignore

    /// Removes terminal protections before dropping permanent-failure fields and restoring the prior state range.
    override _.Down(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(
            """
DROP TRIGGER IF EXISTS ops.TR_ops_BillingPeriod_PermanentFailureProtection;
DROP TRIGGER IF EXISTS ops.TR_ops_RawUsageFact_TerminalBillingProtection;
ALTER TABLE ops.BillingPeriod DROP CONSTRAINT CK_ops_BillingPeriod_PermanentFailureEvidence;
ALTER TABLE ops.BillingPeriod DROP CONSTRAINT CK_ops_BillingPeriod_State;
ALTER TABLE ops.BillingPeriod DROP COLUMN
    PermanentFailureCode,
    PermanentFailureDetail,
    PermanentlyFailedAtUtc,
    PermanentFailureInitiatedByPrincipalId,
    PermanentFailureReasonCode,
    PermanentFailureReasonText,
    PermanentFailureCorrelationId;
ALTER TABLE ops.BillingPeriod ADD CONSTRAINT CK_ops_BillingPeriod_State CHECK (State BETWEEN 0 AND 3);
"""
        )
        |> ignore

    /// Freezes the new period fields in a literal target model rather than delegating migration metadata to runtime configuration.
    override _.BuildTargetModel(modelBuilder: ModelBuilder) =
        modelBuilder.HasAnnotation("ProductVersion", "10.0.9")
        |> ignore

        modelBuilder.HasAnnotation("Relational:MaxIdentifierLength", 128)
        |> ignore

        let period = modelBuilder.Entity<BillingPeriodEntity>()
        period.ToTable("BillingPeriod", "ops") |> ignore

        period
            .HasKey([| "BillingPeriodId" |])
            .HasName("PK_ops_BillingPeriod")
        |> ignore

        for name in
            [
                "BillingPeriodId"
                "OwnerId"
                "OrganizationId"
                "RepositoryId"
            ] do
            period
                .Property<Guid>(name)
                .HasColumnType("uniqueidentifier")
                .IsRequired()
            |> ignore

        for name in
            [
                "PeriodFromUtc"
                "PeriodToUtc"
                "CreatedAtUtc"
            ] do
            period
                .Property<DateTime>(name)
                .HasColumnType("datetime2(7)")
                .IsRequired()
            |> ignore

        period.Property<int>("State").IsRequired()
        |> ignore

        for name, length in
            [
                "CloseBlockedCode", 64
                "CloseBlockedDetail", 1024
                "CloseInitiatedByPrincipalId", 256
                "CloseReasonCode", 64
                "CloseReasonText", 1024
                "CloseCorrelationId", 200
                "PermanentFailureCode", 64
                "PermanentFailureDetail", 1024
                "PermanentFailureInitiatedByPrincipalId", 256
                "PermanentFailureReasonCode", 64
                "PermanentFailureReasonText", 1024
                "PermanentFailureCorrelationId", 200
            ] do
            period.Property<string>(name).HasMaxLength(length)
            |> ignore

        for name in
            [
                "LastCloseAttemptAtUtc"
                "ClosedAtUtc"
                "PermanentlyFailedAtUtc"
            ] do
            period
                .Property<Nullable<DateTime>>(name)
                .HasColumnType("datetime2(7)")
            |> ignore

        period
            .Property<int>("ConsecutiveCloseFailureCount")
            .IsRequired()
        |> ignore

        period
            .Property<DateTime>("CreatedAtUtc")
            .HasDefaultValueSql("SYSUTCDATETIME()")
        |> ignore

        period
            .HasIndex(
                [|
                    "OwnerId"
                    "OrganizationId"
                    "RepositoryId"
                    "PeriodFromUtc"
                    "PeriodToUtc"
                |]
            )
            .HasDatabaseName("UX_ops_BillingPeriod_ScopeMonth")
            .IsUnique()
        |> ignore

        period
            .HasIndex([| "State"; "PeriodToUtc" |])
            .HasDatabaseName("IX_ops_BillingPeriod_StatePeriodTo")
        |> ignore
