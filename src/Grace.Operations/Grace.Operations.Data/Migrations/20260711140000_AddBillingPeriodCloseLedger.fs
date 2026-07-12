namespace Grace.Operations.Data.Migrations

open Grace.Operations.Data
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Migrations
open Microsoft.EntityFrameworkCore.Metadata.Builders
open System

/// Adds UTC billing lifecycle, durable preview freshness, immutable ledger, scoped failures, and correction delivery.
[<Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof<OperationsDbContext>)>]
[<Migration("20260711140000_AddBillingPeriodCloseLedger")>]
type AddBillingPeriodCloseLedger() =
    inherit Migration()

    /// Applies billing close persistence and SQL-enforced historical immutability.
    override _.Up(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(
            """

CREATE TABLE ops.BillingPeriod (
 BillingPeriodId uniqueidentifier NOT NULL, CustomerId uniqueidentifier NOT NULL, OwnerId uniqueidentifier NOT NULL,
 OrganizationId uniqueidentifier NOT NULL, RepositoryId uniqueidentifier NOT NULL, PeriodFromUtc datetime2(7) NOT NULL,
 PeriodToUtc datetime2(7) NOT NULL, State int NOT NULL, CloseBlockedCode nvarchar(64) NULL,
 CloseBlockedDetail nvarchar(1024) NULL, LastCloseAttemptAtUtc datetime2(7) NULL,
 ConsecutiveCloseFailureCount int NOT NULL CONSTRAINT DF_ops_BillingPeriod_FailureCount DEFAULT 0,
 ClosedAtUtc datetime2(7) NULL, CloseInitiatedByPrincipalId nvarchar(256) NULL, CloseReasonCode nvarchar(64) NULL,
 CloseReasonText nvarchar(1024) NULL, CloseCorrelationId nvarchar(200) NULL,
 CreatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_ops_BillingPeriod_Created DEFAULT SYSUTCDATETIME(),
 CONSTRAINT PK_ops_BillingPeriod PRIMARY KEY (BillingPeriodId),
 CONSTRAINT CK_ops_BillingPeriod_Range CHECK (PeriodFromUtc < PeriodToUtc),
 CONSTRAINT CK_ops_BillingPeriod_State CHECK (State BETWEEN 0 AND 3),
 CONSTRAINT CK_ops_BillingPeriod_FailureCount CHECK (ConsecutiveCloseFailureCount >= 0));
CREATE UNIQUE INDEX UX_ops_BillingPeriod_ScopeMonth ON ops.BillingPeriod(CustomerId,OwnerId,OrganizationId,RepositoryId,PeriodFromUtc,PeriodToUtc);
CREATE INDEX IX_ops_BillingPeriod_StatePeriodTo ON ops.BillingPeriod(State,PeriodToUtc);

CREATE TABLE ops.ChargePreviewFreshness (
 BillingPeriodId uniqueidentifier NOT NULL, AcceptedFactsDigest char(64) NOT NULL, PricingDigest char(64) NOT NULL,
 PreviewCommittedAtUtc datetime2(7) NOT NULL, CONSTRAINT PK_ops_ChargePreviewFreshness PRIMARY KEY (BillingPeriodId),
 CONSTRAINT FK_ops_ChargePreviewFreshness_BillingPeriod FOREIGN KEY(BillingPeriodId) REFERENCES ops.BillingPeriod(BillingPeriodId) ON DELETE CASCADE);

CREATE TABLE ops.ChargeLedgerEntry (
 ChargeLedgerEntryId uniqueidentifier NOT NULL, BillingPeriodId uniqueidentifier NOT NULL, EntryKind int NOT NULL,
 SourceChargePreviewLineId uniqueidentifier NULL, PriorChargeLedgerEntryId uniqueidentifier NULL, FactKind int NOT NULL,
 BillableUsageKindMappingId uniqueidentifier NOT NULL, BillableUsageKind int NOT NULL,
 CustomerPricingAssignmentId uniqueidentifier NOT NULL, PricingPlanId uniqueidentifier NOT NULL, PricingRateId uniqueidentifier NOT NULL,
 CurrencyCode varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL, UnitName nvarchar(64) NOT NULL,
 UnitQuantity bigint NOT NULL, UnitPriceMicros bigint NOT NULL, EffectiveFromUtc datetime2(7) NOT NULL,
 EffectiveToUtc datetime2(7) NOT NULL, Quantity bigint NOT NULL, ChargeMicros bigint NOT NULL,
 InitiatedByPrincipalId nvarchar(256) NOT NULL, ReasonCode nvarchar(64) NOT NULL, ReasonText nvarchar(1024) NOT NULL,
 CorrelationId nvarchar(200) NOT NULL, CreatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_ops_ChargeLedgerEntry_Created DEFAULT SYSUTCDATETIME(),
 CONSTRAINT PK_ops_ChargeLedgerEntry PRIMARY KEY(ChargeLedgerEntryId),
 CONSTRAINT FK_ops_ChargeLedgerEntry_BillingPeriod FOREIGN KEY(BillingPeriodId) REFERENCES ops.BillingPeriod(BillingPeriodId),
 CONSTRAINT FK_ops_ChargeLedgerEntry_ChargePreviewLine FOREIGN KEY(SourceChargePreviewLineId) REFERENCES ops.ChargePreviewLine(ChargePreviewLineId),
 CONSTRAINT FK_ops_ChargeLedgerEntry_Prior FOREIGN KEY(PriorChargeLedgerEntryId) REFERENCES ops.ChargeLedgerEntry(ChargeLedgerEntryId),
 CONSTRAINT CK_ops_ChargeLedgerEntry_Kind CHECK (EntryKind BETWEEN 0 AND 2),
 CONSTRAINT CK_ops_ChargeLedgerEntry_Currency CHECK (LEN(CurrencyCode)=3 AND CurrencyCode=UPPER(CurrencyCode) AND CurrencyCode NOT LIKE '%[^A-Z]%'),
 CONSTRAINT CK_ops_ChargeLedgerEntry_ChargeSource CHECK ((EntryKind=0 AND SourceChargePreviewLineId IS NOT NULL AND PriorChargeLedgerEntryId IS NULL) OR (EntryKind IN (1,2) AND SourceChargePreviewLineId IS NULL)),
 CONSTRAINT CK_ops_ChargeLedgerEntry_UnitQuantity CHECK (UnitQuantity > 0),
 CONSTRAINT CK_ops_ChargeLedgerEntry_UnitPriceMicros CHECK (UnitPriceMicros >= 0));
CREATE UNIQUE INDEX UX_ops_ChargeLedgerEntry_Initial ON ops.ChargeLedgerEntry(BillingPeriodId,EntryKind,SourceChargePreviewLineId) WHERE SourceChargePreviewLineId IS NOT NULL;
CREATE UNIQUE INDEX UX_ops_ChargeLedgerEntry_Correction ON ops.ChargeLedgerEntry(BillingPeriodId,CorrelationId,EntryKind,FactKind,BillableUsageKindMappingId,BillableUsageKind,CustomerPricingAssignmentId,PricingPlanId,PricingRateId,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc,EffectiveToUtc,Quantity,ChargeMicros,PriorChargeLedgerEntryId) WHERE SourceChargePreviewLineId IS NULL;
CREATE INDEX IX_ChargeLedgerEntry_SourceChargePreviewLineId ON ops.ChargeLedgerEntry(SourceChargePreviewLineId);
CREATE INDEX IX_ChargeLedgerEntry_PriorChargeLedgerEntryId ON ops.ChargeLedgerEntry(PriorChargeLedgerEntryId);

CREATE TABLE ops.BillingIngestionFailure (
 BillingIngestionFailureId uniqueidentifier NOT NULL, UsageFactId uniqueidentifier NULL, CustomerId uniqueidentifier NULL,
 OwnerId uniqueidentifier NULL, OrganizationId uniqueidentifier NULL, RepositoryId uniqueidentifier NULL, ObservedAtUtc datetime2(7) NULL,
 FailureCode nvarchar(64) NOT NULL, FailureDetail nvarchar(1024) NOT NULL,
 CreatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_ops_BillingIngestionFailure_Created DEFAULT SYSUTCDATETIME(),
 ResolvedAtUtc datetime2(7) NULL, ResolvedByPrincipalId nvarchar(256) NULL, ResolutionReasonCode nvarchar(64) NULL,
 ResolutionReasonText nvarchar(1024) NULL, ResolutionCorrelationId nvarchar(200) NULL,
 CONSTRAINT PK_ops_BillingIngestionFailure PRIMARY KEY(BillingIngestionFailureId));
CREATE UNIQUE INDEX UX_ops_BillingIngestionFailure_ActiveFact ON ops.BillingIngestionFailure(UsageFactId) WHERE UsageFactId IS NOT NULL AND ResolvedAtUtc IS NULL;
CREATE INDEX IX_ops_BillingIngestionFailure_ActiveScope ON ops.BillingIngestionFailure(CustomerId,OwnerId,OrganizationId,RepositoryId,ObservedAtUtc) WHERE ResolvedAtUtc IS NULL;

CREATE TABLE ops.BillingCorrectionWork (
 BillingCorrectionWorkId uniqueidentifier NOT NULL, BillingPeriodId uniqueidentifier NOT NULL, UsageFactId uniqueidentifier NOT NULL,
 CorrelationId nvarchar(200) NOT NULL, CreatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_ops_BillingCorrectionWork_Created DEFAULT SYSUTCDATETIME(),
 CompletedAtUtc datetime2(7) NULL, CONSTRAINT PK_ops_BillingCorrectionWork PRIMARY KEY(BillingCorrectionWorkId),
 CONSTRAINT FK_ops_BillingCorrectionWork_BillingPeriod FOREIGN KEY(BillingPeriodId) REFERENCES ops.BillingPeriod(BillingPeriodId));
CREATE UNIQUE INDEX UX_ops_BillingCorrectionWork_PeriodFact ON ops.BillingCorrectionWork(BillingPeriodId,UsageFactId);
CREATE INDEX IX_ops_BillingCorrectionWork_Pending ON ops.BillingCorrectionWork(CompletedAtUtc,CreatedAtUtc);

EXEC(N'CREATE OR ALTER TRIGGER ops.TR_ops_ChargeLedgerEntry_Immutable ON ops.ChargeLedgerEntry INSTEAD OF UPDATE, DELETE AS BEGIN SET NOCOUNT ON; THROW 51020, ''Posted charge ledger entries are immutable.'', 1; END;');
EXEC(N'CREATE OR ALTER TRIGGER ops.TR_ops_PricingRate_HistoricalProtection ON ops.PricingRate AFTER INSERT, UPDATE, DELETE AS BEGIN SET NOCOUNT ON; IF EXISTS(SELECT 1 FROM (SELECT PricingPlanId,EffectiveFromUtc,EffectiveToUtc FROM inserted UNION ALL SELECT PricingPlanId,EffectiveFromUtc,EffectiveToUtc FROM deleted) d JOIN ops.CustomerPricingAssignment a ON a.PricingPlanId=d.PricingPlanId JOIN ops.BillingPeriod p ON p.CustomerId=a.CustomerId AND p.OwnerId=a.OwnerId AND p.OrganizationId=a.OrganizationId AND p.RepositoryId=a.RepositoryId WHERE p.State IN(2,3) AND a.EffectiveFromUtc<p.PeriodToUtc AND (a.EffectiveToUtc IS NULL OR a.EffectiveToUtc>p.PeriodFromUtc) AND d.EffectiveFromUtc<p.PeriodToUtc AND (d.EffectiveToUtc IS NULL OR d.EffectiveToUtc>p.PeriodFromUtc)) THROW 51021, ''Historical pricing applicable to a closed billing period is immutable.'', 1; END;');
EXEC(N'CREATE OR ALTER TRIGGER ops.TR_ops_PricingPlan_HistoricalProtection ON ops.PricingPlan AFTER INSERT, UPDATE, DELETE AS BEGIN SET NOCOUNT ON; IF EXISTS(SELECT 1 FROM (SELECT PricingPlanId,EffectiveFromUtc,EffectiveToUtc FROM inserted UNION ALL SELECT PricingPlanId,EffectiveFromUtc,EffectiveToUtc FROM deleted) d JOIN ops.CustomerPricingAssignment a ON a.PricingPlanId=d.PricingPlanId JOIN ops.BillingPeriod p ON p.CustomerId=a.CustomerId AND p.OwnerId=a.OwnerId AND p.OrganizationId=a.OrganizationId AND p.RepositoryId=a.RepositoryId WHERE p.State IN(2,3) AND a.EffectiveFromUtc<p.PeriodToUtc AND (a.EffectiveToUtc IS NULL OR a.EffectiveToUtc>p.PeriodFromUtc) AND d.EffectiveFromUtc<p.PeriodToUtc AND (d.EffectiveToUtc IS NULL OR d.EffectiveToUtc>p.PeriodFromUtc)) THROW 51022, ''Historical pricing plan applicable to a closed billing period is immutable.'', 1; END;');
EXEC(N'CREATE OR ALTER TRIGGER ops.TR_ops_BillableUsageKindMapping_HistoricalProtection ON ops.BillableUsageKindMapping AFTER INSERT, UPDATE, DELETE AS BEGIN SET NOCOUNT ON; IF EXISTS(SELECT 1 FROM (SELECT EffectiveFromUtc,EffectiveToUtc FROM inserted UNION ALL SELECT EffectiveFromUtc,EffectiveToUtc FROM deleted) d JOIN ops.BillingPeriod p ON d.EffectiveFromUtc<p.PeriodToUtc AND (d.EffectiveToUtc IS NULL OR d.EffectiveToUtc>p.PeriodFromUtc) JOIN ops.CustomerPricingAssignment a ON a.CustomerId=p.CustomerId AND a.OwnerId=p.OwnerId AND a.OrganizationId=p.OrganizationId AND a.RepositoryId=p.RepositoryId AND a.EffectiveFromUtc<p.PeriodToUtc AND (a.EffectiveToUtc IS NULL OR a.EffectiveToUtc>p.PeriodFromUtc) WHERE p.State IN(2,3)) THROW 51023, ''Historical billable mapping applicable to a closed billing period is immutable.'', 1; END;');
EXEC(N'CREATE OR ALTER TRIGGER ops.TR_ops_CustomerPricingAssignment_HistoricalProtection ON ops.CustomerPricingAssignment AFTER INSERT, UPDATE, DELETE AS BEGIN SET NOCOUNT ON; IF EXISTS(SELECT 1 FROM (SELECT CustomerId,OwnerId,OrganizationId,RepositoryId,EffectiveFromUtc,EffectiveToUtc FROM inserted UNION ALL SELECT CustomerId,OwnerId,OrganizationId,RepositoryId,EffectiveFromUtc,EffectiveToUtc FROM deleted) d JOIN ops.BillingPeriod p ON p.CustomerId=d.CustomerId AND p.OwnerId=d.OwnerId AND p.OrganizationId=d.OrganizationId AND p.RepositoryId=d.RepositoryId WHERE p.State IN(2,3) AND d.EffectiveFromUtc<p.PeriodToUtc AND (d.EffectiveToUtc IS NULL OR d.EffectiveToUtc>p.PeriodFromUtc)) THROW 51024, ''Historical customer pricing assignment applicable to a closed billing period is immutable.'', 1; END;');
"""
        )
        |> ignore

    /// Removes billing close persistence in reverse dependency order.
    override _.Down(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(
            "DROP TRIGGER IF EXISTS ops.TR_ops_CustomerPricingAssignment_HistoricalProtection; DROP TRIGGER IF EXISTS ops.TR_ops_BillableUsageKindMapping_HistoricalProtection; DROP TRIGGER IF EXISTS ops.TR_ops_PricingPlan_HistoricalProtection; DROP TRIGGER IF EXISTS ops.TR_ops_PricingRate_HistoricalProtection; DROP TRIGGER IF EXISTS ops.TR_ops_ChargeLedgerEntry_Immutable; DROP TABLE ops.BillingCorrectionWork; DROP TABLE ops.BillingIngestionFailure; DROP TABLE ops.ChargeLedgerEntry; DROP TABLE ops.ChargePreviewFreshness; DROP TABLE ops.BillingPeriod;"
        )
        |> ignore

    /// Captures the complete independently literal Operations model represented by this migration.
    override _.BuildTargetModel(modelBuilder: ModelBuilder) =
        modelBuilder.HasAnnotation("ProductVersion", "10.0.9")
        |> ignore

        // Deliberately keep this snapshot frozen with literals so future runtime model edits
        // cannot change the latest reviewed migration point before a new migration updates it.
        modelBuilder.HasDefaultSchema("ops") |> ignore

        let rawFact = modelBuilder.Entity<RawUsageFactEntity>()

        rawFact.ToTable("RawUsageFact", "ops") |> ignore

        rawFact
            .HasKey([| "UsageFactId" |])
            .HasName("PK_ops_RawUsageFact")
        |> ignore

        rawFact
            .Property<System.Guid>("UsageFactId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever()
        |> ignore

        rawFact
            .Property<byte array>("RawPayload")
            .HasColumnType("varbinary(max)")
        |> ignore

        rawFact
            .Property<string>("CorrelationId")
            .HasMaxLength(200)
            .IsRequired()
        |> ignore

        rawFact.Property<int>("FactKind").IsRequired()
        |> ignore

        rawFact
            .Property<System.Guid>("OwnerId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        rawFact
            .Property<System.Guid>("OrganizationId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        rawFact
            .Property<System.Guid>("RepositoryId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        rawFact
            .Property<string>("StoragePoolId")
            .HasMaxLength(256)
            .UseCollation("Latin1_General_100_BIN2")
            .IsRequired()
        |> ignore

        rawFact.Property<int64>("Quantity").IsRequired()
        |> ignore

        rawFact
            .Property<System.DateTime>("ObservedAtUtc")
            .HasColumnType("datetime2(7)")
            .IsRequired()
        |> ignore

        rawFact.Property<int>("ArchiveState").IsRequired()
        |> ignore

        rawFact
            .Property<string>("ArchiveBlobName")
            .HasMaxLength(512)
        |> ignore

        rawFact
            .Property<string>("ArchiveChecksumSha256Hex")
            .HasMaxLength(64)
            .IsFixedLength()
            .IsUnicode(false)
        |> ignore

        rawFact
            .Property<System.Nullable<int64>>("ArchiveByteLength")
            .HasColumnType("bigint")
        |> ignore

        rawFact
            .Property<System.Nullable<System.DateTime>>("ArchiveVerifiedAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        rawFact
            .Property<System.Nullable<System.DateTime>>("ArchivedAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        rawFact
            .Property<System.Nullable<System.DateTime>>("RehydrationExpiresAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        rawFact
            .Property<string>("LastArchiveFailureReason")
            .HasMaxLength(400)
        |> ignore

        rawFact
            .Property<System.Nullable<System.DateTime>>("LastArchiveFailureAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        rawFact
            .Property<int>("ArchiveFailureCount")
            .IsRequired()
        |> ignore

        rawFact
            .Property<System.Nullable<System.DateTime>>("ArchiveRetiredAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        rawFact
            .Property<System.DateTime>("CreatedAtUtc")
            .HasColumnType("datetime2(7)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .IsRequired()
        |> ignore

        rawFact
            .HasIndex(
                [|
                    "OwnerId"
                    "OrganizationId"
                    "RepositoryId"
                    "FactKind"
                    "ObservedAtUtc"
                |]
            )
            .HasDatabaseName("IX_ops_RawUsageFact_ScopeKindObservedAt")
        |> ignore

        rawFact
            .HasIndex(
                [|
                    "ArchiveState"
                    "ObservedAtUtc"
                    "UsageFactId"
                |]
            )
            .HasDatabaseName("IX_ops_RawUsageFact_ArchiveStateObservedAt")
        |> ignore

        rawFact
            .HasIndex([| "RehydrationExpiresAtUtc" |])
            .HasDatabaseName("IX_ops_RawUsageFact_RehydrationExpiresAtUtc")
            .HasFilter("[RehydrationExpiresAtUtc] IS NOT NULL")
        |> ignore

        let aggregate = modelBuilder.Entity<UsageAggregateMinuteEntity>()

        aggregate.ToTable("UsageAggregateMinute", "ops")
        |> ignore

        aggregate
            .HasKey(
                [|
                    "FactKind"
                    "OwnerId"
                    "OrganizationId"
                    "RepositoryId"
                    "StoragePoolId"
                    "BucketStartUtc"
                |]
            )
            .HasName("PK_ops_UsageAggregateMinute")
        |> ignore

        aggregate.Property<int>("FactKind").IsRequired()
        |> ignore

        aggregate
            .Property<System.Guid>("OwnerId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        aggregate
            .Property<System.Guid>("OrganizationId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        aggregate
            .Property<System.Guid>("RepositoryId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        aggregate
            .Property<string>("StoragePoolId")
            .HasMaxLength(256)
            .UseCollation("Latin1_General_100_BIN2")
            .IsRequired()
        |> ignore

        aggregate
            .Property<System.DateTime>("BucketStartUtc")
            .HasColumnType("datetime2(7)")
            .IsRequired()
        |> ignore

        aggregate.Property<int64>("Quantity").IsRequired()
        |> ignore

        aggregate
            .Property<System.DateTime>("UpdatedAtUtc")
            .HasColumnType("datetime2(7)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .IsRequired()
        |> ignore

        aggregate
            .HasIndex(
                [|
                    "OwnerId"
                    "OrganizationId"
                    "RepositoryId"
                    "FactKind"
                    "BucketStartUtc"
                |]
            )
            .HasDatabaseName("IX_ops_UsageAggregateMinute_ScopeKindBucket")
        |> ignore

        let pricingPlan = modelBuilder.Entity<PricingPlanEntity>()

        pricingPlan.ToTable("PricingPlan", "ops")
        |> ignore

        pricingPlan
            .HasKey([| "PricingPlanId" |])
            .HasName("PK_ops_PricingPlan")
        |> ignore

        pricingPlan
            .Property<System.Guid>("PricingPlanId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever()
        |> ignore

        pricingPlan
            .Property<string>("PlanCode")
            .HasMaxLength(128)
            .IsRequired()
        |> ignore

        pricingPlan
            .Property<string>("DisplayName")
            .HasMaxLength(200)
            .IsRequired()
        |> ignore

        pricingPlan
            .Property<System.DateTime>("EffectiveFromUtc")
            .HasColumnType("datetime2(7)")
            .IsRequired()
        |> ignore

        pricingPlan
            .Property<System.Nullable<System.DateTime>>("EffectiveToUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        pricingPlan
            .Property<System.DateTime>("CreatedAtUtc")
            .HasColumnType("datetime2(7)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .IsRequired()
        |> ignore

        pricingPlan
            .HasIndex([| "PlanCode"; "EffectiveFromUtc" |])
            .HasDatabaseName("UX_ops_PricingPlan_CodeEffectiveFrom")
            .IsUnique()
        |> ignore

        let mapping = modelBuilder.Entity<BillableUsageKindMappingEntity>()

        mapping.ToTable("BillableUsageKindMapping", "ops")
        |> ignore

        mapping
            .HasKey([| "BillableUsageKindMappingId" |])
            .HasName("PK_ops_BillableUsageKindMapping")
        |> ignore

        mapping
            .Property<System.Guid>("BillableUsageKindMappingId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever()
        |> ignore

        mapping.Property<int>("FactKind").IsRequired()
        |> ignore

        mapping
            .Property<int>("BillableUsageKind")
            .IsRequired()
        |> ignore

        mapping
            .Property<string>("DisplayName")
            .HasMaxLength(200)
            .IsRequired()
        |> ignore

        mapping
            .Property<System.DateTime>("EffectiveFromUtc")
            .HasColumnType("datetime2(7)")
            .IsRequired()
        |> ignore

        mapping
            .Property<System.Nullable<System.DateTime>>("EffectiveToUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        mapping
            .Property<System.DateTime>("CreatedAtUtc")
            .HasColumnType("datetime2(7)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .IsRequired()
        |> ignore

        mapping
            .HasIndex([| "FactKind"; "EffectiveFromUtc" |])
            .HasDatabaseName("UX_ops_BillableUsageKindMapping_FactKindEffectiveFrom")
            .IsUnique()
        |> ignore

        mapping
            .HasIndex(
                [|
                    "FactKind"
                    "EffectiveFromUtc"
                    "EffectiveToUtc"
                |]
            )
            .HasDatabaseName("IX_ops_BillableUsageKindMapping_FactKindEffective")
        |> ignore

        let pricingRate = modelBuilder.Entity<PricingRateEntity>()

        pricingRate.ToTable("PricingRate", "ops")
        |> ignore

        pricingRate
            .HasKey([| "PricingRateId" |])
            .HasName("PK_ops_PricingRate")
        |> ignore

        pricingRate
            .Property<System.Guid>("PricingRateId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever()
        |> ignore

        pricingRate
            .Property<System.Guid>("PricingPlanId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        pricingRate
            .Property<int>("BillableUsageKind")
            .IsRequired()
        |> ignore

        pricingRate
            .Property<string>("CurrencyCode")
            .HasColumnType("varchar(3)")
            .HasMaxLength(3)
            .IsUnicode(false)
            .UseCollation("Latin1_General_100_BIN2")
            .IsRequired()
        |> ignore

        pricingRate
            .Property<string>("UnitName")
            .HasMaxLength(64)
            .IsRequired()
        |> ignore

        pricingRate
            .Property<int64>("UnitQuantity")
            .IsRequired()
        |> ignore

        pricingRate
            .Property<int64>("UnitPriceMicros")
            .IsRequired()
        |> ignore

        pricingRate
            .Property<System.DateTime>("EffectiveFromUtc")
            .HasColumnType("datetime2(7)")
            .IsRequired()
        |> ignore

        pricingRate
            .Property<System.Nullable<System.DateTime>>("EffectiveToUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        pricingRate
            .Property<System.DateTime>("CreatedAtUtc")
            .HasColumnType("datetime2(7)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .IsRequired()
        |> ignore

        pricingRate
            .HasIndex(
                [|
                    "PricingPlanId"
                    "BillableUsageKind"
                    "EffectiveFromUtc"
                |]
            )
            .HasDatabaseName("UX_ops_PricingRate_PlanUsageKindEffectiveFrom")
            .IsUnique()
        |> ignore

        pricingRate
            .HasIndex(
                [|
                    "PricingPlanId"
                    "BillableUsageKind"
                    "EffectiveFromUtc"
                    "EffectiveToUtc"
                |]
            )
            .HasDatabaseName("IX_ops_PricingRate_PlanUsageKindEffective")
        |> ignore

        pricingRate
            .HasOne(fun rate -> rate.PricingPlan)
            .WithMany()
            .HasForeignKey("PricingPlanId")
            .HasConstraintName("FK_ops_PricingRate_PricingPlan")
            .OnDelete(DeleteBehavior.Restrict)
        |> ignore

        let assignment = modelBuilder.Entity<CustomerPricingAssignmentEntity>()

        assignment.ToTable("CustomerPricingAssignment", "ops")
        |> ignore

        assignment
            .HasKey([| "CustomerPricingAssignmentId" |])
            .HasName("PK_ops_CustomerPricingAssignment")
        |> ignore

        assignment
            .Property<System.Guid>("CustomerPricingAssignmentId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever()
        |> ignore

        assignment
            .Property<System.Guid>("CustomerId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        assignment
            .Property<System.Guid>("OwnerId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        assignment
            .Property<System.Guid>("OrganizationId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        assignment
            .Property<System.Guid>("RepositoryId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        assignment
            .Property<System.Guid>("PricingPlanId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        assignment
            .Property<System.DateTime>("EffectiveFromUtc")
            .HasColumnType("datetime2(7)")
            .IsRequired()
        |> ignore

        assignment
            .Property<System.Nullable<System.DateTime>>("EffectiveToUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        assignment
            .Property<System.DateTime>("CreatedAtUtc")
            .HasColumnType("datetime2(7)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .IsRequired()
        |> ignore

        assignment
            .HasIndex([| "PricingPlanId" |])
            .HasDatabaseName("IX_CustomerPricingAssignment_PricingPlanId")
        |> ignore

        assignment
            .HasIndex(
                [|
                    "CustomerId"
                    "OwnerId"
                    "OrganizationId"
                    "RepositoryId"
                    "EffectiveFromUtc"
                |]
            )
            .HasDatabaseName("UX_ops_CustomerPricingAssignment_ScopeEffectiveFrom")
            .IsUnique()
        |> ignore

        assignment
            .HasIndex(
                [|
                    "CustomerId"
                    "OwnerId"
                    "OrganizationId"
                    "RepositoryId"
                    "EffectiveFromUtc"
                    "EffectiveToUtc"
                |]
            )
            .HasDatabaseName("IX_ops_CustomerPricingAssignment_ScopeEffective")
        |> ignore

        assignment
            .HasOne(fun assignment -> assignment.PricingPlan)
            .WithMany()
            .HasForeignKey("PricingPlanId")
            .HasConstraintName("FK_ops_CustomerPricingAssignment_PricingPlan")
            .OnDelete(DeleteBehavior.Restrict)
        |> ignore

        let line = modelBuilder.Entity<ChargePreviewLineEntity>()

        line.ToTable(
            "ChargePreviewLine",
            "ops",
            fun (table: TableBuilder<ChargePreviewLineEntity>) ->
                table.HasCheckConstraint("CK_ops_ChargePreviewLine_PeriodRange", "[PeriodFromUtc] < [PeriodToUtc]")
                |> ignore

                table.HasCheckConstraint(
                    "CK_ops_ChargePreviewLine_EffectiveRange",
                    "[PeriodFromUtc] <= [EffectiveFromUtc] AND [EffectiveFromUtc] < [EffectiveToUtc] AND [EffectiveToUtc] <= [PeriodToUtc]"
                )
                |> ignore

                table.HasCheckConstraint("CK_ops_ChargePreviewLine_UnitQuantity", "[UnitQuantity] > 0")
                |> ignore

                table.HasCheckConstraint("CK_ops_ChargePreviewLine_Amounts", "[UnitPriceMicros] >= 0 AND [TotalQuantity] >= 0 AND [ChargeMicros] >= 0")
                |> ignore

                table.HasCheckConstraint(
                    "CK_ops_ChargePreviewLine_Currency",
                    "LEN([CurrencyCode]) = 3 AND [CurrencyCode] = UPPER([CurrencyCode]) AND [CurrencyCode] NOT LIKE '%[^A-Z]%'"
                )
                |> ignore
        )
        |> ignore

        line
            .HasKey([| "ChargePreviewLineId" |])
            .HasName("PK_ops_ChargePreviewLine")
        |> ignore

        line
            .Property<Guid>("ChargePreviewLineId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever()
        |> ignore

        for name in
            [
                "CustomerId"
                "OwnerId"
                "OrganizationId"
                "RepositoryId"
                "BillableUsageKindMappingId"
                "CustomerPricingAssignmentId"
                "PricingPlanId"
                "PricingRateId"
            ] do
            line
                .Property<Guid>(name)
                .HasColumnType("uniqueidentifier")
                .IsRequired()
            |> ignore

        for name in
            [
                "PeriodFromUtc"
                "PeriodToUtc"
                "EffectiveFromUtc"
                "EffectiveToUtc"
            ] do
            line
                .Property<DateTime>(name)
                .HasColumnType("datetime2(7)")
                .IsRequired()
            |> ignore

        line.Property<int>("FactKind").IsRequired()
        |> ignore

        line
            .Property<int>("BillableUsageKind")
            .IsRequired()
        |> ignore

        line
            .Property<string>("CurrencyCode")
            .HasColumnType("varchar(3)")
            .HasMaxLength(3)
            .IsUnicode(false)
            .UseCollation("Latin1_General_100_BIN2")
            .IsRequired()
        |> ignore

        line
            .Property<string>("UnitName")
            .HasMaxLength(64)
            .IsRequired()
        |> ignore

        for name in
            [
                "UnitQuantity"
                "UnitPriceMicros"
                "TotalQuantity"
                "ChargeMicros"
            ] do
            line.Property<int64>(name).IsRequired() |> ignore

        line
            .HasIndex(
                [|
                    "CustomerId"
                    "OwnerId"
                    "OrganizationId"
                    "RepositoryId"
                    "PeriodFromUtc"
                    "PeriodToUtc"
                |]
            )
            .HasDatabaseName("IX_ops_ChargePreviewLine_Scope")
        |> ignore

        line
            .HasIndex(
                [|
                    "CustomerId"
                    "OwnerId"
                    "OrganizationId"
                    "RepositoryId"
                    "PeriodFromUtc"
                    "PeriodToUtc"
                    "FactKind"
                    "BillableUsageKindMappingId"
                    "BillableUsageKind"
                    "CustomerPricingAssignmentId"
                    "PricingPlanId"
                    "PricingRateId"
                    "CurrencyCode"
                    "UnitName"
                    "UnitQuantity"
                    "UnitPriceMicros"
                    "EffectiveFromUtc"
                    "EffectiveToUtc"
                |]
            )
            .HasDatabaseName("UX_ops_ChargePreviewLine_CompleteGrain")
            .IsUnique()
        |> ignore


        // Billing close model is independently encoded here; do not delegate to runtime configuration.
        let guid (entity: EntityTypeBuilder<'T>) (name: string) =
            entity
                .Property<Guid>(name)
                .HasColumnType("uniqueidentifier")
                .IsRequired()
            |> ignore

        let utc (entity: EntityTypeBuilder<'T>) (name: string) =
            entity
                .Property<DateTime>(name)
                .HasColumnType("datetime2(7)")
                .IsRequired()
            |> ignore

        let period = modelBuilder.Entity<BillingPeriodEntity>()

        period.ToTable(
            "BillingPeriod",
            "ops",
            fun (table: TableBuilder<BillingPeriodEntity>) ->
                table.HasCheckConstraint("CK_ops_BillingPeriod_Range", "[PeriodFromUtc] < [PeriodToUtc]")
                |> ignore

                table.HasCheckConstraint("CK_ops_BillingPeriod_State", "[State] BETWEEN 0 AND 3")
                |> ignore

                table.HasCheckConstraint("CK_ops_BillingPeriod_FailureCount", "[ConsecutiveCloseFailureCount] >= 0")
                |> ignore
        )
        |> ignore

        period
            .HasKey([| "BillingPeriodId" |])
            .HasName("PK_ops_BillingPeriod")
        |> ignore

        [
            "BillingPeriodId"
            "CustomerId"
            "OwnerId"
            "OrganizationId"
            "RepositoryId"
        ]
        |> List.iter (guid period)

        [
            "PeriodFromUtc"
            "PeriodToUtc"
            "CreatedAtUtc"
        ]
        |> List.iter (utc period)

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
            .Property<Nullable<DateTime>>("LastCloseAttemptAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        period
            .Property<int>("ConsecutiveCloseFailureCount")
            .IsRequired()
        |> ignore

        period
            .Property<Nullable<DateTime>>("ClosedAtUtc")
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
            .Property<DateTime>("CreatedAtUtc")
            .HasDefaultValueSql("SYSUTCDATETIME()")
        |> ignore

        period
            .HasIndex(
                [|
                    "CustomerId"
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

        guid freshness "BillingPeriodId"

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

        utc freshness "PreviewCommittedAtUtc"

        freshness
            .HasOne<BillingPeriodEntity>()
            .WithOne()
            .HasForeignKey<ChargePreviewFreshnessEntity>("BillingPeriodId")
            .HasConstraintName("FK_ops_ChargePreviewFreshness_BillingPeriod")
            .OnDelete(DeleteBehavior.Cascade)
        |> ignore

        let ledger = modelBuilder.Entity<ChargeLedgerEntryEntity>()

        ledger.ToTable(
            "ChargeLedgerEntry",
            "ops",
            fun (table: TableBuilder<ChargeLedgerEntryEntity>) ->
                table.HasCheckConstraint("CK_ops_ChargeLedgerEntry_Kind", "[EntryKind] BETWEEN 0 AND 2")
                |> ignore

                table.HasCheckConstraint(
                    "CK_ops_ChargeLedgerEntry_Currency",
                    "LEN([CurrencyCode]) = 3 AND [CurrencyCode] = UPPER([CurrencyCode]) AND [CurrencyCode] NOT LIKE '%[^A-Z]%'"
                )
                |> ignore

                table.HasCheckConstraint(
                    "CK_ops_ChargeLedgerEntry_ChargeSource",
                    "([EntryKind] = 0 AND [SourceChargePreviewLineId] IS NOT NULL AND [PriorChargeLedgerEntryId] IS NULL) OR ([EntryKind] IN (1,2) AND [SourceChargePreviewLineId] IS NULL)"
                )
                |> ignore

                table.HasCheckConstraint("CK_ops_ChargeLedgerEntry_UnitQuantity", "[UnitQuantity] > 0")
                |> ignore

                table.HasCheckConstraint("CK_ops_ChargeLedgerEntry_UnitPriceMicros", "[UnitPriceMicros] >= 0")
                |> ignore
        )
        |> ignore

        ledger
            .HasKey([| "ChargeLedgerEntryId" |])
            .HasName("PK_ops_ChargeLedgerEntry")
        |> ignore

        [
            "ChargeLedgerEntryId"
            "BillingPeriodId"
            "BillableUsageKindMappingId"
            "CustomerPricingAssignmentId"
            "PricingPlanId"
            "PricingRateId"
        ]
        |> List.iter (guid ledger)

        ledger
            .Property<Nullable<Guid>>("SourceChargePreviewLineId")
            .HasColumnType("uniqueidentifier")
        |> ignore

        ledger
            .Property<Nullable<Guid>>("PriorChargeLedgerEntryId")
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
            .UseCollation("Latin1_General_100_BIN2")
            .IsRequired()
        |> ignore

        ledger
            .Property<string>("UnitName")
            .HasMaxLength(64)
            .IsRequired()
        |> ignore

        [
            "UnitQuantity"
            "UnitPriceMicros"
            "Quantity"
            "ChargeMicros"
        ]
        |> List.iter (fun name ->
            ledger.Property<int64>(name).IsRequired()
            |> ignore)

        [
            "EffectiveFromUtc"
            "EffectiveToUtc"
            "CreatedAtUtc"
        ]
        |> List.iter (utc ledger)

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
            .Property<DateTime>("CreatedAtUtc")
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
                    "CustomerPricingAssignmentId"
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
                |]
            )
            .HasDatabaseName("UX_ops_ChargeLedgerEntry_Correction")
            .IsUnique()
            .HasFilter("[SourceChargePreviewLineId] IS NULL")
        |> ignore

        ledger
            .HasIndex([| "SourceChargePreviewLineId" |])
            .HasDatabaseName("IX_ChargeLedgerEntry_SourceChargePreviewLineId")
        |> ignore

        ledger
            .HasIndex([| "PriorChargeLedgerEntryId" |])
            .HasDatabaseName("IX_ChargeLedgerEntry_PriorChargeLedgerEntryId")
        |> ignore

        ledger
            .HasOne<BillingPeriodEntity>()
            .WithMany()
            .HasForeignKey("BillingPeriodId")
            .HasConstraintName("FK_ops_ChargeLedgerEntry_BillingPeriod")
            .OnDelete(DeleteBehavior.Restrict)
        |> ignore

        ledger
            .HasOne<ChargePreviewLineEntity>()
            .WithMany()
            .HasForeignKey("SourceChargePreviewLineId")
            .HasConstraintName("FK_ops_ChargeLedgerEntry_ChargePreviewLine")
            .OnDelete(DeleteBehavior.Restrict)
        |> ignore

        ledger
            .HasOne<ChargeLedgerEntryEntity>()
            .WithMany()
            .HasForeignKey("PriorChargeLedgerEntryId")
            .HasConstraintName("FK_ops_ChargeLedgerEntry_Prior")
            .OnDelete(DeleteBehavior.Restrict)
        |> ignore

        let failure = modelBuilder.Entity<BillingIngestionFailureEntity>()

        failure.ToTable("BillingIngestionFailure", "ops")
        |> ignore

        failure
            .HasKey([| "BillingIngestionFailureId" |])
            .HasName("PK_ops_BillingIngestionFailure")
        |> ignore

        guid failure "BillingIngestionFailureId"

        [
            "UsageFactId"
            "CustomerId"
            "OwnerId"
            "OrganizationId"
            "RepositoryId"
        ]
        |> List.iter (fun name ->
            failure
                .Property<Nullable<Guid>>(name)
                .HasColumnType("uniqueidentifier")
            |> ignore)

        failure
            .Property<Nullable<DateTime>>("ObservedAtUtc")
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

        utc failure "CreatedAtUtc"

        failure
            .Property<DateTime>("CreatedAtUtc")
            .HasDefaultValueSql("SYSUTCDATETIME()")
        |> ignore

        failure
            .Property<Nullable<DateTime>>("ResolvedAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        failure
            .Property<string>("ResolvedByPrincipalId")
            .HasMaxLength(256)
        |> ignore

        failure
            .Property<string>("ResolutionReasonCode")
            .HasMaxLength(64)
        |> ignore

        failure
            .Property<string>("ResolutionReasonText")
            .HasMaxLength(1024)
        |> ignore

        failure
            .Property<string>("ResolutionCorrelationId")
            .HasMaxLength(200)
        |> ignore

        failure
            .HasIndex([| "UsageFactId" |])
            .HasDatabaseName("UX_ops_BillingIngestionFailure_ActiveFact")
            .IsUnique()
            .HasFilter("[UsageFactId] IS NOT NULL AND [ResolvedAtUtc] IS NULL")
        |> ignore

        failure
            .HasIndex(
                [|
                    "CustomerId"
                    "OwnerId"
                    "OrganizationId"
                    "RepositoryId"
                    "ObservedAtUtc"
                |]
            )
            .HasDatabaseName("IX_ops_BillingIngestionFailure_ActiveScope")
            .HasFilter("[ResolvedAtUtc] IS NULL")
        |> ignore

        let work = modelBuilder.Entity<BillingCorrectionWorkEntity>()

        work.ToTable("BillingCorrectionWork", "ops")
        |> ignore

        work
            .HasKey([| "BillingCorrectionWorkId" |])
            .HasName("PK_ops_BillingCorrectionWork")
        |> ignore

        [
            "BillingCorrectionWorkId"
            "BillingPeriodId"
            "UsageFactId"
        ]
        |> List.iter (guid work)

        work
            .Property<string>("CorrelationId")
            .HasMaxLength(200)
            .IsRequired()
        |> ignore

        utc work "CreatedAtUtc"

        work
            .Property<DateTime>("CreatedAtUtc")
            .HasDefaultValueSql("SYSUTCDATETIME()")
        |> ignore

        work
            .Property<Nullable<DateTime>>("CompletedAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        work
            .HasIndex([| "BillingPeriodId"; "UsageFactId" |])
            .HasDatabaseName("UX_ops_BillingCorrectionWork_PeriodFact")
            .IsUnique()
        |> ignore

        work
            .HasIndex([| "CompletedAtUtc"; "CreatedAtUtc" |])
            .HasDatabaseName("IX_ops_BillingCorrectionWork_Pending")
        |> ignore

        work
            .HasOne<BillingPeriodEntity>()
            .WithMany()
            .HasForeignKey("BillingPeriodId")
            .HasConstraintName("FK_ops_BillingCorrectionWork_BillingPeriod")
            .OnDelete(DeleteBehavior.Restrict)
        |> ignore
