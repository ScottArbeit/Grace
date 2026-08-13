namespace Grace.Operations.Data.Migrations

open Grace.Operations.Data
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Infrastructure
open Microsoft.EntityFrameworkCore.Migrations
open Microsoft.EntityFrameworkCore.Metadata.Builders
open System

/// Holds the literal billing-close model fragment shared only by frozen migration representations.
[<RequireQualifiedAccess>]
module BillingPeriodFrozenModel =

    /// Applies the immutable literal model fragment without reading runtime SQL or model helpers.
    let apply (modelBuilder: Microsoft.EntityFrameworkCore.ModelBuilder) =
        let period = modelBuilder.Entity<BillingPeriodEntity>()

        period.ToTable(
            "BillingPeriod",
            "ops",
            fun (table: TableBuilder<BillingPeriodEntity>) ->
                table.HasCheckConstraint(
                    "CK_ops_BillingPeriod_MonthRange",
                    "[MonthStartUtc] < [NextMonthStartUtc] AND [MonthStartUtc] = DATETIME2FROMPARTS(YEAR([MonthStartUtc]), MONTH([MonthStartUtc]), 1, 0, 0, 0, 0, 7) AND [NextMonthStartUtc] = DATEADD(month, 1, [MonthStartUtc])"
                )
                |> ignore

                table.HasCheckConstraint("CK_ops_BillingPeriod_State", "[State] IN (0, 1, 2)")
                |> ignore

                table.HasCheckConstraint(
                    "CK_ops_BillingPeriod_Diagnostic",
                    "([RetryDiagnostic] IS NULL AND [RetryDiagnosticAtUtc] IS NULL) OR ([State] IN (0, 1) AND LEN(LTRIM(RTRIM([RetryDiagnostic]))) > 0 AND [RetryDiagnosticAtUtc] IS NOT NULL)"
                )
                |> ignore
        )
        |> ignore

        period
            .HasKey([| "BillingPeriodId" |])
            .HasName("PK_ops_BillingPeriod")
        |> ignore

        period
            .Property<Guid>("BillingPeriodId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever()
        |> ignore

        for name in
            [
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
                "MonthStartUtc"
                "NextMonthStartUtc"
                "CreatedAtUtc"
                "UpdatedAtUtc"
            ] do
            period
                .Property<DateTime>(name)
                .HasColumnType("datetime2(7)")
                .IsRequired()
            |> ignore

        period.Property<int>("State").IsRequired()
        |> ignore

        period
            .Property<string>("RetryDiagnostic")
            .HasMaxLength(400)
        |> ignore

        period
            .Property<Nullable<DateTime>>("RetryDiagnosticAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        period
            .Property<DateTime>("CreatedAtUtc")
            .HasDefaultValueSql("SYSUTCDATETIME()")
        |> ignore

        period
            .Property<DateTime>("UpdatedAtUtc")
            .HasDefaultValueSql("SYSUTCDATETIME()")
        |> ignore

        period
            .HasIndex(
                [|
                    "OwnerId"
                    "OrganizationId"
                    "RepositoryId"
                    "MonthStartUtc"
                    "NextMonthStartUtc"
                |]
            )
            .HasDatabaseName("UX_ops_BillingPeriod_ExactScope")
            .IsUnique()
        |> ignore

        let charge = modelBuilder.Entity<ChargeEntity>()

        charge.ToTable(
            "Charge",
            "ops",
            fun (table: TableBuilder<ChargeEntity>) ->
                table.HasCheckConstraint("CK_ops_Charge_Amount", "[ChargeMicros] >= 0")
                |> ignore

                table.HasCheckConstraint(
                    "CK_ops_Charge_Currency",
                    "LEN([CurrencyCode]) = 3 AND [CurrencyCode] = UPPER([CurrencyCode]) AND [CurrencyCode] NOT LIKE '%[^A-Z]%'"
                )
                |> ignore
        )
        |> ignore

        charge
            .HasKey([| "ChargeId" |])
            .HasName("PK_ops_Charge")
        |> ignore

        charge
            .Property<Guid>("ChargeId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever()
        |> ignore

        charge
            .Property<Guid>("BillingPeriodId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        charge
            .Property<Guid>("ChargePreviewLineId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        charge
            .Property<string>("CurrencyCode")
            .HasColumnType("varchar(3)")
            .HasMaxLength(3)
            .IsUnicode(false)
            .UseCollation("Latin1_General_100_BIN2")
            .IsRequired()
        |> ignore

        charge
            .Property<int64>("ChargeMicros")
            .IsRequired()
        |> ignore

        charge
            .Property<DateTime>("CreatedAtUtc")
            .HasColumnType("datetime2(7)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .IsRequired()
        |> ignore

        charge
            .HasIndex(
                [|
                    "BillingPeriodId"
                    "ChargePreviewLineId"
                |]
            )
            .HasDatabaseName("UX_ops_Charge_InitialPosting")
            .IsUnique()
        |> ignore

        charge
            .HasIndex([| "ChargePreviewLineId" |])
            .HasDatabaseName("IX_ops_Charge_ChargePreviewLine")
        |> ignore

        charge
            .HasOne<BillingPeriodEntity>()
            .WithMany()
            .HasForeignKey("BillingPeriodId")
            .HasConstraintName("FK_ops_Charge_BillingPeriod")
            .OnDelete(Microsoft.EntityFrameworkCore.DeleteBehavior.Restrict)
        |> ignore

        charge
            .HasOne<ChargePreviewLineEntity>()
            .WithMany()
            .HasForeignKey("ChargePreviewLineId")
            .HasConstraintName("FK_ops_Charge_ChargePreviewLine")
            .OnDelete(Microsoft.EntityFrameworkCore.DeleteBehavior.Restrict)
        |> ignore

        let evidence = modelBuilder.Entity<BillingPeriodCloseEvidenceEntity>()

        evidence.ToTable(
            "BillingPeriodCloseEvidence",
            "ops",
            fun (table: TableBuilder<BillingPeriodCloseEvidenceEntity>) ->
                table.HasCheckConstraint(
                    "CK_ops_BillingPeriodCloseEvidence_Digests",
                    "LEN([AcceptedFactDigestSha256Hex]) = 64 AND [AcceptedFactDigestSha256Hex] NOT LIKE '%[^0-9A-F]%' AND LEN([PricingPreviewDigestSha256Hex]) = 64 AND [PricingPreviewDigestSha256Hex] NOT LIKE '%[^0-9A-F]%'"
                )
                |> ignore

                table.HasCheckConstraint("CK_ops_BillingPeriodCloseEvidence_Provenance", "LEN(LTRIM(RTRIM([ScheduledOperationProvenance]))) > 0")
                |> ignore
        )
        |> ignore

        evidence
            .HasKey([| "BillingPeriodId" |])
            .HasName("PK_ops_BillingPeriodCloseEvidence")
        |> ignore

        evidence
            .Property<Guid>("BillingPeriodId")
            .HasColumnType("uniqueidentifier")
            .ValueGeneratedNever()
        |> ignore

        evidence
            .Property<string>("AcceptedFactDigestSha256Hex")
            .HasColumnType("char(64)")
            .HasMaxLength(64)
            .IsUnicode(false)
            .IsRequired()
        |> ignore

        evidence
            .Property<string>("PricingPreviewDigestSha256Hex")
            .HasColumnType("char(64)")
            .HasMaxLength(64)
            .IsUnicode(false)
            .IsRequired()
        |> ignore

        evidence
            .Property<DateTime>("ClosedAtUtc")
            .HasColumnType("datetime2(7)")
            .IsRequired()
        |> ignore

        evidence
            .Property<string>("ScheduledOperationProvenance")
            .HasMaxLength(200)
            .IsRequired()
        |> ignore

        evidence
            .HasOne<BillingPeriodEntity>()
            .WithMany()
            .HasForeignKey("BillingPeriodId")
            .HasConstraintName("FK_ops_BillingPeriodCloseEvidence_BillingPeriod")
            .OnDelete(Microsoft.EntityFrameworkCore.DeleteBehavior.Restrict)
        |> ignore

        let lateWork = modelBuilder.Entity<BillingPeriodLateWorkEntity>()

        lateWork.ToTable("BillingPeriodLateWork", "ops")
        |> ignore

        lateWork
            .HasKey([| "BillingPeriodId"; "UsageFactId" |])
            .HasName("PK_ops_BillingPeriodLateWork")
        |> ignore

        lateWork
            .Property<Guid>("BillingPeriodId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        lateWork
            .Property<Guid>("UsageFactId")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

        lateWork
            .Property<DateTime>("CreatedAtUtc")
            .HasColumnType("datetime2(7)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .IsRequired()
        |> ignore

        lateWork
            .HasIndex([| "UsageFactId" |])
            .HasDatabaseName("IX_ops_BillingPeriodLateWork_UsageFact")
        |> ignore

        lateWork
            .HasOne<BillingPeriodEntity>()
            .WithMany()
            .HasForeignKey("BillingPeriodId")
            .HasConstraintName("FK_ops_BillingPeriodLateWork_BillingPeriod")
            .OnDelete(Microsoft.EntityFrameworkCore.DeleteBehavior.Restrict)
        |> ignore

        lateWork
            .HasOne<RawUsageFactEntity>()
            .WithMany()
            .HasForeignKey("UsageFactId")
            .HasConstraintName("FK_ops_BillingPeriodLateWork_RawUsageFact")
            .OnDelete(Microsoft.EntityFrameworkCore.DeleteBehavior.Restrict)
        |> ignore

/// Adds exact-scope billing periods, immutable initial postings, close evidence, and the minimal late-work handoff.
[<DbContextAttribute(typeof<OperationsDbContext>)>]
[<Migration("20260812130000_AddBillingPeriodClose")>]
type AddBillingPeriodClose() =
    inherit Migration()

    /// Creates the durable close contract without calculating pricing or charges in SQL.
    override _.Up(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(
            """
CREATE TABLE ops.BillingPeriod
(
    BillingPeriodId uniqueidentifier NOT NULL,
    OwnerId uniqueidentifier NOT NULL,
    OrganizationId uniqueidentifier NOT NULL,
    RepositoryId uniqueidentifier NOT NULL,
    MonthStartUtc datetime2(7) NOT NULL,
    NextMonthStartUtc datetime2(7) NOT NULL,
    State int NOT NULL,
    RetryDiagnostic nvarchar(400) NULL,
    RetryDiagnosticAtUtc datetime2(7) NULL,
    CreatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_ops_BillingPeriod_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
    UpdatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_ops_BillingPeriod_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_ops_BillingPeriod PRIMARY KEY (BillingPeriodId),
    CONSTRAINT CK_ops_BillingPeriod_MonthRange CHECK
    (
        [MonthStartUtc] < [NextMonthStartUtc]
        AND [MonthStartUtc] = DATETIME2FROMPARTS(YEAR([MonthStartUtc]), MONTH([MonthStartUtc]), 1, 0, 0, 0, 0, 7)
        AND [NextMonthStartUtc] = DATEADD(month, 1, [MonthStartUtc])
    ),
    CONSTRAINT CK_ops_BillingPeriod_State CHECK ([State] IN (0, 1, 2)),
    CONSTRAINT CK_ops_BillingPeriod_Diagnostic CHECK
    (
        ([RetryDiagnostic] IS NULL AND [RetryDiagnosticAtUtc] IS NULL)
        OR ([State] IN (0, 1) AND LEN(LTRIM(RTRIM([RetryDiagnostic]))) > 0 AND [RetryDiagnosticAtUtc] IS NOT NULL)
    )
);

CREATE UNIQUE INDEX UX_ops_BillingPeriod_ExactScope
    ON ops.BillingPeriod(OwnerId, OrganizationId, RepositoryId, MonthStartUtc, NextMonthStartUtc);

CREATE TABLE ops.Charge
(
    ChargeId uniqueidentifier NOT NULL,
    BillingPeriodId uniqueidentifier NOT NULL,
    ChargePreviewLineId uniqueidentifier NOT NULL,
    CurrencyCode varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ChargeMicros bigint NOT NULL,
    CreatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_ops_Charge_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_ops_Charge PRIMARY KEY (ChargeId),
    CONSTRAINT FK_ops_Charge_BillingPeriod FOREIGN KEY (BillingPeriodId) REFERENCES ops.BillingPeriod(BillingPeriodId),
    CONSTRAINT FK_ops_Charge_ChargePreviewLine FOREIGN KEY (ChargePreviewLineId) REFERENCES ops.ChargePreviewLine(ChargePreviewLineId),
    CONSTRAINT CK_ops_Charge_Amount CHECK ([ChargeMicros] >= 0),
    CONSTRAINT CK_ops_Charge_Currency CHECK (LEN([CurrencyCode]) = 3 AND [CurrencyCode] = UPPER([CurrencyCode]) AND [CurrencyCode] NOT LIKE '%[^A-Z]%')
);
CREATE UNIQUE INDEX UX_ops_Charge_InitialPosting ON ops.Charge(BillingPeriodId, ChargePreviewLineId);
CREATE INDEX IX_ops_Charge_ChargePreviewLine ON ops.Charge(ChargePreviewLineId);

CREATE TABLE ops.BillingPeriodCloseEvidence
(
    BillingPeriodId uniqueidentifier NOT NULL,
    AcceptedFactDigestSha256Hex char(64) NOT NULL,
    PricingPreviewDigestSha256Hex char(64) NOT NULL,
    ClosedAtUtc datetime2(7) NOT NULL,
    ScheduledOperationProvenance nvarchar(200) NOT NULL,
    CONSTRAINT PK_ops_BillingPeriodCloseEvidence PRIMARY KEY (BillingPeriodId),
    CONSTRAINT FK_ops_BillingPeriodCloseEvidence_BillingPeriod FOREIGN KEY (BillingPeriodId) REFERENCES ops.BillingPeriod(BillingPeriodId),
    CONSTRAINT CK_ops_BillingPeriodCloseEvidence_Digests CHECK
    (
        LEN([AcceptedFactDigestSha256Hex]) = 64 AND [AcceptedFactDigestSha256Hex] NOT LIKE '%[^0-9A-F]%'
        AND LEN([PricingPreviewDigestSha256Hex]) = 64 AND [PricingPreviewDigestSha256Hex] NOT LIKE '%[^0-9A-F]%'
    ),
    CONSTRAINT CK_ops_BillingPeriodCloseEvidence_Provenance CHECK (LEN(LTRIM(RTRIM([ScheduledOperationProvenance]))) > 0)
);

CREATE TABLE ops.BillingPeriodLateWork
(
    BillingPeriodId uniqueidentifier NOT NULL,
    UsageFactId uniqueidentifier NOT NULL,
    CreatedAtUtc datetime2(7) NOT NULL CONSTRAINT DF_ops_BillingPeriodLateWork_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_ops_BillingPeriodLateWork PRIMARY KEY (BillingPeriodId, UsageFactId),
    CONSTRAINT FK_ops_BillingPeriodLateWork_BillingPeriod FOREIGN KEY (BillingPeriodId) REFERENCES ops.BillingPeriod(BillingPeriodId),
    CONSTRAINT FK_ops_BillingPeriodLateWork_RawUsageFact FOREIGN KEY (UsageFactId) REFERENCES ops.RawUsageFact(UsageFactId)
);
CREATE INDEX IX_ops_BillingPeriodLateWork_UsageFact ON ops.BillingPeriodLateWork(UsageFactId);
"""
        )
        |> ignore

        migrationBuilder.Sql(
            """
CREATE TRIGGER ops.TR_ops_Charge_Immutable ON ops.Charge AFTER UPDATE, DELETE AS
BEGIN
    THROW 57231, 'Initial charge postings are append-only.', 1;
END;
"""
        )
        |> ignore

        migrationBuilder.Sql(
            """
CREATE TRIGGER ops.TR_ops_BillingPeriodCloseEvidence_Immutable ON ops.BillingPeriodCloseEvidence AFTER UPDATE, DELETE AS
BEGIN
    THROW 57232, 'Billing-period close evidence is immutable.', 1;
END;
"""
        )
        |> ignore

    /// Removes only the close structures introduced by this migration.
    override _.Down(migrationBuilder: MigrationBuilder) =
        migrationBuilder.Sql(
            """
DROP TRIGGER IF EXISTS ops.TR_ops_BillingPeriodCloseEvidence_Immutable;
DROP TRIGGER IF EXISTS ops.TR_ops_Charge_Immutable;
DROP TABLE ops.BillingPeriodLateWork;
DROP TABLE ops.BillingPeriodCloseEvidence;
DROP TABLE ops.Charge;
DROP TABLE ops.BillingPeriod;
"""
        )
        |> ignore

    /// Captures the literal owned billing-close model used to validate this migration independently of runtime helpers.
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

        journal.ToTable(
            "UsageFactJournal",
            "ops",
            fun (table: TableBuilder<UsageFactJournalEntity>) ->
                table.HasCheckConstraint(
                    "CK_ops_UsageFactJournal_Identity",
                    "[UsageFactId] <> '00000000-0000-0000-0000-000000000000' AND [OwnerId] <> '00000000-0000-0000-0000-000000000000' AND [OrganizationId] <> '00000000-0000-0000-0000-000000000000' AND [RepositoryId] <> '00000000-0000-0000-0000-000000000000' AND LEN(LTRIM(RTRIM([CorrelationId]))) > 0 AND LEN(LTRIM(RTRIM([StoragePoolId]))) > 0 AND DATALENGTH([RawPayload]) > 0"
                )
                |> ignore

                table.HasCheckConstraint(
                    "CK_ops_UsageFactJournal_State",
                    "([State] = 0 AND [TerminalAtUtc] IS NULL) OR ([State] IN (1, 2) AND [TerminalAtUtc] IS NOT NULL)"
                )
                |> ignore
        )
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

        BillingPeriodFrozenModel.apply modelBuilder
