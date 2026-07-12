namespace Grace.Operations.Data

open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Metadata.Builders
open System
open System.Security.Cryptography
open System.Text

/// Names persisted billing states in their stable SQL representation.
[<RequireQualifiedAccess>]
type BillingPeriodState =
    | Open = 0
    | Provisional = 1
    | Closed = 2
    | Corrected = 3

/// Names immutable ledger entry kinds in their stable SQL representation.
[<RequireQualifiedAccess>]
type ChargeLedgerEntryKind =
    | Charge = 0
    | Adjustment = 1
    | Reversal = 2

/// Carries complete provenance for an operator-initiated billing mutation.
type BillingOperationProvenance = { InitiatedByPrincipalId: string; ReasonCode: string; ReasonText: string; CorrelationId: string }

/// Defines the immutable customer/repository/month identity shared by preview and close locks.
type BillingPeriodScope = { CustomerId: Guid; OwnerId: Guid; OrganizationId: Guid; RepositoryId: Guid; PeriodFromUtc: DateTime; PeriodToUtc: DateTime }

/// Provides deterministic UTC lifecycle and identity rules without process-local state.
[<RequireQualifiedAccess>]
module BillingPeriodRules =
    /// Returns the exact UTC calendar month containing the supplied UTC instant.
    let monthContaining (instant: DateTime) =
        if instant.Kind <> DateTimeKind.Utc then
            invalidArg "instant" "Billing calendar inputs must be UTC."

        let fromUtc = DateTime(instant.Year, instant.Month, 1, 0, 0, 0, DateTimeKind.Utc)
        fromUtc, fromUtc.AddMonths 1

    /// Returns every UTC month intersecting an effective half-open assignment interval.
    let intersectingMonths (effectiveFromUtc: DateTime) (effectiveToUtc: DateTime option) (throughUtc: DateTime) =
        if effectiveFromUtc.Kind <> DateTimeKind.Utc
           || throughUtc.Kind <> DateTimeKind.Utc then
            invalidArg "effectiveFromUtc" "Billing calendar inputs must be UTC."

        let first, _ = monthContaining effectiveFromUtc
        let exclusiveEnd = effectiveToUtc |> Option.defaultValue throughUtc
        let result = ResizeArray<DateTime * DateTime>()
        let mutable cursor = first

        while cursor < throughUtc && cursor < exclusiveEnd do
            let next = cursor.AddMonths 1

            if next > effectiveFromUtc && cursor < exclusiveEnd then
                result.Add(cursor, next)

            cursor <- next

        result |> Seq.toList

    /// Computes lifecycle state at exact +24-hour and +72-hour boundaries.
    let stateAt (periodToUtc: DateTime) (nowUtc: DateTime) =
        if periodToUtc.Kind <> DateTimeKind.Utc
           || nowUtc.Kind <> DateTimeKind.Utc then
            invalidArg "nowUtc" "Billing lifecycle timestamps must be UTC."

        if nowUtc < periodToUtc.AddHours 24.0 then BillingPeriodState.Open
        elif nowUtc < periodToUtc.AddHours 72.0 then BillingPeriodState.Provisional
        else BillingPeriodState.Provisional

    /// Returns whether automatic or operator retry may attempt close without bypassing timing.
    let isCloseEligible (periodToUtc: DateTime) (nowUtc: DateTime) = nowUtc >= periodToUtc.AddHours 72.0

    /// Derives a stable UUID from a complete invariant tuple.
    let deterministicId (parts: string seq) =
        let bytes =
            parts
            |> String.concat "|"
            |> Encoding.UTF8.GetBytes
            |> SHA256.HashData

        let guidBytes = bytes[0..15]
        guidBytes[6] <- (guidBytes[6] &&& 0x0Fuy) ||| 0x50uy
        guidBytes[8] <- (guidBytes[8] &&& 0x3Fuy) ||| 0x80uy
        Guid guidBytes

    /// Derives the persisted billing period identity from the full scope and month.
    let periodId scope =
        deterministicId [ scope.CustomerId.ToString("D")
                          scope.OwnerId.ToString("D")
                          scope.OrganizationId.ToString("D")
                          scope.RepositoryId.ToString("D")
                          scope.PeriodFromUtc.Ticks.ToString(Globalization.CultureInfo.InvariantCulture)
                          scope.PeriodToUtc.Ticks.ToString(Globalization.CultureInfo.InvariantCulture) ]

/// Validates complete provenance instead of persisting dangling audit references.
[<RequireQualifiedAccess>]
module BillingProvenance =
    /// Rejects missing or oversized provenance before a billing mutation starts.
    let validate provenance =
        let required name maximum value =
            if String.IsNullOrWhiteSpace value then invalidArg name $"{name} is required."

            if value.Length > maximum then
                invalidArg name $"{name} cannot exceed {maximum} characters."

        required "InitiatedByPrincipalId" 256 provenance.InitiatedByPrincipalId
        required "ReasonCode" 64 provenance.ReasonCode
        required "ReasonText" 1024 provenance.ReasonText
        required "CorrelationId" OperationsUsageSql.CorrelationIdMaxLength provenance.CorrelationId

/// Owns literal runtime EF configuration for billing close persistence.
[<RequireQualifiedAccess>]
module OperationsBillingModel =
    let private guid (entity: EntityTypeBuilder<'T>) (name: string) =
        entity
            .Property<Guid>(name)
            .HasColumnType("uniqueidentifier")
            .IsRequired()
        |> ignore

    let private utc (entity: EntityTypeBuilder<'T>) (name: string) =
        entity
            .Property<DateTime>(name)
            .HasColumnType("datetime2(7)")
            .IsRequired()
        |> ignore

    /// Configures period, freshness, immutable ledger, scoped failures, and correction delivery.
    let configure (modelBuilder: ModelBuilder) =
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
            .HasMaxLength(OperationsUsageSql.CorrelationIdMaxLength)
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
            .HasMaxLength(OperationsUsageSql.CorrelationIdMaxLength)
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
            .HasMaxLength(OperationsUsageSql.CorrelationIdMaxLength)
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
            .HasMaxLength(OperationsUsageSql.CorrelationIdMaxLength)
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

/// Contains SQL guards that make billing history immutable below application code.
[<RequireQualifiedAccess>]
module OperationsBillingSql =
    /// Uses the exact preview lock namespace and complete scope identity under transaction ownership.
    let AcquireScopeLock = OperationsChargePreviewSql.AcquireScopeLock

    /// Rejects all posted ledger updates and deletes, including direct SQL access.
    let CreateLedgerImmutabilityTrigger =
        "CREATE OR ALTER TRIGGER [ops].[TR_ops_ChargeLedgerEntry_Immutable] ON [ops].[ChargeLedgerEntry] INSTEAD OF UPDATE, DELETE AS BEGIN SET NOCOUNT ON; THROW 51020, 'Posted charge ledger entries are immutable.', 1; END;"

    /// Rejects mutation of pricing rows already referenced by a Closed or Corrected period while allowing future rows.
    let CreateHistoricalPricingProtectionTriggers =
        """CREATE OR ALTER TRIGGER [ops].[TR_ops_PricingRate_HistoricalProtection] ON [ops].[PricingRate] AFTER INSERT, UPDATE, DELETE AS BEGIN SET NOCOUNT ON; IF EXISTS (SELECT 1 FROM (SELECT PricingPlanId,EffectiveFromUtc,EffectiveToUtc FROM inserted UNION ALL SELECT PricingPlanId,EffectiveFromUtc,EffectiveToUtc FROM deleted) d JOIN ops.CustomerPricingAssignment a ON a.PricingPlanId=d.PricingPlanId JOIN ops.BillingPeriod p ON p.CustomerId=a.CustomerId AND p.OwnerId=a.OwnerId AND p.OrganizationId=a.OrganizationId AND p.RepositoryId=a.RepositoryId WHERE p.State IN (2,3) AND a.EffectiveFromUtc<p.PeriodToUtc AND (a.EffectiveToUtc IS NULL OR a.EffectiveToUtc>p.PeriodFromUtc) AND d.EffectiveFromUtc<p.PeriodToUtc AND (d.EffectiveToUtc IS NULL OR d.EffectiveToUtc>p.PeriodFromUtc)) THROW 51021, 'Historical pricing applicable to a closed billing period is immutable.', 1; END;
CREATE OR ALTER TRIGGER [ops].[TR_ops_PricingPlan_HistoricalProtection] ON [ops].[PricingPlan] AFTER INSERT, UPDATE, DELETE AS BEGIN SET NOCOUNT ON; IF EXISTS (SELECT 1 FROM (SELECT PricingPlanId,EffectiveFromUtc,EffectiveToUtc FROM inserted UNION ALL SELECT PricingPlanId,EffectiveFromUtc,EffectiveToUtc FROM deleted) d JOIN ops.CustomerPricingAssignment a ON a.PricingPlanId=d.PricingPlanId JOIN ops.BillingPeriod p ON p.CustomerId=a.CustomerId AND p.OwnerId=a.OwnerId AND p.OrganizationId=a.OrganizationId AND p.RepositoryId=a.RepositoryId WHERE p.State IN (2,3) AND a.EffectiveFromUtc<p.PeriodToUtc AND (a.EffectiveToUtc IS NULL OR a.EffectiveToUtc>p.PeriodFromUtc) AND d.EffectiveFromUtc<p.PeriodToUtc AND (d.EffectiveToUtc IS NULL OR d.EffectiveToUtc>p.PeriodFromUtc)) THROW 51022, 'Historical pricing plan applicable to a closed billing period is immutable.', 1; END;
CREATE OR ALTER TRIGGER [ops].[TR_ops_BillableUsageKindMapping_HistoricalProtection] ON [ops].[BillableUsageKindMapping] AFTER INSERT, UPDATE, DELETE AS BEGIN SET NOCOUNT ON; IF EXISTS (SELECT 1 FROM (SELECT EffectiveFromUtc,EffectiveToUtc FROM inserted UNION ALL SELECT EffectiveFromUtc,EffectiveToUtc FROM deleted) d JOIN ops.BillingPeriod p ON d.EffectiveFromUtc<p.PeriodToUtc AND (d.EffectiveToUtc IS NULL OR d.EffectiveToUtc>p.PeriodFromUtc) JOIN ops.CustomerPricingAssignment a ON a.CustomerId=p.CustomerId AND a.OwnerId=p.OwnerId AND a.OrganizationId=p.OrganizationId AND a.RepositoryId=p.RepositoryId AND a.EffectiveFromUtc<p.PeriodToUtc AND (a.EffectiveToUtc IS NULL OR a.EffectiveToUtc>p.PeriodFromUtc) WHERE p.State IN (2,3)) THROW 51023, 'Historical billable mapping applicable to a closed billing period is immutable.', 1; END;
CREATE OR ALTER TRIGGER [ops].[TR_ops_CustomerPricingAssignment_HistoricalProtection] ON [ops].[CustomerPricingAssignment] AFTER INSERT, UPDATE, DELETE AS BEGIN SET NOCOUNT ON; IF EXISTS (SELECT 1 FROM (SELECT CustomerId,OwnerId,OrganizationId,RepositoryId,EffectiveFromUtc,EffectiveToUtc FROM inserted UNION ALL SELECT CustomerId,OwnerId,OrganizationId,RepositoryId,EffectiveFromUtc,EffectiveToUtc FROM deleted) d JOIN ops.BillingPeriod p ON p.CustomerId=d.CustomerId AND p.OwnerId=d.OwnerId AND p.OrganizationId=d.OrganizationId AND p.RepositoryId=d.RepositoryId WHERE p.State IN (2,3) AND d.EffectiveFromUtc<p.PeriodToUtc AND (d.EffectiveToUtc IS NULL OR d.EffectiveToUtc>p.PeriodFromUtc)) THROW 51024, 'Historical customer pricing assignment applicable to a closed billing period is immutable.', 1; END;"""
