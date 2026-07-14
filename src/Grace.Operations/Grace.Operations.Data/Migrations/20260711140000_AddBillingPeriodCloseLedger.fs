namespace Grace.Operations.Data.Migrations

open Grace.Operations.Data
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Migrations
open System

/// Adds owner-scoped billing lifecycle, immutable ledger, bounded failures, and retry-isolated correction work.
[<Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof<OperationsDbContext>)>]
[<Migration("20260711140000_AddBillingPeriodCloseLedger")>]
type AddBillingPeriodCloseLedger() =
    inherit Migration()

    /// Creates the complete owner-scoped billing schema and direct-SQL integrity guards.
    override _.Up(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(
            """
ALTER TABLE ops.RawUsageFact ADD AcceptedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_ops_RawUsageFact_AcceptedAtUtc DEFAULT SYSUTCDATETIME() WITH VALUES;

CREATE TABLE ops.BillingPeriod
(
    BillingPeriodId uniqueidentifier NOT NULL,
    OwnerId uniqueidentifier NOT NULL,
    OrganizationId uniqueidentifier NOT NULL,
    RepositoryId uniqueidentifier NOT NULL,
    PeriodFromUtc datetime2(7) NOT NULL,
    PeriodToUtc datetime2(7) NOT NULL,
    State int NOT NULL,
    CloseBlockedCode nvarchar(64) NULL,
    CloseBlockedDetail nvarchar(1024) NULL,
    LastCloseAttemptAtUtc datetime2(7) NULL,
    ConsecutiveCloseFailureCount int NOT NULL,
    ClosedAtUtc datetime2(7) NULL,
    CloseInitiatedByPrincipalId nvarchar(256) NULL,
    CloseReasonCode nvarchar(64) NULL,
    CloseReasonText nvarchar(1024) NULL,
    CloseCorrelationId nvarchar(200) NULL,
    CreatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_ops_BillingPeriod_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_ops_BillingPeriod PRIMARY KEY (BillingPeriodId),
    CONSTRAINT CK_ops_BillingPeriod_Range CHECK (PeriodFromUtc < PeriodToUtc),
    CONSTRAINT CK_ops_BillingPeriod_State CHECK (State BETWEEN 0 AND 3),
    CONSTRAINT CK_ops_BillingPeriod_FailureCount CHECK (ConsecutiveCloseFailureCount >= 0)
);
CREATE UNIQUE INDEX UX_ops_BillingPeriod_ScopeMonth ON ops.BillingPeriod(OwnerId,OrganizationId,RepositoryId,PeriodFromUtc,PeriodToUtc);
CREATE INDEX IX_ops_BillingPeriod_StatePeriodTo ON ops.BillingPeriod(State,PeriodToUtc);

CREATE TABLE ops.ChargePreviewFreshness
(
    BillingPeriodId uniqueidentifier NOT NULL,
    AcceptedFactsDigest char(64) NOT NULL,
    PricingDigest char(64) NOT NULL,
    PreviewCommittedAtUtc datetime2(7) NOT NULL,
    CONSTRAINT PK_ops_ChargePreviewFreshness PRIMARY KEY (BillingPeriodId),
    CONSTRAINT FK_ops_ChargePreviewFreshness_BillingPeriod FOREIGN KEY (BillingPeriodId) REFERENCES ops.BillingPeriod(BillingPeriodId) ON DELETE CASCADE
);

CREATE TABLE ops.ChargeLedgerEntry
(
    ChargeLedgerEntryId uniqueidentifier NOT NULL,
    BillingPeriodId uniqueidentifier NOT NULL,
    EntryKind int NOT NULL,
    SourceChargePreviewLineId uniqueidentifier NULL,
    PriorChargeLedgerEntryId uniqueidentifier NULL,
    BillingCorrectionWorkId uniqueidentifier NULL,
    FactKind int NOT NULL,
    BillableUsageKindMappingId uniqueidentifier NOT NULL,
    BillableUsageKind int NOT NULL,
    PricingAssignmentId uniqueidentifier NOT NULL,
    PricingPlanId uniqueidentifier NOT NULL,
    PricingRateId uniqueidentifier NOT NULL,
    CurrencyCode varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    UnitName nvarchar(64) NOT NULL,
    UnitQuantity bigint NOT NULL,
    UnitPriceMicros bigint NOT NULL,
    EffectiveFromUtc datetime2(7) NOT NULL,
    EffectiveToUtc datetime2(7) NOT NULL,
    Quantity bigint NOT NULL,
    ChargeMicros bigint NOT NULL,
    InitiatedByPrincipalId nvarchar(256) NOT NULL,
    ReasonCode nvarchar(64) NOT NULL,
    ReasonText nvarchar(1024) NOT NULL,
    CorrelationId nvarchar(200) NOT NULL,
    CreatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_ops_ChargeLedgerEntry_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_ops_ChargeLedgerEntry PRIMARY KEY (ChargeLedgerEntryId),
    CONSTRAINT FK_ops_ChargeLedgerEntry_BillingPeriod FOREIGN KEY (BillingPeriodId) REFERENCES ops.BillingPeriod(BillingPeriodId),
    CONSTRAINT CK_ops_ChargeLedgerEntry_Kind CHECK (EntryKind BETWEEN 0 AND 1),
    CONSTRAINT CK_ops_ChargeLedgerEntry_Source CHECK ((EntryKind=0 AND SourceChargePreviewLineId IS NOT NULL AND PriorChargeLedgerEntryId IS NULL) OR (EntryKind=1 AND SourceChargePreviewLineId IS NULL)),
    CONSTRAINT CK_ops_ChargeLedgerEntry_Currency CHECK (LEN(CurrencyCode)=3 AND CurrencyCode COLLATE Latin1_General_100_BIN2=UPPER(CurrencyCode) COLLATE Latin1_General_100_BIN2 AND CurrencyCode COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^A-Z]%'),
    CONSTRAINT CK_ops_ChargeLedgerEntry_UnitQuantity CHECK (UnitQuantity>0),
    CONSTRAINT CK_ops_ChargeLedgerEntry_UnitPriceMicros CHECK (UnitPriceMicros>=0),
    CONSTRAINT CK_ops_ChargeLedgerEntry_EffectiveRange CHECK (EffectiveFromUtc<EffectiveToUtc)
);
CREATE UNIQUE INDEX UX_ops_ChargeLedgerEntry_Initial ON ops.ChargeLedgerEntry(BillingPeriodId,EntryKind,SourceChargePreviewLineId) WHERE SourceChargePreviewLineId IS NOT NULL;
CREATE UNIQUE INDEX UX_ops_ChargeLedgerEntry_Correction ON ops.ChargeLedgerEntry(BillingPeriodId,CorrelationId,EntryKind,FactKind,BillableUsageKindMappingId,BillableUsageKind,PricingAssignmentId,PricingPlanId,PricingRateId,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc,EffectiveToUtc,Quantity,ChargeMicros,PriorChargeLedgerEntryId,BillingCorrectionWorkId) WHERE SourceChargePreviewLineId IS NULL;

CREATE TABLE ops.BillingIngestionFailure
(
    BillingIngestionFailureId uniqueidentifier NOT NULL,
    UsageFactId uniqueidentifier NULL,
    OwnerId uniqueidentifier NULL,
    OrganizationId uniqueidentifier NULL,
    RepositoryId uniqueidentifier NULL,
    ObservedAtUtc datetime2(7) NULL,
    FailureCode nvarchar(64) NOT NULL,
    FailureDetail nvarchar(1024) NOT NULL,
    CorrelationId nvarchar(200) NOT NULL,
    CreatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_ops_BillingIngestionFailure_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
    ResolvedAtUtc datetime2(7) NULL,
    ResolutionDetail nvarchar(1024) NULL,
    CONSTRAINT PK_ops_BillingIngestionFailure PRIMARY KEY (BillingIngestionFailureId)
);
CREATE UNIQUE INDEX UX_ops_BillingIngestionFailure_ActiveFact ON ops.BillingIngestionFailure(UsageFactId) WHERE UsageFactId IS NOT NULL AND ResolvedAtUtc IS NULL;
CREATE INDEX IX_ops_BillingIngestionFailure_ScopeObserved ON ops.BillingIngestionFailure(OwnerId,OrganizationId,RepositoryId,ObservedAtUtc) WHERE ResolvedAtUtc IS NULL;

CREATE TABLE ops.BillingCorrectionWork
(
    BillingCorrectionWorkId uniqueidentifier NOT NULL,
    BillingPeriodId uniqueidentifier NOT NULL,
    UsageFactId uniqueidentifier NOT NULL,
    BlockedCode nvarchar(64) NULL,
    BlockedDetail nvarchar(1024) NULL,
    IsAutomaticRetryEligible bit NOT NULL CONSTRAINT DF_ops_BillingCorrectionWork_IsAutomaticRetryEligible DEFAULT 1,
    ReenabledAtUtc datetime2(7) NULL,
    ReenabledByPrincipalId nvarchar(256) NULL,
    ReenabledReasonCode nvarchar(64) NULL,
    ReenabledReasonText nvarchar(1024) NULL,
    ReenabledCorrelationId nvarchar(200) NULL,
    CompletedAtUtc datetime2(7) NULL,
    CreatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_ops_BillingCorrectionWork_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_ops_BillingCorrectionWork PRIMARY KEY (BillingCorrectionWorkId),
    CONSTRAINT FK_ops_BillingCorrectionWork_BillingPeriod FOREIGN KEY (BillingPeriodId) REFERENCES ops.BillingPeriod(BillingPeriodId)
);
CREATE UNIQUE INDEX UX_ops_BillingCorrectionWork_PeriodFact ON ops.BillingCorrectionWork(BillingPeriodId,UsageFactId);
CREATE INDEX IX_ops_BillingCorrectionWork_Pending ON ops.BillingCorrectionWork(CompletedAtUtc,IsAutomaticRetryEligible,CreatedAtUtc);
"""
        )
        |> ignore

        migrationBuilder.Sql(OperationsBillingSql.CreateLedgerImmutabilityTrigger)
        |> ignore

        OperationsBillingSql.CreateHistoricalPricingProtectionTriggers.Split([| "GO" |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun statement -> statement.Trim())
        |> Array.filter (fun statement -> not (String.IsNullOrWhiteSpace(statement)))
        |> Array.iter (fun statement -> migrationBuilder.Sql(statement) |> ignore)

    /// Removes billing protection triggers before their referenced billing tables and then removes this migration's schema.
    override _.Down(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(
            "DROP TRIGGER IF EXISTS ops.TR_ops_PricingRate_HistoricalProtection; DROP TRIGGER IF EXISTS ops.TR_ops_BillableUsageKindMapping_HistoricalProtection; DROP TRIGGER IF EXISTS ops.TR_ops_PricingPlan_HistoricalProtection; DROP TRIGGER IF EXISTS ops.TR_ops_PricingAssignment_HistoricalProtection; DROP TRIGGER IF EXISTS ops.TR_ops_ChargeLedgerEntry_Immutable; DROP TABLE IF EXISTS ops.BillingCorrectionWork; DROP TABLE IF EXISTS ops.BillingIngestionFailure; DROP TABLE IF EXISTS ops.ChargeLedgerEntry; DROP TABLE IF EXISTS ops.ChargePreviewFreshness; DROP TABLE IF EXISTS ops.BillingPeriod; ALTER TABLE ops.RawUsageFact DROP CONSTRAINT IF EXISTS DF_ops_RawUsageFact_AcceptedAtUtc; ALTER TABLE ops.RawUsageFact DROP COLUMN IF EXISTS AcceptedAtUtc;"
        )
        |> ignore

    /// Captures the complete current Operations model at this migration boundary.
    override _.BuildTargetModel(modelBuilder: ModelBuilder) =
        modelBuilder.HasAnnotation("ProductVersion", "10.0.9")
        |> ignore
        // The preceding migration owns the pre-billing model. The billing delta remains literal here so runtime edits cannot rewrite this target.
        OperationsModel.configure modelBuilder
        let rawFact = modelBuilder.Entity<RawUsageFactEntity>()

        rawFact
            .Property<System.DateTime>("AcceptedAtUtc")
            .HasColumnType("datetime2(7)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .IsRequired()
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
                .Property<System.Guid>(name)
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
                .Property<System.DateTime>(name)
                .HasColumnType("datetime2(7)")
                .IsRequired()
            |> ignore

        period.Property<int>("State").IsRequired()
        |> ignore

        period
            .Property<string>("CloseBlockedCode")
            .HasMaxLength(64)
        |> ignore

        period
            .Property<string>("CloseBlockedDetail")
            .HasMaxLength(1024)
        |> ignore

        period
            .Property<System.Nullable<System.DateTime>>("LastCloseAttemptAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        period
            .Property<int>("ConsecutiveCloseFailureCount")
            .IsRequired()
        |> ignore

        period
            .Property<System.Nullable<System.DateTime>>("ClosedAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        period
            .Property<string>("CloseInitiatedByPrincipalId")
            .HasMaxLength(256)
        |> ignore

        period
            .Property<string>("CloseReasonCode")
            .HasMaxLength(64)
        |> ignore

        period
            .Property<string>("CloseReasonText")
            .HasMaxLength(1024)
        |> ignore

        period
            .Property<string>("CloseCorrelationId")
            .HasMaxLength(200)
        |> ignore

        period
            .Property<System.DateTime>("CreatedAtUtc")
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

        let freshness = modelBuilder.Entity<ChargePreviewFreshnessEntity>()

        freshness.ToTable("ChargePreviewFreshness", "ops")
        |> ignore

        freshness
            .HasKey([| "BillingPeriodId" |])
            .HasName("PK_ops_ChargePreviewFreshness")
        |> ignore

        freshness
            .Property<System.Guid>("BillingPeriodId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        freshness
            .Property<string>("AcceptedFactsDigest")
            .HasColumnType("char(64)")
            .HasMaxLength(64)
            .IsUnicode(false)
            .IsFixedLength()
            .IsRequired()
        |> ignore

        freshness
            .Property<string>("PricingDigest")
            .HasColumnType("char(64)")
            .HasMaxLength(64)
            .IsUnicode(false)
            .IsFixedLength()
            .IsRequired()
        |> ignore

        freshness
            .Property<System.DateTime>("PreviewCommittedAtUtc")
            .HasColumnType("datetime2(7)")
            .IsRequired()
        |> ignore

        freshness
            .HasOne<BillingPeriodEntity>()
            .WithOne()
            .HasForeignKey<ChargePreviewFreshnessEntity>("BillingPeriodId")
            .HasConstraintName("FK_ops_ChargePreviewFreshness_BillingPeriod")
            .OnDelete(DeleteBehavior.Cascade)
        |> ignore

        let ledger = modelBuilder.Entity<ChargeLedgerEntryEntity>()

        ledger.ToTable("ChargeLedgerEntry", "ops")
        |> ignore

        ledger
            .HasKey([| "ChargeLedgerEntryId" |])
            .HasName("PK_ops_ChargeLedgerEntry")
        |> ignore

        for name in
            [
                "ChargeLedgerEntryId"
                "BillingPeriodId"
                "BillableUsageKindMappingId"
                "PricingAssignmentId"
                "PricingPlanId"
                "PricingRateId"
            ] do
            ledger
                .Property<System.Guid>(name)
                .HasColumnType("uniqueidentifier")
                .IsRequired()
            |> ignore

        for name in
            [
                "SourceChargePreviewLineId"
                "PriorChargeLedgerEntryId"
                "BillingCorrectionWorkId"
            ] do
            ledger
                .Property<System.Nullable<System.Guid>>(name)
                .HasColumnType("uniqueidentifier")
            |> ignore

        ledger.Property<int>("EntryKind").IsRequired()
        |> ignore

        ledger.Property<int>("FactKind").IsRequired()
        |> ignore

        ledger
            .Property<int>("BillableUsageKind")
            .IsRequired()
        |> ignore

        ledger
            .Property<string>("CurrencyCode")
            .HasColumnType("varchar(3)")
            .HasMaxLength(3)
            .IsUnicode(false)
            .IsRequired()
        |> ignore

        ledger
            .Property<string>("UnitName")
            .HasMaxLength(64)
            .IsRequired()
        |> ignore

        for name in
            [
                "UnitQuantity"
                "UnitPriceMicros"
                "Quantity"
                "ChargeMicros"
            ] do
            ledger.Property<int64>(name).IsRequired()
            |> ignore

        for name in
            [
                "EffectiveFromUtc"
                "EffectiveToUtc"
                "CreatedAtUtc"
            ] do
            ledger
                .Property<System.DateTime>(name)
                .HasColumnType("datetime2(7)")
                .IsRequired()
            |> ignore

        ledger
            .Property<string>("InitiatedByPrincipalId")
            .HasMaxLength(256)
            .IsRequired()
        |> ignore

        ledger
            .Property<string>("ReasonCode")
            .HasMaxLength(64)
            .IsRequired()
        |> ignore

        ledger
            .Property<string>("ReasonText")
            .HasMaxLength(1024)
            .IsRequired()
        |> ignore

        ledger
            .Property<string>("CorrelationId")
            .HasMaxLength(200)
            .IsRequired()
        |> ignore

        ledger
            .Property<System.DateTime>("CreatedAtUtc")
            .HasDefaultValueSql("SYSUTCDATETIME()")
        |> ignore

        ledger
            .HasIndex(
                [|
                    "BillingPeriodId"
                    "EntryKind"
                    "SourceChargePreviewLineId"
                |]
            )
            .HasDatabaseName("UX_ops_ChargeLedgerEntry_Initial")
            .IsUnique()
            .HasFilter("[SourceChargePreviewLineId] IS NOT NULL")
        |> ignore

        ledger
            .HasIndex(
                [|
                    "BillingPeriodId"
                    "CorrelationId"
                    "EntryKind"
                    "FactKind"
                    "BillableUsageKindMappingId"
                    "BillableUsageKind"
                    "PricingAssignmentId"
                    "PricingPlanId"
                    "PricingRateId"
                    "CurrencyCode"
                    "UnitName"
                    "UnitQuantity"
                    "UnitPriceMicros"
                    "EffectiveFromUtc"
                    "EffectiveToUtc"
                    "Quantity"
                    "ChargeMicros"
                    "PriorChargeLedgerEntryId"
                    "BillingCorrectionWorkId"
                |]
            )
            .HasDatabaseName("UX_ops_ChargeLedgerEntry_Correction")
            .IsUnique()
            .HasFilter("[SourceChargePreviewLineId] IS NULL")
        |> ignore

        ledger
            .HasOne<BillingPeriodEntity>()
            .WithMany()
            .HasForeignKey("BillingPeriodId")
            .HasConstraintName("FK_ops_ChargeLedgerEntry_BillingPeriod")
            .OnDelete(DeleteBehavior.Restrict)
        |> ignore

        let failure = modelBuilder.Entity<BillingIngestionFailureEntity>()

        failure.ToTable("BillingIngestionFailure", "ops")
        |> ignore

        failure
            .HasKey([| "BillingIngestionFailureId" |])
            .HasName("PK_ops_BillingIngestionFailure")
        |> ignore

        failure
            .Property<System.Guid>("BillingIngestionFailureId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        for name in
            [
                "UsageFactId"
                "OwnerId"
                "OrganizationId"
                "RepositoryId"
            ] do
            failure
                .Property<System.Nullable<System.Guid>>(name)
                .HasColumnType("uniqueidentifier")
            |> ignore

        failure
            .Property<System.Nullable<System.DateTime>>("ObservedAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        failure
            .Property<string>("FailureCode")
            .HasMaxLength(64)
            .IsRequired()
        |> ignore

        failure
            .Property<string>("FailureDetail")
            .HasMaxLength(1024)
            .IsRequired()
        |> ignore

        failure
            .Property<string>("CorrelationId")
            .HasMaxLength(200)
            .IsRequired()
        |> ignore

        failure
            .Property<System.DateTime>("CreatedAtUtc")
            .HasDefaultValueSql("SYSUTCDATETIME()")
        |> ignore

        failure
            .Property<System.Nullable<System.DateTime>>("ResolvedAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        failure
            .Property<string>("ResolutionDetail")
            .HasMaxLength(1024)
        |> ignore

        failure
            .HasIndex(
                [|
                    "OwnerId"
                    "OrganizationId"
                    "RepositoryId"
                    "ObservedAtUtc"
                |]
            )
            .HasDatabaseName("IX_ops_BillingIngestionFailure_ScopeObserved")
            .HasFilter("[ResolvedAtUtc] IS NULL")
        |> ignore

        failure
            .HasIndex([| "UsageFactId" |])
            .HasDatabaseName("UX_ops_BillingIngestionFailure_ActiveFact")
            .IsUnique()
            .HasFilter("[UsageFactId] IS NOT NULL AND [ResolvedAtUtc] IS NULL")
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
                .Property<System.Guid>(name)
                .HasColumnType("uniqueidentifier")
                .IsRequired()
            |> ignore

        work
            .Property<string>("BlockedCode")
            .HasMaxLength(64)
        |> ignore

        work
            .Property<string>("BlockedDetail")
            .HasMaxLength(1024)
        |> ignore

        work
            .Property<bool>("IsAutomaticRetryEligible")
            .HasDefaultValue(true)
            .IsRequired()
        |> ignore

        work
            .Property<System.Nullable<System.DateTime>>("ReenabledAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        work
            .Property<string>("ReenabledByPrincipalId")
            .HasMaxLength(256)
        |> ignore

        work
            .Property<string>("ReenabledReasonCode")
            .HasMaxLength(64)
        |> ignore

        work
            .Property<string>("ReenabledReasonText")
            .HasMaxLength(1024)
        |> ignore

        work
            .Property<string>("ReenabledCorrelationId")
            .HasMaxLength(200)
        |> ignore

        work
            .Property<System.Nullable<System.DateTime>>("CompletedAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        work
            .Property<System.DateTime>("CreatedAtUtc")
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

        work
            .HasOne<BillingPeriodEntity>()
            .WithMany()
            .HasForeignKey("BillingPeriodId")
            .HasConstraintName("FK_ops_BillingCorrectionWork_BillingPeriod")
            .OnDelete(DeleteBehavior.Restrict)
        |> ignore
