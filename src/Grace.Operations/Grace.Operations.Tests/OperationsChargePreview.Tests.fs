namespace Grace.Operations.Tests

open Grace.Operations.Data
open Grace.Operations.Data.Migrations
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Infrastructure
open Microsoft.EntityFrameworkCore.Migrations
open Microsoft.EntityFrameworkCore.Metadata
open Microsoft.Extensions.DependencyInjection
open NUnit.Framework
open System
open System.IO
open System.Threading

/// Supplies independent fixtures for deterministic charge-preview proofs.
[<RequireQualifiedAccess>]
module private ChargePreviewTestData =

    let multiple action = Assert.Multiple(Action action)

    let utc day hour = DateTime(2026, 7, day, hour, 0, 0, DateTimeKind.Utc)

    let scope =
        {
            OwnerId = Guid.Parse "22222222-2222-2222-2222-222222222222"
            OrganizationId = Guid.Parse "33333333-3333-3333-3333-333333333333"
            RepositoryId = Guid.Parse "44444444-4444-4444-4444-444444444444"
            PeriodFromUtc = utc 1 0
            PeriodToUtc = utc 31 0
        }

    let fact usageFactId quantity observedAt =
        {
            UsageFactId = usageFactId
            FactKind = 1
            Quantity = quantity
            ObservedAtUtc = observedAt
            PricingAssignmentId = Guid.Parse "55555555-5555-5555-5555-555555555555"
            BillableUsageKindMappingId = Guid.Parse "66666666-6666-6666-6666-666666666666"
            BillableUsageKind = 10
            PricingPlanId = Guid.Parse "77777777-7777-7777-7777-777777777777"
            PricingRateId = Guid.Parse "88888888-8888-8888-8888-888888888888"
            CurrencyCode = "USD"
            UnitName = "byte-minute"
            UnitQuantity = 3L
            UnitPriceMicros = 1L
            EffectiveFromUtc = utc 1 0
            EffectiveToUtc = utc 31 0
        }

/// Proves arithmetic, line grain, diagnostics, SQL locking, and model/migration agreement for issue 575.
[<TestFixture>]
type OperationsChargePreviewTests() =

    /// Verifies many facts sum before one exact whole-micro calculation.
    [<Test>]
    member _.ManyFactsAggregateBeforeOneRoundedCalculation() =
        let facts =
            [
                ChargePreviewTestData.fact (Guid.NewGuid()) 1L (ChargePreviewTestData.utc 2 0)
                ChargePreviewTestData.fact (Guid.NewGuid()) 1L (ChargePreviewTestData.utc 3 0)
            ]

        let lines = ChargePreviewCalculation.buildLines ChargePreviewTestData.scope facts

        ChargePreviewTestData.multiple (fun () ->
            Assert.That(lines, Has.Length.EqualTo(1))
            Assert.That(lines[0].TotalQuantity, Is.EqualTo(2L))
            Assert.That(lines[0].ChargeMicros, Is.EqualTo(1L)))

    /// Verifies midpoint values round away from zero and exact divisions remain exact.
    [<TestCase(1L, 1L, 2L, 1L)>]
    [<TestCase(2L, 1L, 2L, 1L)>]
    [<TestCase(5L, 2L, 5L, 2L)>]
    member _.WholeMicroRoundingMatchesTheApprovedRule(quantity: int64, price: int64, unitQuantity: int64, expected: int64) =
        Assert.That(ChargePreviewCalculation.calculateChargeMicros quantity price unitQuantity, Is.EqualTo(expected))

    /// Verifies intentionally free pricing still emits an auditable zero-charge line.
    [<Test>]
    member _.ExplicitZeroPriceProducesAZeroChargeLine() =
        let fact = { ChargePreviewTestData.fact (Guid.NewGuid()) 99L (ChargePreviewTestData.utc 2 0) with UnitPriceMicros = 0L }
        let lines = ChargePreviewCalculation.buildLines ChargePreviewTestData.scope [ fact ]

        ChargePreviewTestData.multiple (fun () ->
            Assert.That(lines, Has.Length.EqualTo(1))
            Assert.That(lines[0].TotalQuantity, Is.EqualTo(99L))
            Assert.That(lines[0].ChargeMicros, Is.Zero))

    /// Verifies BigInteger intermediates avoid multiplication overflow while persisted overflow is rejected.
    [<Test>]
    member _.LargeIntermediateArithmeticIsExactAndPersistedOverflowIsRejected() =
        Assert.That(ChargePreviewCalculation.calculateChargeMicros Int64.MaxValue Int64.MaxValue Int64.MaxValue, Is.EqualTo(Int64.MaxValue))

        Assert.Throws<OverflowException>(
            Action (fun () ->
                ChargePreviewCalculation.calculateChargeMicros Int64.MaxValue Int64.MaxValue 1L
                |> ignore)
        )
        |> ignore

    /// Verifies duplicate immutable fact identities cannot contribute twice.
    [<Test>]
    member _.DuplicateUsageFactIdsAreCountedOnce() =
        let id = Guid.NewGuid()
        let fact = ChargePreviewTestData.fact id 6L (ChargePreviewTestData.utc 2 0)
        let lines = ChargePreviewCalculation.buildLines ChargePreviewTestData.scope [ fact; fact ]
        Assert.That(lines[0].TotalQuantity, Is.EqualTo(6L))

    /// Verifies complete pricing identity, currency, and applicability boundaries each produce separate lines.
    [<Test>]
    member _.CompletePricingApplicabilityAndCurrenciesRemainSeparate() =
        let baseline = ChargePreviewTestData.fact (Guid.NewGuid()) 3L (ChargePreviewTestData.utc 2 0)

        let changedAssignment =
            { ChargePreviewTestData.fact (Guid.NewGuid()) 3L (ChargePreviewTestData.utc 6 0) with
                PricingAssignmentId = Guid.NewGuid()
                EffectiveFromUtc = ChargePreviewTestData.utc 5 0
            }

        let changedPlan =
            { ChargePreviewTestData.fact (Guid.NewGuid()) 3L (ChargePreviewTestData.utc 11 0) with
                PricingPlanId = Guid.NewGuid()
                EffectiveFromUtc = ChargePreviewTestData.utc 10 0
            }

        let changedMapping =
            { ChargePreviewTestData.fact (Guid.NewGuid()) 3L (ChargePreviewTestData.utc 16 0) with
                BillableUsageKindMappingId = Guid.NewGuid()
                EffectiveFromUtc = ChargePreviewTestData.utc 15 0
            }

        let changedRate =
            { ChargePreviewTestData.fact (Guid.NewGuid()) 3L (ChargePreviewTestData.utc 21 0) with
                PricingRateId = Guid.NewGuid()
                EffectiveFromUtc = ChargePreviewTestData.utc 20 0
            }

        let changedCurrency =
            { ChargePreviewTestData.fact (Guid.NewGuid()) 3L (ChargePreviewTestData.utc 26 0) with
                PricingRateId = Guid.NewGuid()
                CurrencyCode = "EUR"
                EffectiveFromUtc = ChargePreviewTestData.utc 25 0
            }

        let lines =
            ChargePreviewCalculation.buildLines
                ChargePreviewTestData.scope
                [
                    baseline
                    changedAssignment
                    changedPlan
                    changedMapping
                    changedRate
                    changedCurrency
                ]

        ChargePreviewTestData.multiple (fun () ->
            Assert.That(lines, Has.Length.EqualTo(6))
            Assert.That(lines |> Array.map (fun line -> line.CurrencyCode), Does.Contain("USD"))
            Assert.That(lines |> Array.map (fun line -> line.CurrencyCode), Does.Contain("EUR")))

    /// Verifies unchanged rebuild inputs produce stable identities and values regardless of fact order.
    [<Test>]
    member _.IdenticalRebuildsAreDeterministicAndIdempotent() =
        let facts =
            [
                ChargePreviewTestData.fact (Guid.NewGuid()) 3L (ChargePreviewTestData.utc 2 0)
                ChargePreviewTestData.fact (Guid.NewGuid()) 6L (ChargePreviewTestData.utc 3 0)
            ]

        let first = ChargePreviewCalculation.buildLines ChargePreviewTestData.scope facts
        let second = ChargePreviewCalculation.buildLines ChargePreviewTestData.scope (List.rev facts)

        Assert.Multiple(
            Action (fun () ->
                Assert.That(second[0].ChargePreviewLineId, Is.EqualTo(first[0].ChargePreviewLineId))
                Assert.That(second[0].TotalQuantity, Is.EqualTo(first[0].TotalQuantity))
                Assert.That(second[0].ChargeMicros, Is.EqualTo(first[0].ChargeMicros)))
        )

    /// Verifies SQL datetime2 values do not make line identities depend on the rebuilding host's local timezone.
    [<Test>]
    member _.UnspecifiedSqlUtcTimestampsProduceTheSameIdentityAsUtcValues() =
        let utcFact = ChargePreviewTestData.fact (Guid.NewGuid()) 3L (ChargePreviewTestData.utc 2 0)

        let sqlFact =
            { utcFact with
                EffectiveFromUtc = DateTime.SpecifyKind(utcFact.EffectiveFromUtc, DateTimeKind.Unspecified)
                EffectiveToUtc = DateTime.SpecifyKind(utcFact.EffectiveToUtc, DateTimeKind.Unspecified)
            }

        Assert.That(
            ChargePreviewCalculation.lineId ChargePreviewTestData.scope sqlFact,
            Is.EqualTo(ChargePreviewCalculation.lineId ChargePreviewTestData.scope utcFact)
        )

    /// Verifies each independently missing pricing prerequisite gets an exact diagnostic classification.
    [<TestCase(false, true, true, true, "Assignment")>]
    [<TestCase(true, false, true, true, "Plan")>]
    [<TestCase(true, true, false, true, "Mapping")>]
    [<TestCase(true, true, true, false, "Rate")>]
    member _.MissingPricingPrerequisitesAreDistinguished(assignment, plan, mapping, rate, expected: string) =
        Assert.That(
            ChargePreviewCalculation.missingPrerequisite assignment plan mapping rate
            |> Option.map string,
            Is.EqualTo(Some expected)
        )

    /// Verifies compact fields drive pricing without any archived payload or Blob dependency.
    [<Test>]
    member _.PreviewSqlReadsArchivedCompactRowsWithoutPayloadRehydration() =
        let sql = OperationsChargePreviewSql.SelectSourceAndPricing

        ChargePreviewTestData.multiple (fun () ->
            Assert.That(sql, Does.Contain("fact.UsageFactId, fact.FactKind, fact.Quantity, fact.ObservedAtUtc"))
            Assert.That(sql, Does.Not.Contain("RawPayload"))
            Assert.That(sql, Does.Not.Contain("ArchiveState"))
            Assert.That(sql, Does.Not.Contain("Blob")))

    /// Verifies half-open source selection and every complete applicability contributor are explicit in SQL.
    [<Test>]
    member _.PreviewSqlUsesHalfOpenObservedTimeAndAllApplicabilityBoundaries() =
        let sql = OperationsChargePreviewSql.SelectSourceAndPricing

        ChargePreviewTestData.multiple (fun () ->
            Assert.That(sql, Does.Contain("fact.ObservedAtUtc >= @PeriodFromUtc"))
            Assert.That(sql, Does.Contain("fact.ObservedAtUtc < @PeriodToUtc"))
            Assert.That(sql, Does.Contain("assignment.EffectiveFromUtc"))
            Assert.That(sql, Does.Contain("plan.EffectiveFromUtc"))
            Assert.That(sql, Does.Contain("mapping.EffectiveFromUtc"))
            Assert.That(sql, Does.Contain("rate.EffectiveFromUtc"))
            Assert.That(sql, Does.Contain("MAX(boundary.EffectiveFromUtc)"))
            Assert.That(sql, Does.Contain("MIN(boundary.EffectiveToUtc)")))

    /// Verifies the migration target is literal and the complete EF model agrees across all reviewed representations.
    [<Test>]
    member _.PreviewMigrationAndModelsAgreeOnPersistedIdentity() =
        let migrationPath =
            Path.Combine(
                TestContext.CurrentContext.TestDirectory,
                "..",
                "..",
                "..",
                "..",
                "Grace.Operations.Data",
                "Migrations",
                "20260711090000_AddChargePreviewLines.fs"
            )
            |> Path.GetFullPath

        let migrationSource = File.ReadAllText migrationPath
        let targetModelStart = migrationSource.IndexOf("override _.BuildTargetModel(modelBuilder: ModelBuilder) =", StringComparison.Ordinal)
        Assert.That(targetModelStart, Is.GreaterThanOrEqualTo(0))
        let targetModelSource = migrationSource.Substring(targetModelStart)

        let snapshotPath =
            Path.Combine(
                TestContext.CurrentContext.TestDirectory,
                "..",
                "..",
                "..",
                "..",
                "Grace.Operations.Data",
                "Migrations",
                "OperationsDbContextModelSnapshot.fs"
            )
            |> Path.GetFullPath

        let snapshotSource = File.ReadAllText snapshotPath
        let snapshotModelStart = snapshotSource.IndexOf("override _.BuildModel(modelBuilder: ModelBuilder) =", StringComparison.Ordinal)
        Assert.That(snapshotModelStart, Is.GreaterThanOrEqualTo(0))
        let snapshotModelSource = snapshotSource.Substring(snapshotModelStart)

        use context = OperationsDbContextFactory.create "Server=(localdb)\\MSSQLLocalDB;Database=GraceOperationsChargePreviewModel;Integrated Security=true;"
        let runtime = context.GetService<IDesignTimeModel>().Model
        let snapshot = OperationsDbContextModelSnapshot().Model
        let migration = AddChargePreviewLines().TargetModel

        let modelShape (model: Microsoft.EntityFrameworkCore.Metadata.IModel) =
            model.GetEntityTypes()
            |> Seq.map (fun entity ->
                let keys =
                    entity.GetKeys()
                    |> Seq.map (fun key ->
                        key.GetName(),
                        key.Properties
                        |> Seq.map (fun property -> property.Name)
                        |> Seq.toList)
                    |> Set.ofSeq

                let properties =
                    entity.GetProperties()
                    |> Seq.map (fun property -> property.Name, property.ClrType.FullName, property.IsNullable)
                    |> Set.ofSeq

                let indexes =
                    entity.GetIndexes()
                    |> Seq.map (fun index ->
                        index.GetDatabaseName(),
                        index.IsUnique,
                        index.Properties
                        |> Seq.map (fun property -> property.Name)
                        |> Seq.toList)
                    |> Set.ofSeq

                let foreignKeys =
                    entity.GetForeignKeys()
                    |> Seq.map (fun foreignKey ->
                        foreignKey.GetConstraintName(),
                        foreignKey.PrincipalEntityType.Name,
                        foreignKey.DeleteBehavior,
                        foreignKey.Properties
                        |> Seq.map (fun property -> property.Name)
                        |> Seq.toList)
                    |> Set.ofSeq

                let checkConstraints =
                    entity.GetCheckConstraints()
                    |> Seq.map (fun constraint' -> constraint'.Name, constraint'.Sql)
                    |> Set.ofSeq

                entity.Name, entity.GetSchema(), entity.GetTableName(), keys, properties, indexes, foreignKeys, checkConstraints)
            |> Set.ofSeq

        let runtimeShape = modelShape runtime
        let snapshotShape = modelShape snapshot
        let migrationShape = modelShape migration
        let preview = runtime.FindEntityType(typeof<ChargePreviewLineEntity>)

        ChargePreviewTestData.multiple (fun () ->
            Assert.That(targetModelSource, Does.Not.Match(@"\bOperations[A-Za-z0-9_]*(?:Sql|Model|Configuration|Options|Schema)\."))
            Assert.That(snapshotModelSource, Does.Not.Match(@"\bOperations[A-Za-z0-9_]*(?:Sql|Model|Configuration|Options|Schema)\."))
            Assert.That(targetModelSource, Does.Not.Match(@"(?im)^\s*[A-Za-z0-9_.]*(?:configure|configureModel)\s+modelBuilder\s*$"))
            Assert.That(snapshotModelSource, Does.Not.Match(@"(?im)^\s*[A-Za-z0-9_.]*(?:configure|configureModel)\s+modelBuilder\s*$"))
            Assert.That(targetModelSource, Does.Contain("modelBuilder.HasDefaultSchema(\"ops\")"))
            Assert.That(snapshotModelSource, Does.Contain("modelBuilder.HasDefaultSchema(\"ops\")"))
            Assert.That(targetModelSource, Does.Contain("let rawFact = modelBuilder.Entity<RawUsageFactEntity>()"))
            Assert.That(snapshotModelSource, Does.Contain("let rawFact = modelBuilder.Entity<RawUsageFactEntity>()"))
            Assert.That(targetModelSource, Does.Contain("let line = modelBuilder.Entity<ChargePreviewLineEntity>()"))
            Assert.That(snapshotModelSource, Does.Contain("let line = modelBuilder.Entity<ChargePreviewLineEntity>()"))
            Assert.That((migrationShape = snapshotShape), Is.True)

            Assert.That(
                (migrationShape = runtimeShape),
                Is.True,
                $"Migration-only: {Set.difference migrationShape runtimeShape}; runtime-only: {Set.difference runtimeShape migrationShape}"
            )

            Assert.That(
                preview.GetIndexes()
                |> Seq.map (fun index -> index.GetDatabaseName()),
                Does.Contain(OperationsChargePreviewSql.GrainIndexName)
            )

            Assert.That(
                preview.GetIndexes()
                |> Seq.map (fun index -> index.GetDatabaseName()),
                Does.Contain(OperationsChargePreviewSql.ScopeIndexName)
            ))

    /// Verifies the transactional insertion path avoids resumable-CE enumeration while preserving ordered indexing.
    [<Test>]
    member _.RebuildInsertionUsesFs3511SafeIndexedLoop() =
        let sourcePath =
            Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "Grace.Operations.Data", "OperationsChargePreview.fs")
            |> Path.GetFullPath

        let source = File.ReadAllText sourcePath
        let rebuildStart = source.IndexOf("member _.RebuildAsync(scope, cancellationToken) =", StringComparison.Ordinal)
        Assert.That(rebuildStart, Is.GreaterThanOrEqualTo(0))
        let rebuildSource = source.Substring(rebuildStart)

        ChargePreviewTestData.multiple (fun () ->
            Assert.That(rebuildSource, Does.Not.Match(@"\bfor\s+line\s+in\s+lines\s+do\b"))
            Assert.That(rebuildSource, Does.Contain("while lineIndex < lines.Length do"))
            Assert.That(rebuildSource, Does.Contain("let line = lines[lineIndex]"))
            Assert.That(rebuildSource, Does.Contain("lineIndex <- lineIndex + 1")))

    /// Verifies generated migration SQL contains the atomic identity constraints expected by SQL Server.
    [<Test>]
    member _.PreviewMigrationScriptContainsConstraintsAndCompleteGrainIndex() =
        use context =
            OperationsDbContextFactory.create "Server=(localdb)\\MSSQLLocalDB;Database=GraceOperationsChargePreviewMigration;Integrated Security=true;"

        let script = context.GetService<IMigrator>().GenerateScript()

        ChargePreviewTestData.multiple (fun () ->
            Assert.That(script, Does.Contain("CREATE TABLE ops.ChargePreviewLine"))
            Assert.That(script, Does.Contain("CK_ops_ChargePreviewLine_EffectiveRange"))
            Assert.That(script, Does.Contain("UX_ops_ChargePreviewLine_CompleteGrain"))
            Assert.That(script, Does.Contain("CurrencyCode varchar(3) COLLATE Latin1_General_100_BIN2")))

    /// Verifies runtime SQL uses parameterized scope values and transaction-owned application locking.
    [<Test>]
    member _.RebuildSourceAndMutationSqlAvoidValueInterpolationAndRequireScopeParameters() =
        let source = OperationsChargePreviewSql.SelectSourceAndPricing

        ChargePreviewTestData.multiple (fun () ->
            Assert.That(source, Does.Not.Contain("@" + "Customer" + "Id"))
            Assert.That(source, Does.Contain("@RepositoryId"))
            Assert.That(OperationsChargePreviewSql.AcquireScopeLock, Does.Contain("sys.sp_getapplock"))
            Assert.That(OperationsChargePreviewSql.AcquireScopeLock, Does.Contain("@LockOwner='Transaction'"))
            Assert.That(OperationsChargePreviewSql.DeleteScope, Does.Contain("PeriodFromUtc=@PeriodFromUtc"))
            Assert.That(OperationsChargePreviewSql.DeleteScope, Does.Contain("PeriodToUtc=@PeriodToUtc")))

    /// Verifies invalid or non-UTC periods fail before a SQL connection or mutation can begin.
    [<Test>]
    member _.InvalidPeriodIsRejectedBeforeSqlMutation() =
        task {
            let invalidScope = { ChargePreviewTestData.scope with PeriodToUtc = ChargePreviewTestData.scope.PeriodFromUtc }

            let rebuilder = SqlChargePreviewRebuilder("invalid connection string") :> IChargePreviewRebuilder

            try
                let! _ = rebuilder.RebuildAsync(invalidScope, CancellationToken.None)
                Assert.Fail("Expected the empty preview period to be rejected.")
            with
            | :? ArgumentException as ex -> Assert.That(ex.ParamName, Is.EqualTo("scope"))
        }

    /// Verifies the worker exposes the callable rebuild service without registering scheduling state.
    [<Test>]
    member _.WorkerRegistersCallablePreviewRebuilderOnly() =
        let sourcePath =
            IO.Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "Grace.Operations.Worker", "Program.fs")
            |> IO.Path.GetFullPath

        let source = IO.File.ReadAllText sourcePath

        ChargePreviewTestData.multiple (fun () ->
            Assert.That(source, Does.Contain("AddSingleton<IChargePreviewRebuilder>"))
            Assert.That(source, Does.Not.Contain("ChargePreviewWorkerService"))
            Assert.That(source, Does.Not.Contain("AddHostedService<ChargePreview")))
