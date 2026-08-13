namespace Grace.Operations.Tests

open Grace.Operations.Data
open Grace.Operations.Data.Migrations
open Grace.Operations.Worker
open Grace.Types.Common
open Grace.Types.Usage
open Microsoft.Data.SqlClient
open Microsoft.EntityFrameworkCore.Infrastructure
open Microsoft.EntityFrameworkCore.Metadata
open NUnit.Framework
open NodaTime
open System
open System.Globalization
open System.Security.Cryptography
open System.Text
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

/// Cancels a real close only after the named durable mutation stage has been reached inside its transaction.
type private BillingCloseCancellationInterleaving(stage: string, cancellation: CancellationTokenSource) =
    let reached = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    /// Cancels the supplied operation token at the selected production transaction seam.
    let cancel expected (cancellationToken: CancellationToken) =
        task {
            if stage = expected then
                reached.TrySetResult() |> ignore
                cancellation.Cancel()
                cancellationToken.ThrowIfCancellationRequested()
        }
        :> Task

    /// Completes when the selected production stage has been reached before cancellation is observed.
    member _.Reached = reached.Task

    interface IBillingPeriodCloseTransactionInterleaving with
        member _.AfterPreviewReplacementAsync cancellationToken = cancel "preview" cancellationToken
        member _.AfterLedgerInsertionAsync cancellationToken = cancel "ledger" cancellationToken
        member _.AfterCloseEvidenceStagedAsync cancellationToken = cancel "evidence" cancellationToken

/// Cancels accepted usage after raw, aggregate, and late-work staging but before the journal state can commit Accepted.
type private UsageJournalCancellationInterleaving(cancellation: CancellationTokenSource) =
    let reached = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    /// Cancels the supplied processing token at the production accepted-usage transaction seam.
    let cancel (cancellationToken: CancellationToken) =
        task {
            reached.TrySetResult() |> ignore
            cancellation.Cancel()
            cancellationToken.ThrowIfCancellationRequested()
        }
        :> Task

    /// Completes when raw, aggregate, and late-work mutations are staged but not yet committed.
    member _.Reached = reached.Task

    interface IOperationsUsageJournalTransactionInterleaving with
        member _.AfterRawAndAggregateStagedAsync cancellationToken = cancel cancellationToken

/// Returns a fixed transaction-local UTC instant while still requiring the production closer to acquire its SQL lock.
type private FixedBillingCloseClock(now: DateTime) =
    interface IBillingPeriodCloseClock with
        member _.UtcNowAsync(_connection, _transaction, cancellationToken) =
            task {
                cancellationToken.ThrowIfCancellationRequested()
                return now
            }

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

    /// Reads an independently queried integer total from the isolated SQL database.
    let int64Async connectionString sql =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()
            command.CommandText <- sql
            let! value = command.ExecuteScalarAsync CancellationToken.None
            return Convert.ToInt64 value
        }

    /// Reads one database-owned UTC time value without using the process clock in a close proof.
    let dateTimeAsync connectionString sql =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()
            command.CommandText <- sql
            let! value = command.ExecuteScalarAsync CancellationToken.None
            return value :?> DateTime
        }

    /// Recomputes accepted-fact evidence from independently queried immutable raw rows.
    let acceptedFactEvidenceDigestAsync connectionString =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()

            command.CommandText <- "SELECT UsageFactId,FactKind,Quantity,ObservedAtUtc FROM ops.RawUsageFact ORDER BY ObservedAtUtc,UsageFactId;"

            use! reader = command.ExecuteReaderAsync CancellationToken.None
            let values = ResizeArray<string>()
            let mutable reading = true

            while reading do
                let! hasRow = reader.ReadAsync CancellationToken.None
                reading <- hasRow

                if hasRow then
                    values.Add($"{reader.GetGuid(0):D}|{reader.GetInt32(1)}|{reader.GetInt64(2)}|{reader.GetDateTime(3).Ticks}")

            return
                values
                |> String.concat "\n"
                |> Encoding.UTF8.GetBytes
                |> SHA256.HashData
                |> Convert.ToHexString
        }

    /// Reads one ordered scalar string projection from the isolated SQL catalog for physical schema parity proofs.
    let stringsAsync connectionString sql =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()
            command.CommandText <- sql
            use! reader = command.ExecuteReaderAsync CancellationToken.None
            let values = ResizeArray<string>()
            let mutable reading = true

            while reading do
                let! hasRow = reader.ReadAsync CancellationToken.None
                reading <- hasRow

                if hasRow then values.Add(reader.GetString 0)

            return values |> Seq.toList
        }

    /// Captures every durable column in each close-owned and completeness-owned table for rejected-mutation and rollback proof.
    let durableCloseSnapshotAsync connectionString =
        stringsAsync
            connectionString
            """
SELECT CONCAT('BillingPeriod|',(SELECT * FROM ops.BillingPeriod ORDER BY BillingPeriodId FOR JSON PATH, INCLUDE_NULL_VALUES))
UNION ALL SELECT CONCAT('ChargePreviewLine|',(SELECT * FROM ops.ChargePreviewLine ORDER BY ChargePreviewLineId FOR JSON PATH, INCLUDE_NULL_VALUES))
UNION ALL SELECT CONCAT('Charge|',(SELECT * FROM ops.Charge ORDER BY ChargeId FOR JSON PATH, INCLUDE_NULL_VALUES))
UNION ALL SELECT CONCAT('BillingPeriodCloseEvidence|',(SELECT * FROM ops.BillingPeriodCloseEvidence ORDER BY BillingPeriodId FOR JSON PATH, INCLUDE_NULL_VALUES))
UNION ALL SELECT CONCAT('BillingPeriodLateWork|',(SELECT * FROM ops.BillingPeriodLateWork ORDER BY BillingPeriodId,UsageFactId FOR JSON PATH, INCLUDE_NULL_VALUES))
UNION ALL SELECT CONCAT('RawUsageFact|',(SELECT * FROM ops.RawUsageFact ORDER BY UsageFactId FOR JSON PATH, INCLUDE_NULL_VALUES))
UNION ALL SELECT CONCAT('UsageAggregateMinute|',(SELECT * FROM ops.UsageAggregateMinute ORDER BY FactKind,OwnerId,OrganizationId,RepositoryId,StoragePoolId,BucketStartUtc FOR JSON PATH, INCLUDE_NULL_VALUES))
UNION ALL SELECT CONCAT('UsageFactRejection|',(SELECT * FROM ops.UsageFactRejection ORDER BY RejectionId FOR JSON PATH, INCLUDE_NULL_VALUES))
UNION ALL SELECT CONCAT('UsageFactJournal|',(SELECT * FROM ops.UsageFactJournal ORDER BY UsageFactId FOR JSON PATH, INCLUDE_NULL_VALUES))
ORDER BY 1;
"""

    /// Executes one deliberately invalid physical mutation and proves it leaves every relevant durable row byte-for-byte unchanged.
    let rejectSqlAndPreserveAsync connectionString label sql =
        task {
            let! before = durableCloseSnapshotAsync connectionString
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()
            command.CommandText <- sql

            Assert.ThrowsAsync<SqlException>(Func<Task>(fun () -> command.ExecuteNonQueryAsync(CancellationToken.None) :> Task))
            |> ignore

            let! after = durableCloseSnapshotAsync connectionString

            if after <> before then
                Assert.Fail($"{label} changed durable state. Before: {before}; after: {after}.")
        }

    /// Attempts an exact transaction-owned application lock and returns SQL Server's direct grant or timeout result.
    let probeScopeLockAsync connectionString scope =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use! transaction = connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, CancellationToken.None)
            use command = connection.CreateCommand()
            command.Transaction <- transaction :?> SqlTransaction

            command.CommandText <-
                """
DECLARE @LockResult int;
EXEC @LockResult = sys.sp_getapplock
    @Resource = @Resource,
    @LockMode = 'Exclusive',
    @LockOwner = 'Transaction',
    @LockTimeout = 0;
SELECT @LockResult;
"""

            command.Parameters.Add("@Resource", System.Data.SqlDbType.NVarChar, 255).Value <- BillingCompletenessScope.databaseLockIdentity scope
            let! result = command.ExecuteScalarAsync CancellationToken.None
            do! transaction.RollbackAsync CancellationToken.None
            return Convert.ToInt32 result
        }

    /// Recomputes the persisted preview evidence digest from independently read durable rows.
    let previewEvidenceDigestAsync connectionString =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()

            command.CommandText <-
                "SELECT ChargePreviewLineId,OwnerId,OrganizationId,RepositoryId,PeriodFromUtc,PeriodToUtc,FactKind,BillableUsageKindMappingId,BillableUsageKind,PricingAssignmentId,PricingPlanId,PricingRateId,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc,EffectiveToUtc,TotalQuantity,ChargeMicros FROM ops.ChargePreviewLine ORDER BY ChargePreviewLineId;"

            use! reader = command.ExecuteReaderAsync CancellationToken.None
            let lines = ResizeArray<string>()
            let mutable reading = true

            while reading do
                let! hasRow = reader.ReadAsync CancellationToken.None
                reading <- hasRow

                if hasRow then
                    lines.Add(
                        String.Join(
                            "|",
                            [|
                                reader.GetGuid(0).ToString("D")
                                reader.GetGuid(1).ToString("D")
                                reader.GetGuid(2).ToString("D")
                                reader.GetGuid(3).ToString("D")
                                reader
                                    .GetDateTime(4)
                                    .Ticks.ToString(CultureInfo.InvariantCulture)
                                reader
                                    .GetDateTime(5)
                                    .Ticks.ToString(CultureInfo.InvariantCulture)
                                reader
                                    .GetInt32(6)
                                    .ToString(CultureInfo.InvariantCulture)
                                reader.GetGuid(7).ToString("D")
                                reader
                                    .GetInt32(8)
                                    .ToString(CultureInfo.InvariantCulture)
                                reader.GetGuid(9).ToString("D")
                                reader.GetGuid(10).ToString("D")
                                reader.GetGuid(11).ToString("D")
                                reader.GetString(12)
                                reader.GetString(13)
                                reader
                                    .GetInt64(14)
                                    .ToString(CultureInfo.InvariantCulture)
                                reader
                                    .GetInt64(15)
                                    .ToString(CultureInfo.InvariantCulture)
                                reader
                                    .GetDateTime(16)
                                    .Ticks.ToString(CultureInfo.InvariantCulture)
                                reader
                                    .GetDateTime(17)
                                    .Ticks.ToString(CultureInfo.InvariantCulture)
                                reader
                                    .GetInt64(18)
                                    .ToString(CultureInfo.InvariantCulture)
                                reader
                                    .GetInt64(19)
                                    .ToString(CultureInfo.InvariantCulture)
                            |]
                        )
                    )

            return
                lines
                |> String.concat "\n"
                |> Encoding.UTF8.GetBytes
                |> SHA256.HashData
                |> Convert.ToHexString
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

    /// Proves only the database-owned instant at the exact +24h and +72h thresholds makes each operation eligible.
    [<Test>]
    member _.DatabaseClockTreatsPreviewAndCloseThresholdEqualityAsEligible() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, repositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let scope = scopeFor ownerId organizationId repositoryId
                do! acceptAsync connectionString (usageFact (Guid.NewGuid()) ownerId organizationId repositoryId (monthStart + Duration.FromDays 1) 1L)
                do! addPricingAsync connectionString scope

                let nextMonthStartUtc =
                    (BillingCompletenessScope.nextMonthStart scope)
                        .ToDateTimeUtc()

                let inert = BillingCloseInterleaving("none", false) :> IBillingPeriodCloseTransactionInterleaving

                let previewTooEarly =
                    SqlBillingPeriodCloser.CreateForTest(
                        connectionString,
                        inert,
                        FixedBillingCloseClock(nextMonthStartUtc.AddHours(24.0).AddTicks(-1L)) :> IBillingPeriodCloseClock
                    )
                    :> IBillingPeriodCloser

                let previewAtBoundary =
                    SqlBillingPeriodCloser.CreateForTest(
                        connectionString,
                        inert,
                        FixedBillingCloseClock(nextMonthStartUtc.AddHours 24.0) :> IBillingPeriodCloseClock
                    )
                    :> IBillingPeriodCloser

                let closeTooEarly =
                    SqlBillingPeriodCloser.CreateForTest(
                        connectionString,
                        inert,
                        FixedBillingCloseClock(nextMonthStartUtc.AddHours(72.0).AddTicks(-1L)) :> IBillingPeriodCloseClock
                    )
                    :> IBillingPeriodCloser

                let closeAtBoundary =
                    SqlBillingPeriodCloser.CreateForTest(
                        connectionString,
                        inert,
                        FixedBillingCloseClock(nextMonthStartUtc.AddHours 72.0) :> IBillingPeriodCloseClock
                    )
                    :> IBillingPeriodCloser

                let! previewBefore = previewTooEarly.PreviewAsync(request scope, CancellationToken.None)
                let! previewAt = previewAtBoundary.PreviewAsync(request scope, CancellationToken.None)
                let! closeBefore = closeTooEarly.CloseAsync(request scope, CancellationToken.None)
                let! closeAt = closeAtBoundary.CloseAsync(request scope, CancellationToken.None)

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(previewBefore, Is.EqualTo(BillingPeriodCloseResult.NotEligible))

                        Assert.That(
                            (match previewAt with
                             | BillingPeriodCloseResult.Provisional _ -> true
                             | _ -> false),
                            Is.True
                        )

                        Assert.That(closeBefore, Is.EqualTo(BillingPeriodCloseResult.NotEligible))
                        Assert.That(closedChargeCount closeAt, Is.EqualTo(1)))
                )
            })

    /// Proves the half-open billing month includes its first instant and excludes the next month's first instant.
    [<Test>]
    member _.CloseIncludesMonthStartFactAndExcludesNextMonthStartFact() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, repositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let scope = scopeFor ownerId organizationId repositoryId
                let nextMonthStart = BillingCompletenessScope.nextMonthStart scope
                do! acceptAsync connectionString (usageFact (Guid.NewGuid()) ownerId organizationId repositoryId scope.MonthStart 7L)
                do! acceptAsync connectionString (usageFact (Guid.NewGuid()) ownerId organizationId repositoryId nextMonthStart 11L)
                do! addPricingAsync connectionString scope
                let closer = SqlBillingPeriodCloser(connectionString) :> IBillingPeriodCloser
                let! closed = closer.CloseAsync(request scope, CancellationToken.None)

                let! postedQuantity =
                    int64Async
                        connectionString
                        "SELECT COALESCE(SUM(TotalQuantity),0) FROM ops.ChargePreviewLine WHERE PeriodFromUtc='2026-06-01T00:00:00' AND PeriodToUtc='2026-07-01T00:00:00';"

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(closedChargeCount closed, Is.EqualTo(1))
                        Assert.That(postedQuantity, Is.EqualTo(7L)))
                )
            })

    /// Proves physical constraints and immutable triggers reject mutation while preserving the original close records.
    [<Test>]
    member _.PhysicalChargeAndEvidenceMutationsAreRejectedWithoutChangingDurableState() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, repositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let scope = scopeFor ownerId organizationId repositoryId
                do! acceptAsync connectionString (usageFact (Guid.NewGuid()) ownerId organizationId repositoryId (monthStart + Duration.FromDays 2) 7L)
                do! addPricingAsync connectionString scope
                let closer = SqlBillingPeriodCloser(connectionString) :> IBillingPeriodCloser
                let! _ = closer.CloseAsync(request scope, CancellationToken.None)
                use connection = new SqlConnection(connectionString)
                do! connection.OpenAsync CancellationToken.None

                let rejectMutation sql =
                    use command = connection.CreateCommand()
                    command.CommandText <- sql

                    Assert.ThrowsAsync<SqlException>(Func<Task>(fun () -> command.ExecuteNonQueryAsync(CancellationToken.None) :> Task))
                    |> ignore

                rejectMutation "UPDATE ops.Charge SET ChargeMicros=99;"
                rejectMutation "DELETE FROM ops.Charge;"
                rejectMutation "UPDATE ops.BillingPeriodCloseEvidence SET ScheduledOperationProvenance='tampered';"
                rejectMutation "DELETE FROM ops.BillingPeriodCloseEvidence;"
                let! charges = countAsync connectionString "SELECT COUNT(*) FROM ops.Charge;"
                let! evidence = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriodCloseEvidence;"

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(charges, Is.EqualTo(1))
                        Assert.That(evidence, Is.EqualTo(1)))
                )
            })

    /// Proves every close-core EF representation and the migrated SQL catalog retain the complete physical contract.
    [<Test>]
    member _.CloseCoreRuntimeTargetSnapshotAndLiveSqlSchemaAgree() =
        withDatabaseAsync (fun connectionString ->
            task {
                use context =
                    OperationsDbContextFactory.create "Server=(localdb)\\MSSQLLocalDB;Database=GraceOperationsBillingCloseSchemaModel;Integrated Security=true;"

                let runtime = context.GetService<IDesignTimeModel>().Model
                let target = AddBillingPeriodClose().TargetModel
                let snapshot = OperationsDbContextModelSnapshot().Model

                let triggerNames (model: IModel) (entityType: Type) =
                    model
                        .FindEntityType(entityType)
                        .GetDeclaredTriggers()
                    |> Seq.map (fun trigger -> trigger.ModelName)
                    |> Set.ofSeq

                let expectedTriggers =
                    Set.ofList [ "TR_ops_Charge_Immutable"
                                 "TR_ops_BillingPeriodCloseEvidence_Immutable" ]

                let! columns =
                    stringsAsync
                        connectionString
                        """
SELECT CONCAT(t.name COLLATE Latin1_General_100_BIN2,'|' COLLATE Latin1_General_100_BIN2,c.name COLLATE Latin1_General_100_BIN2,'|' COLLATE Latin1_General_100_BIN2,ty.name COLLATE Latin1_General_100_BIN2,'|' COLLATE Latin1_General_100_BIN2,c.max_length,'|' COLLATE Latin1_General_100_BIN2,c.is_nullable,'|' COLLATE Latin1_General_100_BIN2,c.scale)
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id=t.schema_id
JOIN sys.columns c ON c.object_id=t.object_id
JOIN sys.types ty ON ty.user_type_id=c.user_type_id
WHERE s.name='ops' AND t.name IN ('BillingPeriod','Charge','BillingPeriodCloseEvidence','BillingPeriodLateWork')
ORDER BY t.name,c.column_id;
"""

                let! keys =
                    stringsAsync
                        connectionString
                        """
SELECT CONCAT(t.name COLLATE Latin1_General_100_BIN2,'|' COLLATE Latin1_General_100_BIN2,kc.name COLLATE Latin1_General_100_BIN2,'|' COLLATE Latin1_General_100_BIN2,kc.type_desc COLLATE Latin1_General_100_BIN2)
FROM sys.key_constraints kc
JOIN sys.tables t ON t.object_id=kc.parent_object_id
JOIN sys.schemas s ON s.schema_id=t.schema_id
WHERE s.name='ops' AND t.name IN ('BillingPeriod','Charge','BillingPeriodCloseEvidence','BillingPeriodLateWork')
ORDER BY t.name,kc.name;
"""

                let! currencyCollation =
                    stringsAsync
                        connectionString
                        """
SELECT c.collation_name
FROM sys.columns c
WHERE c.object_id=OBJECT_ID('ops.Charge') AND c.name='CurrencyCode';
"""

                let! indexes =
                    stringsAsync
                        connectionString
                        """
SELECT CONCAT(t.name COLLATE Latin1_General_100_BIN2,'|' COLLATE Latin1_General_100_BIN2,i.name COLLATE Latin1_General_100_BIN2,'|' COLLATE Latin1_General_100_BIN2,i.is_unique)
FROM sys.indexes i
JOIN sys.tables t ON t.object_id=i.object_id
JOIN sys.schemas s ON s.schema_id=t.schema_id
WHERE s.name='ops' AND t.name IN ('BillingPeriod','Charge','BillingPeriodCloseEvidence','BillingPeriodLateWork') AND i.name IS NOT NULL
ORDER BY t.name,i.name;
"""

                let! foreignKeys =
                    stringsAsync
                        connectionString
                        """
SELECT CONCAT(t.name COLLATE Latin1_General_100_BIN2,'|' COLLATE Latin1_General_100_BIN2,fk.name COLLATE Latin1_General_100_BIN2,'|' COLLATE Latin1_General_100_BIN2,OBJECT_SCHEMA_NAME(fk.referenced_object_id) COLLATE Latin1_General_100_BIN2,'|' COLLATE Latin1_General_100_BIN2,OBJECT_NAME(fk.referenced_object_id) COLLATE Latin1_General_100_BIN2,'|' COLLATE Latin1_General_100_BIN2,fk.delete_referential_action_desc COLLATE Latin1_General_100_BIN2)
FROM sys.foreign_keys fk
JOIN sys.tables t ON t.object_id=fk.parent_object_id
JOIN sys.schemas s ON s.schema_id=t.schema_id
WHERE s.name='ops' AND t.name IN ('BillingPeriod','Charge','BillingPeriodCloseEvidence','BillingPeriodLateWork')
ORDER BY t.name,fk.name;
"""

                let! checks =
                    stringsAsync
                        connectionString
                        """
SELECT CONCAT(t.name COLLATE Latin1_General_100_BIN2,'|' COLLATE Latin1_General_100_BIN2,cc.name COLLATE Latin1_General_100_BIN2)
FROM sys.check_constraints cc
JOIN sys.tables t ON t.object_id=cc.parent_object_id
JOIN sys.schemas s ON s.schema_id=t.schema_id
WHERE s.name='ops' AND t.name IN ('BillingPeriod','Charge','BillingPeriodCloseEvidence','BillingPeriodLateWork')
ORDER BY t.name,cc.name;
"""

                let! defaults =
                    stringsAsync
                        connectionString
                        """
SELECT CONCAT(t.name COLLATE Latin1_General_100_BIN2,'|' COLLATE Latin1_General_100_BIN2,c.name COLLATE Latin1_General_100_BIN2,'|' COLLATE Latin1_General_100_BIN2,dc.name COLLATE Latin1_General_100_BIN2,'|' COLLATE Latin1_General_100_BIN2,LOWER(dc.definition) COLLATE Latin1_General_100_BIN2)
FROM sys.default_constraints dc
JOIN sys.tables t ON t.object_id=dc.parent_object_id
JOIN sys.schemas s ON s.schema_id=t.schema_id
JOIN sys.columns c ON c.object_id=t.object_id AND c.column_id=dc.parent_column_id
WHERE s.name='ops' AND t.name IN ('BillingPeriod','Charge','BillingPeriodCloseEvidence','BillingPeriodLateWork')
ORDER BY t.name,c.column_id;
"""

                let! triggers =
                    stringsAsync
                        connectionString
                        """
SELECT CONCAT(OBJECT_NAME(parent_id) COLLATE Latin1_General_100_BIN2,'|' COLLATE Latin1_General_100_BIN2,name COLLATE Latin1_General_100_BIN2)
FROM sys.triggers
WHERE parent_id IN (OBJECT_ID('ops.Charge'),OBJECT_ID('ops.BillingPeriodCloseEvidence'))
ORDER BY name;
"""

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(triggerNames runtime typeof<ChargeEntity>, Is.EqualTo(Set.ofList [ "TR_ops_Charge_Immutable" ] :> obj))

                        Assert.That(
                            triggerNames runtime typeof<BillingPeriodCloseEvidenceEntity>,
                            Is.EqualTo(Set.ofList [ "TR_ops_BillingPeriodCloseEvidence_Immutable" ] :> obj)
                        )

                        Assert.That(
                            triggerNames target typeof<ChargeEntity>
                            + triggerNames target typeof<BillingPeriodCloseEvidenceEntity>,
                            Is.EqualTo(expectedTriggers :> obj)
                        )

                        Assert.That(
                            triggerNames snapshot typeof<ChargeEntity>
                            + triggerNames snapshot typeof<BillingPeriodCloseEvidenceEntity>,
                            Is.EqualTo(expectedTriggers :> obj)
                        )

                        Assert.That(columns, Does.Contain("Charge|CurrencyCode|varchar|3|0|0"))
                        Assert.That(columns, Does.Contain("BillingPeriodCloseEvidence|AcceptedFactDigestSha256Hex|char|64|0|0"))
                        Assert.That(columns, Does.Contain("BillingPeriodCloseEvidence|PricingPreviewDigestSha256Hex|char|64|0|0"))
                        Assert.That(columns, Does.Contain("BillingPeriod|RetryDiagnostic|nvarchar|800|1|0"))
                        Assert.That(columns, Does.Contain("BillingPeriodLateWork|UsageFactId|uniqueidentifier|16|0|0"))
                        Assert.That(currencyCollation, Is.EqualTo([ "Latin1_General_100_BIN2" ] :> obj))
                        Assert.That(keys, Does.Contain("BillingPeriod|PK_ops_BillingPeriod|PRIMARY_KEY_CONSTRAINT"))
                        Assert.That(keys, Does.Contain("Charge|PK_ops_Charge|PRIMARY_KEY_CONSTRAINT"))
                        Assert.That(keys, Does.Contain("BillingPeriodCloseEvidence|PK_ops_BillingPeriodCloseEvidence|PRIMARY_KEY_CONSTRAINT"))
                        Assert.That(keys, Does.Contain("BillingPeriodLateWork|PK_ops_BillingPeriodLateWork|PRIMARY_KEY_CONSTRAINT"))
                        Assert.That(indexes, Does.Contain("BillingPeriod|UX_ops_BillingPeriod_ExactScope|1"))
                        Assert.That(indexes, Does.Contain("Charge|UX_ops_Charge_InitialPosting|1"))
                        Assert.That(indexes, Does.Contain("Charge|IX_ops_Charge_ChargePreviewLine|0"))
                        Assert.That(indexes, Does.Contain("BillingPeriodLateWork|IX_ops_BillingPeriodLateWork_UsageFact|0"))
                        Assert.That(foreignKeys, Does.Contain("Charge|FK_ops_Charge_BillingPeriod|ops|BillingPeriod|NO_ACTION"))
                        Assert.That(foreignKeys, Does.Contain("Charge|FK_ops_Charge_ChargePreviewLine|ops|ChargePreviewLine|NO_ACTION"))

                        Assert.That(
                            foreignKeys,
                            Does.Contain("BillingPeriodCloseEvidence|FK_ops_BillingPeriodCloseEvidence_BillingPeriod|ops|BillingPeriod|NO_ACTION")
                        )

                        Assert.That(foreignKeys, Does.Contain("BillingPeriodLateWork|FK_ops_BillingPeriodLateWork_BillingPeriod|ops|BillingPeriod|NO_ACTION"))
                        Assert.That(foreignKeys, Does.Contain("BillingPeriodLateWork|FK_ops_BillingPeriodLateWork_RawUsageFact|ops|RawUsageFact|NO_ACTION"))
                        Assert.That(checks, Does.Contain("Charge|CK_ops_Charge_Amount"))
                        Assert.That(checks, Does.Contain("Charge|CK_ops_Charge_Currency"))
                        Assert.That(checks, Does.Contain("BillingPeriodCloseEvidence|CK_ops_BillingPeriodCloseEvidence_Digests"))
                        Assert.That(checks, Does.Contain("BillingPeriodCloseEvidence|CK_ops_BillingPeriodCloseEvidence_Provenance"))
                        Assert.That(defaults, Does.Contain("Charge|CreatedAtUtc|DF_ops_Charge_CreatedAtUtc|(sysutcdatetime())"))
                        Assert.That(defaults, Does.Contain("BillingPeriod|CreatedAtUtc|DF_ops_BillingPeriod_CreatedAtUtc|(sysutcdatetime())"))
                        Assert.That(defaults, Does.Contain("BillingPeriod|UpdatedAtUtc|DF_ops_BillingPeriod_UpdatedAtUtc|(sysutcdatetime())"))
                        Assert.That(defaults, Does.Contain("BillingPeriodLateWork|CreatedAtUtc|DF_ops_BillingPeriodLateWork_CreatedAtUtc|(sysutcdatetime())"))

                        Assert.That(
                            triggers,
                            Is.EqualTo(
                                [
                                    "BillingPeriodCloseEvidence|TR_ops_BillingPeriodCloseEvidence_Immutable"
                                    "Charge|TR_ops_Charge_Immutable"
                                ]
                                :> obj
                            )
                        ))
                )
            })

    /// Proves owner concurrency and repository identity remain isolated at the exact close scope.
    [<Test>]
    member _.ConcurrentOwnersAndSiblingRepositoriesCloseAsSeparateScopes() =
        withDatabaseAsync (fun connectionString ->
            task {
                let organizationId = Guid.NewGuid()
                let ownerA, ownerB = Guid.NewGuid(), Guid.NewGuid()
                let repositoryA, repositoryB = Guid.NewGuid(), Guid.NewGuid()
                let ownerAScope = scopeFor ownerA organizationId repositoryA
                let ownerBScope = scopeFor ownerB organizationId repositoryA
                let siblingRepositoryScope = scopeFor ownerA organizationId repositoryB
                do! addPricingAsync connectionString ownerAScope
                use setup = new SqlConnection(connectionString)
                do! setup.OpenAsync CancellationToken.None
                use copyAssignments = setup.CreateCommand()

                copyAssignments.CommandText <-
                    """
INSERT INTO ops.PricingAssignment (PricingAssignmentId,OwnerId,OrganizationId,RepositoryId,PricingPlanId,EffectiveFromUtc)
SELECT NEWID(), @OwnerB, @OrganizationId, @RepositoryA, PricingPlanId, EffectiveFromUtc
FROM ops.PricingAssignment WHERE OwnerId=@OwnerA AND OrganizationId=@OrganizationId AND RepositoryId=@RepositoryA;
INSERT INTO ops.PricingAssignment (PricingAssignmentId,OwnerId,OrganizationId,RepositoryId,PricingPlanId,EffectiveFromUtc)
SELECT NEWID(), @OwnerA, @OrganizationId, @RepositoryB, PricingPlanId, EffectiveFromUtc
FROM ops.PricingAssignment WHERE OwnerId=@OwnerA AND OrganizationId=@OrganizationId AND RepositoryId=@RepositoryA;
"""

                copyAssignments.Parameters.Add("@OwnerA", System.Data.SqlDbType.UniqueIdentifier).Value <- ownerA
                copyAssignments.Parameters.Add("@OwnerB", System.Data.SqlDbType.UniqueIdentifier).Value <- ownerB
                copyAssignments.Parameters.Add("@OrganizationId", System.Data.SqlDbType.UniqueIdentifier).Value <- organizationId
                copyAssignments.Parameters.Add("@RepositoryA", System.Data.SqlDbType.UniqueIdentifier).Value <- repositoryA
                copyAssignments.Parameters.Add("@RepositoryB", System.Data.SqlDbType.UniqueIdentifier).Value <- repositoryB
                let! _ = copyAssignments.ExecuteNonQueryAsync CancellationToken.None

                do! acceptAsync connectionString (usageFact (Guid.NewGuid()) ownerA organizationId repositoryA (monthStart + Duration.FromDays 1) 1L)
                do! acceptAsync connectionString (usageFact (Guid.NewGuid()) ownerB organizationId repositoryA (monthStart + Duration.FromDays 1) 1L)
                do! acceptAsync connectionString (usageFact (Guid.NewGuid()) ownerA organizationId repositoryB (monthStart + Duration.FromDays 1) 1L)

                let close scope =
                    (SqlBillingPeriodCloser(connectionString) :> IBillingPeriodCloser)
                        .CloseAsync(request scope, CancellationToken.None)

                let! results = Task.WhenAll(close ownerAScope, close ownerBScope, close siblingRepositoryScope)
                let! periodCount = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriod WHERE State=2;"
                let! evidenceCount = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriodCloseEvidence;"

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(
                            results
                            |> Array.forall (function
                                | BillingPeriodCloseResult.Closed _ -> true
                                | _ -> false),
                            Is.True
                        )

                        Assert.That(periodCount, Is.EqualTo(3))
                        Assert.That(evidenceCount, Is.EqualTo(3)))
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
                let! databaseTimeBeforeClose = dateTimeAsync connectionString "SELECT SYSUTCDATETIME();"
                let! closed = closer.CloseAsync(request scope, CancellationToken.None)
                let! databaseTimeAfterClose = dateTimeAsync connectionString "SELECT SYSUTCDATETIME();"
                let! chargeCount = countAsync connectionString "SELECT COUNT(*) FROM ops.Charge;"
                let! lateWorkCount = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriodLateWork;"
                let! evidenceCount = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriodCloseEvidence;"
                let! previewQuantity = int64Async connectionString "SELECT TotalQuantity FROM ops.ChargePreviewLine;"
                let! previewCharge = int64Async connectionString "SELECT ChargeMicros FROM ops.ChargePreviewLine;"
                let! postedCharge = int64Async connectionString "SELECT ChargeMicros FROM ops.Charge;"
                let! expectedPreviewDigest = previewEvidenceDigestAsync connectionString
                let! expectedFactDigest = acceptedFactEvidenceDigestAsync connectionString

                let! persistedFactDigest =
                    task {
                        use connection = new SqlConnection(connectionString)
                        do! connection.OpenAsync CancellationToken.None
                        use command = connection.CreateCommand()
                        command.CommandText <- "SELECT AcceptedFactDigestSha256Hex FROM ops.BillingPeriodCloseEvidence;"
                        let! value = command.ExecuteScalarAsync CancellationToken.None
                        return value :?> string
                    }

                let! persistedPreviewDigest =
                    task {
                        use connection = new SqlConnection(connectionString)
                        do! connection.OpenAsync CancellationToken.None
                        use command = connection.CreateCommand()
                        command.CommandText <- "SELECT PricingPreviewDigestSha256Hex FROM ops.BillingPeriodCloseEvidence;"
                        let! value = command.ExecuteScalarAsync CancellationToken.None
                        return value :?> string
                    }

                let! persistedProvenance =
                    task {
                        use connection = new SqlConnection(connectionString)
                        do! connection.OpenAsync CancellationToken.None
                        use command = connection.CreateCommand()
                        command.CommandText <- "SELECT ScheduledOperationProvenance FROM ops.BillingPeriodCloseEvidence;"
                        let! value = command.ExecuteScalarAsync CancellationToken.None
                        return value :?> string
                    }

                let! persistedClosedAtUtc = dateTimeAsync connectionString "SELECT ClosedAtUtc FROM ops.BillingPeriodCloseEvidence;"

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(closedChargeCount closed, Is.EqualTo(1))
                        Assert.That(chargeCount, Is.EqualTo(1))
                        Assert.That(lateWorkCount, Is.Zero)
                        Assert.That(evidenceCount, Is.EqualTo(1))
                        Assert.That(previewQuantity, Is.EqualTo(7L))
                        Assert.That(previewCharge, Is.EqualTo(14L))
                        Assert.That(postedCharge, Is.EqualTo(14L))
                        Assert.That(persistedFactDigest, Is.EqualTo(expectedFactDigest))
                        Assert.That(persistedPreviewDigest, Is.EqualTo(expectedPreviewDigest))
                        Assert.That(persistedProvenance, Is.EqualTo("operations-tests/billing-period-close/v1"))
                        Assert.That(persistedClosedAtUtc, Is.GreaterThanOrEqualTo(databaseTimeBeforeClose))
                        Assert.That(persistedClosedAtUtc, Is.LessThanOrEqualTo(databaseTimeAfterClose)))
                )
            })

    /// Proves a close holding the real scope lock wins before accepted usage and that the later acceptance becomes one handoff row.
    [<Test>]
    member _.CloseBeforeAcceptanceCreatesExactlyOneLateWorkRow() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, repositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let scope = scopeFor ownerId organizationId repositoryId
                do! acceptAsync connectionString (usageFact (Guid.NewGuid()) ownerId organizationId repositoryId (monthStart + Duration.FromDays 1) 1L)
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
                        Assert.That(chargeCount, Is.EqualTo(1))
                        Assert.That(lateWorkCount, Is.EqualTo(1)))
                )
            })

    /// Proves an empty period is visibly nonterminal and posts nothing until the dedicated zero-fact coverage slice runs.
    [<Test>]
    member _.ZeroFactCloseRemainsPendingWithoutPosting() =
        withDatabaseAsync (fun connectionString ->
            task {
                let scope = scopeFor (Guid.NewGuid()) (Guid.NewGuid()) (Guid.NewGuid())
                let closer = SqlBillingPeriodCloser(connectionString) :> IBillingPeriodCloser
                let! result = closer.CloseAsync(request scope, CancellationToken.None)
                let! postingCount = countAsync connectionString "SELECT COUNT(*) FROM ops.Charge;"
                let! evidenceCount = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriodCloseEvidence;"

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(result, Is.EqualTo(BillingPeriodCloseResult.Blocked "ZeroFactCoveragePending"))
                        Assert.That(postingCount, Is.Zero)
                        Assert.That(evidenceCount, Is.Zero))
                )
            })

    /// Proves missing pricing blocks first, then corrected committed pricing retries once and clears only the obsolete diagnostic.
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
                do! addPricingAsync connectionString scope
                let! retried = closer.CloseAsync(request scope, CancellationToken.None)
                let! finalChargeCount = countAsync connectionString "SELECT COUNT(*) FROM ops.Charge;"

                let! clearedDiagnosticCount =
                    countAsync
                        connectionString
                        "SELECT COUNT(*) FROM ops.BillingPeriod WHERE State=2 AND RetryDiagnostic IS NULL AND RetryDiagnosticAtUtc IS NULL;"

                let! replay = closer.CloseAsync(request scope, CancellationToken.None)
                let! replayChargeCount = countAsync connectionString "SELECT COUNT(*) FROM ops.Charge;"

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(
                            (match result with
                             | BillingPeriodCloseResult.Blocked _ -> true
                             | _ -> false),
                            Is.True
                        )

                        Assert.That(chargeCount, Is.Zero)
                        Assert.That(blockedCount, Is.EqualTo(1))
                        Assert.That(closedChargeCount retried, Is.EqualTo(1))
                        Assert.That(finalChargeCount, Is.EqualTo(1))
                        Assert.That(clearedDiagnosticCount, Is.EqualTo(1))
                        Assert.That(closedChargeCount replay, Is.EqualTo(1))
                        Assert.That(replayChargeCount, Is.EqualTo(1)))
                )
            })

    /// Proves each unresolved production completeness source independently prevents the final close transaction.
    [<TestCase("pending")>]
    [<TestCase("rejected")>]
    [<TestCase("active-rejection")>]
    member _.PendingRejectedAndActiveRejectionEachBlockCloseWithoutPosting(blocker: string) =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, repositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let scope = scopeFor ownerId organizationId repositoryId
                let factId = Guid.NewGuid()
                let fact = usageFact factId ownerId organizationId repositoryId (monthStart + Duration.FromDays 8) 5L
                let journal = SqlOperationsUsageJournalStore(connectionString)
                let! appended = journal.AppendAsync(fact, CancellationToken.None)

                match appended with
                | Ok UsageFactJournalAppendResult.AppendedPending -> ()
                | other -> Assert.Fail($"Expected a newly appended Pending journal row but received {other}.")

                match blocker with
                | "pending" -> ()
                | "rejected" ->
                    let! rejected = journal.RejectAsync(fact, Array.empty, "test rejection", CancellationToken.None)
                    Assert.That(rejected, Is.EqualTo(UsageFactJournalRejectResult.RejectedFromJournal))
                | "active-rejection" ->
                    let! accepted = journal.ProcessAsync(fact, Array.empty, CancellationToken.None)
                    Assert.That(accepted, Is.EqualTo(UsageFactJournalProcessResult.AcceptedFromJournal))
                    use connection = new SqlConnection(connectionString)
                    do! connection.OpenAsync CancellationToken.None
                    use command = connection.CreateCommand()

                    command.CommandText <-
                        "INSERT INTO ops.UsageFactRejection (RejectionId,UsageFactId,OwnerId,OrganizationId,RepositoryId,MonthStartUtc,Reason,IsActive) VALUES (@RejectionId,@UsageFactId,@OwnerId,@OrganizationId,@RepositoryId,@MonthStartUtc,'independent active rejection',1);"

                    command.Parameters.Add("@RejectionId", System.Data.SqlDbType.UniqueIdentifier).Value <- Guid.NewGuid()
                    command.Parameters.Add("@UsageFactId", System.Data.SqlDbType.UniqueIdentifier).Value <- factId
                    command.Parameters.Add("@OwnerId", System.Data.SqlDbType.UniqueIdentifier).Value <- ownerId
                    command.Parameters.Add("@OrganizationId", System.Data.SqlDbType.UniqueIdentifier).Value <- organizationId
                    command.Parameters.Add("@RepositoryId", System.Data.SqlDbType.UniqueIdentifier).Value <- repositoryId
                    command.Parameters.Add("@MonthStartUtc", System.Data.SqlDbType.DateTime2).Value <- scope.MonthStart.ToDateTimeUtc()
                    let! _ = command.ExecuteNonQueryAsync CancellationToken.None
                    ()
                | unexpected -> invalidArg (nameof blocker) unexpected

                do! addPricingAsync connectionString scope
                let closer = SqlBillingPeriodCloser(connectionString) :> IBillingPeriodCloser
                let! result = closer.CloseAsync(request scope, CancellationToken.None)
                let! previewCount = countAsync connectionString "SELECT COUNT(*) FROM ops.ChargePreviewLine;"
                let! chargeCount = countAsync connectionString "SELECT COUNT(*) FROM ops.Charge;"
                let! evidenceCount = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriodCloseEvidence;"
                let! closedCount = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriod WHERE State=2;"

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(
                            (match result with
                             | BillingPeriodCloseResult.Blocked _ -> true
                             | _ -> false),
                            Is.True
                        )

                        Assert.That(previewCount, Is.Zero)
                        Assert.That(chargeCount, Is.Zero)
                        Assert.That(evidenceCount, Is.Zero)
                        Assert.That(closedCount, Is.Zero))
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

    /// Proves a persisted arithmetic overflow is retryable and corrected committed pricing posts exactly once.
    [<Test>]
    member _.ArithmeticOverflowBlocksThenCorrectedPricingRetriesExactlyOnce() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, repositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let scope = scopeFor ownerId organizationId repositoryId

                do!
                    acceptAsync
                        connectionString
                        (usageFact (Guid.NewGuid()) ownerId organizationId repositoryId (monthStart + Duration.FromDays 4) Int64.MaxValue)

                do! addPricingAsync connectionString scope

                use connection = new SqlConnection(connectionString)
                do! connection.OpenAsync CancellationToken.None
                use setOverflowPrice = connection.CreateCommand()
                setOverflowPrice.CommandText <- "UPDATE ops.PricingRate SET UnitPriceMicros=@Price;"
                setOverflowPrice.Parameters.Add("@Price", System.Data.SqlDbType.BigInt).Value <- Int64.MaxValue
                let! _ = setOverflowPrice.ExecuteNonQueryAsync CancellationToken.None
                let closer = SqlBillingPeriodCloser(connectionString) :> IBillingPeriodCloser
                let! blocked = closer.CloseAsync(request scope, CancellationToken.None)
                let! blockedCharges = countAsync connectionString "SELECT COUNT(*) FROM ops.Charge;"

                let! overflowDiagnostic =
                    countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriod WHERE RetryDiagnostic IS NOT NULL AND State IN (0,1);"

                use setCorrectPrice = connection.CreateCommand()
                setCorrectPrice.CommandText <- "UPDATE ops.PricingRate SET UnitPriceMicros=1;"
                let! _ = setCorrectPrice.ExecuteNonQueryAsync CancellationToken.None
                let! retried = closer.CloseAsync(request scope, CancellationToken.None)
                let! replay = closer.CloseAsync(request scope, CancellationToken.None)
                let! charges = countAsync connectionString "SELECT COUNT(*) FROM ops.Charge;"

                let! clearedDiagnostic =
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

                        Assert.That(blockedCharges, Is.Zero)
                        Assert.That(overflowDiagnostic, Is.EqualTo(1))
                        Assert.That(closedChargeCount retried, Is.EqualTo(1))
                        Assert.That(closedChargeCount replay, Is.EqualTo(1))
                        Assert.That(charges, Is.EqualTo(1))
                        Assert.That(clearedDiagnostic, Is.EqualTo(1)))
                )
            })

    /// Proves accepted-delivery replay and a conflicting cross-scope fact identity cannot enqueue late work.
    [<Test>]
    member _.DuplicateIncludedAndCrossScopeFactIdentityCreateNoLateWork() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, repositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let scope = scopeFor ownerId organizationId repositoryId
                let factId = Guid.NewGuid()
                let included = usageFact factId ownerId organizationId repositoryId (monthStart + Duration.FromDays 4) 5L
                do! acceptAsync connectionString included
                do! addPricingAsync connectionString scope
                let closer = SqlBillingPeriodCloser(connectionString) :> IBillingPeriodCloser
                let! closed = closer.CloseAsync(request scope, CancellationToken.None)
                let journal = SqlOperationsUsageJournalStore(connectionString)
                let! duplicate = journal.ProcessAsync(included, Array.empty, CancellationToken.None)

                let conflicting = usageFact factId (Guid.NewGuid()) (Guid.NewGuid()) (Guid.NewGuid()) (monthStart + Duration.FromDays 4) 5L

                let! crossScopeAppend =
                    task {
                        try
                            let! result = journal.AppendAsync(conflicting, CancellationToken.None)

                            return
                                match result with
                                | Error _ -> true
                                | Ok _ -> false
                        with
                        | :? InvalidOperationException -> return true
                    }

                let! originalLateWork = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriodLateWork;"
                let! allLateWork = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriodLateWork;"
                let! rawFacts = countAsync connectionString "SELECT COUNT(*) FROM ops.RawUsageFact;"

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(closedChargeCount closed, Is.EqualTo(1))
                        Assert.That(duplicate, Is.EqualTo(UsageFactJournalProcessResult.AlreadyAccepted))
                        Assert.That(crossScopeAppend, Is.True)
                        Assert.That(originalLateWork, Is.Zero)
                        Assert.That(allLateWork, Is.Zero)
                        Assert.That(rawFacts, Is.EqualTo(1)))
                )
            })

    /// Proves a later accepted fact creates one minimal handoff without changing any initial close value or evidence.
    [<Test>]
    member _.CloseFirstKeepsInitialPreviewPostingEvidenceTimestampAndProvenanceUnchanged() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, repositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let scope = scopeFor ownerId organizationId repositoryId
                let initial = usageFact (Guid.NewGuid()) ownerId organizationId repositoryId (monthStart + Duration.FromDays 2) 7L
                do! acceptAsync connectionString initial
                do! addPricingAsync connectionString scope
                let closer = SqlBillingPeriodCloser(connectionString) :> IBillingPeriodCloser
                let! closed = closer.CloseAsync(request scope, CancellationToken.None)

                let snapshotSql =
                    """
SELECT CONCAT('period|',BillingPeriodId,'|',State,'|',ISNULL(RetryDiagnostic,''),'|',CONVERT(varchar(33),CreatedAtUtc,126),'|',CONVERT(varchar(33),UpdatedAtUtc,126)) COLLATE Latin1_General_100_BIN2 FROM ops.BillingPeriod
UNION ALL SELECT CONCAT('preview|',ChargePreviewLineId,'|',OwnerId,'|',OrganizationId,'|',RepositoryId,'|',PeriodFromUtc,'|',PeriodToUtc,'|',FactKind,'|',BillableUsageKindMappingId,'|',BillableUsageKind,'|',PricingAssignmentId,'|',PricingPlanId,'|',PricingRateId,'|',CurrencyCode,'|',UnitName,'|',UnitQuantity,'|',UnitPriceMicros,'|',EffectiveFromUtc,'|',EffectiveToUtc,'|',TotalQuantity,'|',ChargeMicros) COLLATE Latin1_General_100_BIN2 FROM ops.ChargePreviewLine
UNION ALL SELECT CONCAT('charge|',ChargeId,'|',BillingPeriodId,'|',ChargePreviewLineId,'|',CurrencyCode,'|',ChargeMicros,'|',CONVERT(varchar(33),CreatedAtUtc,126)) COLLATE Latin1_General_100_BIN2 FROM ops.Charge
UNION ALL SELECT CONCAT('evidence|',BillingPeriodId,'|',AcceptedFactDigestSha256Hex,'|',PricingPreviewDigestSha256Hex,'|',CONVERT(varchar(33),ClosedAtUtc,126),'|',ScheduledOperationProvenance) COLLATE Latin1_General_100_BIN2 FROM ops.BillingPeriodCloseEvidence
ORDER BY 1;
"""

                let! before = stringsAsync connectionString snapshotSql
                do! acceptAsync connectionString (usageFact (Guid.NewGuid()) ownerId organizationId repositoryId (monthStart + Duration.FromDays 3) 9L)
                let! after = stringsAsync connectionString snapshotSql
                let! lateWork = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriodLateWork;"

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(closedChargeCount closed, Is.EqualTo(1))
                        Assert.That(before, Is.EqualTo(after :> obj))
                        Assert.That(lateWork, Is.EqualTo(1)))
                )
            })

    /// Proves cancellation after each close staging point rolls the entire real SQL transaction back to its prior durable truth.
    [<TestCase("preview")>]
    [<TestCase("ledger")>]
    [<TestCase("evidence")>]
    member _.CancellationAfterEveryCloseStageRollsBackAllDurableState(stage: string) =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, repositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let scope = scopeFor ownerId organizationId repositoryId
                let fact = usageFact (Guid.NewGuid()) ownerId organizationId repositoryId (monthStart + Duration.FromDays 9) 17L
                do! acceptAsync connectionString fact
                do! addPricingAsync connectionString scope
                let! before = durableCloseSnapshotAsync connectionString
                use cancellation = new CancellationTokenSource()
                let interleaving = BillingCloseCancellationInterleaving(stage, cancellation)

                let closer =
                    SqlBillingPeriodCloser.CreateForTest(connectionString, interleaving :> IBillingPeriodCloseTransactionInterleaving) :> IBillingPeriodCloser

                Assert.ThrowsAsync<OperationCanceledException>(Func<Task>(fun () -> closer.CloseAsync(request scope, cancellation.Token) :> Task))
                |> ignore

                let! _ = interleaving.Reached
                let! after = durableCloseSnapshotAsync connectionString

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(cancellation.IsCancellationRequested, Is.True)
                        Assert.That(after, Is.EqualTo(before :> obj)))
                )
            })

    /// Proves cancellation after late-work staging rolls raw, aggregate, handoff, rejection, and journal mutations back together.
    [<Test>]
    member _.CancellationAfterLateWorkStagingPreservesEveryDurableRecord() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, repositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let scope = scopeFor ownerId organizationId repositoryId
                do! acceptAsync connectionString (usageFact (Guid.NewGuid()) ownerId organizationId repositoryId (monthStart + Duration.FromDays 10) 19L)
                do! addPricingAsync connectionString scope
                let closer = SqlBillingPeriodCloser(connectionString) :> IBillingPeriodCloser
                let! _ = closer.CloseAsync(request scope, CancellationToken.None)
                let lateFact = usageFact (Guid.NewGuid()) ownerId organizationId repositoryId (monthStart + Duration.FromDays 11) 23L
                let journal = SqlOperationsUsageJournalStore(connectionString)
                let! appended = journal.AppendAsync(lateFact, CancellationToken.None)

                match appended with
                | Ok UsageFactJournalAppendResult.AppendedPending -> ()
                | unexpected -> Assert.Fail($"Expected a newly appended Pending journal row but received {unexpected}.")

                let! before = durableCloseSnapshotAsync connectionString
                use cancellation = new CancellationTokenSource()
                let interleaving = UsageJournalCancellationInterleaving(cancellation)

                let cancellingJournal =
                    SqlOperationsUsageJournalStore.CreateForTest(connectionString, interleaving :> IOperationsUsageJournalTransactionInterleaving)

                Assert.ThrowsAsync<OperationCanceledException>(
                    Func<Task>(fun () -> cancellingJournal.ProcessAsync(lateFact, Array.empty, cancellation.Token) :> Task)
                )
                |> ignore

                let! _ = interleaving.Reached
                let! after = durableCloseSnapshotAsync connectionString

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(cancellation.IsCancellationRequested, Is.True)
                        Assert.That(after, Is.EqualTo(before :> obj)))
                )
            })

    /// Proves a genuinely concurrent second close waits on the exact shared SQL application lock before both callers converge.
    [<Test>]
    member _.CompetingCloseWaitsOnTheExactSqlApplicationLockThenConverges() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, repositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let scope = scopeFor ownerId organizationId repositoryId
                do! acceptAsync connectionString (usageFact (Guid.NewGuid()) ownerId organizationId repositoryId (monthStart + Duration.FromDays 12) 29L)
                do! addPricingAsync connectionString scope
                let firstInterleaving = BillingCloseInterleaving("preview", true)

                let first =
                    (SqlBillingPeriodCloser.CreateForTest(connectionString, firstInterleaving :> IBillingPeriodCloseTransactionInterleaving)
                    :> IBillingPeriodCloser)
                        .CloseAsync(request scope, CancellationToken.None)

                do! firstInterleaving.Reached.WaitAsync(CancellationToken.None)
                let secondStarted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                secondStarted.TrySetResult() |> ignore

                let second: Task<BillingPeriodCloseResult> =
                    (SqlBillingPeriodCloser(connectionString) :> IBillingPeriodCloser)
                        .CloseAsync(request scope, CancellationToken.None)

                do! secondStarted.Task.WaitAsync(CancellationToken.None)
                let! exactScopeProbe = probeScopeLockAsync connectionString scope

                let! lockRows =
                    stringsAsync
                        connectionString
                        """
SELECT CONCAT(request_status,'|',request_mode,'|',request_session_id,'|',resource_description)
FROM sys.dm_tran_locks
WHERE resource_type='APPLICATION' AND resource_database_id=DB_ID()
ORDER BY request_status,request_session_id;
"""

                let observedGrantAndWait =
                    lockRows
                    |> List.exists (fun value -> value.StartsWith("GRANT|X|", StringComparison.Ordinal))
                    && lockRows
                       |> List.exists (fun value -> value.StartsWith("WAIT|X|", StringComparison.Ordinal))

                try
                    Assert.Multiple(
                        Action (fun () ->
                            Assert.That<bool>(first.IsCompleted, Is.False)
                            Assert.That<bool>(second.IsCompleted, Is.False)
                            Assert.That(exactScopeProbe, Is.EqualTo(-1), "The exact expected shared application lock must reject a competing transaction.")
                            Assert.That(observedGrantAndWait, Is.True, $"Expected SQL to report a granted and waiting application lock, got {lockRows}."))
                    )
                finally
                    firstInterleaving.Release()

                let! completed = Task.WhenAll(first, second)
                let! charges = countAsync connectionString "SELECT COUNT(*) FROM ops.Charge;"
                let! evidence = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriodCloseEvidence;"
                let! closed = countAsync connectionString "SELECT COUNT(*) FROM ops.BillingPeriod WHERE State=2;"

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That<int array>(completed |> Array.map closedChargeCount, Is.EqualTo<int>([| 1; 1 |]))
                        Assert.That(charges, Is.EqualTo(1))
                        Assert.That(evidence, Is.EqualTo(1))
                        Assert.That(closed, Is.EqualTo(1)))
                )
            })

    /// Exercises every current close-owned SQL rejection and immutability rule against migrated physical tables.
    [<Test>]
    member _.LivePhysicalCloseRejectionMatrixPreservesAllOriginalDurableValues() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, repositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let scope = scopeFor ownerId organizationId repositoryId
                do! acceptAsync connectionString (usageFact (Guid.NewGuid()) ownerId organizationId repositoryId (monthStart + Duration.FromDays 13) 31L)
                do! addPricingAsync connectionString scope
                let closer = SqlBillingPeriodCloser(connectionString) :> IBillingPeriodCloser
                let! closed = closer.CloseAsync(request scope, CancellationToken.None)

                let companionPeriodId = Guid.NewGuid()
                let companionOwnerId = Guid.NewGuid()

                use setupConnection = new SqlConnection(connectionString)
                do! setupConnection.OpenAsync CancellationToken.None
                use setup = setupConnection.CreateCommand()

                setup.CommandText <-
                    """
INSERT INTO ops.BillingPeriod
    (BillingPeriodId,OwnerId,OrganizationId,RepositoryId,MonthStartUtc,NextMonthStartUtc,State,RetryDiagnostic,RetryDiagnosticAtUtc)
VALUES
    (@BillingPeriodId,@OwnerId,@OrganizationId,@RepositoryId,'2026-06-01T00:00:00','2026-07-01T00:00:00',0,NULL,NULL);
"""

                setup.Parameters.Add("@BillingPeriodId", System.Data.SqlDbType.UniqueIdentifier).Value <- companionPeriodId
                setup.Parameters.Add("@OwnerId", System.Data.SqlDbType.UniqueIdentifier).Value <- companionOwnerId
                setup.Parameters.Add("@OrganizationId", System.Data.SqlDbType.UniqueIdentifier).Value <- Guid.NewGuid()
                setup.Parameters.Add("@RepositoryId", System.Data.SqlDbType.UniqueIdentifier).Value <- Guid.NewGuid()
                let! _ = setup.ExecuteNonQueryAsync CancellationToken.None

                let validDigest = String.replicate 64 "A"
                let companionPeriod = companionPeriodId.ToString("D")

                let closedPeriod =
                    match closed with
                    | BillingPeriodCloseResult.Closed (periodId, _) -> periodId.ToString("D")
                    | unexpected ->
                        Assert.Fail($"Expected the physical rejection fixture to close, got {unexpected}.")
                        String.Empty

                do!
                    rejectSqlAndPreserveAsync
                        connectionString
                        "BillingPeriod rejects an unknown state."
                        """
INSERT INTO ops.BillingPeriod (BillingPeriodId,OwnerId,OrganizationId,RepositoryId,MonthStartUtc,NextMonthStartUtc,State,RetryDiagnostic,RetryDiagnosticAtUtc)
VALUES (NEWID(),NEWID(),NEWID(),NEWID(),'2026-06-01T00:00:00','2026-07-01T00:00:00',3,NULL,NULL);
"""

                do!
                    rejectSqlAndPreserveAsync
                        connectionString
                        "BillingPeriod rejects a non-month-aligned range."
                        """
INSERT INTO ops.BillingPeriod (BillingPeriodId,OwnerId,OrganizationId,RepositoryId,MonthStartUtc,NextMonthStartUtc,State,RetryDiagnostic,RetryDiagnosticAtUtc)
VALUES (NEWID(),NEWID(),NEWID(),NEWID(),'2026-06-02T00:00:00','2026-07-02T00:00:00',0,NULL,NULL);
"""

                do!
                    rejectSqlAndPreserveAsync
                        connectionString
                        "BillingPeriod rejects a diagnostic on a Closed row."
                        """
INSERT INTO ops.BillingPeriod (BillingPeriodId,OwnerId,OrganizationId,RepositoryId,MonthStartUtc,NextMonthStartUtc,State,RetryDiagnostic,RetryDiagnosticAtUtc)
VALUES (NEWID(),NEWID(),NEWID(),NEWID(),'2026-06-01T00:00:00','2026-07-01T00:00:00',2,'must remain retryable',SYSUTCDATETIME());
"""

                do!
                    rejectSqlAndPreserveAsync
                        connectionString
                        "Charge rejects a negative amount with otherwise valid foreign keys."
                        $"""
INSERT INTO ops.Charge (ChargeId,BillingPeriodId,ChargePreviewLineId,CurrencyCode,ChargeMicros)
SELECT NEWID(),'{companionPeriod}',ChargePreviewLineId,'USD',-1 FROM ops.ChargePreviewLine;
"""

                do!
                    rejectSqlAndPreserveAsync
                        connectionString
                        "ChargePreviewLine rejects a negative total quantity."
                        """
INSERT INTO ops.ChargePreviewLine
    (ChargePreviewLineId,OwnerId,OrganizationId,RepositoryId,PeriodFromUtc,PeriodToUtc,FactKind,BillableUsageKindMappingId,BillableUsageKind,PricingAssignmentId,PricingPlanId,PricingRateId,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc,EffectiveToUtc,TotalQuantity,ChargeMicros)
SELECT NEWID(),OwnerId,OrganizationId,RepositoryId,PeriodFromUtc,PeriodToUtc,FactKind,BillableUsageKindMappingId,BillableUsageKind,PricingAssignmentId,PricingPlanId,PricingRateId,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc,EffectiveToUtc,-1,ChargeMicros
FROM ops.ChargePreviewLine;
"""

                do!
                    rejectSqlAndPreserveAsync
                        connectionString
                        "Close evidence rejects a malformed digest."
                        $"""
INSERT INTO ops.BillingPeriodCloseEvidence (BillingPeriodId,AcceptedFactDigestSha256Hex,PricingPreviewDigestSha256Hex,ClosedAtUtc,ScheduledOperationProvenance)
VALUES ('{companionPeriod}','NOT-A-DIGEST','{validDigest}',SYSUTCDATETIME(),'physical-matrix');
"""

                do!
                    rejectSqlAndPreserveAsync
                        connectionString
                        "Close evidence rejects blank provenance."
                        $"""
INSERT INTO ops.BillingPeriodCloseEvidence (BillingPeriodId,AcceptedFactDigestSha256Hex,PricingPreviewDigestSha256Hex,ClosedAtUtc,ScheduledOperationProvenance)
VALUES ('{companionPeriod}','{validDigest}','{validDigest}',SYSUTCDATETIME(),'   ');
"""

                do!
                    rejectSqlAndPreserveAsync
                        connectionString
                        "Charge rejects a duplicate immutable initial-posting identity."
                        $"""
INSERT INTO ops.Charge (ChargeId,BillingPeriodId,ChargePreviewLineId,CurrencyCode,ChargeMicros)
SELECT NEWID(),BillingPeriodId,ChargePreviewLineId,CurrencyCode,ChargeMicros FROM ops.Charge WHERE BillingPeriodId='{closedPeriod}';
"""

                do!
                    rejectSqlAndPreserveAsync
                        connectionString
                        "Charge rejects a missing BillingPeriod foreign key."
                        """
INSERT INTO ops.Charge (ChargeId,BillingPeriodId,ChargePreviewLineId,CurrencyCode,ChargeMicros)
SELECT NEWID(),NEWID(),ChargePreviewLineId,'USD',0 FROM ops.ChargePreviewLine;
"""

                do!
                    rejectSqlAndPreserveAsync
                        connectionString
                        "Charge rejects a missing ChargePreviewLine foreign key."
                        $"""
INSERT INTO ops.Charge (ChargeId,BillingPeriodId,ChargePreviewLineId,CurrencyCode,ChargeMicros)
VALUES (NEWID(),'{companionPeriod}',NEWID(),'USD',0);
"""

                do!
                    rejectSqlAndPreserveAsync
                        connectionString
                        "Close evidence rejects a missing BillingPeriod foreign key."
                        $"""
INSERT INTO ops.BillingPeriodCloseEvidence (BillingPeriodId,AcceptedFactDigestSha256Hex,PricingPreviewDigestSha256Hex,ClosedAtUtc,ScheduledOperationProvenance)
VALUES (NEWID(),'{validDigest}','{validDigest}',SYSUTCDATETIME(),'physical-matrix');
"""

                do!
                    rejectSqlAndPreserveAsync
                        connectionString
                        "Late work rejects a missing BillingPeriod foreign key."
                        """
INSERT INTO ops.BillingPeriodLateWork (BillingPeriodId,UsageFactId)
SELECT NEWID(),UsageFactId FROM ops.RawUsageFact;
"""

                do!
                    rejectSqlAndPreserveAsync
                        connectionString
                        "Late work rejects a missing RawUsageFact foreign key."
                        $"""
INSERT INTO ops.BillingPeriodLateWork (BillingPeriodId,UsageFactId)
VALUES ('{closedPeriod}',NEWID());
"""

                do! rejectSqlAndPreserveAsync connectionString "Immutable Charge rejects update." "UPDATE ops.Charge SET ChargeMicros=99;"

                do! rejectSqlAndPreserveAsync connectionString "Immutable Charge rejects delete." "DELETE FROM ops.Charge;"

                do!
                    rejectSqlAndPreserveAsync
                        connectionString
                        "Immutable close evidence rejects update."
                        "UPDATE ops.BillingPeriodCloseEvidence SET ScheduledOperationProvenance='tampered';"

                do! rejectSqlAndPreserveAsync connectionString "Immutable close evidence rejects delete." "DELETE FROM ops.BillingPeriodCloseEvidence;"
            })
