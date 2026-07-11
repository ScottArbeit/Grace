namespace Grace.Operations.Tests

open Grace.Operations.Data
open Grace.Operations.Data.Migrations
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Infrastructure
open Microsoft.EntityFrameworkCore.Metadata
open Microsoft.EntityFrameworkCore.Migrations
open NUnit.Framework
open System
open System.IO

/// Proves billing calendar, lifecycle, durability, and independently frozen EF contracts for issue 576.
[<TestFixture>]
type OperationsBillingTests() =
    let utc year month day hour = DateTime(year, month, day, hour, 0, 0, DateTimeKind.Utc)
    let multiple action = Assert.Multiple(Action action)

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
            Assert.That(closeSource, Does.Not.Contain("force"))
            Assert.That(dataSource, Does.Contain("RecordAcceptedFactBillingEffectsAsync"))
            Assert.That(dataSource, Does.Contain("period.State IN (2,3)"))
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
            Assert.That(script, Does.Contain("UX_ops_BillingCorrectionWork_PeriodFact"))
            Assert.That(script, Does.Not.Contain("CloseAttemptHistory")))
