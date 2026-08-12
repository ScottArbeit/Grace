namespace Grace.Operations.Data.Migrations

open Grace.Operations.Data
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Migrations
open Microsoft.EntityFrameworkCore.Metadata.Builders
open System

/// Adds the immutable pre-send journal that keeps supported usage visible when Service Bus delivery is delayed or lost.
[<Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute(typeof<OperationsDbContext>)>]
[<Migration("20260812110000_AddUsageFactJournal")>]
type AddUsageFactJournal() =
    inherit Migration()

    /// Creates the one journal row per immutable fact, including the indexes used by dispatch and exact-scope completeness.
    override _.Up(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(
            """
CREATE TABLE ops.UsageFactJournal
(
    UsageFactId uniqueidentifier NOT NULL,
    RawPayload varbinary(max) NOT NULL,
    CorrelationId nvarchar(200) NOT NULL,
    FactKind int NOT NULL,
    OwnerId uniqueidentifier NOT NULL,
    OrganizationId uniqueidentifier NOT NULL,
    RepositoryId uniqueidentifier NOT NULL,
    StoragePoolId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    Quantity bigint NOT NULL,
    ObservedAtUtc datetime2(7) NOT NULL,
    State int NOT NULL,
    CreatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_ops_UsageFactJournal_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
    TerminalAtUtc datetime2(7) NULL,
    CONSTRAINT PK_ops_UsageFactJournal PRIMARY KEY (UsageFactId),
    CONSTRAINT CK_ops_UsageFactJournal_Identity CHECK
    (
        UsageFactId <> '00000000-0000-0000-0000-000000000000'
        AND OwnerId <> '00000000-0000-0000-0000-000000000000'
        AND OrganizationId <> '00000000-0000-0000-0000-000000000000'
        AND RepositoryId <> '00000000-0000-0000-0000-000000000000'
        AND LEN(LTRIM(RTRIM(CorrelationId))) > 0
        AND LEN(LTRIM(RTRIM(StoragePoolId))) > 0
        AND DATALENGTH(RawPayload) > 0
    ),
    CONSTRAINT CK_ops_UsageFactJournal_State CHECK
    (
        (State = 0 AND TerminalAtUtc IS NULL)
        OR (State IN (1, 2) AND TerminalAtUtc IS NOT NULL)
    )
);

CREATE INDEX IX_ops_UsageFactJournal_ScopeState
    ON ops.UsageFactJournal(OwnerId, OrganizationId, RepositoryId, ObservedAtUtc, State, UsageFactId);

CREATE INDEX IX_ops_UsageFactJournal_PendingDispatch
    ON ops.UsageFactJournal(State, CreatedAtUtc, UsageFactId);
"""
        )
        |> ignore

    /// Removes only the journal introduced by this slice.
    override _.Down(migrationBuilder: MigrationBuilder) =
        migrationBuilder.DropTable("UsageFactJournal", "ops")
        |> ignore

    /// Rebuilds the complete model at this reviewed migration point.
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

        let rejection = modelBuilder.Entity<UsageFactRejectionEntity>()

        rejection.ToTable(
            "UsageFactRejection",
            "ops",
            fun (table: TableBuilder<UsageFactRejectionEntity>) ->
                table.HasCheckConstraint(
                    "CK_ops_UsageFactRejection_SuppliedGuids",
                    "[RejectionId] <> '00000000-0000-0000-0000-000000000000' AND ([UsageFactId] IS NULL OR [UsageFactId] <> '00000000-0000-0000-0000-000000000000') AND ([OwnerId] IS NULL OR [OwnerId] <> '00000000-0000-0000-0000-000000000000') AND ([OrganizationId] IS NULL OR [OrganizationId] <> '00000000-0000-0000-0000-000000000000') AND ([RepositoryId] IS NULL OR [RepositoryId] <> '00000000-0000-0000-0000-000000000000')"
                )
                |> ignore

                table.HasCheckConstraint(
                    "CK_ops_UsageFactRejection_CompleteScopeRequiresFact",
                    "NOT ([OwnerId] IS NOT NULL AND [OrganizationId] IS NOT NULL AND [RepositoryId] IS NOT NULL AND [MonthStartUtc] IS NOT NULL) OR ([UsageFactId] IS NOT NULL AND [UsageFactId] <> '00000000-0000-0000-0000-000000000000')"
                )
                |> ignore

                table.HasCheckConstraint(
                    "CK_ops_UsageFactRejection_MonthStartUtc",
                    "[MonthStartUtc] IS NULL OR [MonthStartUtc] = DATETIME2FROMPARTS(YEAR([MonthStartUtc]), MONTH([MonthStartUtc]), 1, 0, 0, 0, 0, 7)"
                )
                |> ignore

                table.HasCheckConstraint(
                    "CK_ops_UsageFactRejection_Resolution",
                    "([IsActive] = 1 AND [ResolvedAtUtc] IS NULL) OR ([IsActive] = 0 AND [ResolvedAtUtc] IS NOT NULL)"
                )
                |> ignore

                table.HasCheckConstraint("CK_ops_UsageFactRejection_Reason", "LEN(LTRIM(RTRIM([Reason]))) > 0")
                |> ignore
        )
        |> ignore

        rejection
            .HasKey([| "RejectionId" |])
            .HasName("PK_ops_UsageFactRejection")
        |> ignore

        rejection
            .Property<Guid>("RejectionId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever()
        |> ignore

        for name in
            [
                "UsageFactId"
                "OwnerId"
                "OrganizationId"
                "RepositoryId"
            ] do
            rejection
                .Property<Nullable<Guid>>(name)
                .HasColumnType("uniqueidentifier")
            |> ignore

        rejection
            .Property<Nullable<DateTime>>("MonthStartUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        rejection
            .Property<string>("Reason")
            .HasMaxLength(400)
            .IsRequired()
        |> ignore

        rejection.Property<bool>("IsActive").IsRequired()
        |> ignore

        rejection
            .Property<Nullable<DateTime>>("ResolvedAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        rejection
            .Property<DateTime>("CreatedAtUtc")
            .HasColumnType("datetime2(7)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .IsRequired()
        |> ignore

        rejection
            .HasIndex(
                [|
                    "UsageFactId"
                    "OwnerId"
                    "OrganizationId"
                    "RepositoryId"
                    "MonthStartUtc"
                |]
            )
            .HasDatabaseName("UX_ops_UsageFactRejection_ActiveScopedFact")
            .IsUnique()
            .HasFilter(
                "[IsActive] = 1 AND [UsageFactId] IS NOT NULL AND [OwnerId] IS NOT NULL AND [OrganizationId] IS NOT NULL AND [RepositoryId] IS NOT NULL AND [MonthStartUtc] IS NOT NULL"
            )
        |> ignore

        rejection
            .HasIndex(
                [|
                    "OwnerId"
                    "OrganizationId"
                    "RepositoryId"
                    "MonthStartUtc"
                    "IsActive"
                |]
            )
            .HasDatabaseName("IX_ops_UsageFactRejection_ActiveScope")
        |> ignore

        let journal = modelBuilder.Entity<UsageFactJournalEntity>()

        journal.ToTable("UsageFactJournal", "ops")
        |> ignore

        journal
            .HasKey([| "UsageFactId" |])
            .HasName("PK_ops_UsageFactJournal")
        |> ignore

        journal
            .Property<Guid>("UsageFactId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever()
        |> ignore

        journal
            .Property<byte array>("RawPayload")
            .HasColumnType("varbinary(max)")
            .IsRequired()
        |> ignore

        journal
            .Property<string>("CorrelationId")
            .HasMaxLength(200)
            .IsRequired()
        |> ignore

        journal.Property<int>("FactKind").IsRequired()
        |> ignore

        for name in
            [
                "OwnerId"
                "OrganizationId"
                "RepositoryId"
            ] do
            journal
                .Property<Guid>(name)
                .HasColumnType("uniqueidentifier")
                .IsRequired()
            |> ignore

        journal
            .Property<string>("StoragePoolId")
            .HasMaxLength(256)
            .UseCollation("Latin1_General_100_BIN2")
            .IsRequired()
        |> ignore

        journal.Property<int64>("Quantity").IsRequired()
        |> ignore

        journal
            .Property<DateTime>("ObservedAtUtc")
            .HasColumnType("datetime2(7)")
            .IsRequired()
        |> ignore

        journal.Property<int>("State").IsRequired()
        |> ignore

        journal
            .Property<DateTime>("CreatedAtUtc")
            .HasColumnType("datetime2(7)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .IsRequired()
        |> ignore

        journal
            .Property<Nullable<DateTime>>("TerminalAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        journal
            .HasIndex(
                [|
                    "OwnerId"
                    "OrganizationId"
                    "RepositoryId"
                    "ObservedAtUtc"
                    "State"
                    "UsageFactId"
                |]
            )
            .HasDatabaseName("IX_ops_UsageFactJournal_ScopeState")
        |> ignore

        journal
            .HasIndex(
                [|
                    "State"
                    "CreatedAtUtc"
                    "UsageFactId"
                |]
            )
            .HasDatabaseName("IX_ops_UsageFactJournal_PendingDispatch")
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
