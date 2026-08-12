namespace Grace.Operations.Tests

open Grace.Operations.Data
open Grace.Operations.Worker
open Grace.Types.Common
open Grace.Types.Usage
open Microsoft.Data.SqlClient
open NUnit.Framework
open NodaTime
open System
open System.Threading
open System.Threading.Tasks

/// Pauses or fails one real billing-close transaction only after a named durable stage.
type private BillingCloseInterleaving(stage: string, pause: bool) =
    let reached = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    /// Fails or pauses the requested stage without writing a test-only database substitute.
    let observe expected (cancellationToken: CancellationToken) =
        task {
            if stage = expected then
                reached.TrySetResult() |> ignore

                if pause then
                    do! release.Task.WaitAsync(cancellationToken)
                else
                    return raise (InvalidOperationException($"Injected billing-close failure after {expected}."))
        }
        :> Task

    /// Completes when the selected production close stage has run under the held SQL scope lock.
    member _.Reached = reached.Task

    /// Allows a paused production close transaction to proceed.
    member _.Release() = release.TrySetResult() |> ignore

    interface IBillingPeriodCloseTransactionInterleaving with
        member _.AfterPreviewReplacementAsync cancellationToken = observe "preview" cancellationToken
        member _.AfterLedgerInsertionAsync cancellationToken = observe "ledger" cancellationToken
        member _.AfterCloseEvidenceStagedAsync cancellationToken = observe "evidence" cancellationToken

/// Proves billing-period close against isolated real SQL Server databases and production acceptance/close seams.
[<TestFixture>]
[<NonParallelizable>]
type OperationsBillingPeriodCloseTests() =

    /// Names the explicit isolated SQL Server connection used by Operations real-database proofs.
    [<Literal>]
    let sqlConnectionStringEnvironmentVariable = "GRACE_OPERATIONS_SQL_TEST_CONNECTION_STRING"

    /// Returns an old UTC billing month whose preview and close thresholds are already eligible on the database clock.
    let monthStart = Instant.FromUtc(2026, 6, 1, 0, 0)

    /// Creates one test database name that cannot share durable state with another proof.
    let databaseName () = $"GraceOperationsBillingClose_{Guid.NewGuid():N}"

    /// Gets the required SQL connection or marks the fixture skipped instead of silently passing without SQL.
    let requireSqlConnectionString () =
        let connectionString = Environment.GetEnvironmentVariable sqlConnectionStringEnvironmentVariable

        if String.IsNullOrWhiteSpace connectionString then
            Assert.Ignore($"{sqlConnectionStringEnvironmentVariable} is required for real SQL billing-period close tests.")

        connectionString

    /// Creates and migrates an isolated database through the production Operations schema initializer.
    let createDatabaseAsync () =
        task {
            let builder = SqlConnectionStringBuilder(requireSqlConnectionString ())
            builder.InitialCatalog <- databaseName ()
            let schema = OperationsUsageSchema(builder.ConnectionString, OperationsUsageSchemaBootstrapMode.CreateDatabaseIfMissing)
            do! schema.EnsureCreatedAsync CancellationToken.None
            return builder.ConnectionString
        }

    /// Deletes a fixture database even when its assertion fails.
    let dropDatabaseAsync connectionString =
        task {
            let builder = SqlConnectionStringBuilder(connectionString)
            let database = builder.InitialCatalog
            builder.InitialCatalog <- "master"
            use connection = new SqlConnection(builder.ConnectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()
            command.CommandText <- $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];"
            let! _ = command.ExecuteNonQueryAsync CancellationToken.None
            return ()
        }

    /// Runs one SQL proof with deterministic database cleanup on every outcome.
    let withDatabaseAsync operation =
        task {
            let! connectionString = createDatabaseAsync ()

            try
                let! result = operation connectionString
                do! dropDatabaseAsync connectionString
                return result
            with
            | ex ->
                do! dropDatabaseAsync connectionString
                return raise ex
        }

    /// Derives one valid exact owner/repository/month scope from the supplied identities.
    let scopeFor ownerId organizationId repositoryId =
        match BillingCompletenessScope.tryCreate ownerId organizationId repositoryId monthStart with
        | Ok scope -> scope
        | Error errors -> invalidOp (String.Join("; ", errors))

    /// Builds supported repository-storage usage that flows through the production immutable journal.
    let usageFact usageFactId ownerId organizationId repositoryId observedAt quantity =
        UsageFact.RepositoryStorageBytesMinute(
            usageFactId,
            CorrelationId $"billing-close-{usageFactId:D}",
            ownerId,
            organizationId,
            repositoryId,
            StoragePoolId "billing-close-pool",
            quantity,
            observedAt
        )

    /// Accepts one fact through production journal processing so raw, aggregate, and journal state share one transaction.
    let acceptAsync connectionString fact =
        task {
            let journal = SqlOperationsUsageJournalStore(connectionString)
            let! _ = journal.AppendAsync(fact, CancellationToken.None)
            let! result = journal.ProcessAsync(fact, Array.empty, CancellationToken.None)
            Assert.That(result, Is.EqualTo(UsageFactJournalProcessResult.AcceptedFromJournal))
        }

    /// Adds complete current pricing through parameterized SQL so real schema triggers remain part of the fixture.
    let addPricingAsync connectionString scope =
        task {
            let planId = Guid.NewGuid()
            let mappingId = Guid.NewGuid()
            let rateId = Guid.NewGuid()
            let assignmentId = Guid.NewGuid()
            let effectiveFrom = scope.MonthStart.ToDateTimeUtc()
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()

            command.CommandText <-
                """
INSERT INTO ops.PricingPlan (PricingPlanId,PlanCode,DisplayName,EffectiveFromUtc)
VALUES (@PlanId,@PlanCode,@DisplayName,@EffectiveFrom);
INSERT INTO ops.BillableUsageKindMapping (BillableUsageKindMappingId,FactKind,BillableUsageKind,DisplayName,EffectiveFromUtc)
VALUES (@MappingId,1,101,@MappingName,@EffectiveFrom);
INSERT INTO ops.PricingRate (PricingRateId,PricingPlanId,BillableUsageKind,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc)
VALUES (@RateId,@PlanId,101,'USD','byte-minute',1,2,@EffectiveFrom);
INSERT INTO ops.PricingAssignment (PricingAssignmentId,OwnerId,OrganizationId,RepositoryId,PricingPlanId,EffectiveFromUtc)
VALUES (@AssignmentId,@OwnerId,@OrganizationId,@RepositoryId,@PlanId,@EffectiveFrom);
"""

            command.Parameters.Add("@PlanId", System.Data.SqlDbType.UniqueIdentifier).Value <- planId
            command.Parameters.Add("@PlanCode", System.Data.SqlDbType.NVarChar, 80).Value <- $"close-{planId:N}"
            command.Parameters.Add("@DisplayName", System.Data.SqlDbType.NVarChar, 200).Value <- "Billing close test plan"
            command.Parameters.Add("@MappingId", System.Data.SqlDbType.UniqueIdentifier).Value <- mappingId
            command.Parameters.Add("@MappingName", System.Data.SqlDbType.NVarChar, 200).Value <- "Storage byte minute"
            command.Parameters.Add("@RateId", System.Data.SqlDbType.UniqueIdentifier).Value <- rateId
            command.Parameters.Add("@AssignmentId", System.Data.SqlDbType.UniqueIdentifier).Value <- assignmentId
            command.Parameters.Add("@OwnerId", System.Data.SqlDbType.UniqueIdentifier).Value <- scope.OwnerId
            command.Parameters.Add("@OrganizationId", System.Data.SqlDbType.UniqueIdentifier).Value <- scope.OrganizationId
            command.Parameters.Add("@RepositoryId", System.Data.SqlDbType.UniqueIdentifier).Value <- scope.RepositoryId
            command.Parameters.Add("@EffectiveFrom", System.Data.SqlDbType.DateTime2).Value <- effectiveFrom
            let! _ = command.ExecuteNonQueryAsync CancellationToken.None
            return ()
        }

    /// Adds one complete effective pricing grain for a chosen half-open interval through the real Operations schema.
    let addPricingWindowAsync connectionString (scope: BillingCompletenessScope) factKind (effectiveFrom: DateTime) (effectiveTo: DateTime) =
        task {
            let planId = Guid.NewGuid()
            let mappingId = Guid.NewGuid()
            let rateId = Guid.NewGuid()
            let assignmentId = Guid.NewGuid()
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()

            command.CommandText <-
                """
INSERT INTO ops.PricingPlan (PricingPlanId,PlanCode,DisplayName,EffectiveFromUtc,EffectiveToUtc)
VALUES (@PlanId,@PlanCode,@DisplayName,@EffectiveFrom,@EffectiveTo);
INSERT INTO ops.BillableUsageKindMapping (BillableUsageKindMappingId,FactKind,BillableUsageKind,DisplayName,EffectiveFromUtc,EffectiveToUtc)
VALUES (@MappingId,@FactKind,@BillableUsageKind,@MappingName,@EffectiveFrom,@EffectiveTo);
INSERT INTO ops.PricingRate (PricingRateId,PricingPlanId,BillableUsageKind,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc,EffectiveToUtc)
VALUES (@RateId,@PlanId,@BillableUsageKind,'USD','byte-minute',1,2,@EffectiveFrom,@EffectiveTo);
INSERT INTO ops.PricingAssignment (PricingAssignmentId,OwnerId,OrganizationId,RepositoryId,PricingPlanId,EffectiveFromUtc,EffectiveToUtc)
VALUES (@AssignmentId,@OwnerId,@OrganizationId,@RepositoryId,@PlanId,@EffectiveFrom,@EffectiveTo);
"""

            command.Parameters.Add("@PlanId", System.Data.SqlDbType.UniqueIdentifier).Value <- planId
            command.Parameters.Add("@PlanCode", System.Data.SqlDbType.NVarChar, 80).Value <- $"close-window-{planId:N}"
            command.Parameters.Add("@DisplayName", System.Data.SqlDbType.NVarChar, 200).Value <- "Billing close test window"
            command.Parameters.Add("@MappingId", System.Data.SqlDbType.UniqueIdentifier).Value <- mappingId
            command.Parameters.Add("@MappingName", System.Data.SqlDbType.NVarChar, 200).Value <- "Storage byte minute"
            command.Parameters.Add("@FactKind", System.Data.SqlDbType.Int).Value <- factKind
            command.Parameters.Add("@BillableUsageKind", System.Data.SqlDbType.Int).Value <- 100 + factKind
            command.Parameters.Add("@RateId", System.Data.SqlDbType.UniqueIdentifier).Value <- rateId
            command.Parameters.Add("@AssignmentId", System.Data.SqlDbType.UniqueIdentifier).Value <- assignmentId
            command.Parameters.Add("@OwnerId", System.Data.SqlDbType.UniqueIdentifier).Value <- scope.OwnerId
            command.Parameters.Add("@OrganizationId", System.Data.SqlDbType.UniqueIdentifier).Value <- scope.OrganizationId
            command.Parameters.Add("@RepositoryId", System.Data.SqlDbType.UniqueIdentifier).Value <- scope.RepositoryId
            command.Parameters.Add("@EffectiveFrom", System.Data.SqlDbType.DateTime2).Value <- effectiveFrom
            command.Parameters.Add("@EffectiveTo", System.Data.SqlDbType.DateTime2).Value <- effectiveTo
            let! _ = command.ExecuteNonQueryAsync CancellationToken.None
            return ()
        }

    /// Executes a trusted scalar count command against the isolated SQL database.
    let countAsync connectionString sql =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()
            command.CommandText <- sql
            let! value = command.ExecuteScalarAsync CancellationToken.None
            return Convert.ToInt32 value
        }

    /// Builds the fixed scheduled-operation request used by all close proofs.
    let request scope = { Scope = scope; ScheduledOperationProvenance = "operations-tests/billing-period-close/v1" }

    /// Extracts the immutable posting count from a successful close result and rejects every nonterminal outcome.
    let closedChargeCount result =
        match result with
        | BillingPeriodCloseResult.Closed (_, count) -> count
        | _ ->
            Assert.Fail($"Expected a Closed billing-period result but received {result}.")
            Unchecked.defaultof<int>

    /// Proves terminal pricing scopes cannot exhaust a bounded discovery batch and every month in one assignment is emitted.
    [<Test>]
    member _.DiscoverySkipsClosedPricingScopesAndExpandsEveryCompletedAssignmentMonth() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, repositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let scope = scopeFor ownerId organizationId repositoryId
                use connection = new SqlConnection(connectionString)
                do! connection.OpenAsync CancellationToken.None
                use command = connection.CreateCommand()

                command.CommandText <-
                    """
DECLARE @PlanId uniqueidentifier = NEWID();
INSERT INTO ops.PricingPlan (PricingPlanId,PlanCode,DisplayName,EffectiveFromUtc)
VALUES (@PlanId, 'discovery-plan', 'Discovery plan', '2026-01-01T00:00:00');

DECLARE @index int = 0;
WHILE @index < 101
BEGIN
    DECLARE @OwnerId uniqueidentifier = CONVERT(uniqueidentifier, HASHBYTES('MD5', CONCAT('owner-', @index)));
    DECLARE @OrganizationId uniqueidentifier = CONVERT(uniqueidentifier, HASHBYTES('MD5', CONCAT('organization-', @index)));
    DECLARE @RepositoryId uniqueidentifier = CONVERT(uniqueidentifier, HASHBYTES('MD5', CONCAT('repository-', @index)));
    DECLARE @MonthStartUtc datetime2(7) = '2026-01-01T00:00:00';
    INSERT INTO ops.PricingAssignment (PricingAssignmentId,OwnerId,OrganizationId,RepositoryId,PricingPlanId,EffectiveFromUtc,EffectiveToUtc)
    VALUES (NEWID(), @OwnerId, @OrganizationId, @RepositoryId, @PlanId, @MonthStartUtc, '2026-02-01T00:00:00');
    INSERT INTO ops.BillingPeriod (BillingPeriodId,OwnerId,OrganizationId,RepositoryId,MonthStartUtc,NextMonthStartUtc,State)
    VALUES (NEWID(), @OwnerId, @OrganizationId, @RepositoryId, @MonthStartUtc, '2026-02-01T00:00:00', 2);
    SET @index = @index + 1;
END;

INSERT INTO ops.PricingAssignment (PricingAssignmentId,OwnerId,OrganizationId,RepositoryId,PricingPlanId,EffectiveFromUtc,EffectiveToUtc)
VALUES (NEWID(), @TargetOwnerId, @TargetOrganizationId, @TargetRepositoryId, @PlanId, '2026-01-01T00:00:00', '2026-07-01T00:00:00');
"""

                command.Parameters.Add("@TargetOwnerId", System.Data.SqlDbType.UniqueIdentifier).Value <- scope.OwnerId
                command.Parameters.Add("@TargetOrganizationId", System.Data.SqlDbType.UniqueIdentifier).Value <- scope.OrganizationId
                command.Parameters.Add("@TargetRepositoryId", System.Data.SqlDbType.UniqueIdentifier).Value <- scope.RepositoryId
                let! _ = command.ExecuteNonQueryAsync CancellationToken.None
                let! firstPass = OperationsBillingPeriodCloseDiscovery.discoverAsync connectionString CancellationToken.None
                let! repeatedPass = OperationsBillingPeriodCloseDiscovery.discoverAsync connectionString CancellationToken.None

                let months (scopes: BillingCompletenessScope array) : Instant array =
                    scopes
                    |> Array.filter (fun candidate ->
                        candidate.OwnerId = scope.OwnerId
                        && candidate.OrganizationId = scope.OrganizationId
                        && candidate.RepositoryId = scope.RepositoryId)
                    |> Array.map (fun candidate -> candidate.MonthStart)
                    |> Array.sort

                let expected =
                    [|
                        Instant.FromUtc(2026, 1, 1, 0, 0)
                        Instant.FromUtc(2026, 2, 1, 0, 0)
                        Instant.FromUtc(2026, 3, 1, 0, 0)
                        Instant.FromUtc(2026, 4, 1, 0, 0)
                        Instant.FromUtc(2026, 5, 1, 0, 0)
                        Instant.FromUtc(2026, 6, 1, 0, 0)
                    |]

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(firstPass.Length, Is.EqualTo(6))
                        Assert.That(months firstPass, Is.EqualTo(expected :> obj))
                        Assert.That(months repeatedPass, Is.EqualTo(expected :> obj)))
                )
            })

    /// Proves a later closable scope is reached after the first page remains occupied by retryable nonterminal periods.
    [<Test>]
    member _.DiscoveryCursorReachesLaterClosableScopeWithoutDiscardingPersistentBlockers() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, repositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let scope = scopeFor ownerId organizationId repositoryId
                use connection = new SqlConnection(connectionString)
                do! connection.OpenAsync CancellationToken.None
                use command = connection.CreateCommand()

                command.CommandText <-
                    """
DECLARE @index int = 0;
WHILE @index < 100
BEGIN
    INSERT INTO ops.BillingPeriod (BillingPeriodId,OwnerId,OrganizationId,RepositoryId,MonthStartUtc,NextMonthStartUtc,State,RetryDiagnostic,RetryDiagnosticAtUtc)
    VALUES
    (
        NEWID(),
        CONVERT(uniqueidentifier, HASHBYTES('MD5', CONCAT('blocked-owner-', @index))),
        CONVERT(uniqueidentifier, HASHBYTES('MD5', CONCAT('blocked-organization-', @index))),
        CONVERT(uniqueidentifier, HASHBYTES('MD5', CONCAT('blocked-repository-', @index))),
        '2026-01-01T00:00:00',
        '2026-02-01T00:00:00',
        0,
        'Missing pricing remains retryable.',
        SYSUTCDATETIME()
    );
    SET @index = @index + 1;
END;
"""

                let! _ = command.ExecuteNonQueryAsync CancellationToken.None
                do! addPricingAsync connectionString scope

                let! firstPage = OperationsBillingPeriodCloseDiscovery.discoverAsync connectionString CancellationToken.None

                let! secondPage =
                    OperationsBillingPeriodCloseDiscovery.discoverAfterAsync connectionString (Some firstPage[firstPage.Length - 1]) CancellationToken.None

                let targetDiscovered =
                    secondPage
                    |> Array.exists (fun candidate ->
                        candidate.OwnerId = scope.OwnerId
                        && candidate.OrganizationId = scope.OrganizationId
                        && candidate.RepositoryId = scope.RepositoryId
                        && candidate.MonthStart = scope.MonthStart)

                let closer = SqlBillingPeriodCloser(connectionString) :> IBillingPeriodCloser
                let! targetClose = closer.CloseAsync(request scope, CancellationToken.None)
                let! blockedCount = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriod WHERE State IN (0,1) AND RetryDiagnostic IS NOT NULL;"
                let! closedCount = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriod WHERE State = 2;"

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(firstPage.Length, Is.EqualTo(100))
                        Assert.That(targetDiscovered, Is.True)
                        Assert.That(closedChargeCount targetClose, Is.Zero)
                        Assert.That(blockedCount, Is.EqualTo(100))
                        Assert.That(closedCount, Is.EqualTo(1)))
                )
            })

    /// Proves accepted usage before close appears in exactly one immutable posting and does not create late work.
    [<Test>]
    member _.AcceptanceBeforeCloseIsIncludedWithoutLateWork() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, repositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let scope = scopeFor ownerId organizationId repositoryId
                let fact = usageFact (Guid.NewGuid()) ownerId organizationId repositoryId (monthStart + Duration.FromDays 2) 7L
                do! acceptAsync connectionString fact
                do! addPricingAsync connectionString scope
                let closer = SqlBillingPeriodCloser(connectionString) :> IBillingPeriodCloser
                let! closed = closer.CloseAsync(request scope, CancellationToken.None)
                let! chargeCount = countAsync connectionString "SELECT COUNT(*) FROM ops.Charge;"
                let! lateWorkCount = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriodLateWork;"
                let! evidenceCount = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriodCloseEvidence;"

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(closedChargeCount closed, Is.EqualTo(1))
                        Assert.That(chargeCount, Is.EqualTo(1))
                        Assert.That(lateWorkCount, Is.Zero)
                        Assert.That(evidenceCount, Is.EqualTo(1)))
                )
            })

    /// Proves a close holding the real scope lock wins before accepted usage and that the later acceptance becomes one handoff row.
    [<Test>]
    member _.CloseBeforeAcceptanceCreatesExactlyOneLateWorkRow() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, repositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let scope = scopeFor ownerId organizationId repositoryId
                do! addPricingAsync connectionString scope
                let interleaving = BillingCloseInterleaving("preview", true)

                let closer =
                    SqlBillingPeriodCloser.CreateForTest(connectionString, interleaving :> IBillingPeriodCloseTransactionInterleaving) :> IBillingPeriodCloser

                let closing = closer.CloseAsync(request scope, CancellationToken.None)
                do! interleaving.Reached.WaitAsync(TimeSpan.FromSeconds 10.0)
                let fact = usageFact (Guid.NewGuid()) ownerId organizationId repositoryId (monthStart + Duration.FromDays 3) 9L
                let accepting = acceptAsync connectionString fact
                interleaving.Release()
                let! _ = closing
                do! accepting
                let! chargeCount = countAsync connectionString "SELECT COUNT(*) FROM ops.Charge;"
                let! lateWorkCount = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriodLateWork;"

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(chargeCount, Is.Zero)
                        Assert.That(lateWorkCount, Is.EqualTo(1)))
                )
            })

    /// Proves an empty period remains nonterminal until complete pricing coverage exists, then closes and hands later usage to late work.
    [<Test>]
    member _.ZeroEntryPricingCoverageBlocksThenRetryClosesAndFirstAcceptanceCreatesLateWork() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, repositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let scope = scopeFor ownerId organizationId repositoryId
                let closer = SqlBillingPeriodCloser(connectionString) :> IBillingPeriodCloser
                let! blocked = closer.CloseAsync(request scope, CancellationToken.None)
                let! blockedChargeCount = countAsync connectionString "SELECT COUNT(*) FROM ops.Charge;"
                let! blockedEvidenceCount = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriodCloseEvidence;"

                let! blockedDiagnosticCount =
                    countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriod WHERE State IN (0, 1) AND RetryDiagnostic IS NOT NULL;"

                do! addPricingAsync connectionString scope
                let! closed = closer.CloseAsync(request scope, CancellationToken.None)
                let fact = usageFact (Guid.NewGuid()) ownerId organizationId repositoryId (monthStart + Duration.FromDays 4) 5L
                do! acceptAsync connectionString fact
                let! periodCount = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriod WHERE State = 2;"
                let! chargeCount = countAsync connectionString "SELECT COUNT(*) FROM ops.Charge;"
                let! lateWorkCount = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriodLateWork;"
                let! clearedDiagnosticCount = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriod WHERE RetryDiagnostic IS NOT NULL;"

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(
                            (match blocked with
                             | BillingPeriodCloseResult.Blocked _ -> true
                             | _ -> false),
                            Is.True
                        )

                        Assert.That(blockedChargeCount, Is.Zero)
                        Assert.That(blockedEvidenceCount, Is.Zero)
                        Assert.That(blockedDiagnosticCount, Is.EqualTo(1))
                        Assert.That(closedChargeCount closed, Is.Zero)
                        Assert.That(periodCount, Is.EqualTo(1))
                        Assert.That(chargeCount, Is.Zero)
                        Assert.That(lateWorkCount, Is.EqualTo(1))
                        Assert.That(clearedDiagnosticCount, Is.Zero))
                )
            })

    /// Proves adjacent effective grains collectively cover a zero-fact month while a one-tick gap remains retryable.
    [<Test>]
    member _.ZeroFactCloseAcceptsAdjacentCoverageAndRejectsOneTickGapUntilRepaired() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, repositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let adjacentScope = scopeFor ownerId organizationId repositoryId
                let gapScope = { adjacentScope with MonthStart = Instant.FromUtc(2026, 7, 1, 0, 0) }
                let monthStartUtc = adjacentScope.MonthStart.ToDateTimeUtc()

                let nextMonthStartUtc =
                    (BillingCompletenessScope.nextMonthStart adjacentScope)
                        .ToDateTimeUtc()

                let gapMonthStartUtc = gapScope.MonthStart.ToDateTimeUtc()

                let gapNextMonthStartUtc =
                    (BillingCompletenessScope.nextMonthStart gapScope)
                        .ToDateTimeUtc()

                let boundary = monthStartUtc.AddDays 15.0
                let gapBoundary = gapMonthStartUtc.AddDays 15.0
                let afterGapBoundary = gapBoundary.AddTicks 1L
                let closer = SqlBillingPeriodCloser(connectionString) :> IBillingPeriodCloser

                do! addPricingWindowAsync connectionString adjacentScope 1 monthStartUtc boundary
                do! addPricingWindowAsync connectionString adjacentScope 1 boundary nextMonthStartUtc
                let! adjacentResult = closer.CloseAsync(request adjacentScope, CancellationToken.None)

                do! addPricingWindowAsync connectionString gapScope 1 gapMonthStartUtc gapBoundary
                do! addPricingWindowAsync connectionString gapScope 1 afterGapBoundary gapNextMonthStartUtc
                let! gappedResult = closer.CloseAsync(request gapScope, CancellationToken.None)

                let! gappedPostingCount =
                    countAsync
                        connectionString
                        "SELECT COUNT(*) FROM ops.Charge WHERE BillingPeriodId IN (SELECT BillingPeriodId FROM ops.BillingPeriod WHERE RepositoryId <> '00000000-0000-0000-0000-000000000000');"

                let! gappedEvidenceCount = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriodCloseEvidence;"
                let! gappedDiagnosticCount = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriod WHERE RetryDiagnostic IS NOT NULL;"

                do! addPricingWindowAsync connectionString gapScope 1 gapBoundary afterGapBoundary
                let! repairedResult = closer.CloseAsync(request gapScope, CancellationToken.None)
                let! repairedDiagnosticCount = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriod WHERE RetryDiagnostic IS NOT NULL;"
                let! closedCount = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriod WHERE State = 2;"

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(closedChargeCount adjacentResult, Is.Zero)

                        Assert.That(
                            (match gappedResult with
                             | BillingPeriodCloseResult.Blocked _ -> true
                             | _ -> false),
                            Is.True
                        )

                        Assert.That(gappedPostingCount, Is.Zero)
                        Assert.That(gappedEvidenceCount, Is.EqualTo(1))
                        Assert.That(gappedDiagnosticCount, Is.EqualTo(1))
                        Assert.That(closedChargeCount repairedResult, Is.Zero)
                        Assert.That(repairedDiagnosticCount, Is.Zero)
                        Assert.That(closedCount, Is.EqualTo(2)))
                )
            })

    /// Proves missing pricing retains a nonterminal period with no posting and a bounded retry diagnostic.
    [<Test>]
    member _.MissingPricingBlocksWithoutPosting() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, repositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let scope = scopeFor ownerId organizationId repositoryId
                let fact = usageFact (Guid.NewGuid()) ownerId organizationId repositoryId (monthStart + Duration.FromDays 5) 3L
                do! acceptAsync connectionString fact
                let closer = SqlBillingPeriodCloser(connectionString) :> IBillingPeriodCloser
                let! result = closer.CloseAsync(request scope, CancellationToken.None)
                let! chargeCount = countAsync connectionString "SELECT COUNT(*) FROM ops.Charge;"
                let! blockedCount = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriod WHERE State IN (0, 1) AND RetryDiagnostic IS NOT NULL;"

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(
                            (match result with
                             | BillingPeriodCloseResult.Blocked _ -> true
                             | _ -> false),
                            Is.True
                        )

                        Assert.That(chargeCount, Is.Zero)
                        Assert.That(blockedCount, Is.EqualTo(1)))
                )
            })

    /// Proves every named mutation-stage failure rolls preview, charges, evidence, and terminal state back together.
    [<TestCase("preview")>]
    [<TestCase("ledger")>]
    [<TestCase("evidence")>]
    member _.InjectedCloseFailureRollsBackEveryStage(stage: string) =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, repositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let scope = scopeFor ownerId organizationId repositoryId
                let fact = usageFact (Guid.NewGuid()) ownerId organizationId repositoryId (monthStart + Duration.FromDays 6) 11L
                do! acceptAsync connectionString fact
                do! addPricingAsync connectionString scope
                let interleaving = BillingCloseInterleaving(stage, false)

                let closer =
                    SqlBillingPeriodCloser.CreateForTest(connectionString, interleaving :> IBillingPeriodCloseTransactionInterleaving) :> IBillingPeriodCloser

                Assert.ThrowsAsync<InvalidOperationException>(Func<Task>(fun () -> closer.CloseAsync(request scope, CancellationToken.None) :> Task))
                |> ignore

                let! previewCount = countAsync connectionString "SELECT COUNT(*) FROM ops.ChargePreviewLine;"
                let! chargeCount = countAsync connectionString "SELECT COUNT(*) FROM ops.Charge;"
                let! evidenceCount = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriodCloseEvidence;"
                let! closedCount = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriod WHERE State = 2;"
                let! lateWorkCount = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriodLateWork;"

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(previewCount, Is.Zero)
                        Assert.That(chargeCount, Is.Zero)
                        Assert.That(evidenceCount, Is.Zero)
                        Assert.That(closedCount, Is.Zero)
                        Assert.That(lateWorkCount, Is.Zero))
                )
            })

    /// Proves competing close retries converge on one immutable posting and no duplicate evidence.
    [<Test>]
    member _.CompetingCloseAndRestartConvergeOnOnePosting() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, repositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let scope = scopeFor ownerId organizationId repositoryId
                let fact = usageFact (Guid.NewGuid()) ownerId organizationId repositoryId (monthStart + Duration.FromDays 7) 13L
                do! acceptAsync connectionString fact
                do! addPricingAsync connectionString scope

                let first =
                    (SqlBillingPeriodCloser(connectionString) :> IBillingPeriodCloser)
                        .CloseAsync(request scope, CancellationToken.None)

                let second =
                    (SqlBillingPeriodCloser(connectionString) :> IBillingPeriodCloser)
                        .CloseAsync(request scope, CancellationToken.None)

                let! _ = Task.WhenAll(first, second)

                let! restart =
                    (SqlBillingPeriodCloser(connectionString) :> IBillingPeriodCloser)
                        .CloseAsync(request scope, CancellationToken.None)

                let! chargeCount = countAsync connectionString "SELECT COUNT(*) FROM ops.Charge;"
                let! evidenceCount = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriodCloseEvidence;"

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(closedChargeCount restart, Is.EqualTo(1))
                        Assert.That(chargeCount, Is.EqualTo(1))
                        Assert.That(evidenceCount, Is.EqualTo(1)))
                )
            })
