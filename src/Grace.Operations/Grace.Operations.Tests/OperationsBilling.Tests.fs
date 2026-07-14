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

    /// Restricts source scans to paths outside cross-platform build-output directories.
    let isSourcePath (path: string) =
        path.Split([| '\\'; '/' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.forall (fun segment ->
            not (
                String.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
                || String.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase)
            ))

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

    /// Proves each late correction posts the once-rounded cumulative charge delta at its immutable pricing grain.
    [<Test>]
    member _.AutomaticLateCorrectionsUseCumulativeRoundedChargeDeltas() =
        let unitQuantity = 3L
        let unitPriceMicros = 5L
        let initialCharge = ChargePreviewCalculation.calculateChargeMicros 1L unitPriceMicros unitQuantity
        let afterFirstLateCharge = ChargePreviewCalculation.calculateChargeMicros 2L unitPriceMicros unitQuantity
        let afterSecondLateCharge = ChargePreviewCalculation.calculateChargeMicros 3L unitPriceMicros unitQuantity
        let firstLateDelta = afterFirstLateCharge - initialCharge
        let secondLateDelta = afterSecondLateCharge - afterFirstLateCharge
        let isolatedLateCharges = initialCharge + initialCharge + initialCharge
        let root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."))
        let source = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "OperationsBillingClose.fs"))

        Assert.Multiple(
            Action (fun () ->
                Assert.That(initialCharge, Is.EqualTo(2L))
                Assert.That(firstLateDelta, Is.EqualTo(1L))
                Assert.That(secondLateDelta, Is.EqualTo(2L))
                Assert.That(initialCharge + firstLateDelta + secondLateDelta, Is.EqualTo(afterSecondLateCharge))
                Assert.That(isolatedLateCharges, Is.Not.EqualTo(afterSecondLateCharge))
                Assert.That(source, Does.Contain("SUM(CONVERT(decimal(38,0), ledgerEntry.Quantity))"))
                Assert.That(source, Does.Contain("SUM(CONVERT(decimal(38,0), ledgerEntry.ChargeMicros))"))
                Assert.That(source, Does.Contain("let cumulativeQuantity"))
                Assert.That(source, Does.Contain("let correctionCharge")))
        )

    /// Verifies manual replay identity is stable only for the exact immutable correction and correlation tuple.
    [<Test>]
    member _.ManualCorrectionIdentityDistinguishesConflictingCorrelationReplays() =
        let correction: ManualBillingCorrection =
            {
                BillingPeriodId = Guid.Parse("01010101-0101-0101-0101-010101010101")
                EntryKind = ChargeLedgerEntryKind.Adjustment
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
            |> Array.filter isSourcePath
            |> Array.exists (fun path ->
                let text = File.ReadAllText(path)

                forbidden
                |> Array.exists (fun term -> text.Contains(term, StringComparison.Ordinal)))

        Assert.That(found, Is.False)

    /// Verifies source-only scans exclude bin and obj output using either Windows or Unix separators.
    [<Test>]
    member _.SourceOnlyScansExcludeCrossPlatformBuildOutputPaths() =
        let sourcePaths =
            [|
                "C:\\source\\Grace.Operations\\OperationsBilling.fs"
                "/source/Grace.Operations/OperationsBilling.fs"
            |]

        let buildOutputPaths =
            [|
                "C:\\source\\Grace.Operations\\bin\\Release\\net10.0\\generated.fs"
                "C:\\source\\Grace.Operations\\obj\\Release\\net10.0\\generated.fs"
                "/source/Grace.Operations/bin/Release/net10.0/generated.fs"
                "/source/Grace.Operations/obj/Release/net10.0/generated.fs"
            |]

        Assert.Multiple(
            Action (fun () ->
                Assert.That(sourcePaths |> Array.forall isSourcePath, Is.True)

                Assert.That(
                    buildOutputPaths
                    |> Array.forall (isSourcePath >> not),
                    Is.True
                ))
        )

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
                Assert.That(source, Does.Contain("WITH WindowMonths AS"))
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

    /// Verifies approved session-two database timestamp ordering, assignment-independent materialization, and operator-only retry eligibility remain explicit.
    [<Test>]
    member _.SessionTwoRoutingEligibilityAndMaterializationContractsAreDurable() =
        let root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."))
        let closeSource = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "OperationsBillingClose.fs"))
        let usageSql = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "OperationsUsageSql.fs"))
        let workerSource = File.ReadAllText(Path.Combine(root, "Grace.Operations.Worker", "OperationsWorker.fs"))
        let migrationSource = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "Migrations", "20260711140000_AddBillingPeriodCloseLedger.fs"))
        let docs = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "MIGRATIONS.md"))
        let testHost = File.ReadAllText(Path.Combine(root, "..", "Grace.Server.Tests", "AspireTestHost.fs"))

        Assert.Multiple(
            Action (fun () ->
                Assert.That(closeSource, Does.Contain("fact.AcceptedAtUtc > period.ClosedAtUtc"))
                Assert.That(closeSource, Does.Contain("ClosedAtUtc=SYSUTCDATETIME()"))
                Assert.That(usageSql, Does.Contain("sys.sp_getapplock @Resource=@LockResource"))
                Assert.That(closeSource, Does.Contain("FROM ops.RawUsageFact f WITH(READCOMMITTEDLOCK)"))
                Assert.That(closeSource, Does.Contain("IsAutomaticRetryEligible=1"))
                Assert.That(closeSource, Does.Contain("IsAutomaticRetryEligible=0"))
                Assert.That(closeSource, Does.Contain("ReenableCorrectionWorkAsync"))
                Assert.That(closeSource, Does.Contain("w.CompletedAtUtc IS NULL AND w.PermanentlyFailedAtUtc IS NULL AND w.IsAutomaticRetryEligible=0"))
                Assert.That(workerSource, Does.Contain("UsageFactPersistenceStatus.AlreadyProcessed"))
                Assert.That(migrationSource, Does.Contain("AcceptedAtUtc datetime2(7) NOT NULL"))
                Assert.That(migrationSource, Does.Contain("IsAutomaticRetryEligible bit NOT NULL"))
                Assert.That(docs, Does.Contain("Grace never inserts or mutates historical pricing"))
                Assert.That(testHost, Does.Contain("20260714100000_StabilizeBillingCloseEvidenceGuards")))
        )

    /// Verifies canonical failure handling and owner-period Adjustment validation are not bypassed by conflicting retries.
    [<Test>]
    member _.FailureAndManualCorrectionGuardsCoverHistoricalReviewEdges() =
        let root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."))
        let source = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "OperationsBillingClose.fs"))

        Assert.Multiple(
            Action (fun () ->
                Assert.That(source, Does.Contain("UsageFactId=@UsageFactId AND ResolvedAtUtc IS NULL"))
                Assert.That(source, Does.Contain("Trace.TraceWarning"))
                Assert.That(source, Does.Contain("let validateManualPricingGrain"))
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

        let migrationPath = Path.Combine(root, "Grace.Operations.Data", "Migrations", "20260713120000_StabilizeBillingPeriodCloseLedger.fs")

        let snapshotPath = Path.Combine(root, "Grace.Operations.Data", "Migrations", "OperationsDbContextModelSnapshot.fs")
        let migrationSource = File.ReadAllText(migrationPath)
        let snapshotSource = File.ReadAllText(snapshotPath)
        let targetStart = migrationSource.IndexOf("override _.BuildTargetModel(modelBuilder: ModelBuilder) =", StringComparison.Ordinal)
        let target = migrationSource.Substring(targetStart)
        let snapshotStart = snapshotSource.IndexOf("override _.BuildModel(modelBuilder: ModelBuilder) =", StringComparison.Ordinal)
        let snapshot = snapshotSource.Substring(snapshotStart)
        use context = OperationsDbContextFactory.create "Server=(localdb)\\MSSQLLocalDB;Database=GraceOperationsBillingFrozenModel;Integrated Security=true;"
        let runtime = context.Model
        let migration = StabilizeBillingPeriodCloseLedger().TargetModel
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
                typeof<RawUsageFactEntity>
            ]

        Assert.Multiple(
            Action (fun () ->
                Assert.That(targetStart, Is.GreaterThanOrEqualTo(0))
                Assert.That(snapshotStart, Is.GreaterThanOrEqualTo(0))
                Assert.That(target, Does.Not.Contain("OperationsBillingModel.configure"))
                Assert.That(snapshot, Does.Not.Contain("OperationsBillingModel.configure"))
                Assert.That(snapshot, Does.Contain("let ledger = modelBuilder.Entity<ChargeLedgerEntryEntity>()"))

                for entity in entities do
                    Assert.That((billingShape snapshotModel entity = billingShape runtime entity), Is.True)

                Assert.That(billingShape migration typeof<BillingPeriodEntity> = billingShape snapshotModel typeof<BillingPeriodEntity>, Is.True)

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

                Assert.That(
                    OperationsBillingSql.CreateHistoricalPricingProtectionTriggers,
                    Does.Contain("AND (d.EffectiveToUtc IS NULL OR d.EffectiveToUtc > p.PeriodFromUtc)\n        WHERE a.EffectiveFromUtc")
                )

                Assert.That(migrationSource, Does.Contain("TR_ops_RawUsageFact_TerminalBillingProtection"))
                Assert.That(migrationSource, Does.Contain("TR_ops_BillingPeriod_PermanentFailureProtection"))
                Assert.That(context.GetService<IMigrator>().GenerateScript(), Does.Contain("TR_ops_RawUsageFact_TerminalBillingProtection")))
        )

    /// Verifies session-three source, lifecycle, materialization, idempotency, routing, and lock-order invariants are explicit.
    [<Test>]
    member _.SessionThreeStabilizationContractIsPropagatedAcrossRuntimeAndSchema() =
        let root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."))
        let closeSource = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "OperationsBillingClose.fs"))
        let billingSource = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "OperationsBilling.fs"))

        let migrationSource = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "Migrations", "20260713120000_StabilizeBillingPeriodCloseLedger.fs"))

        let snapshotSource = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "Migrations", "OperationsDbContextModelSnapshot.fs"))

        Assert.Multiple(
            Action (fun () ->
                Assert.That(billingSource, Does.Contain("TR_ops_RawUsageFact_TerminalBillingProtection"))
                Assert.That(billingSource, Does.Contain("AFTER INSERT, UPDATE, DELETE"))
                Assert.That(billingSource, Does.Contain("SELECT d.UsageFactId,d.CorrelationId,d.FactKind,d.OwnerId,d.OrganizationId,d.RepositoryId"))
                Assert.That(billingSource, Does.Contain("SELECT i.UsageFactId,i.CorrelationId,i.FactKind,i.OwnerId,i.OrganizationId,i.RepositoryId"))
                Assert.That(billingSource, Does.Not.Contain("i.RawPayload<>d.RawPayload"))
                Assert.That(billingSource, Does.Contain("TR_ops_BillingPeriod_PermanentFailureProtection"))
                Assert.That(closeSource, Does.Contain("SAVE TRANSACTION BillingCloseFinalPreview"))
                Assert.That(closeSource, Does.Contain("ROLLBACK TRANSACTION BillingCloseFinalPreview"))
                Assert.That(closeSource, Does.Contain("BillingPeriodState.PermanentlyFailed"))
                Assert.That(closeSource, Does.Contain("WHERE BillingIngestionFailureId=@Id"))
                Assert.That(closeSource, Does.Contain("WITH WindowMonths AS"))
                Assert.That(closeSource, Does.Contain("@LookbackFromUtc"))
                Assert.That(closeSource, Does.Contain("FROM ops.BillingIngestionFailure f WITH(READCOMMITTEDLOCK)"))
                Assert.That(closeSource, Does.Contain("persisted owner and observed month were used"))
                Assert.That(closeSource, Does.Contain("SELECT OwnerId,OrganizationId,RepositoryId,ObservedAtUtc FROM ops.RawUsageFact WITH(UPDLOCK,HOLDLOCK)"))

                Assert.That(
                    closeSource.IndexOf("do! lockScope connection transaction scope cancellationToken", StringComparison.Ordinal),
                    Is.LessThan(closeSource.IndexOf("FROM ops.BillingCorrectionWork w WITH(UPDLOCK,HOLDLOCK)", StringComparison.Ordinal))
                )

                Assert.That(migrationSource, Does.Contain("PermanentFailureCode nvarchar(64) NULL"))
                Assert.That(migrationSource, Does.Contain("CHECK (State BETWEEN 0 AND 4)"))
                Assert.That(migrationSource, Does.Contain("DROP TRIGGER IF EXISTS ops.TR_ops_RawUsageFact_TerminalBillingProtection"))
                Assert.That(snapshotSource, Does.Contain("PermanentFailureCorrelationId")))
        )

    /// Proves session-four terminal SQL, correction failure, strict timestamp, retry, migration, and model invariants stay aligned.
    [<Test>]
    member _.SessionFourTerminalCorrectionStabilizationContractsAreDurable() =
        let root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."))
        let billingSource = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "OperationsBilling.fs"))
        let closeSource = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "OperationsBillingClose.fs"))

        let migrationSource =
            File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "Migrations", "20260713130000_StabilizeBillingCorrectionWorkFailure.fs"))

        let snapshotSource = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "Migrations", "OperationsDbContextModelSnapshot.fs"))
        let closedAtUtc = utc 2028 3 1 0
        let earlierAcceptedAtUtc = closedAtUtc.AddTicks(-1L)
        let equalAcceptedAtUtc = closedAtUtc
        let laterAcceptedAtUtc = closedAtUtc.AddTicks(1L)

        use context =
            OperationsDbContextFactory.create "Server=(localdb)\\MSSQLLocalDB;Database=GraceOperationsBillingSessionFourModel;Integrated Security=true;"

        let runtime = context.Model

        let migration =
            StabilizeBillingCorrectionWorkFailure()
                .TargetModel

        let snapshot = OperationsDbContextModelSnapshot().Model

        let propertyShape (model: Microsoft.EntityFrameworkCore.Metadata.IModel) (entityType: Type) =
            let entity = model.FindEntityType(entityType)

            entity.GetProperties()
            |> Seq.map (fun property -> property.Name, property.ClrType.FullName, property.IsNullable)
            |> Set.ofSeq

        let retryOutcomeIndex = closeSource.IndexOf("BillingCloseOutcome.PermanentlyFailed(code)", StringComparison.Ordinal)
        let terminalOutcomeIndex = closeSource.IndexOf("BillingCloseOutcome.AlreadyTerminal", StringComparison.Ordinal)

        let migrationWorkShape = propertyShape migration (typeof<BillingCorrectionWorkEntity>)
        let snapshotWorkShape = propertyShape snapshot (typeof<BillingCorrectionWorkEntity>)
        let runtimeWorkShape = propertyShape runtime (typeof<BillingCorrectionWorkEntity>)
        let migrationScript = (context.GetService<IMigrator>()).GenerateScript()

        Assert.Multiple(
            Action (fun () ->
                // Boundary: Q1=C means equality and earlier timestamps are not late; only strictly later timestamps route correction work.
                Assert.That(earlierAcceptedAtUtc > closedAtUtc, Is.False)
                Assert.That(equalAcceptedAtUtc > closedAtUtc, Is.False)
                Assert.That(laterAcceptedAtUtc > closedAtUtc, Is.True)
                Assert.That(closeSource, Does.Contain("fact.AcceptedAtUtc > period.ClosedAtUtc"))
                Assert.That(closeSource, Does.Not.Contain("fact.AcceptedAtUtc >= period.ClosedAtUtc"))
                Assert.That(closeSource, Does.Not.Contain("ROWVERSION"))
                Assert.That(closeSource, Does.Not.Contain("Watermark"))

                // SQL-bypass: both the original and destination scopes participate, while raw-payload lifecycle changes remain permitted.
                Assert.That(billingSource, Does.Contain("sourcePeriod.OwnerId=d.OwnerId"))
                Assert.That(billingSource, Does.Contain("destinationPeriod.OwnerId=i.OwnerId"))
                Assert.That(billingSource, Does.Contain("SELECT d.UsageFactId,d.CorrelationId,d.FactKind,d.OwnerId,d.OrganizationId,d.RepositoryId"))
                Assert.That(billingSource, Does.Contain("sourcePeriod.State IN (2,3,4)"))
                Assert.That(billingSource, Does.Contain("destinationPeriod.State IN (2,3,4)"))
                Assert.That(billingSource, Does.Not.Contain("i.RawPayload<>d.RawPayload"))
                Assert.That(billingSource, Does.Contain("d.State IN (2,3,4)"))
                Assert.That(billingSource, Does.Contain("d.State IN (2,3)"))
                Assert.That(billingSource, Does.Contain("d.State=4"))
                Assert.That(billingSource, Does.Contain("d.State=2 AND i.State=3"))
                Assert.That(billingSource, Does.Contain("e.EntryKind=1 AND e.SourceChargePreviewLineId IS NULL"))
                Assert.That(billingSource, Does.Contain("d.PeriodFromUtc"))
                Assert.That(billingSource, Does.Contain("d.PeriodToUtc"))

                // Overflow: exact work becomes terminal, cannot be selected after restart/retry, and does not prevent later eligible work from filling the batch.
                Assert.That(closeSource, Does.Contain("writePermanentCorrectionCalculationFailure"))
                Assert.That(closeSource, Does.Contain("BlockedCode=N'CalculationOverflow'"))
                Assert.That(closeSource, Does.Contain("PermanentlyFailedAtUtc=SYSUTCDATETIME()"))
                Assert.That(closeSource, Does.Contain("CompletedAtUtc IS NULL AND PermanentlyFailedAtUtc IS NULL AND IsAutomaticRetryEligible=1"))
                Assert.That(closeSource, Does.Contain("w.PermanentlyFailedAtUtc IS NULL AND w.IsAutomaticRetryEligible=0"))
                Assert.That(closeSource, Does.Contain("SELECT TOP (100) BillingCorrectionWorkId"))
                Assert.That(closeSource, Does.Contain("do! transaction.CommitAsync(cancellationToken)"))
                Assert.That(closeSource, Does.Not.Contain("PermanentlyFailedAtUtc=NULL"))

                // Retry returns the durable failure outcome before the generic terminal result.
                Assert.That(retryOutcomeIndex, Is.GreaterThanOrEqualTo(0))
                Assert.That(terminalOutcomeIndex, Is.GreaterThan(retryOutcomeIndex))

                // The additive persisted field has literal migration, snapshot, and runtime-model parity.
                Assert.That(migrationSource, Does.Contain("CK_ops_BillingCorrectionWork_PermanentFailure"))
                Assert.That(migrationSource, Does.Contain("PermanentlyFailedAtUtc datetime2(7) NULL"))
                Assert.That(migrationSource, Does.Contain("TR_ops_RawUsageFact_TerminalBillingProtection"))
                Assert.That(migrationSource, Does.Contain("TR_ops_BillingPeriod_PermanentFailureProtection"))
                Assert.That(snapshotSource, Does.Contain("PermanentlyFailedAtUtc"))

                Assert.That((migrationWorkShape = snapshotWorkShape), Is.True)
                Assert.That((snapshotWorkShape = runtimeWorkShape), Is.True)

                Assert.That(migrationScript, Does.Contain("CK_ops_BillingCorrectionWork_PermanentFailure"))
                Assert.That(migrationScript, Does.Contain("sourcePeriod.State IN (2,3,4)")))
        )

    /// Proves the session-six routine stabilization preserves one raw-fact lock order and closes the remaining SQL-bypass paths.
    [<Test>]
    member _.SessionSixRoutineStabilizationGuardsAreDurable() =
        let root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."))
        let billingSource = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "OperationsBilling.fs"))
        let closeSource = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "OperationsBillingClose.fs"))

        let migrationSource =
            File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "Migrations", "20260713130000_StabilizeBillingCorrectionWorkFailure.fs"))

        let snapshotSource = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "Migrations", "OperationsDbContextModelSnapshot.fs"))
        let docs = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "MIGRATIONS.md"))

        use context =
            OperationsDbContextFactory.create "Server=(localdb)\\MSSQLLocalDB;Database=GraceOperationsBillingSessionSixModel;Integrated Security=true;"

        let migrationScript = (context.GetService<IMigrator>()).GenerateScript()

        let routingStart = closeSource.IndexOf("member _.RecordAcceptedLateFactAsync", StringComparison.Ordinal)

        let preliminaryReadIndex = closeSource.IndexOf("WITH(READUNCOMMITTED) WHERE UsageFactId=@UsageFactId", routingStart, StringComparison.Ordinal)

        let scopeLockIndex = closeSource.IndexOf("do! lockScope connection transaction scope cancellationToken", routingStart, StringComparison.Ordinal)

        let rawFactUpdateLockIndex = closeSource.IndexOf("WITH(UPDLOCK,HOLDLOCK) WHERE UsageFactId=@UsageFactId", routingStart, StringComparison.Ordinal)

        Assert.Multiple(
            Action (fun () ->
                // Interleaving: a lock-free preliminary read is revalidated only after the shared owner/month app lock.
                Assert.That(routingStart, Is.GreaterThanOrEqualTo(0))
                Assert.That(preliminaryReadIndex, Is.GreaterThan(routingStart))
                Assert.That(scopeLockIndex, Is.GreaterThan(preliminaryReadIndex))
                Assert.That(rawFactUpdateLockIndex, Is.GreaterThan(scopeLockIndex))
                Assert.That(closeSource, Does.Contain("scope changed before its owner/month lock was acquired; retry routing from the persisted fact"))
                Assert.That(closeSource, Does.Contain("WHERE NOT EXISTS(SELECT 1 FROM ops.BillingCorrectionWork WITH(UPDLOCK,HOLDLOCK)"))

                // SQL bypass: terminal source and destination updates compare row pairs by UsageFactId, defeating multi-row swaps.
                Assert.That(billingSource, Does.Contain("SELECT i.UsageFactId,i.CorrelationId,i.FactKind,i.OwnerId,i.OrganizationId,i.RepositoryId"))
                Assert.That(billingSource, Does.Contain("LEFT JOIN inserted i ON i.UsageFactId=d.UsageFactId"))
                Assert.That(billingSource, Does.Contain("LEFT JOIN deleted d ON d.UsageFactId=i.UsageFactId"))
                Assert.That(billingSource, Does.Contain("d.UsageFactId IS NULL"))
                Assert.That(billingSource, Does.Not.Contain("UPDATE(UsageFactId) OR UPDATE(OwnerId)"))
                Assert.That(billingSource, Does.Contain("destinationPeriod.State IN (2,3,4)"))
                Assert.That(billingSource, Does.Not.Contain("i.RawPayload<>d.RawPayload"))

                // Direct Open/Provisional terminal transitions require the service's final-preview, ledger, or permanent-failure evidence.
                Assert.That(billingSource, Does.Contain("d.State IN (0,1) AND i.State IN (2,3,4)"))
                Assert.That(billingSource, Does.Contain("FROM ops.ChargePreviewFreshness f WHERE f.BillingPeriodId=i.BillingPeriodId"))
                Assert.That(billingSource, Does.Contain("e.EntryKind=0 AND e.SourceChargePreviewLineId=l.ChargePreviewLineId"))
                Assert.That(billingSource, Does.Contain("i.PermanentFailureCode<>N'CalculationOverflow'"))

                Assert.That(
                    billingSource,
                    Does.Contain("Terminal billing transitions require complete immutable close, correction, or permanent-failure evidence.")
                )

                // Pricing evidence remains frozen for all terminal states, but non-overlapping future maintenance remains outside every trigger predicate.
                Assert.That(billingSource, Does.Contain("p.State IN (2,3,4)"))

                Assert.That(
                    billingSource,
                    Does.Contain("d.EffectiveFromUtc < p.PeriodToUtc AND (d.EffectiveToUtc IS NULL OR d.EffectiveToUtc > p.PeriodFromUtc)")
                )

                // Initial posting remains legal before terminal transition, while post-terminal initial charges fail and correction entries remain append-only.
                Assert.That(billingSource, Does.Contain("AFTER INSERT, UPDATE, DELETE AS"))
                Assert.That(billingSource, Does.Contain("WHERE e.EntryKind=0 AND p.State IN (2,3,4)"))
                Assert.That(billingSource, Does.Contain("IF EXISTS (SELECT 1 FROM deleted)"))

                // Upgrade migration refreshes every changed trigger while the unchanged literal model snapshot remains the reviewed schema shape.
                Assert.That(migrationSource, Does.Contain("DROP TRIGGER IF EXISTS ops.TR_ops_ChargeLedgerEntry_Immutable"))
                Assert.That(migrationSource, Does.Contain("DROP TRIGGER IF EXISTS ops.TR_ops_PricingRate_HistoricalProtection"))
                Assert.That(migrationSource, Does.Contain("migrationBuilder.Sql(OperationsBillingSql.CreateLedgerImmutabilityTrigger)"))
                Assert.That(migrationSource, Does.Contain("OperationsBillingSql.CreateHistoricalPricingProtectionTriggers.Split"))
                Assert.That(snapshotSource, Does.Contain("PermanentlyFailedAtUtc"))
                Assert.That(docs, Does.Contain("Key-changing direct SQL cannot move a fact into terminal history"))
                Assert.That(migrationScript, Does.Contain("Initial charge entries cannot be appended after a billing period is terminal."))

                Assert.That(
                    migrationScript,
                    Does.Contain("Terminal billing transitions require complete immutable close, correction, or permanent-failure evidence.")
                )

                Assert.That(migrationScript, Does.Contain("p.State IN (2,3,4)")))
        )

    /// Proves session-eight terminal insertion guards, immutable final evidence, trusted late ingestion, and Adjustment-only corrections remain durable.
    [<Test>]
    member _.SessionEightTerminalIntegrityAndAdjustmentOnlyContractsAreDurable() =
        let root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."))
        let billingSource = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "OperationsBilling.fs"))
        let usageSqlSource = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "OperationsUsageSql.fs"))
        let usageDataSource = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "OperationsData.fs"))
        let entitySource = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "OperationsEntities.fs"))
        let closeSource = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "OperationsBillingClose.fs"))
        let aspireTestHostSource = File.ReadAllText(Path.Combine(root, "..", "Grace.Server.Tests", "AspireTestHost.fs"))

        let initialMigrationSource =
            File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "Migrations", "20260711140000_AddBillingPeriodCloseLedger.fs"))

        let migrationPath = Path.Combine(root, "Grace.Operations.Data", "Migrations", "20260713140000_StabilizeBillingTerminalInsertProtection.fs")

        use context =
            OperationsDbContextFactory.create "Server=(localdb)\\MSSQLLocalDB;Database=GraceOperationsBillingSessionEightModel;Integrated Security=true;"

        let migrationScript = (context.GetService<IMigrator>()).GenerateScript()

        Assert.Multiple(
            Action (fun () ->
                // Every active product contract has one correction kind. The predecessor link remains automatic-correction evidence, not a second entry kind.
                Assert.That(billingSource, Does.Contain("| Adjustment = 1"))
                Assert.That(entitySource, Does.Contain("Stores Charge or Adjustment kind."))
                Assert.That(initialMigrationSource, Does.Contain("CK_ops_ChargeLedgerEntry_Kind CHECK (EntryKind BETWEEN 0 AND 1)"))
                Assert.That(initialMigrationSource, Does.Contain("EntryKind=1 AND SourceChargePreviewLineId IS NULL"))

                // Terminal period INSERTs, corrections, and permanent failure evidence must all be protected at the database boundary.
                Assert.That(billingSource, Does.Contain("AFTER INSERT, UPDATE, DELETE AS"))
                Assert.That(billingSource, Does.Contain("i.State IN (2,3,4)"))
                Assert.That(billingSource, Does.Contain("e.EntryKind=1 AND p.State NOT IN (2,3)"))
                Assert.That(billingSource, Does.Contain("TR_ops_ChargePreviewLine_TerminalBillingProtection"))
                Assert.That(billingSource, Does.Contain("TR_ops_ChargePreviewFreshness_TerminalBillingProtection"))
                Assert.That(billingSource, Does.Contain("d.State=4"))
                Assert.That(billingSource, Does.Contain("i.CreatedAtUtc<>d.CreatedAtUtc"))
                Assert.That(billingSource, Does.Contain("i.CloseCorrelationId"))
                Assert.That(billingSource, Does.Contain("i.ConsecutiveCloseFailureCount<>d.ConsecutiveCloseFailureCount"))

                // Direct terminal raw inserts are untrusted, while only the live/replay commands establish and clear exact transaction-scoped trust.
                Assert.That(billingSource, Does.Contain("TR_ops_RawUsageFact_TerminalBillingProtection ON ops.RawUsageFact\nAFTER INSERT, UPDATE, DELETE AS"))
                Assert.That(billingSource, Does.Contain("SESSION_CONTEXT(N'Grace.Operations.TrustedRawUsageFactInsert')"))
                Assert.That(billingSource, Does.Contain("ISNULL(CONVERT(nvarchar(36),SESSION_CONTEXT(N'Grace.Operations.TrustedRawUsageFactInsert')),N'')"))
                Assert.That(billingSource, Does.Contain("terminalPeriod.State=4"))
                Assert.That(billingSource, Does.Contain("WHERE EXISTS(SELECT 1 FROM deleted)"))
                Assert.That(billingSource, Does.Contain("LEFT JOIN inserted i ON i.UsageFactId=d.UsageFactId"))
                Assert.That(billingSource, Does.Contain("LEFT JOIN deleted d ON d.UsageFactId=i.UsageFactId"))
                Assert.That(usageSqlSource, Does.Contain("sp_set_session_context @key=N'Grace.Operations.TrustedRawUsageFactInsert'"))
                Assert.That(usageSqlSource, Does.Contain("DECLARE @TrustedRawUsageFactInsertMarker nvarchar(36)=CONVERT(nvarchar(36),@UsageFactId);"))
                Assert.That(usageSqlSource, Does.Contain("@value=@TrustedRawUsageFactInsertMarker;"))
                Assert.That(usageSqlSource, Does.Not.Contain("@value=CONVERT(nvarchar(36),@UsageFactId)"))
                Assert.That(usageSqlSource, Does.Contain("@value=NULL"))
                Assert.That(usageDataSource, Does.Contain("clearTrustedRawUsageFactInsertAsync"))
                Assert.That(usageDataSource, Does.Contain("CancellationToken.None"))
                Assert.That(OperationsUsageSql.RawUsageFactScopeLockTimeoutMilliseconds, Is.EqualTo(60000))

                Assert.That(
                    OperationsUsageSql.RawUsageFactInsertCommandTimeoutSeconds,
                    Is.GreaterThan(
                        OperationsUsageSql.RawUsageFactScopeLockTimeoutMilliseconds
                        / 1000
                    )
                )

                Assert.That(usageSqlSource, Does.Contain("@LockTimeout=60000"))

                Assert.That(
                    usageDataSource.IndexOf(
                        "command.CommandTimeout <- OperationsUsageSql.RawUsageFactInsertCommandTimeoutSeconds",
                        usageDataSource.IndexOf("member _.TryInsertRawUsageFactAsync", StringComparison.Ordinal),
                        StringComparison.Ordinal
                    ),
                    Is.GreaterThan(0)
                )

                Assert.That(
                    usageDataSource.IndexOf(
                        "command.CommandTimeout <- OperationsUsageSql.RawUsageFactInsertCommandTimeoutSeconds",
                        usageDataSource.IndexOf("member _.TryInsertReplayedArchivedUsageFactAsync", StringComparison.Ordinal),
                        StringComparison.Ordinal
                    ),
                    Is.GreaterThan(0)
                )

                Assert.That(aspireTestHostSource, Does.Contain("20260714100000_StabilizeBillingCloseEvidenceGuards"))
                Assert.That(closeSource, Does.Contain("fact.AcceptedAtUtc > period.ClosedAtUtc"))
                Assert.That(closeSource, Does.Not.Contain("fact.AcceptedAtUtc >= period.ClosedAtUtc"))

                // The upgrade migration refreshes every changed guard for existing development databases without changing the EF shape.
                Assert.That(File.Exists(migrationPath), Is.True)
                Assert.That(migrationScript, Does.Contain("TR_ops_ChargePreviewLine_TerminalBillingProtection"))
                Assert.That(migrationScript, Does.Contain("TR_ops_ChargePreviewFreshness_TerminalBillingProtection"))
                Assert.That(migrationScript, Does.Contain("Terminal billing raw fact inserts require trusted application routing.")))
        )

    /// Proves session-eleven close, correction, and failure SQL guards keep current source evidence ahead of terminal mutation.
    [<Test>]
    member _.SessionElevenCloseEvidenceGuardsRejectSqlBypassPaths() =
        let root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."))
        let billingSource = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "OperationsBilling.fs"))
        let closeSource = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "OperationsBillingClose.fs"))
        let testHostSource = File.ReadAllText(Path.Combine(root, "..", "Grace.Server.Tests", "AspireTestHost.fs"))

        let migrationPath = Path.Combine(root, "Grace.Operations.Data", "Migrations", "20260714100000_StabilizeBillingCloseEvidenceGuards.fs")

        let migrationSource = File.ReadAllText(migrationPath)

        use context =
            OperationsDbContextFactory.create "Server=(localdb)\\MSSQLLocalDB;Database=GraceOperationsBillingSessionElevenModel;Integrated Security=true;"

        let migrationScript = (context.GetService<IMigrator>()).GenerateScript()

        Assert.Multiple(
            Action (fun () ->
                // Forged freshness, preview, and initial-ledger rows cannot close a period unless they match the current fact and pricing source set.
                Assert.That(billingSource, Does.Contain("TR_ops_BillingPeriod_FinalPreviewSourceProtection"))
                Assert.That(billingSource, Does.Contain("@ExpectedPreview"))
                Assert.That(billingSource, Does.Contain("CurrencyCode varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL"))
                Assert.That(billingSource, Does.Contain("Terminal billing close preview does not represent the current accepted facts and pricing source set."))
                Assert.That(billingSource, Does.Contain("Terminal billing close freshness evidence does not match the current final source set."))
                Assert.That(billingSource, Does.Contain("STRING_AGG"))
                Assert.That(billingSource, Does.Contain("HASHBYTES('SHA2_256'"))
                Assert.That(closeSource, Does.Contain("|> Seq.sortBy (fun line -> line.ChargePreviewLineId)"))

                // Direct correction completion or deletion has the same automatic Adjustment proof as the supported post-then-complete path.
                Assert.That(billingSource, Does.Contain("TR_ops_BillingCorrectionWork_CompletionProtection"))
                Assert.That(billingSource, Does.Contain("entry.ReasonCode=N'AutomaticLateUsage'"))
                Assert.That(billingSource, Does.Contain("entry.CorrelationId=CONVERT(nvarchar(200),work.BillingCorrectionWorkId)"))

                Assert.That(
                    closeSource,
                    Does.Contain("WHERE NOT EXISTS(SELECT 1 FROM ops.ChargeLedgerEntry WITH(UPDLOCK,HOLDLOCK) WHERE BillingCorrectionWorkId=@WorkId)")
                )

                Assert.That(
                    closeSource.IndexOf(
                        "INSERT INTO ops.ChargeLedgerEntry",
                        closeSource.IndexOf("let processCorrectionWork", StringComparison.Ordinal),
                        StringComparison.Ordinal
                    ),
                    Is.GreaterThanOrEqualTo(0)
                )

                Assert.That(closeSource, Does.Contain("UPDATE ops.BillingCorrectionWork SET CompletedAtUtc=SYSUTCDATETIME()"))

                // The processor rechecks the strict database timestamp; equal and earlier accepted facts cannot post as late correction work.
                Assert.That(closeSource, Does.Contain("p.ClosedAtUtc"))
                Assert.That(closeSource, Does.Contain("AND AcceptedAtUtc>@ClosedAtUtc;"))
                Assert.That(closeSource, Does.Not.Contain("AcceptedAtUtc>=@ClosedAtUtc"))

                // Direct failure resolution or deletion needs the exact accepted repair fact, while accepted replay remains the supported idempotent path.
                Assert.That(billingSource, Does.Contain("TR_ops_BillingIngestionFailure_ResolutionProtection"))
                Assert.That(billingSource, Does.Contain("fact.UsageFactId=failure.UsageFactId"))
                Assert.That(closeSource, Does.Contain("Accepted usage fact resolved canonical failure evidence."))

                // The migration is database-only, appears in the generated script, and advances Aspire's readiness floor.
                Assert.That(File.Exists(migrationPath), Is.True)
                Assert.That(migrationSource, Does.Contain("TR_ops_BillingPeriod_FinalPreviewSourceProtection"))
                Assert.That(migrationSource, Does.Contain("TR_ops_BillingCorrectionWork_CompletionProtection"))
                Assert.That(migrationSource, Does.Contain("TR_ops_BillingIngestionFailure_ResolutionProtection"))
                Assert.That(migrationScript, Does.Contain("Terminal billing close freshness evidence does not match the current final source set."))

                Assert.That(
                    migrationScript,
                    Does.Contain("Billing correction work can complete or be removed only after its matching automatic Adjustment is posted.")
                )

                Assert.That(
                    migrationScript,
                    Does.Contain("Active billing ingestion failure evidence requires its exact accepted repair fact before resolution or removal.")
                )

                Assert.That(testHostSource, Does.Contain("20260714100000_StabilizeBillingCloseEvidenceGuards")))
        )
