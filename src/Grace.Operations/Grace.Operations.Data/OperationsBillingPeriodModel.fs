namespace Grace.Operations.Data

open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Metadata.Builders
open System

/// Configures billing-period, immutable-charge, close-evidence, and late-work persistence constraints.
[<RequireQualifiedAccess>]
module OperationsBillingPeriodModel =

    /// Adds the exact-scope period lifecycle and append-only close records to the Operations model.
    let configure (modelBuilder: ModelBuilder) =
        let period = modelBuilder.Entity<BillingPeriodEntity>()

        period.ToTable(
            "BillingPeriod",
            OperationsUsageSql.SchemaName,
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
            OperationsUsageSql.SchemaName,
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
            .OnDelete(DeleteBehavior.Restrict)
        |> ignore

        charge
            .HasOne<ChargePreviewLineEntity>()
            .WithMany()
            .HasForeignKey("ChargePreviewLineId")
            .HasConstraintName("FK_ops_Charge_ChargePreviewLine")
            .OnDelete(DeleteBehavior.Restrict)
        |> ignore

        let evidence = modelBuilder.Entity<BillingPeriodCloseEvidenceEntity>()

        evidence.ToTable(
            "BillingPeriodCloseEvidence",
            OperationsUsageSql.SchemaName,
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
            .OnDelete(DeleteBehavior.Restrict)
        |> ignore

        let lateWork = modelBuilder.Entity<BillingPeriodLateWorkEntity>()

        lateWork.ToTable("BillingPeriodLateWork", OperationsUsageSql.SchemaName)
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
            .OnDelete(DeleteBehavior.Restrict)
        |> ignore

        lateWork
            .HasOne<RawUsageFactEntity>()
            .WithMany()
            .HasForeignKey("UsageFactId")
            .HasConstraintName("FK_ops_BillingPeriodLateWork_RawUsageFact")
            .OnDelete(DeleteBehavior.Restrict)
        |> ignore
