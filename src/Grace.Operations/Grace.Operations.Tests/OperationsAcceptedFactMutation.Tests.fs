namespace Grace.Operations.Tests

open Grace.Operations.Data
open Grace.Shared
open Grace.Types.Common
open Grace.Types.Usage
open Microsoft.Data.SqlClient
open NodaTime
open NUnit.Framework
open System
open System.Data
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Fails an accepted-fact caller transaction only after all primitive-owned durable mutations have been staged.
type private FailAfterAcceptedFactStaging() =
    interface IAcceptedFactMutationInterleaving with
        member _.AfterAcceptedFactMutationsStagedAsync(_, _) = Task.FromException(InvalidOperationException("Injected failure after accepted-fact staging."))

/// Supplies the production-equivalent inert callback for direct primitive proofs.
type private NoAcceptedFactMutationTestInterleaving() =
    interface IAcceptedFactMutationInterleaving with
        member _.AfterAcceptedFactMutationsStagedAsync(_, _) = Task.CompletedTask

/// Counts post-staging callbacks so rejected caller plans prove they never reached the primitive interleaving.
type private CountingAcceptedFactMutationInterleaving() =
    let mutable invocationCount = 0

    /// Exposes how often the accepted-fact primitive reached its post-staging callback.
    member _.InvocationCount = invocationCount

    interface IAcceptedFactMutationInterleaving with
        member _.AfterAcceptedFactMutationsStagedAsync(_, _) =
            invocationCount <- invocationCount + 1
            Task.CompletedTask

/// Exercises the transaction-scoped accepted-fact primitive against disposable SQL Server databases.
[<TestFixture>]
[<NonParallelizable>]
type OperationsAcceptedFactMutationTests() =

    /// Names the explicit SQL connection used only by the Operations disposable-database proof profile.
    [<Literal>]
    let sqlConnectionStringEnvironmentVariable = "GRACE_OPERATIONS_SQL_TEST_CONNECTION_STRING"

    /// Uses a unique database name so this fixture never observes another proof's rows.
    let databaseName () = $"GraceOperationsAcceptedFactMutation_{Guid.NewGuid():N}"

    /// Obtains the configured SQL Server endpoint or records the repository's explicit environment skip.
    let requireSqlConnectionString () =
        let value = Environment.GetEnvironmentVariable(sqlConnectionStringEnvironmentVariable)

        if String.IsNullOrWhiteSpace value then
            Assert.Ignore($"{sqlConnectionStringEnvironmentVariable} is required for accepted-fact SQL tests.")

        value

    /// Creates one migrated disposable Operations database for a single real-SQL proof.
    let createDatabaseAsync () =
        task {
            let builder = SqlConnectionStringBuilder(requireSqlConnectionString ())
            builder.InitialCatalog <- databaseName ()
            let schema = OperationsUsageSchema(builder.ConnectionString, OperationsUsageSchemaBootstrapMode.CreateDatabaseIfMissing)
            do! schema.EnsureCreatedAsync CancellationToken.None
            return builder.ConnectionString
        }

    /// Removes only the disposable database created by this fixture.
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

    /// Runs an isolated proof and always removes the database owned by this fixture.
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

    /// Builds one valid repository fact for direct primitive invocation.
    let fact usageFactId ownerId organizationId repositoryId observedAt quantity =
        UsageFact.RepositoryStorageBytesMinute(
            usageFactId,
            CorrelationId $"accepted-fact-mutation-{usageFactId}",
            ownerId,
            organizationId,
            repositoryId,
            StoragePoolId "accepted-fact-mutation-pool",
            quantity,
            observedAt
        )

    /// Builds a validated persistence plan or fails the proof with the contract errors.
    let planFor usageFact =
        match UsageFactPersistencePlan.tryCreate usageFact (JsonSerializer.SerializeToUtf8Bytes(usageFact, Constants.JsonSerializerOptions)) with
        | Ok plan -> plan
        | Error errors ->
            Assert.Fail(String.Join("; ", errors))
            Unchecked.defaultof<UsageFactPersistencePlan>

    /// Derives one exact billing scope from the supplied valid fact.
    let scopeFor usageFact =
        match BillingCompletenessScope.tryCreate usageFact.Scope.OwnerId usageFact.Scope.OrganizationId usageFact.Scope.RepositoryId usageFact.ObservedAt with
        | Ok scope -> scope
        | Error errors ->
            Assert.Fail(String.Join("; ", errors))
            Unchecked.defaultof<BillingCompletenessScope>

    /// Inserts the narrow billing-period row used to distinguish open and closed primitive outcomes.
    let seedPeriodAsync connectionString periodId (scope: BillingCompletenessScope) state =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()

            command.CommandText <-
                """
INSERT INTO ops.BillingPeriod (BillingPeriodId,OwnerId,OrganizationId,RepositoryId,MonthStartUtc,NextMonthStartUtc,State)
VALUES (@BillingPeriodId,@OwnerId,@OrganizationId,@RepositoryId,@MonthStartUtc,@NextMonthStartUtc,@State);
"""

            command.Parameters.Add("@BillingPeriodId", SqlDbType.UniqueIdentifier).Value <- periodId
            command.Parameters.Add("@OwnerId", SqlDbType.UniqueIdentifier).Value <- scope.OwnerId
            command.Parameters.Add("@OrganizationId", SqlDbType.UniqueIdentifier).Value <- scope.OrganizationId
            command.Parameters.Add("@RepositoryId", SqlDbType.UniqueIdentifier).Value <- scope.RepositoryId
            command.Parameters.Add("@MonthStartUtc", SqlDbType.DateTime2).Value <- scope.MonthStart.ToDateTimeUtc()

            command.Parameters.Add("@NextMonthStartUtc", SqlDbType.DateTime2).Value <- (BillingCompletenessScope.nextMonthStart scope)
                .ToDateTimeUtc()

            command.Parameters.Add("@State", SqlDbType.Int).Value <- state
            let! _ = command.ExecuteNonQueryAsync CancellationToken.None
            return ()
        }

    /// Writes active scoped rejection evidence that acceptance must repair only when the raw fact is newly inserted.
    let seedActiveRejectionAsync connectionString usageFactId (scope: BillingCompletenessScope) =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()

            command.CommandText <-
                """
INSERT INTO ops.UsageFactRejection (RejectionId,UsageFactId,OwnerId,OrganizationId,RepositoryId,MonthStartUtc,Reason,IsActive)
VALUES (@RejectionId,@UsageFactId,@OwnerId,@OrganizationId,@RepositoryId,@MonthStartUtc,N'accepted-fact mutation repair fixture',1);
"""

            command.Parameters.Add("@RejectionId", SqlDbType.UniqueIdentifier).Value <- Guid.NewGuid()
            command.Parameters.Add("@UsageFactId", SqlDbType.UniqueIdentifier).Value <- usageFactId
            command.Parameters.Add("@OwnerId", SqlDbType.UniqueIdentifier).Value <- scope.OwnerId
            command.Parameters.Add("@OrganizationId", SqlDbType.UniqueIdentifier).Value <- scope.OrganizationId
            command.Parameters.Add("@RepositoryId", SqlDbType.UniqueIdentifier).Value <- scope.RepositoryId
            command.Parameters.Add("@MonthStartUtc", SqlDbType.DateTime2).Value <- scope.MonthStart.ToDateTimeUtc()
            let! _ = command.ExecuteNonQueryAsync CancellationToken.None
            return ()
        }

    /// Captures every row the primitive may mutate so duplicate, conflict, and rollback paths can prove exact preservation.
    let durableProjectionAsync connectionString =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()

            command.CommandText <-
                """
SELECT CONCAT('RawUsageFact|',(SELECT * FROM ops.RawUsageFact ORDER BY UsageFactId FOR JSON PATH, INCLUDE_NULL_VALUES))
UNION ALL SELECT CONCAT('UsageAggregateMinute|',(SELECT * FROM ops.UsageAggregateMinute ORDER BY OwnerId,OrganizationId,RepositoryId,StoragePoolId,BucketStartUtc FOR JSON PATH, INCLUDE_NULL_VALUES))
UNION ALL SELECT CONCAT('UsageFactRejection|',(SELECT * FROM ops.UsageFactRejection ORDER BY RejectionId FOR JSON PATH, INCLUDE_NULL_VALUES))
UNION ALL SELECT CONCAT('BillingPeriodLateWork|',(SELECT * FROM ops.BillingPeriodLateWork ORDER BY BillingPeriodId,UsageFactId FOR JSON PATH, INCLUDE_NULL_VALUES))
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

    /// Reads one scalar count from the disposable SQL database.
    let countAsync connectionString query =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()
            command.CommandText <- query
            let! value = command.ExecuteScalarAsync CancellationToken.None
            return Convert.ToInt32 value
        }

    /// Invokes the primitive in a caller-owned transaction and commits only after the primitive has returned.
    let acceptAndCommitAsync connectionString interleaving usageFact =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use transaction = connection.BeginTransaction()
            let mutation = SqlAcceptedFactMutation(interleaving)
            let! outcome = mutation.AcceptAsync(connection, transaction, planFor usageFact, scopeFor usageFact, CancellationToken.None)
            do! transaction.CommitAsync CancellationToken.None
            return outcome
        }

    /// Proves new open/closed facts, duplicates, conflicts, and caller-owned rollback through the one shared primitive.
    [<Test>]
    member _.AcceptedFactMutationPreservesScopeAndStagesPendingWorkOnlyAfterClose() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, openRepositoryId, closedRepositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let observedAt = Instant.FromUtc(2026, 8, 12, 10, 15)
                let openFact = fact (Guid.NewGuid()) ownerId organizationId openRepositoryId observedAt 7L
                let closedFact = fact (Guid.NewGuid()) ownerId organizationId closedRepositoryId observedAt 11L
                let openScope = scopeFor openFact
                let closedScope = scopeFor closedFact
                let openPeriodId, closedPeriodId = Guid.NewGuid(), Guid.NewGuid()
                do! seedPeriodAsync connectionString openPeriodId openScope 0
                do! seedPeriodAsync connectionString closedPeriodId closedScope 2
                do! seedActiveRejectionAsync connectionString closedFact.UsageFactId closedScope

                let noInterleaving = NoAcceptedFactMutationTestInterleaving() :> IAcceptedFactMutationInterleaving
                let! openOutcome = acceptAndCommitAsync connectionString noInterleaving openFact
                let! closedOutcome = acceptAndCommitAsync connectionString noInterleaving closedFact
                let! beforeDuplicate = durableProjectionAsync connectionString
                let! duplicateOutcome = acceptAndCommitAsync connectionString noInterleaving closedFact
                let! afterDuplicate = durableProjectionAsync connectionString

                let conflictingFact = fact closedFact.UsageFactId ownerId organizationId openRepositoryId observedAt 13L
                let! beforeConflict = durableProjectionAsync connectionString
                use conflictConnection = new SqlConnection(connectionString)
                do! conflictConnection.OpenAsync CancellationToken.None
                use conflictTransaction = conflictConnection.BeginTransaction()
                let conflictMutation = SqlAcceptedFactMutation(null)

                let! conflictRaised =
                    task {
                        try
                            let! _ =
                                conflictMutation.AcceptAsync(
                                    conflictConnection,
                                    conflictTransaction,
                                    planFor conflictingFact,
                                    scopeFor conflictingFact,
                                    CancellationToken.None
                                )

                            return false
                        with
                        | :? SqlException -> return true
                    }

                do! conflictTransaction.RollbackAsync CancellationToken.None
                let! afterConflict = durableProjectionAsync connectionString

                let! beforeMismatchedScope = durableProjectionAsync connectionString
                use mismatchedScopeConnection = new SqlConnection(connectionString)
                do! mismatchedScopeConnection.OpenAsync CancellationToken.None
                use mismatchedScopeTransaction = mismatchedScopeConnection.BeginTransaction()
                let mismatchedScopeMutation = SqlAcceptedFactMutation(noInterleaving)

                let! mismatchedScopeRaised =
                    task {
                        try
                            let! _ =
                                mismatchedScopeMutation.AcceptAsync(
                                    mismatchedScopeConnection,
                                    mismatchedScopeTransaction,
                                    planFor openFact,
                                    closedScope,
                                    CancellationToken.None
                                )

                            return false
                        with
                        | :? ArgumentException -> return true
                    }

                do! mismatchedScopeTransaction.RollbackAsync CancellationToken.None
                let! afterMismatchedScope = durableProjectionAsync connectionString

                let rollbackFact = fact (Guid.NewGuid()) ownerId organizationId closedRepositoryId (observedAt + Duration.FromDays 1) 17L
                let! beforeRollback = durableProjectionAsync connectionString
                use rollbackConnection = new SqlConnection(connectionString)
                do! rollbackConnection.OpenAsync CancellationToken.None
                use rollbackTransaction = rollbackConnection.BeginTransaction()
                let rollbackMutation = SqlAcceptedFactMutation(FailAfterAcceptedFactStaging() :> IAcceptedFactMutationInterleaving)

                let! rollbackRaised =
                    task {
                        try
                            let! _ =
                                rollbackMutation.AcceptAsync(
                                    rollbackConnection,
                                    rollbackTransaction,
                                    planFor rollbackFact,
                                    scopeFor rollbackFact,
                                    CancellationToken.None
                                )

                            return false
                        with
                        | :? InvalidOperationException -> return true
                    }

                do! rollbackTransaction.RollbackAsync CancellationToken.None
                let! afterRollback = durableProjectionAsync connectionString
                let! openLateWork = countAsync connectionString $"SELECT COUNT(*) FROM ops.BillingPeriodLateWork WHERE BillingPeriodId = '{openPeriodId:D}';"

                let! closedLateWork =
                    countAsync
                        connectionString
                        $"SELECT COUNT(*) FROM ops.BillingPeriodLateWork WHERE BillingPeriodId = '{closedPeriodId:D}' AND UsageFactId = '{closedFact.UsageFactId:D}';"

                let! repairedRejection =
                    countAsync
                        connectionString
                        $"SELECT COUNT(*) FROM ops.UsageFactRejection WHERE UsageFactId = '{closedFact.UsageFactId:D}' AND IsActive = 1;"

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(openOutcome, Is.EqualTo(InsertedIntoOpenPeriod))
                        Assert.That(closedOutcome, Is.EqualTo(InsertedIntoClosedPeriod))
                        Assert.That(duplicateOutcome, Is.EqualTo(ExistingSameScope))
                        Assert.That<string list>(afterDuplicate, Is.EqualTo<string list>(beforeDuplicate))
                        Assert.That(conflictRaised, Is.True)
                        Assert.That<string list>(afterConflict, Is.EqualTo<string list>(beforeConflict))
                        Assert.That(mismatchedScopeRaised, Is.True)
                        Assert.That<string list>(afterMismatchedScope, Is.EqualTo<string list>(beforeMismatchedScope))
                        Assert.That(rollbackRaised, Is.True)
                        Assert.That<string list>(afterRollback, Is.EqualTo<string list>(beforeRollback))
                        Assert.That(openLateWork, Is.Zero)
                        Assert.That(closedLateWork, Is.EqualTo(1))
                        Assert.That(repairedRejection, Is.Zero))
                )
            })

    /// Rejects record-updated aggregates before they can diverge durable raw, aggregate, rejection, or late-work truth.
    [<Test>]
    member _.AcceptedFactMutationRejectsAggregateThatDoesNotExactlyMatchRawFact() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId, organizationId, repositoryId = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let observedAt = Instant.FromUtc(2026, 8, 12, 10, 15)
                let baselineFact = fact (Guid.NewGuid()) ownerId organizationId repositoryId observedAt 7L
                let baselineScope = scopeFor baselineFact
                let closedPeriodId = Guid.NewGuid()
                do! seedPeriodAsync connectionString closedPeriodId baselineScope 2
                do! seedActiveRejectionAsync connectionString baselineFact.UsageFactId baselineScope

                let noInterleaving = NoAcceptedFactMutationTestInterleaving() :> IAcceptedFactMutationInterleaving
                let! baselineOutcome = acceptAndCommitAsync connectionString noInterleaving baselineFact
                let keyMismatchFact = fact (Guid.NewGuid()) ownerId organizationId repositoryId observedAt 11L
                let quantityMismatchFact = fact (Guid.NewGuid()) ownerId organizationId repositoryId observedAt 13L
                let keyMismatchPlan = planFor keyMismatchFact
                let quantityMismatchPlan = planFor quantityMismatchFact

                let aggregateKeyMismatchPlan =
                    { keyMismatchPlan with
                        Aggregate = { keyMismatchPlan.Aggregate with Key = { keyMismatchPlan.Aggregate.Key with RepositoryId = Guid.NewGuid() } }
                    }

                let aggregateQuantityMismatchPlan =
                    { quantityMismatchPlan with Aggregate = { quantityMismatchPlan.Aggregate with Quantity = quantityMismatchPlan.Aggregate.Quantity + 1L } }

                let! beforeRejectedPlans = durableProjectionAsync connectionString
                let keyMismatchInterleaving = CountingAcceptedFactMutationInterleaving()
                use keyMismatchConnection = new SqlConnection(connectionString)
                do! keyMismatchConnection.OpenAsync CancellationToken.None
                use keyMismatchTransaction = keyMismatchConnection.BeginTransaction()
                let keyMismatchMutation = SqlAcceptedFactMutation(keyMismatchInterleaving :> IAcceptedFactMutationInterleaving)

                let! keyMismatchRaised =
                    task {
                        try
                            let! _ =
                                keyMismatchMutation.AcceptAsync(
                                    keyMismatchConnection,
                                    keyMismatchTransaction,
                                    aggregateKeyMismatchPlan,
                                    scopeFor keyMismatchFact,
                                    CancellationToken.None
                                )

                            return false
                        with
                        | :? ArgumentException -> return true
                    }

                do! keyMismatchTransaction.RollbackAsync CancellationToken.None
                let! afterKeyMismatch = durableProjectionAsync connectionString
                let quantityMismatchInterleaving = CountingAcceptedFactMutationInterleaving()
                use quantityMismatchConnection = new SqlConnection(connectionString)
                do! quantityMismatchConnection.OpenAsync CancellationToken.None
                use quantityMismatchTransaction = quantityMismatchConnection.BeginTransaction()

                let quantityMismatchMutation = SqlAcceptedFactMutation(quantityMismatchInterleaving :> IAcceptedFactMutationInterleaving)

                let! quantityMismatchRaised =
                    task {
                        try
                            let! _ =
                                quantityMismatchMutation.AcceptAsync(
                                    quantityMismatchConnection,
                                    quantityMismatchTransaction,
                                    aggregateQuantityMismatchPlan,
                                    scopeFor quantityMismatchFact,
                                    CancellationToken.None
                                )

                            return false
                        with
                        | :? ArgumentException -> return true
                    }

                do! quantityMismatchTransaction.RollbackAsync CancellationToken.None
                let! afterQuantityMismatch = durableProjectionAsync connectionString

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(baselineOutcome, Is.EqualTo(InsertedIntoClosedPeriod))
                        Assert.That(keyMismatchRaised, Is.True)
                        Assert.That(quantityMismatchRaised, Is.True)
                        Assert.That(keyMismatchInterleaving.InvocationCount, Is.Zero)
                        Assert.That(quantityMismatchInterleaving.InvocationCount, Is.Zero)
                        Assert.That<string list>(afterKeyMismatch, Is.EqualTo<string list>(beforeRejectedPlans))
                        Assert.That<string list>(afterQuantityMismatch, Is.EqualTo<string list>(beforeRejectedPlans)))
                )
            })
