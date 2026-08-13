namespace Grace.Operations.Tests

open Grace.Operations.Data
open Grace.Types.Common
open Grace.Types.Usage
open Microsoft.Data.SqlClient
open NUnit.Framework
open NodaTime
open System
open System.Data
open System.Globalization
open System.Security.Cryptography
open System.Text
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

/// Supplies one controlled database instant for policy-boundary tests without changing the production clock proof.
type private FixedBillingPeriodCloseClock(databaseUtcNow: DateTime) =
    interface IBillingPeriodCloseClock with
        member _.UtcNowAsync(_, _, _) = Task.FromResult databaseUtcNow

/// Cancels a production transaction immediately after one named close stage has staged its durable writes.
type private StageCancellationInterleaving(stage: string, cancellation: CancellationTokenSource) =
    let cancelAt requestedStage =
        if stage = requestedStage then
            cancellation.Cancel()
            Task.FromCanceled cancellation.Token
        else
            Task.CompletedTask

    interface IBillingPeriodCloseTransactionInterleaving with
        member _.BeforeScopeLockAcquisitionAsync(_, _, _) = Task.CompletedTask
        member _.AfterScopeLockGrantedAsync(_, _, _) = Task.CompletedTask
        member _.AfterDatabaseClockReadAsync(_, _, _) = Task.CompletedTask
        member _.AfterPreviewReplacementAsync _ = cancelAt "preview"
        member _.AfterChargeInsertionAsync _ = cancelAt "charge"
        member _.AfterCloseEvidenceStagedAsync _ = cancelAt "evidence"

/// Proves the exact database-time policy boundary independently of the production SQL clock source.
[<TestFixture; NonParallelizable>]
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

    /// Builds one supported fact with explicit identity, month position, and quantity for close-boundary proof.
    let usageFact factId ownerId organizationId repositoryId observedAt quantity =
        UsageFact.RepositoryStorageBytesMinute(
            factId,
            CorrelationId $"billing-close-{factId:D}",
            ownerId,
            organizationId,
            repositoryId,
            StoragePoolId "billing-close-pool",
            quantity,
            observedAt
        )

    /// Persists one chosen supported fact through the production journal and acceptance transaction.
    let acceptFactAsync connectionString fact =
        task {
            let journal = SqlOperationsUsageJournalStore(connectionString)
            let! _ = journal.AppendAsync(fact, CancellationToken.None)
            let! result = journal.ProcessAsync(fact, Array.empty, CancellationToken.None)
            Assert.That(result, Is.EqualTo(UsageFactJournalProcessResult.AcceptedFromJournal))
        }

    /// Adds one complete pricing grain through the live Operations SQL schema.
    let addPricingAsync connectionString (scope: BillingCompletenessScope) =
        task {
            let planId, rateId, assignmentId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
            let mappingId = Guid.Parse "47a4d91d-43e7-450b-8800-917000000001"
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()

            command.CommandText <-
                "INSERT INTO ops.PricingPlan (PricingPlanId,PlanCode,DisplayName,EffectiveFromUtc) VALUES (@PlanId,@PlanCode,@DisplayName,@EffectiveFrom); IF NOT EXISTS (SELECT 1 FROM ops.BillableUsageKindMapping WHERE FactKind=1 AND EffectiveFromUtc=@EffectiveFrom) INSERT INTO ops.BillableUsageKindMapping (BillableUsageKindMappingId,FactKind,BillableUsageKind,DisplayName,EffectiveFromUtc) VALUES (@MappingId,1,101,@MappingName,@EffectiveFrom); INSERT INTO ops.PricingRate (PricingRateId,PricingPlanId,BillableUsageKind,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc) VALUES (@RateId,@PlanId,101,'USD','byte-minute',1,2,@EffectiveFrom); INSERT INTO ops.PricingAssignment (PricingAssignmentId,OwnerId,OrganizationId,RepositoryId,PricingPlanId,EffectiveFromUtc) VALUES (@AssignmentId,@OwnerId,@OrganizationId,@RepositoryId,@PlanId,@EffectiveFrom);"

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
                "SELECT CONCAT(request_status,'|',request_mode,'|',request_session_id,'|',RTRIM(resource_description)) FROM sys.dm_tran_locks WHERE resource_type='APPLICATION' AND resource_database_id=DB_ID() AND request_session_id IN (@FirstSessionId,@SecondSessionId) ORDER BY request_session_id,request_status;"

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

    /// Derives SQL Server's exact DMV description for one supplied application-lock resource before contention begins.
    let applicationLockDmvDescriptionAsync connectionString resource =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use transaction = connection.BeginTransaction()
            use acquireCommand = connection.CreateCommand()
            acquireCommand.Transaction <- transaction
            acquireCommand.CommandText <- OperationsUsageSql.AcquireBillingCompletenessScopeLock
            acquireCommand.Parameters.Add("@BillingCompletenessLockResource", SqlDbType.NVarChar, 255).Value <- resource
            acquireCommand.Parameters.Add("@BillingCompletenessLockTimeoutMilliseconds", SqlDbType.Int).Value <- 0
            let! _ = acquireCommand.ExecuteNonQueryAsync CancellationToken.None
            use descriptionCommand = connection.CreateCommand()
            descriptionCommand.Transaction <- transaction

            descriptionCommand.CommandText <-
                "SELECT TOP(1) RTRIM(resource_description) FROM sys.dm_tran_locks WHERE resource_type='APPLICATION' AND resource_database_id=DB_ID() AND request_session_id=@@SPID AND request_status='GRANT';"

            let! description = descriptionCommand.ExecuteScalarAsync CancellationToken.None
            do! transaction.RollbackAsync CancellationToken.None
            return Convert.ToString(description, CultureInfo.InvariantCulture)
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

    /// Reads a byte-for-byte JSON projection of the durable direct-close records for rollback proof.
    let closeProjectionAsync connectionString =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()

            command.CommandText <-
                """
SELECT CONCAT('BillingPeriod|',(SELECT * FROM ops.BillingPeriod ORDER BY BillingPeriodId FOR JSON PATH, INCLUDE_NULL_VALUES))
UNION ALL SELECT CONCAT('ChargePreviewLine|',(SELECT * FROM ops.ChargePreviewLine ORDER BY ChargePreviewLineId FOR JSON PATH, INCLUDE_NULL_VALUES))
UNION ALL SELECT CONCAT('Charge|',(SELECT * FROM ops.Charge ORDER BY ChargeId FOR JSON PATH, INCLUDE_NULL_VALUES))
UNION ALL SELECT CONCAT('BillingPeriodCloseEvidence|',(SELECT * FROM ops.BillingPeriodCloseEvidence ORDER BY BillingPeriodId FOR JSON PATH, INCLUDE_NULL_VALUES))
ORDER BY 1;
"""

            use! reader = command.ExecuteReaderAsync CancellationToken.None
            let rows = ResizeArray<string>()
            let mutable reading = true

            while reading do
                let! hasRow = reader.ReadAsync CancellationToken.None
                reading <- hasRow

                if hasRow then rows.Add(reader.GetString 0)

            return rows |> Seq.toList
        }

    /// Recomputes the fact-evidence digest from independent persisted raw facts.
    let acceptedFactDigestAsync connectionString (scope: BillingCompletenessScope) =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()

            command.CommandText <-
                "SELECT UsageFactId,FactKind,Quantity,ObservedAtUtc FROM ops.RawUsageFact WHERE OwnerId=@OwnerId AND OrganizationId=@OrganizationId AND RepositoryId=@RepositoryId AND ObservedAtUtc>=@MonthStartUtc AND ObservedAtUtc<@NextMonthStartUtc ORDER BY ObservedAtUtc,UsageFactId;"

            command.Parameters.Add("@OwnerId", SqlDbType.UniqueIdentifier).Value <- scope.OwnerId
            command.Parameters.Add("@OrganizationId", SqlDbType.UniqueIdentifier).Value <- scope.OrganizationId
            command.Parameters.Add("@RepositoryId", SqlDbType.UniqueIdentifier).Value <- scope.RepositoryId
            command.Parameters.Add("@MonthStartUtc", SqlDbType.DateTime2).Value <- scope.MonthStart.ToDateTimeUtc()

            command.Parameters.Add("@NextMonthStartUtc", SqlDbType.DateTime2).Value <- (BillingCompletenessScope.nextMonthStart scope)
                .ToDateTimeUtc()

            use! reader = command.ExecuteReaderAsync CancellationToken.None
            let values = ResizeArray<string>()
            let mutable reading = true

            while reading do
                let! hasRow = reader.ReadAsync CancellationToken.None
                reading <- hasRow

                if hasRow then
                    values.Add(
                        $"{reader.GetGuid(0):D}|{Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture)}|{reader.GetInt64(2)}|{reader.GetDateTime(3).Ticks}"
                    )

            return
                values
                |> String.concat "\n"
                |> Encoding.UTF8.GetBytes
                |> SHA256.HashData
                |> Convert.ToHexString
        }

    /// Rebuilds the full pricing-preview evidence digest from persisted rows without calling the closer's digest helper.
    let pricingPreviewDigestAsync connectionString =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()

            command.CommandText <-
                "SELECT ChargePreviewLineId,OwnerId,OrganizationId,RepositoryId,PeriodFromUtc,PeriodToUtc,FactKind,BillableUsageKindMappingId,BillableUsageKind,PricingAssignmentId,PricingPlanId,PricingRateId,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc,EffectiveToUtc,TotalQuantity,ChargeMicros FROM ops.ChargePreviewLine;"

            use! reader = command.ExecuteReaderAsync CancellationToken.None
            let lines = ResizeArray<Guid * string>()
            let mutable reading = true

            while reading do
                let! hasRow = reader.ReadAsync CancellationToken.None
                reading <- hasRow

                if hasRow then
                    let lineId = reader.GetGuid 0

                    let canonical =
                        String.Join(
                            "|",
                            [|
                                lineId.ToString "D"
                                (reader.GetGuid 1).ToString "D"
                                (reader.GetGuid 2).ToString "D"
                                (reader.GetGuid 3).ToString "D"
                                (reader.GetDateTime 4)
                                    .Ticks.ToString CultureInfo.InvariantCulture
                                (reader.GetDateTime 5)
                                    .Ticks.ToString CultureInfo.InvariantCulture
                                (Convert.ToInt32(reader.GetValue 6, CultureInfo.InvariantCulture))
                                    .ToString CultureInfo.InvariantCulture
                                (reader.GetGuid 7).ToString "D"
                                (Convert.ToInt32(reader.GetValue 8, CultureInfo.InvariantCulture))
                                    .ToString CultureInfo.InvariantCulture
                                (reader.GetGuid 9).ToString "D"
                                (reader.GetGuid 10).ToString "D"
                                (reader.GetGuid 11).ToString "D"
                                reader.GetString 12
                                reader.GetString 13
                                (reader.GetInt64 14)
                                    .ToString CultureInfo.InvariantCulture
                                (reader.GetInt64 15)
                                    .ToString CultureInfo.InvariantCulture
                                (reader.GetDateTime 16)
                                    .Ticks.ToString CultureInfo.InvariantCulture
                                (reader.GetDateTime 17)
                                    .Ticks.ToString CultureInfo.InvariantCulture
                                (reader.GetInt64 18)
                                    .ToString CultureInfo.InvariantCulture
                                (reader.GetInt64 19)
                                    .ToString CultureInfo.InvariantCulture
                            |]
                        )

                    lines.Add(lineId, canonical)

            return
                lines
                |> Seq.sortBy fst
                |> Seq.map snd
                |> String.concat "\n"
                |> Encoding.UTF8.GetBytes
                |> SHA256.HashData
                |> Convert.ToHexString
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
                let expectedResource = BillingCompletenessScope.databaseLockIdentity scope
                let! expectedDmvResource = applicationLockDmvDescriptionAsync connectionString expectedResource

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
                            Assert.That(firstResource, Is.EqualTo(expectedResource))
                            Assert.That(secondResource, Is.EqualTo(expectedResource))
                            Assert.That(expectedDmvResource, Is.Not.Empty)

                            Assert.That(
                                locks
                                |> List.exists (fun row -> row = $"GRANT|X|{firstSessionId}|{expectedDmvResource}"),
                                Is.True,
                                $"Expected first session grant; rows: {locks}"
                            )

                            Assert.That(
                                locks
                                |> List.exists (fun row -> row = $"WAIT|X|{secondSessionId}|{expectedDmvResource}"),
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

    /// Proves the half-open month, exact arithmetic, independent fact digest, and database-time evidence bracket.
    [<Test>]
    member _.CloseUsesExactHalfOpenFactsAndCompleteIndependentEvidence() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, repositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let scope = scopeFor ownerId organizationId repositoryId
                let nextMonthStart = BillingCompletenessScope.nextMonthStart scope
                do! acceptFactAsync connectionString (usageFact (Guid.NewGuid()) ownerId organizationId repositoryId scope.MonthStart 7L)
                do! acceptFactAsync connectionString (usageFact (Guid.NewGuid()) ownerId organizationId repositoryId nextMonthStart 11L)
                do! addPricingAsync connectionString scope
                let request = { Scope = scope; ScheduledOperationProvenance = "operations-tests/close-evidence/v1" }

                let! beforeClose =
                    task {
                        use connection = new SqlConnection(connectionString)
                        do! connection.OpenAsync CancellationToken.None
                        use command = connection.CreateCommand()
                        command.CommandText <- "SELECT SYSUTCDATETIME();"
                        let! value = command.ExecuteScalarAsync CancellationToken.None
                        return value :?> DateTime
                    }

                let! result =
                    (SqlBillingPeriodCloser(connectionString) :> IBillingPeriodCloser)
                        .CloseAsync(request, CancellationToken.None)

                let! afterClose =
                    task {
                        use connection = new SqlConnection(connectionString)
                        do! connection.OpenAsync CancellationToken.None
                        use command = connection.CreateCommand()
                        command.CommandText <- "SELECT SYSUTCDATETIME();"
                        let! value = command.ExecuteScalarAsync CancellationToken.None
                        return value :?> DateTime
                    }

                let! expectedFactDigest = acceptedFactDigestAsync connectionString scope
                let! expectedPreviewDigest = pricingPreviewDigestAsync connectionString
                use connection = new SqlConnection(connectionString)
                do! connection.OpenAsync CancellationToken.None
                use command = connection.CreateCommand()

                command.CommandText <-
                    "SELECT TotalQuantity,ChargeMicros,(SELECT ChargeMicros FROM ops.Charge),AcceptedFactDigestSha256Hex,PricingPreviewDigestSha256Hex,ClosedAtUtc,ScheduledOperationProvenance FROM ops.ChargePreviewLine CROSS JOIN ops.BillingPeriodCloseEvidence;"

                use! reader = command.ExecuteReaderAsync CancellationToken.None
                let! hasRow = reader.ReadAsync CancellationToken.None
                Assert.That(hasRow, Is.True)

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(
                            (match result with
                             | BillingPeriodCloseResult.Closed (_, 1) -> true
                             | _ -> false),
                            Is.True
                        )

                        Assert.That(reader.GetInt64 0, Is.EqualTo(7L))
                        Assert.That(reader.GetInt64 1, Is.EqualTo(14L))
                        Assert.That(reader.GetInt64 2, Is.EqualTo(14L))
                        Assert.That(reader.GetString 3, Is.EqualTo(expectedFactDigest))
                        Assert.That(reader.GetString 4, Is.EqualTo(expectedPreviewDigest))
                        Assert.That(reader.GetDateTime 5, Is.GreaterThanOrEqualTo(beforeClose))
                        Assert.That(reader.GetDateTime 5, Is.LessThanOrEqualTo(afterClose))
                        Assert.That(reader.GetString 6, Is.EqualTo("operations-tests/close-evidence/v1")))
                )
            })

    /// Proves every unresolved completeness source independently prevents a final preview, posting, evidence, or Closed state.
    [<TestCase("pending")>]
    [<TestCase("rejected")>]
    [<TestCase("active-rejection")>]
    member _.EveryCompletenessBlockerLeavesNoFinalCloseProjection(blocker: string) =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, repositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let scope = scopeFor ownerId organizationId repositoryId
                let factId = Guid.NewGuid()
                let fact = usageFact factId ownerId organizationId repositoryId (monthStart + Duration.FromDays 2) 5L
                let journal = SqlOperationsUsageJournalStore(connectionString)
                let! _ = journal.AppendAsync(fact, CancellationToken.None)

                match blocker with
                | "pending" -> ()
                | "rejected" ->
                    let! rejected = journal.RejectAsync(fact, Array.empty, "close blocker", CancellationToken.None)
                    Assert.That(rejected, Is.EqualTo(UsageFactJournalRejectResult.RejectedFromJournal))
                | "active-rejection" ->
                    let! accepted = journal.ProcessAsync(fact, Array.empty, CancellationToken.None)
                    Assert.That(accepted, Is.EqualTo(UsageFactJournalProcessResult.AcceptedFromJournal))
                    use connection = new SqlConnection(connectionString)
                    do! connection.OpenAsync CancellationToken.None
                    use command = connection.CreateCommand()

                    command.CommandText <-
                        "INSERT INTO ops.UsageFactRejection (RejectionId,UsageFactId,OwnerId,OrganizationId,RepositoryId,MonthStartUtc,Reason,IsActive) VALUES (@Id,@FactId,@OwnerId,@OrganizationId,@RepositoryId,@MonthStartUtc,'test',1);"

                    command.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value <- Guid.NewGuid()
                    command.Parameters.Add("@FactId", SqlDbType.UniqueIdentifier).Value <- factId
                    command.Parameters.Add("@OwnerId", SqlDbType.UniqueIdentifier).Value <- ownerId
                    command.Parameters.Add("@OrganizationId", SqlDbType.UniqueIdentifier).Value <- organizationId
                    command.Parameters.Add("@RepositoryId", SqlDbType.UniqueIdentifier).Value <- repositoryId
                    command.Parameters.Add("@MonthStartUtc", SqlDbType.DateTime2).Value <- scope.MonthStart.ToDateTimeUtc()
                    let! _ = command.ExecuteNonQueryAsync CancellationToken.None
                    ()
                | value -> invalidArg (nameof blocker) value

                do! addPricingAsync connectionString scope
                let request = { Scope = scope; ScheduledOperationProvenance = "operations-tests/blockers/v1" }

                let! result =
                    (SqlBillingPeriodCloser(connectionString) :> IBillingPeriodCloser)
                        .CloseAsync(request, CancellationToken.None)

                let! previews = countAsync connectionString "SELECT COUNT(*) FROM ops.ChargePreviewLine;"
                let! charges = countAsync connectionString "SELECT COUNT(*) FROM ops.Charge;"
                let! evidence = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriodCloseEvidence;"
                let! closed = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriod WHERE State=2;"

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(
                            (match result with
                             | BillingPeriodCloseResult.Blocked _ -> true
                             | _ -> false),
                            Is.True
                        )

                        Assert.That(previews, Is.Zero)
                        Assert.That(charges, Is.Zero)
                        Assert.That(evidence, Is.Zero)
                        Assert.That(closed, Is.Zero))
                )
            })

    /// Proves missing pricing and arithmetic overflow each retain retry truth until corrected pricing closes exactly once.
    [<TestCase(false)>]
    [<TestCase(true)>]
    member _.RepairablePricingFailuresThenReplayConvergeOnOnePosting(overflows: bool) =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, repositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let scope = scopeFor ownerId organizationId repositoryId
                let quantity = if overflows then Int64.MaxValue else 3L

                do!
                    acceptFactAsync
                        connectionString
                        (usageFact (Guid.NewGuid()) ownerId organizationId repositoryId (monthStart + Duration.FromDays 3) quantity)

                let request = { Scope = scope; ScheduledOperationProvenance = "operations-tests/pricing-retry/v1" }
                let closer = SqlBillingPeriodCloser(connectionString) :> IBillingPeriodCloser

                if overflows then
                    do! addPricingAsync connectionString scope
                    use setOverflow = new SqlConnection(connectionString)
                    do! setOverflow.OpenAsync CancellationToken.None
                    use command = setOverflow.CreateCommand()
                    command.CommandText <- "UPDATE ops.PricingRate SET UnitPriceMicros=@Price;"
                    command.Parameters.Add("@Price", SqlDbType.BigInt).Value <- Int64.MaxValue
                    let! _ = command.ExecuteNonQueryAsync CancellationToken.None
                    ()

                let! blocked = closer.CloseAsync(request, CancellationToken.None)
                let! diagnostic = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriod WHERE RetryDiagnostic IS NOT NULL AND State IN (0,1);"

                if overflows then
                    use correctPrice = new SqlConnection(connectionString)
                    do! correctPrice.OpenAsync CancellationToken.None
                    use command = correctPrice.CreateCommand()
                    command.CommandText <- "UPDATE ops.PricingRate SET UnitPriceMicros=1;"
                    let! _ = command.ExecuteNonQueryAsync CancellationToken.None
                    ()
                else
                    do! addPricingAsync connectionString scope

                let! repaired = closer.CloseAsync(request, CancellationToken.None)
                let! replay = closer.CloseAsync(request, CancellationToken.None)
                let! charges = countAsync connectionString "SELECT COUNT(*) FROM ops.Charge;"

                let! cleared =
                    countAsync
                        connectionString
                        "SELECT COUNT(*) FROM ops.BillingPeriod WHERE State=2 AND RetryDiagnostic IS NULL AND RetryDiagnosticAtUtc IS NULL;"

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(
                            (match blocked with
                             | BillingPeriodCloseResult.Blocked _ -> true
                             | _ -> false),
                            Is.True
                        )

                        Assert.That(diagnostic, Is.EqualTo(1))

                        Assert.That(
                            (match repaired with
                             | BillingPeriodCloseResult.Closed (_, 1) -> true
                             | _ -> false),
                            Is.True
                        )

                        Assert.That(repaired, Is.EqualTo(replay))
                        Assert.That(charges, Is.EqualTo(1))
                        Assert.That(cleared, Is.EqualTo(1)))
                )
            })

    /// Proves cancellation after every close mutation stage restores the complete preexisting durable projection.
    [<TestCase("preview")>]
    [<TestCase("charge")>]
    [<TestCase("evidence")>]
    member _.CancellationAfterEveryStagedCloseMutationPreservesPriorProjection(stage: string) =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, repositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let scope = scopeFor ownerId organizationId repositoryId
                do! acceptFactAsync connectionString (usageFact (Guid.NewGuid()) ownerId organizationId repositoryId (monthStart + Duration.FromDays 4) 9L)
                do! addPricingAsync connectionString scope

                let nextMonthStartUtc =
                    (BillingCompletenessScope.nextMonthStart scope)
                        .ToDateTimeUtc()

                let previewClock = FixedBillingPeriodCloseClock(nextMonthStartUtc.AddHours 24.0) :> IBillingPeriodCloseClock
                let inert = StageCancellationInterleaving("none", new CancellationTokenSource()) :> IBillingPeriodCloseTransactionInterleaving
                let request = { Scope = scope; ScheduledOperationProvenance = "operations-tests/cancellation/v1" }
                let previewCloser = SqlBillingPeriodCloser.CreateForTest(connectionString, inert, previewClock) :> IBillingPeriodCloser
                let! preview = previewCloser.PreviewAsync(request, CancellationToken.None)

                Assert.That(
                    (match preview with
                     | BillingPeriodCloseResult.Provisional _ -> true
                     | _ -> false),
                    Is.True
                )

                let! before = closeProjectionAsync connectionString
                use cancellation = new CancellationTokenSource()
                let interleaving = StageCancellationInterleaving(stage, cancellation)

                let closer =
                    SqlBillingPeriodCloser.CreateForTest(connectionString, interleaving :> IBillingPeriodCloseTransactionInterleaving) :> IBillingPeriodCloser

                Assert.CatchAsync<OperationCanceledException>(Func<Task>(fun () -> closer.CloseAsync(request, cancellation.Token) :> Task))
                |> ignore

                let! after = closeProjectionAsync connectionString

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(cancellation.IsCancellationRequested, Is.True)
                        Assert.That(after, Is.EqualTo(before :> obj)))
                )
            })

    /// Proves distinct owners and sibling repositories close independently and a fresh closer replays without reposting.
    [<Test>]
    member _.OwnersSiblingRepositoriesAndRestartRemainIsolatedAndIdempotent() =
        withDatabaseAsync (fun connectionString ->
            task {
                let organizationId = Guid.NewGuid()
                let ownerA, ownerB = Guid.NewGuid(), Guid.NewGuid()
                let repositoryA, repositoryB = Guid.NewGuid(), Guid.NewGuid()
                let scopeA = scopeFor ownerA organizationId repositoryA
                let scopeB = scopeFor ownerB organizationId repositoryA
                let siblingScope = scopeFor ownerA organizationId repositoryB
                do! acceptFactAsync connectionString (usageFact (Guid.NewGuid()) ownerA organizationId repositoryA (monthStart + Duration.FromDays 5) 1L)
                do! acceptFactAsync connectionString (usageFact (Guid.NewGuid()) ownerB organizationId repositoryA (monthStart + Duration.FromDays 5) 2L)
                do! acceptFactAsync connectionString (usageFact (Guid.NewGuid()) ownerA organizationId repositoryB (monthStart + Duration.FromDays 5) 3L)
                do! addPricingAsync connectionString scopeA
                do! addPricingAsync connectionString scopeB
                do! addPricingAsync connectionString siblingScope

                let close scope =
                    let request = { Scope = scope; ScheduledOperationProvenance = "operations-tests/isolation-replay/v1" }

                    (SqlBillingPeriodCloser(connectionString) :> IBillingPeriodCloser)
                        .CloseAsync(request, CancellationToken.None)

                let! results = Task.WhenAll(close scopeA, close scopeB, close siblingScope)
                let! replay = close scopeA
                let! periods = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriod WHERE State=2;"
                let! charges = countAsync connectionString "SELECT COUNT(*) FROM ops.Charge;"
                let! evidence = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriodCloseEvidence;"

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(
                            results
                            |> Array.forall (function
                                | BillingPeriodCloseResult.Closed (_, 1) -> true
                                | _ -> false),
                            Is.True
                        )

                        Assert.That(
                            (match replay with
                             | BillingPeriodCloseResult.Closed (_, 1) -> true
                             | _ -> false),
                            Is.True
                        )

                        Assert.That(periods, Is.EqualTo(3))
                        Assert.That(charges, Is.EqualTo(3))
                        Assert.That(evidence, Is.EqualTo(3)))
                )
            })
