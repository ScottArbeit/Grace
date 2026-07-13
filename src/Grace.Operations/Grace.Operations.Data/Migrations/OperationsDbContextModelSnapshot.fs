namespace Grace.Operations.Data.Migrations

open Grace.Operations.Data
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Infrastructure
open Microsoft.EntityFrameworkCore.Metadata.Builders
open System

/// Captures the current Operations EF model so future migrations can detect reviewed schema drift.
[<DbContextAttribute(typeof<OperationsDbContext>)>]
type OperationsDbContextModelSnapshot() =
    inherit ModelSnapshot()

    /// Rebuilds the Operations model represented by the latest migration.
    override _.BuildModel(modelBuilder: ModelBuilder) =
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

        rawFact
            .Property<System.DateTime>("AcceptedAtUtc")
            .HasColumnType("datetime2(7)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
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

        let assignment = modelBuilder.Entity<PricingAssignmentEntity>()

        assignment.ToTable("PricingAssignment", "ops")
        |> ignore

        assignment
            .HasKey([| "PricingAssignmentId" |])
            .HasName("PK_ops_PricingAssignment")
        |> ignore

        assignment
            .Property<System.Guid>("PricingAssignmentId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever()
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
            .HasDatabaseName("IX_PricingAssignment_PricingPlanId")
        |> ignore

        assignment
            .HasIndex(
                [|
                    "OwnerId"
                    "OrganizationId"
                    "RepositoryId"
                    "EffectiveFromUtc"
                |]
            )
            .HasDatabaseName("UX_ops_PricingAssignment_ScopeEffectiveFrom")
            .IsUnique()
        |> ignore

        assignment
            .HasIndex(
                [|
                    "OwnerId"
                    "OrganizationId"
                    "RepositoryId"
                    "EffectiveFromUtc"
                    "EffectiveToUtc"
                |]
            )
            .HasDatabaseName("IX_ops_PricingAssignment_ScopeEffective")
        |> ignore

        assignment
            .HasOne(fun assignment -> assignment.PricingPlan)
            .WithMany()
            .HasForeignKey("PricingPlanId")
            .HasConstraintName("FK_ops_PricingAssignment_PricingPlan")
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
                "OwnerId"
                "OrganizationId"
                "RepositoryId"
                "BillableUsageKindMappingId"
                "PricingAssignmentId"
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
                    "OwnerId"
                    "OrganizationId"
                    "RepositoryId"
                    "PeriodFromUtc"
                    "PeriodToUtc"
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
                |]
            )
            .HasDatabaseName("UX_ops_ChargePreviewLine_CompleteGrain")
            .IsUnique()
        |> ignore

        // Billing is intentionally declared here rather than delegated to the mutable runtime model.
        let billingPeriod = modelBuilder.Entity<BillingPeriodEntity>()

        billingPeriod.ToTable("BillingPeriod", "ops")
        |> ignore

        billingPeriod
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
            billingPeriod
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
            billingPeriod
                .Property<DateTime>(name)
                .HasColumnType("datetime2(7)")
                .IsRequired()
            |> ignore

        billingPeriod.Property<int>("State").IsRequired()
        |> ignore

        billingPeriod
            .Property<string>("CloseBlockedCode")
            .HasMaxLength(64)
        |> ignore

        billingPeriod
            .Property<string>("CloseBlockedDetail")
            .HasMaxLength(1024)
        |> ignore

        billingPeriod
            .Property<Nullable<DateTime>>("LastCloseAttemptAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        billingPeriod
            .Property<int>("ConsecutiveCloseFailureCount")
            .IsRequired()
        |> ignore

        billingPeriod
            .Property<Nullable<DateTime>>("ClosedAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        billingPeriod
            .Property<string>("CloseInitiatedByPrincipalId")
            .HasMaxLength(256)
        |> ignore

        billingPeriod
            .Property<string>("CloseReasonCode")
            .HasMaxLength(64)
        |> ignore

        billingPeriod
            .Property<string>("CloseReasonText")
            .HasMaxLength(1024)
        |> ignore

        billingPeriod
            .Property<string>("CloseCorrelationId")
            .HasMaxLength(200)
        |> ignore

        billingPeriod
            .Property<DateTime>("CreatedAtUtc")
            .HasDefaultValueSql("SYSUTCDATETIME()")
        |> ignore

        billingPeriod
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

        billingPeriod
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
            .Property<Guid>("BillingPeriodId")
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
            .Property<DateTime>("PreviewCommittedAtUtc")
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
                .Property<Guid>(name)
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
                .Property<Nullable<Guid>>(name)
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
                .Property<DateTime>(name)
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
            .Property<Guid>("BillingIngestionFailureId")
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
                .Property<Nullable<Guid>>(name)
                .HasColumnType("uniqueidentifier")
            |> ignore

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

        failure
            .Property<string>("CorrelationId")
            .HasMaxLength(200)
            .IsRequired()
        |> ignore

        failure
            .Property<DateTime>("CreatedAtUtc")
            .HasDefaultValueSql("SYSUTCDATETIME()")
        |> ignore

        failure
            .Property<Nullable<DateTime>>("ResolvedAtUtc")
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
                .Property<Guid>(name)
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
            .Property<Nullable<DateTime>>("ReenabledAtUtc")
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
            .Property<Nullable<DateTime>>("CompletedAtUtc")
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

        work
            .HasOne<BillingPeriodEntity>()
            .WithMany()
            .HasForeignKey("BillingPeriodId")
            .HasConstraintName("FK_ops_BillingCorrectionWork_BillingPeriod")
            .OnDelete(DeleteBehavior.Restrict)
        |> ignore
