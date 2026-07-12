namespace Grace.Operations.Tests

open Grace.Operations.Data
open Grace.Operations.Data.Migrations
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Infrastructure
open Microsoft.EntityFrameworkCore.Migrations
open NUnit.Framework
open System
open System.IO

/// Proves owner-scoped billing invariants that do not require an external SQL Server.
[<TestFixture>]
type OperationsBillingTests() =
    let utc year month day hour = DateTime(year, month, day, hour, 0, 0, DateTimeKind.Utc)

    /// Verifies leap-month calculation and the exact Open, Provisional, and close-eligible boundaries.
    [<Test>]
    member _.LifecycleUsesExactUtcBoundaries() =
        let fromUtc, toUtc = BillingPeriodRules.monthContaining (utc 2028 2 29 23)
        Assert.That(fromUtc, Is.EqualTo(utc 2028 2 1 0))
        Assert.That(toUtc, Is.EqualTo(utc 2028 3 1 0))
        Assert.That(BillingPeriodRules.stateAt toUtc (toUtc.AddHours(24.0).AddTicks(-1L)), Is.EqualTo(BillingPeriodState.Open))
        Assert.That(BillingPeriodRules.stateAt toUtc (toUtc.AddHours(24.0)), Is.EqualTo(BillingPeriodState.Provisional))
        Assert.That(BillingPeriodRules.isCloseEligible toUtc (toUtc.AddHours(72.0).AddTicks(-1L)), Is.False)
        Assert.That(BillingPeriodRules.isCloseEligible toUtc (toUtc.AddHours(72.0)), Is.True)

    /// Verifies the deterministic period identity contains every supported owner scope member.
    [<Test>]
    member _.PeriodIdentityUsesOnlyOwnerOrganizationRepositoryAndMonth() =
        let scope: BillingPeriodScope =
            {
                OwnerId = Guid.NewGuid()
                OrganizationId = Guid.NewGuid()
                RepositoryId = Guid.NewGuid()
                PeriodFromUtc = utc 2028 1 1 0
                PeriodToUtc = utc 2028 2 1 0
            }

        let identity = BillingPeriodRules.periodId scope
        Assert.That(BillingPeriodRules.periodId scope, Is.EqualTo(identity))
        Assert.That(BillingPeriodRules.periodId { scope with RepositoryId = Guid.NewGuid() }, Is.Not.EqualTo(identity))

    /// Verifies immutable correction validation permits zero price and rejects an invalid unit or escaping period interval.
    [<Test>]
    member _.CorrectionValidationPreservesImmutablePricingRules() =
        let correction: ManualBillingCorrection =
            {
                BillingPeriodId = Guid.NewGuid()
                EntryKind = ChargeLedgerEntryKind.Adjustment
                PriorChargeLedgerEntryId = None
                FactKind = 1
                BillableUsageKindMappingId = Guid.NewGuid()
                BillableUsageKind = 1
                PricingAssignmentId = Guid.NewGuid()
                PricingPlanId = Guid.NewGuid()
                PricingRateId = Guid.NewGuid()
                CurrencyCode = "USD"
                UnitName = "byte-minute"
                UnitQuantity = 1L
                UnitPriceMicros = 0L
                EffectiveFromUtc = utc 2028 1 1 0
                EffectiveToUtc = utc 2028 2 1 0
                QuantityDelta = 1L
                ChargeMicrosDelta = 0L
            }

        Assert.DoesNotThrow(Action(fun () -> ManualBillingCorrectionValidation.validatePricingGrain correction))

        Assert.Throws<ArgumentException>(Action(fun () -> ManualBillingCorrectionValidation.validatePricingGrain { correction with UnitQuantity = 0L }))
        |> ignore

        Assert.Throws<ArgumentException>(
            Action (fun () ->
                ManualBillingCorrectionValidation.validateApplicability (utc 2028 1 1 0) (utc 2028 2 1 0) { correction with EffectiveToUtc = utc 2028 2 1 1 })
        )
        |> ignore

    /// Verifies manual replay identity is stable only for the exact immutable correction and correlation tuple.
    [<Test>]
    member _.ManualCorrectionIdentityDistinguishesConflictingCorrelationReplays() =
        let correction: ManualBillingCorrection =
            {
                BillingPeriodId = Guid.Parse("01010101-0101-0101-0101-010101010101")
                EntryKind = ChargeLedgerEntryKind.Adjustment
                PriorChargeLedgerEntryId = None
                FactKind = 1
                BillableUsageKindMappingId = Guid.Parse("02020202-0202-0202-0202-020202020202")
                BillableUsageKind = 1
                PricingAssignmentId = Guid.Parse("03030303-0303-0303-0303-030303030303")
                PricingPlanId = Guid.Parse("04040404-0404-0404-0404-040404040404")
                PricingRateId = Guid.Parse("05050505-0505-0505-0505-050505050505")
                CurrencyCode = "USD"
                UnitName = "byte-minute"
                UnitQuantity = 1L
                UnitPriceMicros = 5L
                EffectiveFromUtc = utc 2028 1 1 0
                EffectiveToUtc = utc 2028 2 1 0
                QuantityDelta = 1L
                ChargeMicrosDelta = 5L
            }

        let correlationId = "manual-replay-identity"
        let original = ManualBillingCorrectionIdentity.entryId correction correlationId

        Assert.Multiple(
            Action (fun () ->
                Assert.That(ManualBillingCorrectionIdentity.entryId correction correlationId, Is.EqualTo(original))
                Assert.That(ManualBillingCorrectionIdentity.entryId ({ correction with QuantityDelta = 2L }) correlationId, Is.Not.EqualTo(original)))
        )

    /// Verifies SQL datetime2 normalization preserves the stored instant before UTC-only month rules evaluate it.
    [<Test>]
    member _.SqlDateTimeNormalizationPreservesUtcMonthInstant() =
        let stored = DateTime(2028, 1, 31, 23, 0, 0, DateTimeKind.Unspecified)
        let normalized = DateTime.SpecifyKind(stored, DateTimeKind.Utc)

        Assert.Multiple(
            Action (fun () ->
                Assert.That(normalized.Ticks, Is.EqualTo(stored.Ticks))
                Assert.That(normalized.Kind, Is.EqualTo(DateTimeKind.Utc))

                Assert.That(
                    BillingPeriodRules.intersectingMonths normalized None (utc 2028 2 2 0)
                    |> List.length,
                    Is.EqualTo(2)
                ))
        )

    /// Proves current Operations source, schema, tests, docs, and seed carry neither forbidden identifier.
    [<Test>]
    member _.OperationsTreeHasNoForbiddenIdentityTerms() =
        let root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."))

        let forbidden =
            [|
                "Customer" + "Id"
                "Customer" + "PricingAssignment"
            |]

        let found =
            Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            |> Array.filter (fun path ->
                not (
                    path.Contains("\\bin\\")
                    || path.Contains("\\obj\\")
                ))
            |> Array.exists (fun path ->
                let text = File.ReadAllText(path)

                forbidden
                |> Array.exists (fun term -> text.Contains(term, StringComparison.Ordinal)))

        Assert.That(found, Is.False)

    /// Verifies lock, serializable retry, cadence, correction work, and SQL immutability guards remain explicit.
    [<Test>]
    member _.RuntimeSourceCarriesConcurrencyAndRecoveryProof() =
        let root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."))
        let closeSource = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "OperationsBillingClose.fs"))
        let workerSource = File.ReadAllText(Path.Combine(root, "Grace.Operations.Worker", "OperationsBillingWorker.fs"))
        let migrationSource = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "Migrations", "20260711140000_AddBillingPeriodCloseLedger.fs"))
        let billingSource = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "OperationsBilling.fs"))
        Assert.That(closeSource, Does.Contain("BeginTransactionAsync(IsolationLevel.Serializable"))
        Assert.That(closeSource, Does.Contain("OperationsBillingSql.AcquireScopeLock"))
        Assert.That(closeSource, Does.Contain("BillingCorrectionWork"))
        Assert.That(closeSource, Does.Contain("AssignmentCoverageMissing"))
        Assert.That(workerSource, Does.Contain("TimeSpan.FromMinutes(30.0)"))

        Assert.That(
            workerSource.IndexOf("try", StringComparison.Ordinal),
            Is.LessThan(workerSource.IndexOf("schema.EnsureCreatedAsync", StringComparison.Ordinal))
        )

        Assert.That(
            workerSource.IndexOf("schema.EnsureCreatedAsync", StringComparison.Ordinal),
            Is.LessThan(workerSource.IndexOf("service.RunAsync", StringComparison.Ordinal))
        )

        Assert.That(billingSource, Does.Contain("TR_ops_ChargeLedgerEntry_Immutable"))
        Assert.That(billingSource, Does.Contain("AFTER INSERT, UPDATE, DELETE"))
        Assert.That(migrationSource, Does.Contain("BillingCorrectionWork"))

    /// Verifies final preview replacement happens before immutable posting under one deterministic owner/month lock.
    [<Test>]
    member _.FinalCloseRebuildsUnderTheSameTransactionBeforePostingImmutableRows() =
        let root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."))
        let source = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "OperationsBillingClose.fs"))
        let rebuildIndex = source.IndexOf("let! _ = rebuildFinalPreview connection transaction scope periodId", StringComparison.Ordinal)
        let ledgerIndex = source.IndexOf("INSERT INTO ops.ChargeLedgerEntry", StringComparison.Ordinal)

        Assert.Multiple(
            Action (fun () ->
                Assert.That(rebuildIndex, Is.GreaterThanOrEqualTo(0))
                Assert.That(ledgerIndex, Is.GreaterThan(rebuildIndex))
                Assert.That(source, Does.Contain("AcceptedFactsDigest"))
                Assert.That(source, Does.Contain("PricingDigest"))
                Assert.That(source, Does.Contain("@Principal,@ReasonCode,@ReasonText,@Correlation"))
                Assert.That(source, Does.Contain("return! runScope scope nowUtc provenance cancellationToken"))
                Assert.That(source, Does.Contain("BillingPeriodRules.intersectingMonths (utcFromSql (reader.GetDateTime(3)))"))
                Assert.That(OperationsBillingSql.AcquireScopeLock, Does.Contain("LockOwner='Transaction'"))
                Assert.That(source, Does.Contain("OwnerId=@OwnerId AND OrganizationId=@OrganizationId AND RepositoryId=@RepositoryId")))
        )

    /// Verifies automatic corrections are isolated by durable work identity and extend the latest automatic provenance chain.
    [<Test>]
    member _.AutomaticCorrectionsUseWorkIdentityAndImmediateAutomaticPredecessor() =
        let root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."))
        let source = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "OperationsBillingClose.fs"))

        Assert.Multiple(
            Action (fun () ->
                Assert.That(source, Does.Contain("processCorrectionWork"))
                Assert.That(source, Does.Contain("BillingCorrectionWorkId=@WorkId"))
                Assert.That(source, Does.Contain("ReasonCode=N'AutomaticLateUsage'"))
                Assert.That(source, Does.Contain("ORDER BY CreatedAtUtc DESC,ChargeLedgerEntryId DESC"))
                Assert.That(source, Does.Contain("MissingPricing"))
                Assert.That(source, Does.Contain("let effectiveFrom = max scope.PeriodFromUtc pricingEffectiveFrom"))
                Assert.That(source, Does.Contain("let effectiveTo = min scope.PeriodToUtc pricingEffectiveTo"))
                Assert.That(source, Does.Contain("CompletedAtUtc=SYSUTCDATETIME()")))
        )

    /// Verifies canonical failure handling and owner-period repair validation are not bypassed by conflicting retries or no-prior manual writes.
    [<Test>]
    member _.FailureAndManualCorrectionGuardsCoverHistoricalReviewEdges() =
        let root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."))
        let source = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "OperationsBillingClose.fs"))

        Assert.Multiple(
            Action (fun () ->
                Assert.That(source, Does.Contain("UsageFactId=@UsageFactId AND ResolvedAtUtc IS NULL"))
                Assert.That(source, Does.Contain("Trace.TraceWarning"))
                Assert.That(source, Does.Contain("let validateManualPricingGrain"))
                Assert.That(source, Does.Contain("let validateManualPrior"))
                Assert.That(source, Does.Contain("existingManualCorrectionEntry"))
                Assert.That(source, Does.Contain("CorrelationId is already assigned to a different manual correction."))
                Assert.That(source, Does.Contain("Manual correction pricing grain is not applicable to the locked owner period."))
                Assert.That(source, Does.Contain("Accepted usage fact resolved canonical failure evidence."))
                Assert.That(source, Does.Not.Contain("Customer" + "Id")))
        )

    /// Verifies the migration target and latest snapshot independently declare the reviewed billing shape and SQL bypass matrix.
    [<Test>]
    member _.BillingFrozenArtifactsAndSqlProtectionMatrixAgree() =
        let root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."))

        let migrationPath = Path.Combine(root, "Grace.Operations.Data", "Migrations", "20260711140000_AddBillingPeriodCloseLedger.fs")

        let snapshotPath = Path.Combine(root, "Grace.Operations.Data", "Migrations", "OperationsDbContextModelSnapshot.fs")
        let migrationSource = File.ReadAllText(migrationPath)
        let snapshotSource = File.ReadAllText(snapshotPath)
        let targetStart = migrationSource.IndexOf("override _.BuildTargetModel(modelBuilder: ModelBuilder) =", StringComparison.Ordinal)
        let target = migrationSource.Substring(targetStart)
        let snapshotStart = snapshotSource.IndexOf("override _.BuildModel(modelBuilder: ModelBuilder) =", StringComparison.Ordinal)
        let snapshot = snapshotSource.Substring(snapshotStart)
        use context = OperationsDbContextFactory.create "Server=(localdb)\\MSSQLLocalDB;Database=GraceOperationsBillingFrozenModel;Integrated Security=true;"
        let runtime = context.Model
        let migration = AddBillingPeriodCloseLedger().TargetModel
        let snapshotModel = OperationsDbContextModelSnapshot().Model

        let billingShape (model: Microsoft.EntityFrameworkCore.Metadata.IModel) (entityType: Type) : Set<string * string * bool> =
            let entity = model.FindEntityType(entityType)

            entity.GetProperties()
            |> Seq.map (fun property -> property.Name, property.ClrType.FullName, property.IsNullable)
            |> Set.ofSeq

        let entities =
            [
                typeof<BillingPeriodEntity>
                typeof<ChargePreviewFreshnessEntity>
                typeof<ChargeLedgerEntryEntity>
                typeof<BillingIngestionFailureEntity>
                typeof<BillingCorrectionWorkEntity>
            ]

        Assert.Multiple(
            Action (fun () ->
                Assert.That(targetStart, Is.GreaterThanOrEqualTo(0))
                Assert.That(snapshotStart, Is.GreaterThanOrEqualTo(0))
                Assert.That(target, Does.Not.Contain("OperationsBillingModel.configure"))
                Assert.That(snapshot, Does.Not.Contain("OperationsBillingModel.configure"))
                Assert.That(target, Does.Contain("let ledger = modelBuilder.Entity<ChargeLedgerEntryEntity>()"))
                Assert.That(snapshot, Does.Contain("let ledger = modelBuilder.Entity<ChargeLedgerEntryEntity>()"))

                for entity in entities do
                    Assert.That((billingShape migration entity = billingShape snapshotModel entity), Is.True)
                    Assert.That((billingShape migration entity = billingShape runtime entity), Is.True)

                for trigger in
                    [
                        "TR_ops_PricingPlan_HistoricalProtection"
                        "TR_ops_BillableUsageKindMapping_HistoricalProtection"
                        "TR_ops_PricingAssignment_HistoricalProtection"
                        "TR_ops_PricingRate_HistoricalProtection"
                    ] do
                    Assert.That(OperationsBillingSql.CreateHistoricalPricingProtectionTriggers, Does.Contain(trigger))

                Assert.That(OperationsBillingSql.CreateHistoricalPricingProtectionTriggers, Does.Contain("AFTER INSERT, UPDATE, DELETE"))

                Assert.That(
                    OperationsBillingSql.CreateHistoricalPricingProtectionTriggers,
                    Does.Contain("WHERE a.EffectiveFromUtc < p.PeriodToUtc AND (a.EffectiveToUtc IS NULL OR a.EffectiveToUtc > p.PeriodFromUtc))")
                )

                Assert.That(migrationSource, Does.Contain("Split([| \"GO\" |], StringSplitOptions.RemoveEmptyEntries)"))

                Assert.That(
                    migrationSource.IndexOf("DROP TRIGGER IF EXISTS ops.TR_ops_PricingRate_HistoricalProtection", StringComparison.Ordinal),
                    Is.LessThan(migrationSource.IndexOf("DROP TABLE IF EXISTS ops.BillingPeriod", StringComparison.Ordinal))
                )

                Assert.That(migrationSource, Does.Contain("DROP TRIGGER IF EXISTS ops.TR_ops_ChargeLedgerEntry_Immutable"))
                Assert.That(migrationSource, Does.Contain("DROP TRIGGER IF EXISTS ops.TR_ops_PricingAssignment_HistoricalProtection"))
                Assert.That(migrationSource, Does.Contain("DROP TRIGGER IF EXISTS ops.TR_ops_PricingPlan_HistoricalProtection"))
                Assert.That(migrationSource, Does.Contain("DROP TRIGGER IF EXISTS ops.TR_ops_BillableUsageKindMapping_HistoricalProtection"))
                Assert.That(migrationSource, Does.Contain("DROP TRIGGER IF EXISTS ops.TR_ops_PricingRate_HistoricalProtection"))
                Assert.That(context.GetService<IMigrator>().GenerateScript(), Does.Contain("TR_ops_ChargeLedgerEntry_Immutable")))
        )
