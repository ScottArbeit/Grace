namespace Grace.Operations.Tests

open Grace.Operations.Data
open Grace.Operations.Data.Migrations
open Grace.Types.Common
open Grace.Types.Usage
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Infrastructure
open Microsoft.EntityFrameworkCore.Migrations
open Microsoft.EntityFrameworkCore.Metadata
open NodaTime
open NUnit.Framework
open System
open System.IO

/// Provides deterministic pricing catalog rows for Operations pricing tests.
module OperationsPricingTestData =

    /// Provides the customer that owns the default pricing assignment.
    let customerId = Guid.Parse("57400000-1000-0000-0000-000000000001")

    /// Provides a second customer used to prove customer isolation.
    let otherCustomerId = Guid.Parse("57400000-1000-0000-0000-000000000002")

    /// Provides the owner used by the default customer assignment.
    let ownerId = OwnerId.Parse("57400000-2000-0000-0000-000000000001")

    /// Provides the organization used by the default customer assignment.
    let organizationId = OrganizationId.Parse("57400000-3000-0000-0000-000000000001")

    /// Provides the repository used by the default customer assignment.
    let repositoryId = RepositoryId.Parse("57400000-4000-0000-0000-000000000001")

    /// Provides a second repository used to prove repository isolation.
    let otherRepositoryId = RepositoryId.Parse("57400000-4000-0000-0000-000000000002")

    /// Provides the pricing plan selected by the default customer assignment.
    let planId = Guid.Parse("57400000-5000-0000-0000-000000000001")

    /// Provides the internal billable usage-kind id for repository storage.
    let repositoryStorageBillableUsageKind = 1

    /// Provides the start of the initial pricing window.
    let julyStart = Instant.FromUtc(2026, 7, 1, 0, 0)

    /// Provides the first instant covered by a future pricing rate.
    let augustStart = Instant.FromUtc(2026, 8, 1, 0, 0)

    /// Builds a query for repository storage pricing at the supplied timestamp.
    let query observedAt =
        {
            CustomerId = customerId
            OwnerId = ownerId
            OrganizationId = organizationId
            RepositoryId = repositoryId
            FactKind = UsageFactKind.RepositoryStorageBytesMinute
            ObservedAt = observedAt
        }

    /// Provides one active pricing plan for selection tests.
    let plan = { PricingPlanId = planId; PlanCode = "grace-standard-v1"; EffectiveFrom = julyStart; EffectiveTo = None }

    /// Provides one active usage-kind mapping for repository storage.
    let mapping =
        {
            BillableUsageKindMappingId = Guid.Parse("57400000-6000-0000-0000-000000000001")
            FactKind = UsageFactKind.RepositoryStorageBytesMinute
            BillableUsageKind = repositoryStorageBillableUsageKind
            EffectiveFrom = julyStart
            EffectiveTo = None
        }

    /// Provides the initial customer rate for repository storage.
    let julyRate =
        {
            PricingRateId = Guid.Parse("57400000-7000-0000-0000-000000000001")
            PricingPlanId = planId
            BillableUsageKind = repositoryStorageBillableUsageKind
            CurrencyCode = "USD"
            UnitName = "byte-minute"
            UnitQuantity = 1_073_741_824L
            UnitPriceMicros = 10L
            EffectiveFrom = julyStart
            EffectiveTo = Some augustStart
        }

    /// Provides a future rate that must not rewrite historical July selections.
    let augustRate =
        {
            PricingRateId = Guid.Parse("57400000-7000-0000-0000-000000000002")
            PricingPlanId = planId
            BillableUsageKind = repositoryStorageBillableUsageKind
            CurrencyCode = "USD"
            UnitName = "byte-minute"
            UnitQuantity = 1_073_741_824L
            UnitPriceMicros = 20L
            EffectiveFrom = augustStart
            EffectiveTo = None
        }

    /// Provides the customer assignment that makes the plan selectable for one repository scope.
    let assignment =
        {
            CustomerPricingAssignmentId = Guid.Parse("57400000-8000-0000-0000-000000000001")
            CustomerId = customerId
            OwnerId = ownerId
            OrganizationId = organizationId
            RepositoryId = repositoryId
            PricingPlanId = planId
            EffectiveFrom = julyStart
            EffectiveTo = None
        }

    /// Provides a complete catalog with one customer assignment and two effective rates.
    let catalog =
        {
            PricingPlans = [ plan ]
            BillableUsageKindMappings = [ mapping ]
            PricingRates = [ julyRate; augustRate ]
            CustomerPricingAssignments = [ assignment ]
        }

/// Covers Operations pricing schema, seed, and deterministic selection behavior.
[<TestFixture>]
type OperationsPricingTests() =

    /// Generates the Operations migration script without connecting to SQL Server.
    let migrationScript () =
        use context =
            OperationsDbContextFactory.create "Server=(localdb)\\MSSQLLocalDB;Database=GraceOperationsPricingMigrationScript;Integrated Security=true;"

        let migrator = context.GetService<IMigrator>()
        migrator.GenerateScript(options = MigrationsSqlGenerationOptions.Idempotent)

    /// Reads the EF entity metadata that future migrations use for pricing schema drift.
    let entityType (entityClrType: Type) : IEntityType =
        use context = OperationsDbContextFactory.create "Server=(localdb)\\MSSQLLocalDB;Database=GraceOperationsPricingMigrationModel;Integrated Security=true;"

        context.Model.FindEntityType(entityClrType)

    /// Resolves the Operations root from the compiled test assembly.
    let operationsRoot () =
        let rec find (directory: DirectoryInfo) =
            let candidate = Path.Combine(directory.FullName, "Grace.Operations.slnx")

            if File.Exists candidate then
                directory.FullName
            elif isNull directory.Parent then
                failwith $"Could not find Grace.Operations.slnx above {TestContext.CurrentContext.TestDirectory}."
            else
                find directory.Parent

        find (DirectoryInfo(TestContext.CurrentContext.TestDirectory))

    /// Verifies effective-rate selection requires customer assignment, mapping, and rate rows.
    [<Test>]
    member _.EffectiveRateSelectionFindsCustomerRateForBillableUsageKind() =
        let selected = PricingRateSelection.trySelect (OperationsPricingTestData.query (Instant.FromUtc(2026, 7, 15, 12, 0))) OperationsPricingTestData.catalog

        Assert.Multiple(
            Action (fun () ->
                Assert.That(selected.IsSome, Is.True)
                Assert.That(selected.Value.CustomerId, Is.EqualTo(OperationsPricingTestData.customerId))
                Assert.That(selected.Value.PricingPlanId, Is.EqualTo(OperationsPricingTestData.planId))
                Assert.That(selected.Value.PricingRateId, Is.EqualTo(OperationsPricingTestData.julyRate.PricingRateId))
                Assert.That(selected.Value.BillableUsageKind, Is.EqualTo(OperationsPricingTestData.repositoryStorageBillableUsageKind))
                Assert.That(selected.Value.UnitPriceMicros, Is.EqualTo(10L)))
        )

    /// Verifies tracked usage is not billable unless an explicit usage-kind mapping exists.
    [<Test>]
    member _.TrackedUsageWithoutBillableMappingSelectsNoRate() =
        let catalog = { OperationsPricingTestData.catalog with BillableUsageKindMappings = [] }

        let selected = PricingRateSelection.trySelect (OperationsPricingTestData.query (Instant.FromUtc(2026, 7, 15, 12, 0))) catalog

        Assert.That(selected, Is.EqualTo(None))

    /// Verifies mapped usage is still not billable unless the assigned plan has an effective rate.
    [<Test>]
    member _.BillableMappingWithoutEffectiveRateSelectsNoRate() =
        let catalog = { OperationsPricingTestData.catalog with PricingRates = [] }

        let selected = PricingRateSelection.trySelect (OperationsPricingTestData.query (Instant.FromUtc(2026, 7, 15, 12, 0))) catalog

        Assert.That(selected, Is.EqualTo(None))

    /// Verifies half-open effective windows preserve historical rate meaning after a later rate starts.
    [<Test>]
    member _.FutureRateChangePreservesHistoricalSelectionAtEffectiveBoundary() =
        let beforeBoundary =
            PricingRateSelection.trySelect (OperationsPricingTestData.query (Instant.FromUtc(2026, 7, 31, 23, 59))) OperationsPricingTestData.catalog

        let atBoundary =
            PricingRateSelection.trySelect (OperationsPricingTestData.query OperationsPricingTestData.augustStart) OperationsPricingTestData.catalog

        Assert.Multiple(
            Action (fun () ->
                Assert.That(beforeBoundary.Value.PricingRateId, Is.EqualTo(OperationsPricingTestData.julyRate.PricingRateId))
                Assert.That(beforeBoundary.Value.UnitPriceMicros, Is.EqualTo(10L))
                Assert.That(atBoundary.Value.PricingRateId, Is.EqualTo(OperationsPricingTestData.augustRate.PricingRateId))
                Assert.That(atBoundary.Value.UnitPriceMicros, Is.EqualTo(20L)))
        )

    /// Verifies customer and repository identifiers must both match before pricing can be selected.
    [<Test>]
    member _.EffectiveRateSelectionDoesNotLeakAcrossCustomerOrRepositoryScope() =
        let otherCustomerQuery =
            { OperationsPricingTestData.query (Instant.FromUtc(2026, 7, 15, 12, 0)) with CustomerId = OperationsPricingTestData.otherCustomerId }

        let otherRepositoryQuery =
            { OperationsPricingTestData.query (Instant.FromUtc(2026, 7, 15, 12, 0)) with RepositoryId = OperationsPricingTestData.otherRepositoryId }

        Assert.Multiple(
            Action (fun () ->
                Assert.That(PricingRateSelection.trySelect otherCustomerQuery OperationsPricingTestData.catalog, Is.EqualTo(None))
                Assert.That(PricingRateSelection.trySelect otherRepositoryQuery OperationsPricingTestData.catalog, Is.EqualTo(None)))
        )

    /// Verifies pricing entities are part of the runtime EF model with the expected keys and lookup indexes.
    [<Test>]
    member _.OperationsEfModelContainsPricingFoundationEntities() =
        let plan = entityType typeof<PricingPlanEntity>
        let mapping = entityType typeof<BillableUsageKindMappingEntity>
        let rate = entityType typeof<PricingRateEntity>
        let assignment = entityType typeof<CustomerPricingAssignmentEntity>

        let hasRateIndex =
            rate.GetIndexes()
            |> Seq.exists (fun index -> index.GetDatabaseName() = OperationsPricingSql.PricingRateEffectiveIndexName)

        let hasAssignmentIndex =
            assignment.GetIndexes()
            |> Seq.exists (fun index -> index.GetDatabaseName() = OperationsPricingSql.CustomerPricingAssignmentScopeIndexName)

        Assert.Multiple(
            Action (fun () ->
                Assert.That(plan.GetTableName(), Is.EqualTo(OperationsPricingSql.PricingPlanTableName))
                Assert.That(plan.FindPrimaryKey().GetName(), Is.EqualTo("PK_ops_PricingPlan"))
                Assert.That(mapping.GetTableName(), Is.EqualTo(OperationsPricingSql.BillableUsageKindMappingTableName))
                Assert.That(rate.GetTableName(), Is.EqualTo(OperationsPricingSql.PricingRateTableName))
                Assert.That(assignment.GetTableName(), Is.EqualTo(OperationsPricingSql.CustomerPricingAssignmentTableName))
                Assert.That(hasRateIndex, Is.True)
                Assert.That(hasAssignmentIndex, Is.True))
        )

    /// Verifies the latest model snapshot carries pricing entities for future migration drift checks.
    [<Test>]
    member _.OperationsModelSnapshotContainsPricingFoundationEntities() =
        let snapshot = OperationsDbContextModelSnapshot()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(snapshot.Model.FindEntityType(typeof<PricingPlanEntity>), Is.Not.Null)
                Assert.That(snapshot.Model.FindEntityType(typeof<BillableUsageKindMappingEntity>), Is.Not.Null)
                Assert.That(snapshot.Model.FindEntityType(typeof<PricingRateEntity>), Is.Not.Null)
                Assert.That(snapshot.Model.FindEntityType(typeof<CustomerPricingAssignmentEntity>), Is.Not.Null))
        )

    /// Verifies the migration script creates pricing tables and overlap rejection instead of relying on query tie-breaks.
    [<Test>]
    member _.PricingMigrationScriptContainsEffectiveDatingTablesAndOverlapRejection() =
        let script = migrationScript ()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(script, Does.Contain("CREATE TABLE ops.PricingPlan"))
                Assert.That(script, Does.Contain("CREATE TABLE ops.BillableUsageKindMapping"))
                Assert.That(script, Does.Contain("CREATE TABLE ops.PricingRate"))
                Assert.That(script, Does.Contain("CREATE TABLE ops.CustomerPricingAssignment"))
                Assert.That(script, Does.Contain("CK_ops_PricingRate_EffectiveRange"))
                Assert.That(script, Does.Contain(OperationsPricingSql.PricingRateOverlapTriggerName))
                Assert.That(script, Does.Contain(OperationsPricingSql.CustomerPricingAssignmentOverlapTriggerName))
                Assert.That(script, Does.Contain("effective windows cannot overlap")))
        )

    /// Verifies the SQL selector cannot return a customer rate through missing mapping or loose scope matching.
    [<Test>]
    member _.EffectiveRateSqlRequiresExplicitMappingRateAndRepositoryScope() =
        let sql = OperationsPricingSql.SelectEffectivePricingRate

        Assert.Multiple(
            Action (fun () ->
                Assert.That(sql, Does.Contain("INNER JOIN ops.BillableUsageKindMapping AS mapping"))
                Assert.That(sql, Does.Contain("INNER JOIN ops.PricingRate AS rate"))
                Assert.That(sql, Does.Contain("assignment.CustomerId = @CustomerId"))
                Assert.That(sql, Does.Contain("assignment.OwnerId = @OwnerId"))
                Assert.That(sql, Does.Contain("assignment.OrganizationId = @OrganizationId"))
                Assert.That(sql, Does.Contain("assignment.RepositoryId = @RepositoryId"))
                Assert.That(sql, Does.Contain("mapping.FactKind = @FactKind"))
                Assert.That(sql, Does.Contain("ORDER BY")))
        )

    /// Verifies the initial seed script exists and does not assign customers or post charges.
    [<Test>]
    member _.InitialPricingSeedScriptDocumentsFoundationRowsOnly() =
        let seedPath = Path.Combine(operationsRoot (), "scripts", "seed-pricing-foundation.sql")
        Assert.That(File.Exists seedPath, Is.True)
        let script = File.ReadAllText seedPath

        Assert.Multiple(
            Action (fun () ->
                Assert.That(script, Does.Contain("ops.PricingPlan"))
                Assert.That(script, Does.Contain("ops.BillableUsageKindMapping"))
                Assert.That(script, Does.Contain("ops.PricingRate"))
                Assert.That(script, Does.Not.Contain("INSERT INTO ops.CustomerPricingAssignment"))
                Assert.That(script, Does.Not.Contain("ChargeLedger")))
        )
