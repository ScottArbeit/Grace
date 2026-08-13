namespace Grace.Operations.Tests

open Grace.Operations.Data
open Grace.Types.Common
open Grace.Types.Usage
open Microsoft.Data.SqlClient
open NUnit.Framework
open NodaTime
open System
open System.Data
open System.Threading
open System.Threading.Tasks

/// Records the production session, exact lock resource, and database-clock order without replacing the clock.
type private ClockOrderingInterleaving() =
    let events = ResizeArray<string>()
    let mutable sessionId = 0
    let mutable resource = String.Empty
    let mutable databaseUtcNow = DateTime.MinValue

    /// Gets the ordered production events captured from one SQL close transaction.
    member _.Events = events |> Seq.toList

    /// Gets the SQL session used by lock acquisition and database-clock reads.
    member _.SessionId = sessionId

    /// Gets the exact shared application-lock resource supplied by the closer.
    member _.Resource = resource

    /// Gets the SQL Server UTC instant that drove the close decision.
    member _.DatabaseUtcNow = databaseUtcNow

    interface IBillingPeriodCloseTransactionInterleaving with
        member _.BeforeScopeLockAcquisitionAsync(observedSessionId, observedResource, _) =
            sessionId <- observedSessionId
            resource <- observedResource
            events.Add("before-lock")
            Task.CompletedTask

        member _.AfterScopeLockGrantedAsync(observedSessionId, observedResource, _) =
            Assert.That(observedSessionId, Is.EqualTo(sessionId))
            Assert.That(observedResource, Is.EqualTo(resource))
            events.Add("lock-granted")
            Task.CompletedTask

        member _.AfterDatabaseClockReadAsync(observedSessionId, observedDatabaseUtcNow, _) =
            Assert.That(observedSessionId, Is.EqualTo(sessionId))
            databaseUtcNow <- observedDatabaseUtcNow
            events.Add("database-clock-read")
            Task.CompletedTask

        member _.AfterPreviewReplacementAsync _ = Task.CompletedTask
        member _.AfterChargeInsertionAsync _ = Task.CompletedTask
        member _.AfterCloseEvidenceStagedAsync _ = Task.CompletedTask

/// Holds one production close after its exact SQL lock grant so a second production call can be observed waiting.
type private ContentionInterleaving(holdAfterGrant: bool) =
    let beforeAcquisition = TaskCompletionSource<int * string>(TaskCreationOptions.RunContinuationsAsynchronously)
    let granted = TaskCompletionSource<int * string>(TaskCreationOptions.RunContinuationsAsynchronously)
    let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    /// Completes when the production closer has entered exact-resource acquisition on its own SQL session.
    member _.BeforeAcquisition = beforeAcquisition.Task

    /// Completes when the production closer has been granted the exact shared SQL lock.
    member _.Granted = granted.Task

    /// Allows the lock holder to continue its transaction after the contention observation is complete.
    member _.Release() = release.TrySetResult() |> ignore

    interface IBillingPeriodCloseTransactionInterleaving with
        member _.BeforeScopeLockAcquisitionAsync(sessionId, resource, _) =
            beforeAcquisition.TrySetResult(sessionId, resource)
            |> ignore

            Task.CompletedTask

        member _.AfterScopeLockGrantedAsync(sessionId, resource, cancellationToken) =
            task {
                granted.TrySetResult(sessionId, resource)
                |> ignore

                if holdAfterGrant then do! release.Task.WaitAsync(cancellationToken)
            }
            :> Task

        member _.AfterDatabaseClockReadAsync(_, _, _) = Task.CompletedTask
        member _.AfterPreviewReplacementAsync _ = Task.CompletedTask
        member _.AfterChargeInsertionAsync _ = Task.CompletedTask
        member _.AfterCloseEvidenceStagedAsync _ = Task.CompletedTask

/// Proves the exact database-time policy boundary independently of the production SQL clock source.
[<TestFixture>]
type OperationsBillingPeriodCloseBehaviorTests() =

    /// Names the isolated SQL connection needed by the retained real-SQL production-clock proof.
    [<Literal>]
    let sqlConnectionStringEnvironmentVariable = "GRACE_OPERATIONS_SQL_TEST_CONNECTION_STRING"

    /// Uses a fixed past month so the production SQL clock is independently eligible for final close.
    let monthStart = Instant.FromUtc(2026, 6, 1, 0, 0)

    /// Returns a distinct disposable database name for one real-SQL proof.
    let databaseName () = $"GraceOperationsBillingCloseBehavior_{Guid.NewGuid():N}"

    /// Opens the explicit real-SQL test resource or marks the named integration proof unavailable.
    let requireSqlConnectionString () =
        let connectionString = Environment.GetEnvironmentVariable sqlConnectionStringEnvironmentVariable

        if String.IsNullOrWhiteSpace connectionString then
            Assert.Ignore($"{sqlConnectionStringEnvironmentVariable} is required for real SQL billing-period close tests.")

        connectionString

    /// Creates an isolated Operations schema through the production bootstrap seam.
    let createDatabaseAsync () =
        task {
            let builder = SqlConnectionStringBuilder(requireSqlConnectionString ())
            builder.InitialCatalog <- databaseName ()
            let schema = OperationsUsageSchema(builder.ConnectionString, OperationsUsageSchemaBootstrapMode.CreateDatabaseIfMissing)
            do! schema.EnsureCreatedAsync CancellationToken.None
            return builder.ConnectionString
        }

    /// Removes only the disposable database owned by this test.
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

    /// Runs an isolated real-SQL proof and always removes the database it created.
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

    /// Creates a valid exact owner/repository/month scope for the production closer.
    let scopeFor ownerId organizationId repositoryId =
        match BillingCompletenessScope.tryCreate ownerId organizationId repositoryId monthStart with
        | Ok scope -> scope
        | Error errors -> invalidOp (String.Join("; ", errors))

    /// Accepts one supported usage fact through the journal before a close reads committed facts.
    let acceptAsync connectionString ownerId organizationId repositoryId =
        task {
            let fact =
                UsageFact.RepositoryStorageBytesMinute(
                    Guid.NewGuid(),
                    CorrelationId "billing-close-clock-ordering",
                    ownerId,
                    organizationId,
                    repositoryId,
                    StoragePoolId "billing-close-pool",
                    7L,
                    monthStart + Duration.FromDays 1
                )

            let journal = SqlOperationsUsageJournalStore(connectionString)
            let! _ = journal.AppendAsync(fact, CancellationToken.None)
            let! result = journal.ProcessAsync(fact, Array.empty, CancellationToken.None)
            Assert.That(result, Is.EqualTo(UsageFactJournalProcessResult.AcceptedFromJournal))
        }

    /// Adds one complete pricing grain through the live Operations SQL schema.
    let addPricingAsync connectionString (scope: BillingCompletenessScope) =
        task {
            let planId, mappingId, rateId, assignmentId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()

            command.CommandText <-
                "INSERT INTO ops.PricingPlan (PricingPlanId,PlanCode,DisplayName,EffectiveFromUtc) VALUES (@PlanId,@PlanCode,@DisplayName,@EffectiveFrom); INSERT INTO ops.BillableUsageKindMapping (BillableUsageKindMappingId,FactKind,BillableUsageKind,DisplayName,EffectiveFromUtc) VALUES (@MappingId,1,101,@MappingName,@EffectiveFrom); INSERT INTO ops.PricingRate (PricingRateId,PricingPlanId,BillableUsageKind,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc) VALUES (@RateId,@PlanId,101,'USD','byte-minute',1,2,@EffectiveFrom); INSERT INTO ops.PricingAssignment (PricingAssignmentId,OwnerId,OrganizationId,RepositoryId,PricingPlanId,EffectiveFromUtc) VALUES (@AssignmentId,@OwnerId,@OrganizationId,@RepositoryId,@PlanId,@EffectiveFrom);"

            command.Parameters.Add("@PlanId", SqlDbType.UniqueIdentifier).Value <- planId
            command.Parameters.Add("@PlanCode", SqlDbType.NVarChar, 80).Value <- $"close-{planId:N}"
            command.Parameters.Add("@DisplayName", SqlDbType.NVarChar, 200).Value <- "Billing close clock ordering"
            command.Parameters.Add("@MappingId", SqlDbType.UniqueIdentifier).Value <- mappingId
            command.Parameters.Add("@MappingName", SqlDbType.NVarChar, 200).Value <- "Storage byte minute"
            command.Parameters.Add("@RateId", SqlDbType.UniqueIdentifier).Value <- rateId
            command.Parameters.Add("@AssignmentId", SqlDbType.UniqueIdentifier).Value <- assignmentId
            command.Parameters.Add("@OwnerId", SqlDbType.UniqueIdentifier).Value <- scope.OwnerId
            command.Parameters.Add("@OrganizationId", SqlDbType.UniqueIdentifier).Value <- scope.OrganizationId
            command.Parameters.Add("@RepositoryId", SqlDbType.UniqueIdentifier).Value <- scope.RepositoryId
            command.Parameters.Add("@EffectiveFrom", SqlDbType.DateTime2).Value <- scope.MonthStart.ToDateTimeUtc()
            let! _ = command.ExecuteNonQueryAsync CancellationToken.None
            return ()
        }

    /// Reads application-lock rows only for the two captured production sessions from this contention test.
    let applicationLocksAsync connectionString firstSessionId secondSessionId =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()

            command.CommandText <-
                "SELECT CONCAT(request_status,'|',request_mode,'|',request_session_id,'|',resource_description) FROM sys.dm_tran_locks WHERE resource_type='APPLICATION' AND resource_database_id=DB_ID() AND request_session_id IN (@FirstSessionId,@SecondSessionId) ORDER BY request_session_id,request_status;"

            command.Parameters.Add("@FirstSessionId", SqlDbType.Int).Value <- firstSessionId
            command.Parameters.Add("@SecondSessionId", SqlDbType.Int).Value <- secondSessionId
            use! reader = command.ExecuteReaderAsync CancellationToken.None
            let rows = ResizeArray<string>()
            let mutable reading = true

            while reading do
                let! hasRow = reader.ReadAsync CancellationToken.None
                reading <- hasRow
                if hasRow then rows.Add(reader.GetString 0)

            return rows |> Seq.toList
        }

    /// Counts the exact durable rows that must converge after competing close calls.
    let countAsync connectionString sql =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()
            command.CommandText <- sql
            let! value = command.ExecuteScalarAsync CancellationToken.None
            return Convert.ToInt32 value
        }

    /// Confirms one SQL tick before the preview threshold is rejected while equality is eligible.
    [<Test>]
    member _.PreviewThresholdRejectsOneSqlTickBeforeAndAcceptsEquality() =
        let nextMonthStart = DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)
        let threshold = nextMonthStart.AddHours 24.0
        let oneSqlTickBefore = threshold.AddTicks -1L

        Assert.That(BillingPeriodCloseEligibility.isEligible false nextMonthStart oneSqlTickBefore, Is.False)
        Assert.That(BillingPeriodCloseEligibility.isEligible false nextMonthStart threshold, Is.True)

    /// Confirms one SQL tick before the final-close threshold is rejected while equality is eligible.
    [<Test>]
    member _.FinalCloseThresholdRejectsOneSqlTickBeforeAndAcceptsEquality() =
        let nextMonthStart = DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)
        let threshold = nextMonthStart.AddHours 72.0
        let oneSqlTickBefore = threshold.AddTicks -1L

        Assert.That(BillingPeriodCloseEligibility.isEligible true nextMonthStart oneSqlTickBefore, Is.False)
        Assert.That(BillingPeriodCloseEligibility.isEligible true nextMonthStart threshold, Is.True)

    /// Proves the production clock reads SQL time after exact-lock grant on one session and persists that value as close evidence.
    [<Test>]
    member _.ProductionSqlClockRunsAfterExactScopeLockAndPersistsItsValue() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, repositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let scope = scopeFor ownerId organizationId repositoryId
                do! acceptAsync connectionString ownerId organizationId repositoryId
                do! addPricingAsync connectionString scope
                let interleaving = ClockOrderingInterleaving()
                let request = { Scope = scope; ScheduledOperationProvenance = "operations-tests/database-clock-ordering/v1" }

                let closer =
                    SqlBillingPeriodCloser.CreateForTest(connectionString, interleaving :> IBillingPeriodCloseTransactionInterleaving) :> IBillingPeriodCloser

                let! result = closer.CloseAsync(request, CancellationToken.None)
                use connection = new SqlConnection(connectionString)
                do! connection.OpenAsync CancellationToken.None
                use command = connection.CreateCommand()
                command.CommandText <- "SELECT ClosedAtUtc FROM ops.BillingPeriodCloseEvidence;"
                let! persistedClosedAt = command.ExecuteScalarAsync CancellationToken.None

                match result with
                | BillingPeriodCloseResult.Closed (_, chargeCount) -> Assert.That(chargeCount, Is.EqualTo(1))
                | unexpected -> Assert.Fail($"Expected a closed billing period but received {unexpected}.")

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(
                            interleaving.Events,
                            Is.EqualTo<string list>(
                                [
                                    "before-lock"
                                    "lock-granted"
                                    "database-clock-read"
                                ]
                            )
                        )

                        Assert.That(interleaving.SessionId, Is.GreaterThan(0))
                        Assert.That(interleaving.Resource, Is.EqualTo(BillingCompletenessScope.databaseLockIdentity scope))
                        Assert.That(persistedClosedAt :?> DateTime, Is.EqualTo(interleaving.DatabaseUtcNow)))
                )
            })

    /// Proves two production close calls expose their own sessions, share the exact resource, show grant-plus-wait, and converge.
    [<Test>]
    member _.TwoProductionClosersWaitOnTheExactSharedResourceAndConverge() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, repositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let scope = scopeFor ownerId organizationId repositoryId
                do! acceptAsync connectionString ownerId organizationId repositoryId
                do! addPricingAsync connectionString scope
                let firstInterleaving = ContentionInterleaving(true)
                let secondInterleaving = ContentionInterleaving(false)
                let request = { Scope = scope; ScheduledOperationProvenance = "operations-tests/two-session-contention/v1" }

                let firstCloser =
                    SqlBillingPeriodCloser.CreateForTest(connectionString, firstInterleaving :> IBillingPeriodCloseTransactionInterleaving)
                    :> IBillingPeriodCloser

                let secondCloser =
                    SqlBillingPeriodCloser.CreateForTest(connectionString, secondInterleaving :> IBillingPeriodCloseTransactionInterleaving)
                    :> IBillingPeriodCloser

                let first = firstCloser.CloseAsync(request, CancellationToken.None)
                let! firstSessionId, firstResource = firstInterleaving.Granted.WaitAsync(TimeSpan.FromSeconds 30.0)
                let second = secondCloser.CloseAsync(request, CancellationToken.None)
                let! secondSessionId, secondResource = secondInterleaving.BeforeAcquisition.WaitAsync(TimeSpan.FromSeconds 30.0)
                let! locks = applicationLocksAsync connectionString firstSessionId secondSessionId

                try
                    Assert.Multiple(
                        Action (fun () ->
                            Assert.That(first.IsCompleted, Is.False)
                            Assert.That(second.IsCompleted, Is.False)
                            Assert.That(firstSessionId, Is.Not.EqualTo(secondSessionId))
                            Assert.That(firstResource, Is.EqualTo(BillingCompletenessScope.databaseLockIdentity scope))
                            Assert.That(secondResource, Is.EqualTo(firstResource))

                            Assert.That(
                                locks
                                |> List.exists (fun row -> row.StartsWith($"GRANT|X|{firstSessionId}|", StringComparison.Ordinal)),
                                Is.True,
                                $"Expected first session grant; rows: {locks}"
                            )

                            Assert.That(
                                locks
                                |> List.exists (fun row -> row.StartsWith($"WAIT|X|{secondSessionId}|", StringComparison.Ordinal)),
                                Is.True,
                                $"Expected second session wait; rows: {locks}"
                            ))
                    )
                finally
                    firstInterleaving.Release()

                let! results = Task.WhenAll(first, second)
                let! periods = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriod WHERE State=2;"
                let! charges = countAsync connectionString "SELECT COUNT(*) FROM ops.Charge;"
                let! evidence = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriodCloseEvidence;"

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(results.Length, Is.EqualTo(2))
                        Assert.That(periods, Is.EqualTo(1))
                        Assert.That(charges, Is.EqualTo(1))
                        Assert.That(evidence, Is.EqualTo(1)))
                )
            })
