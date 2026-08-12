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

        period
            .HasIndex(
                [|
                    "State"
                    "MonthStartUtc"
                    "OwnerId"
                    "OrganizationId"
                    "RepositoryId"
                |]
            )
            .HasDatabaseName("IX_ops_BillingPeriod_NonterminalDiscovery")
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
CREATE INDEX IX_ops_BillingPeriod_NonterminalDiscovery
    ON ops.BillingPeriod(State, MonthStartUtc, OwnerId, OrganizationId, RepositoryId);

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

        modelBuilder.HasDefaultSchema("ops") |> ignore

        BillingPeriodFrozenModel.apply modelBuilder
