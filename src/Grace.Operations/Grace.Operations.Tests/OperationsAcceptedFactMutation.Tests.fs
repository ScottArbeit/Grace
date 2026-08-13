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

/// Represents the immutable identity and value fields persisted for one accepted raw usage fact.
type private PersistedRawUsageFact =
    {
        UsageFactId: Guid
        RawPayloadBase64: string
        CorrelationId: string
        FactKind: int
        OwnerId: Guid
        OrganizationId: Guid
        RepositoryId: Guid
        StoragePoolId: string
        Quantity: int64
        ObservedAtUtc: DateTime
    }

/// Represents the exact key and quantity of one persisted minute aggregate.
type private PersistedUsageAggregateMinute =
    {
        FactKind: int
        OwnerId: Guid
        OrganizationId: Guid
        RepositoryId: Guid
        StoragePoolId: string
        BucketStartUtc: DateTime
        Quantity: int64
    }

/// Represents the logical state of one scoped rejection without depending on its server-assigned timestamps.
type private PersistedUsageFactRejection =
    {
        RejectionId: Guid
        UsageFactId: Guid
        OwnerId: Guid
        OrganizationId: Guid
        RepositoryId: Guid
        MonthStartUtc: DateTime
        IsActive: bool
        IsResolved: bool
    }

/// Represents one closed-period Pending handoff row staged by the accepted-fact primitive.
type private PersistedBillingPeriodLateWork = { BillingPeriodId: Guid; UsageFactId: Guid; State: int }

/// Groups every logical projection written by the accepted-fact primitive for direct SQL comparison.
type private AcceptedFactProjection =
    {
        RawFacts: PersistedRawUsageFact list
        Aggregates: PersistedUsageAggregateMinute list
        Rejections: PersistedUsageFactRejection list
        LateWork: PersistedBillingPeriodLateWork list
    }

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
            let rejectionId = Guid.NewGuid()

            command.CommandText <-
                """
INSERT INTO ops.UsageFactRejection (RejectionId,UsageFactId,OwnerId,OrganizationId,RepositoryId,MonthStartUtc,Reason,IsActive)
VALUES (@RejectionId,@UsageFactId,@OwnerId,@OrganizationId,@RepositoryId,@MonthStartUtc,N'accepted-fact mutation repair fixture',1);
"""

            command.Parameters.Add("@RejectionId", SqlDbType.UniqueIdentifier).Value <- rejectionId
            command.Parameters.Add("@UsageFactId", SqlDbType.UniqueIdentifier).Value <- usageFactId
            command.Parameters.Add("@OwnerId", SqlDbType.UniqueIdentifier).Value <- scope.OwnerId
            command.Parameters.Add("@OrganizationId", SqlDbType.UniqueIdentifier).Value <- scope.OrganizationId
            command.Parameters.Add("@RepositoryId", SqlDbType.UniqueIdentifier).Value <- scope.RepositoryId
            command.Parameters.Add("@MonthStartUtc", SqlDbType.DateTime2).Value <- scope.MonthStart.ToDateTimeUtc()
            let! _ = command.ExecuteNonQueryAsync CancellationToken.None
            return rejectionId
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

    /// Reads the complete accepted raw-fact identity and value projection directly from SQL Server.
    let rawFactProjectionAsync connectionString =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()

            command.CommandText <-
                """
SELECT UsageFactId, RawPayload, CorrelationId, FactKind, OwnerId, OrganizationId, RepositoryId, StoragePoolId, Quantity, ObservedAtUtc
FROM ops.RawUsageFact
ORDER BY UsageFactId;
"""

            use! reader = command.ExecuteReaderAsync CancellationToken.None
            let rows = ResizeArray<PersistedRawUsageFact>()
            let mutable reading = true

            while reading do
                let! hasRow = reader.ReadAsync CancellationToken.None
                reading <- hasRow

                if hasRow then
                    rows.Add(
                        {
                            UsageFactId = reader.GetGuid 0
                            RawPayloadBase64 =
                                reader.GetFieldValue<byte array> 1
                                |> Convert.ToBase64String
                            CorrelationId = reader.GetString 2
                            FactKind = reader.GetInt32 3
                            OwnerId = reader.GetGuid 4
                            OrganizationId = reader.GetGuid 5
                            RepositoryId = reader.GetGuid 6
                            StoragePoolId = reader.GetString 7
                            Quantity = reader.GetInt64 8
                            ObservedAtUtc = reader.GetDateTime 9
                        }
                    )

            return rows |> Seq.toList
        }

    /// Reads every derived minute key and quantity directly from SQL Server.
    let aggregateProjectionAsync connectionString =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()

            command.CommandText <-
                """
SELECT FactKind, OwnerId, OrganizationId, RepositoryId, StoragePoolId, BucketStartUtc, Quantity
FROM ops.UsageAggregateMinute
ORDER BY FactKind, OwnerId, OrganizationId, RepositoryId, StoragePoolId, BucketStartUtc;
"""

            use! reader = command.ExecuteReaderAsync CancellationToken.None
            let rows = ResizeArray<PersistedUsageAggregateMinute>()
            let mutable reading = true

            while reading do
                let! hasRow = reader.ReadAsync CancellationToken.None
                reading <- hasRow

                if hasRow then
                    rows.Add(
                        {
                            FactKind = reader.GetInt32 0
                            OwnerId = reader.GetGuid 1
                            OrganizationId = reader.GetGuid 2
                            RepositoryId = reader.GetGuid 3
                            StoragePoolId = reader.GetString 4
                            BucketStartUtc = reader.GetDateTime 5
                            Quantity = reader.GetInt64 6
                        }
                    )

            return rows |> Seq.toList
        }

    /// Reads scoped rejection identity and logical resolution state directly from SQL Server.
    let rejectionProjectionAsync connectionString =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()

            command.CommandText <-
                """
SELECT RejectionId, UsageFactId, OwnerId, OrganizationId, RepositoryId, MonthStartUtc, IsActive, ResolvedAtUtc
FROM ops.UsageFactRejection
ORDER BY RejectionId;
"""

            use! reader = command.ExecuteReaderAsync CancellationToken.None
            let rows = ResizeArray<PersistedUsageFactRejection>()
            let mutable reading = true

            while reading do
                let! hasRow = reader.ReadAsync CancellationToken.None
                reading <- hasRow

                if hasRow then
                    rows.Add(
                        {
                            RejectionId = reader.GetGuid 0
                            UsageFactId = reader.GetGuid 1
                            OwnerId = reader.GetGuid 2
                            OrganizationId = reader.GetGuid 3
                            RepositoryId = reader.GetGuid 4
                            MonthStartUtc = reader.GetDateTime 5
                            IsActive = reader.GetBoolean 6
                            IsResolved = not (reader.IsDBNull 7)
                        }
                    )

            return rows |> Seq.toList
        }

    /// Reads every conditional Pending handoff directly from SQL Server.
    let lateWorkProjectionAsync connectionString =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()

            command.CommandText <-
                """
SELECT BillingPeriodId, UsageFactId, State
FROM ops.BillingPeriodLateWork
ORDER BY BillingPeriodId, UsageFactId;
"""

            use! reader = command.ExecuteReaderAsync CancellationToken.None
            let rows = ResizeArray<PersistedBillingPeriodLateWork>()
            let mutable reading = true

            while reading do
                let! hasRow = reader.ReadAsync CancellationToken.None
                reading <- hasRow

                if hasRow then
                    rows.Add({ BillingPeriodId = reader.GetGuid 0; UsageFactId = reader.GetGuid 1; State = reader.GetInt32 2 })

            return rows |> Seq.toList
        }

    /// Captures all logical durable projections that an accepted-fact transaction may create or repair.
    let acceptedFactProjectionAsync connectionString =
        task {
            let! rawFacts = rawFactProjectionAsync connectionString
            let! aggregates = aggregateProjectionAsync connectionString
            let! rejections = rejectionProjectionAsync connectionString
            let! lateWork = lateWorkProjectionAsync connectionString

            return { RawFacts = rawFacts; Aggregates = aggregates; Rejections = rejections; LateWork = lateWork }
        }

    /// Matches the provider's unspecified-kind DateTime representation for SQL datetime2 assertions.
    let sqlDateTime (instant: Instant) =
        instant.ToDateTimeUtc()
        |> fun value -> DateTime.SpecifyKind(value, DateTimeKind.Unspecified)

    /// Converts an immutable raw plan into its independently queried expected SQL row.
    let expectedRawFact (plan: UsageFactPersistencePlan) =
        {
            UsageFactId = plan.RawFact.UsageFactId
            RawPayloadBase64 = plan.RawFact.RawPayload |> Convert.ToBase64String
            CorrelationId = plan.RawFact.CorrelationId
            FactKind = int plan.RawFact.FactKind
            OwnerId = plan.RawFact.OwnerId
            OrganizationId = plan.RawFact.OrganizationId
            RepositoryId = plan.RawFact.RepositoryId
            StoragePoolId = plan.RawFact.StoragePoolId
            Quantity = plan.RawFact.Quantity
            ObservedAtUtc = sqlDateTime plan.RawFact.ObservedAt
        }

    /// Converts an immutable aggregate plan into its independently queried expected SQL row.
    let expectedAggregate (plan: UsageFactPersistencePlan) =
        {
            FactKind = int plan.Aggregate.Key.FactKind
            OwnerId = plan.Aggregate.Key.OwnerId
            OrganizationId = plan.Aggregate.Key.OrganizationId
            RepositoryId = plan.Aggregate.Key.RepositoryId
            StoragePoolId = plan.Aggregate.Key.StoragePoolId
            BucketStartUtc = sqlDateTime plan.Aggregate.Key.BucketStart
            Quantity = plan.Aggregate.Quantity
        }

    /// Describes the resolved or still-active logical state expected for one seeded scoped rejection.
    let expectedRejection rejectionId usageFactId (scope: BillingCompletenessScope) isActive =
        {
            RejectionId = rejectionId
            UsageFactId = usageFactId
            OwnerId = scope.OwnerId
            OrganizationId = scope.OrganizationId
            RepositoryId = scope.RepositoryId
            MonthStartUtc = sqlDateTime scope.MonthStart
            IsActive = isActive
            IsResolved = not isActive
        }

    /// Canonicalizes every persisted raw identity and value field for exact SQL projection comparison.
    let rawFactSignature (rawFact: PersistedRawUsageFact) =
        String.Join(
            "|",
            [
                rawFact.UsageFactId.ToString "D"
                rawFact.RawPayloadBase64
                rawFact.CorrelationId
                string rawFact.FactKind
                rawFact.OwnerId.ToString "D"
                rawFact.OrganizationId.ToString "D"
                rawFact.RepositoryId.ToString "D"
                rawFact.StoragePoolId
                string rawFact.Quantity
                string rawFact.ObservedAtUtc.Ticks
            ]
        )

    /// Canonicalizes every persisted aggregate key and quantity for exact SQL projection comparison.
    let aggregateSignature (aggregate: PersistedUsageAggregateMinute) =
        String.Join(
            "|",
            [
                string aggregate.FactKind
                aggregate.OwnerId.ToString "D"
                aggregate.OrganizationId.ToString "D"
                aggregate.RepositoryId.ToString "D"
                aggregate.StoragePoolId
                string aggregate.BucketStartUtc.Ticks
                string aggregate.Quantity
            ]
        )

    /// Canonicalizes the logical rejection state needed to prove repair and rollback enlistment.
    let rejectionSignature (rejection: PersistedUsageFactRejection) =
        String.Join(
            "|",
            [
                rejection.RejectionId.ToString "D"
                rejection.UsageFactId.ToString "D"
                rejection.OwnerId.ToString "D"
                rejection.OrganizationId.ToString "D"
                rejection.RepositoryId.ToString "D"
                string rejection.MonthStartUtc.Ticks
                string rejection.IsActive
                string rejection.IsResolved
            ]
        )

    /// Canonicalizes each closed-period Pending handoff for exact SQL projection comparison.
    let lateWorkSignature (lateWork: PersistedBillingPeriodLateWork) =
        String.Join(
            "|",
            [
                lateWork.BillingPeriodId.ToString "D"
                lateWork.UsageFactId.ToString "D"
                string lateWork.State
            ]
        )

    /// Canonicalizes all primitive-owned logical SQL projections for rollback equality assertions.
    let acceptedFactProjectionSignature (projection: AcceptedFactProjection) =
        [
            yield!
                projection.RawFacts
                |> List.map (
                    rawFactSignature
                    >> fun value -> $"RawUsageFact|{value}"
                )
            yield!
                projection.Aggregates
                |> List.map (
                    aggregateSignature
                    >> fun value -> $"UsageAggregateMinute|{value}"
                )
            yield!
                projection.Rejections
                |> List.map (
                    rejectionSignature
                    >> fun value -> $"UsageFactRejection|{value}"
                )
            yield!
                projection.LateWork
                |> List.map (
                    lateWorkSignature
                    >> fun value -> $"BillingPeriodLateWork|{value}"
                )
        ]

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
                let openPlan = planFor openFact
                let closedPlan = planFor closedFact
                let openScope = scopeFor openFact
                let closedScope = scopeFor closedFact
                let openPeriodId, closedPeriodId = Guid.NewGuid(), Guid.NewGuid()
                do! seedPeriodAsync connectionString openPeriodId openScope 0
                do! seedPeriodAsync connectionString closedPeriodId closedScope 2
                let! openRejectionId = seedActiveRejectionAsync connectionString openFact.UsageFactId openScope
                let! closedRejectionId = seedActiveRejectionAsync connectionString closedFact.UsageFactId closedScope

                let noInterleaving = NoAcceptedFactMutationTestInterleaving() :> IAcceptedFactMutationInterleaving
                let! openOutcome = acceptAndCommitAsync connectionString noInterleaving openFact
                let! closedOutcome = acceptAndCommitAsync connectionString noInterleaving closedFact
                let! afterSuccessfulAcceptance = acceptedFactProjectionAsync connectionString

                let expectedSuccessfulAcceptance =
                    {
                        RawFacts =
                            [
                                expectedRawFact openPlan
                                expectedRawFact closedPlan
                            ]
                            |> List.sortBy (fun row -> row.UsageFactId)
                        Aggregates =
                            [
                                expectedAggregate openPlan
                                expectedAggregate closedPlan
                            ]
                            |> List.sortBy (fun row -> row.FactKind, row.OwnerId, row.OrganizationId, row.RepositoryId, row.StoragePoolId, row.BucketStartUtc)
                        Rejections =
                            [
                                expectedRejection openRejectionId openFact.UsageFactId openScope false
                                expectedRejection closedRejectionId closedFact.UsageFactId closedScope false
                            ]
                            |> List.sortBy (fun row -> row.RejectionId)
                        LateWork =
                            [
                                { BillingPeriodId = closedPeriodId; UsageFactId = closedFact.UsageFactId; State = 0 }
                            ]
                    }

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
                let rollbackScope = scopeFor rollbackFact
                let rollbackPlan = planFor rollbackFact
                let! rollbackRejectionId = seedActiveRejectionAsync connectionString rollbackFact.UsageFactId rollbackScope
                let! beforeRollback = durableProjectionAsync connectionString
                let! beforeRollbackProjection = acceptedFactProjectionAsync connectionString
                use rollbackConnection = new SqlConnection(connectionString)
                do! rollbackConnection.OpenAsync CancellationToken.None
                use rollbackTransaction = rollbackConnection.BeginTransaction()
                let rollbackMutation = SqlAcceptedFactMutation(FailAfterAcceptedFactStaging() :> IAcceptedFactMutationInterleaving)

                let! rollbackRaised =
                    task {
                        try
                            let! _ = rollbackMutation.AcceptAsync(rollbackConnection, rollbackTransaction, rollbackPlan, rollbackScope, CancellationToken.None)

                            return false
                        with
                        | :? InvalidOperationException -> return true
                    }

                do! rollbackTransaction.RollbackAsync CancellationToken.None
                let! afterRollback = durableProjectionAsync connectionString
                let! afterRollbackProjection = acceptedFactProjectionAsync connectionString

                let rollbackRejectionAfterRollback =
                    afterRollbackProjection.Rejections
                    |> List.tryFind (fun row -> row.RejectionId = rollbackRejectionId)

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(openOutcome, Is.EqualTo(InsertedIntoOpenPeriod))
                        Assert.That(closedOutcome, Is.EqualTo(InsertedIntoClosedPeriod))

                        Assert.That<string list>(
                            afterSuccessfulAcceptance.RawFacts
                            |> List.map rawFactSignature
                            |> List.sort,
                            Is.EqualTo<string list>(
                                expectedSuccessfulAcceptance.RawFacts
                                |> List.map rawFactSignature
                                |> List.sort
                            )
                        )

                        Assert.That<string list>(
                            afterSuccessfulAcceptance.Aggregates
                            |> List.map aggregateSignature
                            |> List.sort,
                            Is.EqualTo<string list>(
                                expectedSuccessfulAcceptance.Aggregates
                                |> List.map aggregateSignature
                                |> List.sort
                            )
                        )

                        Assert.That<string list>(
                            afterSuccessfulAcceptance.Rejections
                            |> List.map rejectionSignature
                            |> List.sort,
                            Is.EqualTo<string list>(
                                expectedSuccessfulAcceptance.Rejections
                                |> List.map rejectionSignature
                                |> List.sort
                            )
                        )

                        Assert.That<string list>(
                            afterSuccessfulAcceptance.LateWork
                            |> List.map lateWorkSignature
                            |> List.sort,
                            Is.EqualTo<string list>(
                                expectedSuccessfulAcceptance.LateWork
                                |> List.map lateWorkSignature
                                |> List.sort
                            )
                        )

                        Assert.That(duplicateOutcome, Is.EqualTo(ExistingSameScope))
                        Assert.That<string list>(afterDuplicate, Is.EqualTo<string list>(beforeDuplicate))
                        Assert.That(conflictRaised, Is.True)
                        Assert.That<string list>(afterConflict, Is.EqualTo<string list>(beforeConflict))
                        Assert.That(mismatchedScopeRaised, Is.True)
                        Assert.That<string list>(afterMismatchedScope, Is.EqualTo<string list>(beforeMismatchedScope))
                        Assert.That(rollbackRaised, Is.True)
                        Assert.That<string list>(afterRollback, Is.EqualTo<string list>(beforeRollback))

                        Assert.That<string list>(
                            acceptedFactProjectionSignature afterRollbackProjection,
                            Is.EqualTo<string list>(acceptedFactProjectionSignature beforeRollbackProjection)
                        )

                        Assert.That<PersistedUsageFactRejection option>(
                            rollbackRejectionAfterRollback,
                            Is.EqualTo<PersistedUsageFactRejection option>(
                                Some(expectedRejection rollbackRejectionId rollbackFact.UsageFactId rollbackScope true)
                            )
                        ))
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
                let! _ = seedActiveRejectionAsync connectionString baselineFact.UsageFactId baselineScope

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
