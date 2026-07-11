namespace Grace.Operations.Tests

open Grace.Operations.Data
open Grace.Operations.Data.Migrations
open Grace.Types.Common
open Grace.Types.Usage
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Infrastructure
open Microsoft.EntityFrameworkCore.Metadata
open Microsoft.EntityFrameworkCore.Migrations
open NUnit.Framework
open NodaTime
open System
open System.IO

/// Proves billing calendar, lifecycle, durability, and independently frozen EF contracts for issue 576.
[<TestFixture>]
type OperationsBillingTests() =
    let utc year month day hour = DateTime(year, month, day, hour, 0, 0, DateTimeKind.Utc)
    let multiple action = Assert.Multiple(Action action)

    let correction () =
        {
            BillingPeriodId = Guid.NewGuid()
            EntryKind = ChargeLedgerEntryKind.Adjustment
            PriorChargeLedgerEntryId = Some(Guid.NewGuid())
            FactKind = 1
            BillableUsageKindMappingId = Guid.NewGuid()
            BillableUsageKind = 2
            CustomerPricingAssignmentId = Guid.NewGuid()
            PricingPlanId = Guid.NewGuid()
            PricingRateId = Guid.NewGuid()
            CurrencyCode = "USD"
            UnitName = "byte-minute"
            UnitQuantity = 1024L
            UnitPriceMicros = 7L
            EffectiveFromUtc = utc 2028 1 1 0
            EffectiveToUtc = utc 2028 2 1 0
            QuantityDelta = -4L
            ChargeMicrosDelta = -28L
        }

    /// Verifies UTC month boundaries include leap February and remain half-open.
    [<Test>]
    member _.UtcMonthCalendarHandlesLeapFebruary() =
        let fromUtc, toUtc = BillingPeriodRules.monthContaining (utc 2028 2 29 23)

        multiple (fun () ->
            Assert.That(fromUtc, Is.EqualTo(utc 2028 2 1 0))
            Assert.That(toUtc, Is.EqualTo(utc 2028 3 1 0)))

    /// Verifies mid-month assignments materialize every intersecting month but not an exclusive ending boundary.
    [<Test>]
    member _.AssignmentMonthsUseHalfOpenIntersection() =
        let months = BillingPeriodRules.intersectingMonths (utc 2028 1 15 0) (Some(utc 2028 3 1 0)) (utc 2028 4 1 0)
        Assert.That(List.length months, Is.EqualTo(2))

    /// Verifies the period remains Open before +24h and becomes Provisional exactly at +24h.
    [<Test>]
    member _.ProvisionalBoundaryIsExact() =
        let endUtc = utc 2028 2 1 0

        multiple (fun () ->
            Assert.That(BillingPeriodRules.stateAt endUtc (endUtc.AddHours(24.0).AddTicks(-1L)), Is.EqualTo(BillingPeriodState.Open))
            Assert.That(BillingPeriodRules.stateAt endUtc (endUtc.AddHours 24.0), Is.EqualTo(BillingPeriodState.Provisional)))

    /// Verifies no close is eligible before +72h and exact +72h is eligible.
    [<Test>]
    member _.CloseBoundaryIsExactWithoutForceBypass() =
        let endUtc = utc 2028 2 1 0

        multiple (fun () ->
            Assert.That(BillingPeriodRules.isCloseEligible endUtc (endUtc.AddHours(72.0).AddTicks(-1L)), Is.False)
            Assert.That(BillingPeriodRules.isCloseEligible endUtc (endUtc.AddHours 72.0), Is.True))

    /// Verifies period identities are deterministic and include every scope member.
    [<Test>]
    member _.PeriodIdentityUsesCompleteScope() =
        let scope: BillingPeriodScope =
            {
                CustomerId = Guid.NewGuid()
                OwnerId = Guid.NewGuid()
                OrganizationId = Guid.NewGuid()
                RepositoryId = Guid.NewGuid()
                PeriodFromUtc = utc 2028 1 1 0
                PeriodToUtc = utc 2028 2 1 0
            }

        multiple (fun () ->
            Assert.That(BillingPeriodRules.periodId scope, Is.EqualTo(BillingPeriodRules.periodId scope))
            Assert.That(BillingPeriodRules.periodId { scope with RepositoryId = Guid.NewGuid() }, Is.Not.EqualTo(BillingPeriodRules.periodId scope)))

    /// Verifies complete manual provenance is mandatory and bounded.
    [<Test>]
    member _.ManualProvenanceRejectsMissingFields() =
        Assert.Throws<ArgumentException>(
            Action(fun () -> BillingProvenance.validate { InitiatedByPrincipalId = ""; ReasonCode = "Retry"; ReasonText = "repair"; CorrelationId = "c" })
        )
        |> ignore

    /// Verifies every immutable correction dimension and signed delta participates in deterministic identity.
    [<Test>]
    member _.ManualCorrectionIdentityUsesCompleteGrainAndExactRetriesDeduplicate() =
        let value = correction ()
        let identity candidate = ManualBillingCorrectionIdentity.entryId candidate "manual-correlation"
        let original = identity value

        let distinct =
            [
                { value with BillingPeriodId = Guid.NewGuid() }
                { value with EntryKind = ChargeLedgerEntryKind.Reversal }
                { value with PriorChargeLedgerEntryId = Some(Guid.NewGuid()) }
                { value with FactKind = value.FactKind + 1 }
                { value with BillableUsageKindMappingId = Guid.NewGuid() }
                { value with BillableUsageKind = value.BillableUsageKind + 1 }
                { value with CustomerPricingAssignmentId = Guid.NewGuid() }
                { value with PricingPlanId = Guid.NewGuid() }
                { value with PricingRateId = Guid.NewGuid() }
                { value with CurrencyCode = "EUR" }
                { value with UnitName = "operation" }
                { value with UnitQuantity = value.UnitQuantity + 1L }
                { value with UnitPriceMicros = value.UnitPriceMicros + 1L }
                { value with EffectiveFromUtc = value.EffectiveFromUtc.AddTicks(1L) }
                { value with EffectiveToUtc = value.EffectiveToUtc.AddTicks(1L) }
                { value with QuantityDelta = value.QuantityDelta - 1L }
                { value with ChargeMicrosDelta = value.ChargeMicrosDelta - 1L }
            ]

        multiple (fun () ->
            Assert.That(identity value, Is.EqualTo(original), "Exact retry must retain its deterministic identity.")
            Assert.That(distinct |> List.map identity, Has.None.EqualTo(original)))

    /// Verifies empty fact identifiers use stable broker evidence without collapsing scopes or messages.
    [<Test>]
    member _.EmptyFactFailureIdentityUsesMessageAndScopeEvidence() =
        let ownerId = OwnerId.Parse("11111111-1111-1111-1111-111111111111")
        let organizationId = OrganizationId.Parse("22222222-2222-2222-2222-222222222222")
        let repositoryId = RepositoryId.Parse("33333333-3333-3333-3333-333333333333")

        let fact =
            UsageFact.RepositoryStorageBytesMinute(
                Guid.Empty,
                CorrelationId "invalid-fact-correlation",
                ownerId,
                organizationId,
                repositoryId,
                StoragePoolId "pool",
                1L,
                Instant.FromUtc(2028, 1, 15, 0, 0)
            )

        let identity message candidate = BillingIngestionFailureIdentity.failureId candidate "InvalidUsageFact" message
        let original = identity "message-1" fact
        let otherScope = { fact with Scope = { fact.Scope with RepositoryId = RepositoryId.Parse("44444444-4444-4444-8444-444444444444") } }

        multiple (fun () ->
            Assert.That(identity "message-1" fact, Is.EqualTo(original))
            Assert.That(identity "message-2" fact, Is.Not.EqualTo(original))
            Assert.That(identity "message-1" otherScope, Is.Not.EqualTo(original)))

    /// Verifies worker cadence, shared lock, freshness, one transaction, and atomic late-fact delivery remain explicit.
    [<Test>]
    member _.RuntimeSourceCarriesHighRiskProof() =
        let root =
            Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..")
            |> Path.GetFullPath

        let closeSource = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "OperationsBillingClose.fs"))
        let dataSource = File.ReadAllText(Path.Combine(root, "Grace.Operations.Data", "OperationsData.fs"))
        let workerSource = File.ReadAllText(Path.Combine(root, "Grace.Operations.Worker", "OperationsBillingWorker.fs"))

        multiple (fun () ->
            Assert.That(closeSource, Does.Contain("OperationsBillingSql.AcquireScopeLock"))
            Assert.That(closeSource, Does.Contain("AcceptedFactsDigest=@Facts AND PricingDigest=@Pricing"))
            Assert.That(closeSource, Does.Contain("BeginTransactionAsync(IsolationLevel.Serializable"))
            Assert.That(closeSource, Does.Contain("CREATE TABLE #CorrectionExpected"))
            Assert.That(closeSource, Does.Not.Contain("replacePreview connection transaction periodId scope facts"))
            Assert.That(closeSource, Does.Contain("SELECT BillingCorrectionWorkId FROM ops.BillingCorrectionWork"))
            Assert.That(closeSource, Does.Contain("A failed work item remains pending"))
            Assert.That(closeSource, Does.Not.Contain("force"))
            Assert.That(dataSource, Does.Contain("RecordAcceptedFactBillingEffectsAsync"))
            Assert.That(dataSource, Does.Contain("period.State IN (2,3)"))
            Assert.That(workerSource, Does.Contain("schema.EnsureCreatedAsync stoppingToken"))
            Assert.That(workerSource, Does.Contain("TimeSpan.FromMinutes 30.0")))

    /// Verifies runtime, newest migration target, and latest snapshot have identical complete structural models.
    [<Test>]
    member _.BillingMigrationSnapshotAndRuntimeAgree() =
        let shape (model: IModel) =
            model.GetEntityTypes()
            |> Seq.map (fun entity ->
                let properties =
                    entity.GetProperties()
                    |> Seq.map (fun p -> p.Name, p.ClrType.FullName, p.IsNullable)
                    |> Set.ofSeq

                let keys =
                    entity.GetKeys()
                    |> Seq.map (fun k ->
                        k.GetName(),
                        (k.Properties
                         |> Seq.map (fun p -> p.Name)
                         |> Seq.toList))
                    |> Set.ofSeq

                let indexes =
                    entity.GetIndexes()
                    |> Seq.map (fun i ->
                        i.GetDatabaseName(),
                        i.IsUnique,
                        (i.Properties
                         |> Seq.map (fun p -> p.Name)
                         |> Seq.toList))
                    |> Set.ofSeq

                let foreignKeys =
                    entity.GetForeignKeys()
                    |> Seq.map (fun f ->
                        f.GetConstraintName(),
                        f.PrincipalEntityType.Name,
                        f.DeleteBehavior,
                        (f.Properties
                         |> Seq.map (fun p -> p.Name)
                         |> Seq.toList))
                    |> Set.ofSeq

                let checks =
                    entity.GetCheckConstraints()
                    |> Seq.map (fun c -> c.Name, c.Sql)
                    |> Set.ofSeq

                entity.Name, entity.GetSchema(), entity.GetTableName(), properties, keys, indexes, foreignKeys, checks)
            |> Set.ofSeq

        use context = OperationsDbContextFactory.create "Server=(localdb)\\MSSQLLocalDB;Database=GraceOperationsBillingModel;Integrated Security=true;"
        let runtime = context.GetService<IDesignTimeModel>().Model
        let snapshot = OperationsDbContextModelSnapshot().Model
        let migration = AddBillingPeriodCloseLedger().TargetModel
        let runtimeShape = shape runtime
        let snapshotShape = shape snapshot
        let migrationShape = shape migration

        multiple (fun () ->
            Assert.That(
                (snapshotShape = runtimeShape),
                Is.True,
                $"Snapshot-only: {Set.difference snapshotShape runtimeShape}; runtime-only: {Set.difference runtimeShape snapshotShape}"
            )

            Assert.That(
                (migrationShape = runtimeShape),
                Is.True,
                $"Migration-only: {Set.difference migrationShape runtimeShape}; runtime-only: {Set.difference runtimeShape migrationShape}"
            ))

    /// Verifies independently frozen sources do not delegate to runtime feature or SQL helpers.
    [<Test>]
    member _.FrozenBillingModelsAreIndependentLiteralModels() =
        let root =
            Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "Grace.Operations.Data", "Migrations")
            |> Path.GetFullPath

        let migration = File.ReadAllText(Path.Combine(root, "20260711140000_AddBillingPeriodCloseLedger.fs"))
        let snapshot = File.ReadAllText(Path.Combine(root, "OperationsDbContextModelSnapshot.fs"))
        let migrationModel = migration.Substring(migration.IndexOf("override _.BuildTargetModel", StringComparison.Ordinal))
        let snapshotModel = snapshot.Substring(snapshot.IndexOf("override _.BuildModel", StringComparison.Ordinal))

        multiple (fun () ->
            Assert.That(migrationModel, Does.Not.Contain("OperationsBillingModel.configure"))
            Assert.That(snapshotModel, Does.Not.Contain("OperationsBillingModel.configure"))
            Assert.That(migrationModel, Does.Not.Contain("OperationsBillingSql."))
            Assert.That(snapshotModel, Does.Not.Contain("OperationsBillingSql."))
            Assert.That(migrationModel, Does.Contain("let ledger = modelBuilder.Entity<ChargeLedgerEntryEntity>()"))
            Assert.That(snapshotModel, Does.Contain("let work = modelBuilder.Entity<BillingCorrectionWorkEntity>()")))

    /// Verifies generated SQL carries immutable ledger and historical pricing database guards.
    [<Test>]
    member _.GeneratedMigrationSqlContainsImmutabilityGuards() =
        use context = OperationsDbContextFactory.create "Server=(localdb)\\MSSQLLocalDB;Database=GraceOperationsBillingMigration;Integrated Security=true;"
        let script = context.GetService<IMigrator>().GenerateScript()

        multiple (fun () ->
            Assert.That(script, Does.Contain("TR_ops_ChargeLedgerEntry_Immutable"))
            Assert.That(script, Does.Contain("TR_ops_PricingRate_HistoricalProtection"))
            Assert.That(script, Does.Contain("JOIN ops.CustomerPricingAssignment a ON a.PricingPlanId=d.PricingPlanId"))
            Assert.That(script, Does.Contain("p.CustomerId=d.CustomerId AND p.OwnerId=d.OwnerId"))
            Assert.That(script, Does.Contain("d.EffectiveFromUtc<p.PeriodToUtc"))
            Assert.That(script, Does.Contain("EffectiveToUtc,Quantity,ChargeMicros,PriorChargeLedgerEntryId"))
            Assert.That(script, Does.Contain("UX_ops_BillingCorrectionWork_PeriodFact"))
            Assert.That(script, Does.Not.Contain("CloseAttemptHistory")))
