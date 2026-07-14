namespace Grace.Operations.Data

open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Metadata.Builders
open System
open System.Globalization
open System.Security.Cryptography
open System.Text

/// Names persisted billing lifecycle states in their stable SQL representation.
[<RequireQualifiedAccess>]
type BillingPeriodState =
    | Open = 0
    | Provisional = 1
    | Closed = 2
    | Corrected = 3
    | PermanentlyFailed = 4

/// Names immutable charge-ledger entry kinds in their stable SQL representation.
[<RequireQualifiedAccess>]
type ChargeLedgerEntryKind =
    | Charge = 0
    | Adjustment = 1

/// Carries complete provenance for an operator or automatic billing mutation.
type BillingOperationProvenance = { InitiatedByPrincipalId: string; ReasonCode: string; ReasonText: string; CorrelationId: string }

/// Defines the owner-scoped UTC month identity shared by preview and billing locks.
type BillingPeriodScope = { OwnerId: Guid; OrganizationId: Guid; RepositoryId: Guid; PeriodFromUtc: DateTime; PeriodToUtc: DateTime }

/// Provides deterministic UTC calendar, lifecycle, and identity rules.
[<RequireQualifiedAccess>]
module BillingPeriodRules =
    /// Returns the UTC calendar month containing the supplied instant.
    let monthContaining (instant: DateTime) =
        if instant.Kind <> DateTimeKind.Utc then
            invalidArg "instant" "Billing calendar inputs must be UTC."

        let fromUtc = DateTime(instant.Year, instant.Month, 1, 0, 0, 0, DateTimeKind.Utc)
        fromUtc, fromUtc.AddMonths(1)

    /// Returns UTC months that intersect an effective half-open pricing interval.
    let intersectingMonths (effectiveFromUtc: DateTime) (effectiveToUtc: DateTime option) (throughUtc: DateTime) =
        if effectiveFromUtc.Kind <> DateTimeKind.Utc
           || throughUtc.Kind <> DateTimeKind.Utc then
            invalidArg "effectiveFromUtc" "Billing calendar inputs must be UTC."

        let endExclusive = effectiveToUtc |> Option.defaultValue throughUtc
        let first, _ = monthContaining effectiveFromUtc
        let result = ResizeArray<DateTime * DateTime>()
        let mutable cursor = first

        while cursor < throughUtc && cursor < endExclusive do
            let next = cursor.AddMonths(1)

            if next > effectiveFromUtc && cursor < endExclusive then
                result.Add(cursor, next)

            cursor <- next

        result |> Seq.toList

    /// Returns the nonterminal lifecycle state at the supplied UTC instant.
    let stateAt (periodToUtc: DateTime) (nowUtc: DateTime) =
        if periodToUtc.Kind <> DateTimeKind.Utc
           || nowUtc.Kind <> DateTimeKind.Utc then
            invalidArg "nowUtc" "Billing lifecycle timestamps must be UTC."

        if nowUtc < periodToUtc.AddHours(24.0) then
            BillingPeriodState.Open
        else
            BillingPeriodState.Provisional

    /// Returns whether the close path is eligible without any force bypass.
    let isCloseEligible (periodToUtc: DateTime) (nowUtc: DateTime) = nowUtc >= periodToUtc.AddHours(72.0)

    /// Builds a stable UUID from a complete invariant tuple.
    let deterministicId (parts: string seq) =
        let bytes =
            parts
            |> String.concat "|"
            |> Encoding.UTF8.GetBytes
            |> SHA256.HashData

        let guidBytes = bytes[0..15]
        guidBytes[6] <- (guidBytes[6] &&& 0x0Fuy) ||| 0x50uy
        guidBytes[8] <- (guidBytes[8] &&& 0x3Fuy) ||| 0x80uy
        Guid(guidBytes)

    /// Builds the billing period identity from exactly owner scope and UTC month.
    let periodId (scope: BillingPeriodScope) =
        deterministicId [ scope.OwnerId.ToString("D")
                          scope.OrganizationId.ToString("D")
                          scope.RepositoryId.ToString("D")
                          scope.PeriodFromUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                          scope.PeriodToUtc.Ticks.ToString(CultureInfo.InvariantCulture) ]

/// Validates durable billing provenance before mutation begins.
[<RequireQualifiedAccess>]
module BillingProvenance =
    /// Rejects absent or oversized provenance values at the public internal-service boundary.
    let validate provenance =
        let required name maximum value =
            if String.IsNullOrWhiteSpace(value) then invalidArg name $"{name} is required."

            if value.Length > maximum then
                invalidArg name $"{name} cannot exceed {maximum} characters."

        required "InitiatedByPrincipalId" 256 provenance.InitiatedByPrincipalId
        required "ReasonCode" 64 provenance.ReasonCode
        required "ReasonText" 1024 provenance.ReasonText
        required "CorrelationId" OperationsUsageSql.CorrelationIdMaxLength provenance.CorrelationId

/// Defines a manual immutable adjustment under one billing period.
type ManualBillingCorrection =
    {
        BillingPeriodId: Guid
        EntryKind: ChargeLedgerEntryKind
        FactKind: int
        BillableUsageKindMappingId: Guid
        BillableUsageKind: int
        PricingAssignmentId: Guid
        PricingPlanId: Guid
        PricingRateId: Guid
        CurrencyCode: string
        UnitName: string
        UnitQuantity: int64
        UnitPriceMicros: int64
        EffectiveFromUtc: DateTime
        EffectiveToUtc: DateTime
        QuantityDelta: int64
        ChargeMicrosDelta: int64
    }

/// Builds deterministic manual-correction identities from every immutable pricing dimension.
[<RequireQualifiedAccess>]
module ManualBillingCorrectionIdentity =
    /// Derives one stable identity for an exact manual retry.
    let entryId (correction: ManualBillingCorrection) correlationId =
        let entryKind =
            (int correction.EntryKind)
                .ToString(CultureInfo.InvariantCulture)

        BillingPeriodRules.deterministicId [ correction.BillingPeriodId.ToString("D")
                                             entryKind
                                             correction.FactKind.ToString(CultureInfo.InvariantCulture)
                                             correction.BillableUsageKindMappingId.ToString("D")
                                             correction.BillableUsageKind.ToString(CultureInfo.InvariantCulture)
                                             correction.PricingAssignmentId.ToString("D")
                                             correction.PricingPlanId.ToString("D")
                                             correction.PricingRateId.ToString("D")
                                             correction.CurrencyCode
                                             correction.UnitName
                                             correction.UnitQuantity.ToString(CultureInfo.InvariantCulture)
                                             correction.UnitPriceMicros.ToString(CultureInfo.InvariantCulture)
                                             correction.EffectiveFromUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                                             correction.EffectiveToUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                                             correction.QuantityDelta.ToString(CultureInfo.InvariantCulture)
                                             correction.ChargeMicrosDelta.ToString(CultureInfo.InvariantCulture)
                                             correlationId ]

/// Validates correction interval and immutable pricing grain before any SQL mutation.
[<RequireQualifiedAccess>]
module ManualBillingCorrectionValidation =
    /// Rejects pricing dimensions that cannot represent an immutable posted amount.
    let validatePricingGrain (correction: ManualBillingCorrection) =
        if correction.EntryKind
           <> ChargeLedgerEntryKind.Adjustment then
            invalidArg "EntryKind" "Manual corrections must be adjustments."

        if correction.UnitQuantity <= 0L then
            invalidArg "UnitQuantity" "UnitQuantity must be positive."

        if correction.UnitPriceMicros < 0L then
            invalidArg "UnitPriceMicros" "UnitPriceMicros cannot be negative."

        if
            String.IsNullOrWhiteSpace(correction.CurrencyCode)
            || correction.CurrencyCode.Length <> 3
        then
            invalidArg "CurrencyCode" "CurrencyCode must be a three-letter ISO value."

        if
            String.IsNullOrWhiteSpace(correction.UnitName)
            || correction.UnitName.Length > 64
        then
            invalidArg "UnitName" "UnitName is required and bounded."

    /// Requires a correction applicability interval to remain wholly inside the period's half-open interval.
    let validateApplicability (periodFromUtc: DateTime) (periodToUtc: DateTime) (correction: ManualBillingCorrection) =
        if correction.EffectiveFromUtc.Kind
           <> DateTimeKind.Utc
           || correction.EffectiveToUtc.Kind <> DateTimeKind.Utc then
            invalidArg "correction" "Correction applicability timestamps must be UTC."

        if correction.EffectiveFromUtc < periodFromUtc
           || correction.EffectiveFromUtc
              >= correction.EffectiveToUtc
           || correction.EffectiveToUtc > periodToUtc then
            invalidArg "correction" "Correction applicability must be wholly inside the billing period."

/// Owns runtime EF Core configuration for owner-scoped billing state and immutable evidence.
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

    /// Configures lifecycle, freshness, ledger, failure, and correction work persistence.
    let configure (modelBuilder: ModelBuilder) =
        let period = modelBuilder.Entity<BillingPeriodEntity>()
        period.ToTable("BillingPeriod", "ops") |> ignore

        period
            .HasKey([| "BillingPeriodId" |])
            .HasName("PK_ops_BillingPeriod")
        |> ignore

        [
            "BillingPeriodId"
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
            .Property<string>("PermanentFailureCode")
            .HasMaxLength(64)
        |> ignore

        period
            .Property<string>("PermanentFailureDetail")
            .HasMaxLength(1024)
        |> ignore

        period
            .Property<Nullable<DateTime>>("PermanentlyFailedAtUtc")
            .HasColumnType("datetime2(7)")
        |> ignore

        period
            .Property<string>("PermanentFailureInitiatedByPrincipalId")
            .HasMaxLength(256)
        |> ignore

        period
            .Property<string>("PermanentFailureReasonCode")
            .HasMaxLength(64)
        |> ignore

        period
            .Property<string>("PermanentFailureReasonText")
            .HasMaxLength(1024)
        |> ignore

        period
            .Property<string>("PermanentFailureCorrelationId")
            .HasMaxLength(OperationsUsageSql.CorrelationIdMaxLength)
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

        ledger.ToTable("ChargeLedgerEntry", "ops")
        |> ignore

        ledger
            .HasKey([| "ChargeLedgerEntryId" |])
            .HasName("PK_ops_ChargeLedgerEntry")
        |> ignore

        [
            "ChargeLedgerEntryId"
            "BillingPeriodId"
            "BillableUsageKindMappingId"
            "PricingAssignmentId"
            "PricingPlanId"
            "PricingRateId"
        ]
        |> List.iter (guid ledger)

        [
            "SourceChargePreviewLineId"
            "PriorChargeLedgerEntryId"
            "BillingCorrectionWorkId"
        ]
        |> List.iter (fun name ->
            ledger
                .Property<Nullable<Guid>>(name)
                .HasColumnType("uniqueidentifier")
            |> ignore)

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

        guid failure "BillingIngestionFailureId"

        [
            "UsageFactId"
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

        failure
            .Property<string>("CorrelationId")
            .HasMaxLength(OperationsUsageSql.CorrelationIdMaxLength)
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

        [
            "BillingCorrectionWorkId"
            "BillingPeriodId"
            "UsageFactId"
        ]
        |> List.iter (guid work)

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
            .Property<Nullable<DateTime>>("PermanentlyFailedAtUtc")
            .HasColumnType("datetime2(7)")
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

/// Centralizes SQL object names and database-only immutability guards for the billing model.
[<RequireQualifiedAccess>]
module OperationsBillingSql =
    /// Uses the same owner/month lock semantics as preview replacement.
    let AcquireScopeLock = OperationsChargePreviewSql.AcquireScopeLock

    /// Rejects non-Adjustment corrections outside closed history, posted-entry mutation, and initial charges after terminal close.
    let CreateLedgerImmutabilityTrigger =
        """
CREATE TRIGGER ops.TR_ops_ChargeLedgerEntry_Immutable ON ops.ChargeLedgerEntry
AFTER INSERT, UPDATE, DELETE AS
BEGIN
    IF EXISTS
    (
        SELECT 1
        FROM inserted e
        JOIN ops.BillingPeriod p ON p.BillingPeriodId=e.BillingPeriodId
        WHERE e.EntryKind=1 AND p.State NOT IN (2,3)
    )
        THROW 51009, 'Adjustment entries require a closed or corrected billing period.', 1;

    IF EXISTS
    (
        SELECT 1
        FROM inserted e
        JOIN ops.BillingPeriod p ON p.BillingPeriodId=e.BillingPeriodId
        WHERE e.EntryKind=0 AND p.State IN (2,3,4)
    )
        THROW 51008, 'Initial charge entries cannot be appended after a billing period is terminal.', 1;

    IF EXISTS (SELECT 1 FROM deleted)
    THROW 51000, 'Charge ledger entries are immutable.', 1;
END;
"""

    /// Freezes billing source and evidence fields in terminal scopes and admits raw inserts there only from trusted owner-month ingestion.
    let CreateTerminalRawUsageFactProtectionTrigger =
        """
CREATE TRIGGER ops.TR_ops_RawUsageFact_TerminalBillingProtection ON ops.RawUsageFact
AFTER INSERT, UPDATE, DELETE AS
BEGIN
    IF EXISTS
    (
        SELECT 1
        FROM inserted i
        JOIN ops.BillingPeriod terminalPeriod ON terminalPeriod.OwnerId=i.OwnerId AND terminalPeriod.OrganizationId=i.OrganizationId AND terminalPeriod.RepositoryId=i.RepositoryId
          AND i.ObservedAtUtc>=terminalPeriod.PeriodFromUtc AND i.ObservedAtUtc<terminalPeriod.PeriodToUtc AND terminalPeriod.State IN (2,3,4)
        LEFT JOIN deleted d ON d.UsageFactId=i.UsageFactId
        WHERE d.UsageFactId IS NULL
          AND
          (
              terminalPeriod.State=4
              OR ISNULL(CONVERT(nvarchar(36),SESSION_CONTEXT(N'Grace.Operations.TrustedRawUsageFactInsert')),N'')<>CONVERT(nvarchar(36),i.UsageFactId)
          )
    )
        THROW 51010, 'Terminal billing raw fact inserts require trusted application routing.', 1;

    -- UPDATE(column) closes the multi-row swap bypass that set comparison alone cannot distinguish.
    IF EXISTS(SELECT 1 FROM deleted)
       AND (UPDATE(UsageFactId) OR UPDATE(OwnerId) OR UPDATE(OrganizationId) OR UPDATE(RepositoryId) OR UPDATE(ObservedAtUtc))
       AND EXISTS
       (
           SELECT 1
           FROM inserted i
           JOIN ops.BillingPeriod destinationPeriod ON destinationPeriod.OwnerId=i.OwnerId AND destinationPeriod.OrganizationId=i.OrganizationId AND destinationPeriod.RepositoryId=i.RepositoryId
             AND i.ObservedAtUtc>=destinationPeriod.PeriodFromUtc AND i.ObservedAtUtc<destinationPeriod.PeriodToUtc AND destinationPeriod.State IN (2,3,4)
       )
        THROW 51005, 'Terminal billing raw fact source and evidence fields are immutable.', 1;

    IF EXISTS
    (
        SELECT 1
        FROM deleted d
        JOIN ops.BillingPeriod sourcePeriod ON sourcePeriod.OwnerId=d.OwnerId AND sourcePeriod.OrganizationId=d.OrganizationId AND sourcePeriod.RepositoryId=d.RepositoryId
          AND d.ObservedAtUtc>=sourcePeriod.PeriodFromUtc AND d.ObservedAtUtc<sourcePeriod.PeriodToUtc AND sourcePeriod.State IN (2,3,4)
        WHERE EXISTS
        (
            SELECT d.UsageFactId,d.CorrelationId,d.FactKind,d.OwnerId,d.OrganizationId,d.RepositoryId,d.StoragePoolId,d.Quantity,d.ObservedAtUtc,d.AcceptedAtUtc,d.CreatedAtUtc
            EXCEPT
            SELECT i.UsageFactId,i.CorrelationId,i.FactKind,i.OwnerId,i.OrganizationId,i.RepositoryId,i.StoragePoolId,i.Quantity,i.ObservedAtUtc,i.AcceptedAtUtc,i.CreatedAtUtc
            FROM inserted i
        )
    )
        THROW 51005, 'Terminal billing raw fact source and evidence fields are immutable.', 1;

    IF EXISTS
    (
        SELECT 1
        FROM inserted i
        JOIN ops.BillingPeriod destinationPeriod ON destinationPeriod.OwnerId=i.OwnerId AND destinationPeriod.OrganizationId=i.OrganizationId AND destinationPeriod.RepositoryId=i.RepositoryId
          AND i.ObservedAtUtc>=destinationPeriod.PeriodFromUtc AND i.ObservedAtUtc<destinationPeriod.PeriodToUtc AND destinationPeriod.State IN (2,3,4)
        WHERE EXISTS(SELECT 1 FROM deleted)
          AND EXISTS
        (
            SELECT i.UsageFactId,i.CorrelationId,i.FactKind,i.OwnerId,i.OrganizationId,i.RepositoryId,i.StoragePoolId,i.Quantity,i.ObservedAtUtc,i.AcceptedAtUtc,i.CreatedAtUtc
            EXCEPT
            SELECT d.UsageFactId,d.CorrelationId,d.FactKind,d.OwnerId,d.OrganizationId,d.RepositoryId,d.StoragePoolId,d.Quantity,d.ObservedAtUtc,d.AcceptedAtUtc,d.CreatedAtUtc
            FROM deleted d
        )
    )
        THROW 51005, 'Terminal billing raw fact source and evidence fields are immutable.', 1;
END;
"""

    /// Freezes terminal close evidence, rejects terminal row inserts, and preserves permanently failed periods with every supporting field unchanged.
    let CreatePermanentBillingFailureProtectionTrigger =
        """
CREATE TRIGGER ops.TR_ops_BillingPeriod_PermanentFailureProtection ON ops.BillingPeriod
AFTER INSERT, UPDATE, DELETE AS
BEGIN
    IF EXISTS
    (
        SELECT 1
        FROM inserted i
        LEFT JOIN deleted d ON d.BillingPeriodId=i.BillingPeriodId
        WHERE d.BillingPeriodId IS NULL AND i.State IN (2,3,4)
    )
        THROW 51011, 'Terminal billing periods must be established by the supported lifecycle.', 1;

    IF EXISTS
    (
        SELECT 1
        FROM deleted d
        JOIN inserted i ON i.BillingPeriodId=d.BillingPeriodId
        WHERE d.State IN (0,1) AND i.State IN (2,3,4)
          AND
          (
              d.State<>1
              OR i.State=3
              OR
              (
                  i.State=2
                  AND
                  (
                      i.ClosedAtUtc IS NULL
                      OR i.CloseInitiatedByPrincipalId IS NULL
                      OR i.CloseReasonCode IS NULL
                      OR i.CloseReasonText IS NULL
                      OR i.CloseCorrelationId IS NULL
                      OR i.CloseBlockedCode IS NOT NULL
                      OR i.CloseBlockedDetail IS NOT NULL
                      OR i.LastCloseAttemptAtUtc IS NULL
                      OR i.PermanentFailureCode IS NOT NULL
                      OR i.PermanentFailureDetail IS NOT NULL
                      OR i.PermanentlyFailedAtUtc IS NOT NULL
                      OR i.PermanentFailureInitiatedByPrincipalId IS NOT NULL
                      OR i.PermanentFailureReasonCode IS NOT NULL
                      OR i.PermanentFailureReasonText IS NOT NULL
                      OR i.PermanentFailureCorrelationId IS NOT NULL
                      OR NOT EXISTS(SELECT 1 FROM ops.ChargePreviewFreshness f WHERE f.BillingPeriodId=i.BillingPeriodId)
                      OR EXISTS
                      (
                          SELECT 1
                          FROM ops.ChargePreviewLine l
                          WHERE l.OwnerId=i.OwnerId AND l.OrganizationId=i.OrganizationId AND l.RepositoryId=i.RepositoryId
                            AND l.PeriodFromUtc=i.PeriodFromUtc AND l.PeriodToUtc=i.PeriodToUtc
                            AND NOT EXISTS
                            (
                                SELECT 1
                                FROM ops.ChargeLedgerEntry e
                                WHERE e.BillingPeriodId=i.BillingPeriodId AND e.EntryKind=0 AND e.SourceChargePreviewLineId=l.ChargePreviewLineId
                                  AND e.FactKind=l.FactKind
                                  AND e.BillableUsageKindMappingId=l.BillableUsageKindMappingId
                                  AND e.BillableUsageKind=l.BillableUsageKind
                                  AND e.PricingAssignmentId=l.PricingAssignmentId
                                  AND e.PricingPlanId=l.PricingPlanId
                                  AND e.PricingRateId=l.PricingRateId
                                  AND e.CurrencyCode=l.CurrencyCode
                                  AND e.UnitName=l.UnitName
                                  AND e.UnitQuantity=l.UnitQuantity
                                  AND e.UnitPriceMicros=l.UnitPriceMicros
                                  AND e.EffectiveFromUtc=l.EffectiveFromUtc
                                  AND e.EffectiveToUtc=l.EffectiveToUtc
                                  AND e.Quantity=l.TotalQuantity
                                  AND e.ChargeMicros=l.ChargeMicros
                            )
                      )
                      OR EXISTS
                      (
                          SELECT 1
                          FROM ops.ChargeLedgerEntry e
                          WHERE e.BillingPeriodId=i.BillingPeriodId AND e.EntryKind=0
                            AND NOT EXISTS
                            (
                                SELECT 1
                                FROM ops.ChargePreviewLine l
                                WHERE l.ChargePreviewLineId=e.SourceChargePreviewLineId
                                  AND l.OwnerId=i.OwnerId AND l.OrganizationId=i.OrganizationId AND l.RepositoryId=i.RepositoryId
                                  AND l.PeriodFromUtc=i.PeriodFromUtc AND l.PeriodToUtc=i.PeriodToUtc
                                  AND l.FactKind=e.FactKind
                                  AND l.BillableUsageKindMappingId=e.BillableUsageKindMappingId
                                  AND l.BillableUsageKind=e.BillableUsageKind
                                  AND l.PricingAssignmentId=e.PricingAssignmentId
                                  AND l.PricingPlanId=e.PricingPlanId
                                  AND l.PricingRateId=e.PricingRateId
                                  AND l.CurrencyCode=e.CurrencyCode
                                  AND l.UnitName=e.UnitName
                                  AND l.UnitQuantity=e.UnitQuantity
                                  AND l.UnitPriceMicros=e.UnitPriceMicros
                                  AND l.EffectiveFromUtc=e.EffectiveFromUtc
                                  AND l.EffectiveToUtc=e.EffectiveToUtc
                                  AND l.TotalQuantity=e.Quantity
                                  AND l.ChargeMicros=e.ChargeMicros
                            )
                      )
                  )
              )
              OR
              (
                  i.State=4
                  AND
                  (
                      i.ClosedAtUtc IS NOT NULL
                      OR i.CloseInitiatedByPrincipalId IS NOT NULL
                      OR i.CloseReasonCode IS NOT NULL
                      OR i.CloseReasonText IS NOT NULL
                      OR i.CloseCorrelationId IS NOT NULL
                      OR i.CloseBlockedCode IS NOT NULL
                      OR i.CloseBlockedDetail IS NOT NULL
                      OR i.LastCloseAttemptAtUtc IS NULL
                      OR i.PermanentFailureCode<>N'CalculationOverflow'
                      OR i.PermanentFailureDetail IS NULL
                      OR i.PermanentlyFailedAtUtc IS NULL
                      OR i.PermanentFailureInitiatedByPrincipalId IS NULL
                      OR i.PermanentFailureReasonCode IS NULL
                      OR i.PermanentFailureReasonText IS NULL
                      OR i.PermanentFailureCorrelationId IS NULL
                  )
              )
          )
    )
        THROW 51007, 'Terminal billing transitions require complete immutable close, correction, or permanent-failure evidence.', 1;

    IF EXISTS
    (
        SELECT 1
        FROM deleted d
        LEFT JOIN inserted i ON i.BillingPeriodId=d.BillingPeriodId
        WHERE d.State IN (2,3,4)
          AND
          (
              i.BillingPeriodId IS NULL
              OR
              (
                  d.State IN (2,3)
                  AND
                  (
                      i.State NOT IN (2,3)
                      OR (d.State=3 AND i.State<>3)
                      OR
                      (
                          d.State=2 AND i.State=3
                          AND NOT EXISTS
                          (
                              SELECT 1
                              FROM ops.ChargeLedgerEntry e
                              WHERE e.BillingPeriodId=d.BillingPeriodId AND e.EntryKind=1 AND e.SourceChargePreviewLineId IS NULL
                          )
                      )
                      OR i.OwnerId<>d.OwnerId
                      OR i.OrganizationId<>d.OrganizationId
                      OR i.RepositoryId<>d.RepositoryId
                      OR i.PeriodFromUtc<>d.PeriodFromUtc
                      OR i.PeriodToUtc<>d.PeriodToUtc
                      OR ISNULL(i.ClosedAtUtc,CONVERT(datetime2(7),'19000101'))<>ISNULL(d.ClosedAtUtc,CONVERT(datetime2(7),'19000101'))
                      OR ISNULL(i.CloseInitiatedByPrincipalId,N'')<>ISNULL(d.CloseInitiatedByPrincipalId,N'')
                      OR ISNULL(i.CloseReasonCode,N'')<>ISNULL(d.CloseReasonCode,N'')
                      OR ISNULL(i.CloseReasonText,N'')<>ISNULL(d.CloseReasonText,N'')
                      OR ISNULL(i.CloseCorrelationId,N'')<>ISNULL(d.CloseCorrelationId,N'')
                      OR ISNULL(i.CloseBlockedCode,N'')<>ISNULL(d.CloseBlockedCode,N'')
                      OR ISNULL(i.CloseBlockedDetail,N'')<>ISNULL(d.CloseBlockedDetail,N'')
                      OR ISNULL(i.LastCloseAttemptAtUtc,CONVERT(datetime2(7),'19000101'))<>ISNULL(d.LastCloseAttemptAtUtc,CONVERT(datetime2(7),'19000101'))
                      OR i.ConsecutiveCloseFailureCount<>d.ConsecutiveCloseFailureCount
                      OR ISNULL(i.PermanentFailureCode,N'')<>ISNULL(d.PermanentFailureCode,N'')
                      OR ISNULL(i.PermanentFailureDetail,N'')<>ISNULL(d.PermanentFailureDetail,N'')
                      OR ISNULL(i.PermanentlyFailedAtUtc,CONVERT(datetime2(7),'19000101'))<>ISNULL(d.PermanentlyFailedAtUtc,CONVERT(datetime2(7),'19000101'))
                      OR ISNULL(i.PermanentFailureInitiatedByPrincipalId,N'')<>ISNULL(d.PermanentFailureInitiatedByPrincipalId,N'')
                      OR ISNULL(i.PermanentFailureReasonCode,N'')<>ISNULL(d.PermanentFailureReasonCode,N'')
                      OR ISNULL(i.PermanentFailureReasonText,N'')<>ISNULL(d.PermanentFailureReasonText,N'')
                      OR ISNULL(i.PermanentFailureCorrelationId,N'')<>ISNULL(d.PermanentFailureCorrelationId,N'')
                      OR i.CreatedAtUtc<>d.CreatedAtUtc
                  )
              )
              OR
              (
                  d.State=4
                  AND
                  (
                      i.State<>4
                      OR i.OwnerId<>d.OwnerId
                      OR i.OrganizationId<>d.OrganizationId
                      OR i.RepositoryId<>d.RepositoryId
                      OR i.PeriodFromUtc<>d.PeriodFromUtc
                      OR i.PeriodToUtc<>d.PeriodToUtc
                      OR ISNULL(i.PermanentFailureCode,N'')<>ISNULL(d.PermanentFailureCode,N'')
                      OR ISNULL(i.PermanentFailureDetail,N'')<>ISNULL(d.PermanentFailureDetail,N'')
                      OR ISNULL(i.PermanentlyFailedAtUtc,CONVERT(datetime2(7),'19000101'))<>ISNULL(d.PermanentlyFailedAtUtc,CONVERT(datetime2(7),'19000101'))
                      OR ISNULL(i.PermanentFailureInitiatedByPrincipalId,N'')<>ISNULL(d.PermanentFailureInitiatedByPrincipalId,N'')
                      OR ISNULL(i.PermanentFailureReasonCode,N'')<>ISNULL(d.PermanentFailureReasonCode,N'')
                      OR ISNULL(i.PermanentFailureReasonText,N'')<>ISNULL(d.PermanentFailureReasonText,N'')
                      OR ISNULL(i.PermanentFailureCorrelationId,N'')<>ISNULL(d.PermanentFailureCorrelationId,N'')
                      OR ISNULL(i.ClosedAtUtc,CONVERT(datetime2(7),'19000101'))<>ISNULL(d.ClosedAtUtc,CONVERT(datetime2(7),'19000101'))
                      OR ISNULL(i.CloseInitiatedByPrincipalId,N'')<>ISNULL(d.CloseInitiatedByPrincipalId,N'')
                      OR ISNULL(i.CloseReasonCode,N'')<>ISNULL(d.CloseReasonCode,N'')
                      OR ISNULL(i.CloseReasonText,N'')<>ISNULL(d.CloseReasonText,N'')
                      OR ISNULL(i.CloseCorrelationId,N'')<>ISNULL(d.CloseCorrelationId,N'')
                      OR ISNULL(i.CloseBlockedCode,N'')<>ISNULL(d.CloseBlockedCode,N'')
                      OR ISNULL(i.CloseBlockedDetail,N'')<>ISNULL(d.CloseBlockedDetail,N'')
                      OR ISNULL(i.LastCloseAttemptAtUtc,CONVERT(datetime2(7),'19000101'))<>ISNULL(d.LastCloseAttemptAtUtc,CONVERT(datetime2(7),'19000101'))
                      OR i.ConsecutiveCloseFailureCount<>d.ConsecutiveCloseFailureCount
                      OR i.CreatedAtUtc<>d.CreatedAtUtc
                  )
              )
          )
    )
        THROW 51006, 'Terminal billing periods cannot be deleted or lose immutable scope and provenance evidence.', 1;
    END;
"""

    /// Freezes complete final preview lines once their owner scope reaches a terminal billing period.
    let CreateTerminalChargePreviewLineProtectionTrigger =
        """
CREATE TRIGGER ops.TR_ops_ChargePreviewLine_TerminalBillingProtection ON ops.ChargePreviewLine
AFTER INSERT, UPDATE, DELETE AS
BEGIN
    IF EXISTS
    (
        SELECT 1
        FROM
        (
            SELECT OwnerId,OrganizationId,RepositoryId,PeriodFromUtc,PeriodToUtc FROM inserted
            UNION ALL
            SELECT OwnerId,OrganizationId,RepositoryId,PeriodFromUtc,PeriodToUtc FROM deleted
        ) line
        JOIN ops.BillingPeriod period ON period.OwnerId=line.OwnerId AND period.OrganizationId=line.OrganizationId AND period.RepositoryId=line.RepositoryId
          AND period.PeriodFromUtc=line.PeriodFromUtc AND period.PeriodToUtc=line.PeriodToUtc AND period.State IN (2,3,4)
    )
        THROW 51012, 'Final charge preview lines are immutable after billing close.', 1;
END;
"""

    /// Freezes the final preview digests and commit time after the related billing period becomes terminal.
    let CreateTerminalChargePreviewFreshnessProtectionTrigger =
        """
CREATE TRIGGER ops.TR_ops_ChargePreviewFreshness_TerminalBillingProtection ON ops.ChargePreviewFreshness
AFTER INSERT, UPDATE, DELETE AS
BEGIN
    IF EXISTS
    (
        SELECT 1
        FROM
        (
            SELECT BillingPeriodId FROM inserted
            UNION ALL
            SELECT BillingPeriodId FROM deleted
        ) freshness
        JOIN ops.BillingPeriod period ON period.BillingPeriodId=freshness.BillingPeriodId AND period.State IN (2,3,4)
    )
        THROW 51013, 'Final charge preview freshness is immutable after billing close.', 1;
END;
"""

    /// Rejects every historical plan, mapping, assignment, and rate mutation that could rewrite closed owner-month pricing.
    let CreateHistoricalPricingProtectionTriggers =
        """
CREATE TRIGGER ops.TR_ops_PricingAssignment_HistoricalProtection ON ops.PricingAssignment AFTER INSERT, UPDATE, DELETE AS
BEGIN
    IF EXISTS (
        SELECT 1 FROM (SELECT OwnerId,OrganizationId,RepositoryId,EffectiveFromUtc,EffectiveToUtc FROM inserted UNION ALL SELECT OwnerId,OrganizationId,RepositoryId,EffectiveFromUtc,EffectiveToUtc FROM deleted) d
        JOIN ops.BillingPeriod p ON p.OwnerId=d.OwnerId AND p.OrganizationId=d.OrganizationId AND p.RepositoryId=d.RepositoryId
          AND p.State IN (2,3,4) AND d.EffectiveFromUtc < p.PeriodToUtc AND (d.EffectiveToUtc IS NULL OR d.EffectiveToUtc > p.PeriodFromUtc))
        THROW 51001, 'Pricing assignment overlaps immutable billing history.', 1;
END;
GO
CREATE TRIGGER ops.TR_ops_PricingPlan_HistoricalProtection ON ops.PricingPlan AFTER INSERT, UPDATE, DELETE AS
BEGIN
    IF EXISTS (
        SELECT 1
        FROM (SELECT PricingPlanId,EffectiveFromUtc,EffectiveToUtc FROM inserted UNION ALL SELECT PricingPlanId,EffectiveFromUtc,EffectiveToUtc FROM deleted) d
        JOIN ops.PricingAssignment a ON a.PricingPlanId=d.PricingPlanId
        JOIN ops.BillingPeriod p ON p.OwnerId=a.OwnerId AND p.OrganizationId=a.OrganizationId AND p.RepositoryId=a.RepositoryId
          AND p.State IN (2,3,4) AND d.EffectiveFromUtc < p.PeriodToUtc AND (d.EffectiveToUtc IS NULL OR d.EffectiveToUtc > p.PeriodFromUtc)
        WHERE a.EffectiveFromUtc < p.PeriodToUtc AND (a.EffectiveToUtc IS NULL OR a.EffectiveToUtc > p.PeriodFromUtc))
        THROW 51002, 'Pricing plan overlaps immutable billing history.', 1;
END;
GO
CREATE TRIGGER ops.TR_ops_BillableUsageKindMapping_HistoricalProtection ON ops.BillableUsageKindMapping AFTER INSERT, UPDATE, DELETE AS
BEGIN
    IF EXISTS (
        SELECT 1
        FROM (SELECT FactKind,EffectiveFromUtc,EffectiveToUtc FROM inserted UNION ALL SELECT FactKind,EffectiveFromUtc,EffectiveToUtc FROM deleted) d
        JOIN ops.RawUsageFact f ON f.FactKind=d.FactKind
        JOIN ops.BillingPeriod p ON p.OwnerId=f.OwnerId AND p.OrganizationId=f.OrganizationId AND p.RepositoryId=f.RepositoryId
          AND p.State IN (2,3,4) AND f.ObservedAtUtc>=p.PeriodFromUtc AND f.ObservedAtUtc<p.PeriodToUtc
        WHERE d.EffectiveFromUtc < p.PeriodToUtc AND (d.EffectiveToUtc IS NULL OR d.EffectiveToUtc > p.PeriodFromUtc))
        THROW 51003, 'Billable usage-kind mapping overlaps immutable billing history.', 1;
END;
GO
CREATE TRIGGER ops.TR_ops_PricingRate_HistoricalProtection ON ops.PricingRate AFTER INSERT, UPDATE, DELETE AS
BEGIN
    IF EXISTS (
        SELECT 1 FROM (SELECT PricingPlanId,EffectiveFromUtc,EffectiveToUtc FROM inserted UNION ALL SELECT PricingPlanId,EffectiveFromUtc,EffectiveToUtc FROM deleted) d
        JOIN ops.PricingAssignment a ON a.PricingPlanId=d.PricingPlanId
        JOIN ops.BillingPeriod p ON p.OwnerId=a.OwnerId AND p.OrganizationId=a.OrganizationId AND p.RepositoryId=a.RepositoryId
          AND p.State IN (2,3,4) AND d.EffectiveFromUtc < p.PeriodToUtc AND (d.EffectiveToUtc IS NULL OR d.EffectiveToUtc > p.PeriodFromUtc)
        WHERE a.EffectiveFromUtc < p.PeriodToUtc AND (a.EffectiveToUtc IS NULL OR a.EffectiveToUtc > p.PeriodFromUtc))
        THROW 51004, 'Pricing rate overlaps immutable billing history.', 1;
END;
GO
"""
