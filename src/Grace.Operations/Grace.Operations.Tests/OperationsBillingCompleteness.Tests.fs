namespace Grace.Operations.Tests

open Grace.Operations.Data
open Grace.Operations.Worker
open Grace.Shared
open Grace.Types.Common
open Grace.Types.Usage
open Microsoft.Data.SqlClient
open Microsoft.Extensions.Logging.Abstractions
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.Data
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Observes production store lock calls while delegating every durable command to an actual SQL transaction.
type private ObservedOperationsUsageTransaction
    (
        inner: IOperationsUsageTransaction,
        beforeScopeLockAsync: BillingCompletenessScope -> CancellationToken -> Task,
        afterScopeLockAsync: BillingCompletenessScope -> CancellationToken -> Task,
        afterUsageAggregateAsync: UsageAggregateMinute -> CancellationToken -> Task
    ) =

    interface IOperationsUsageTransaction with

        member _.AcquireBillingCompletenessScopeAsync(scope, cancellationToken) =
            task {
                do! beforeScopeLockAsync scope cancellationToken
                do! inner.AcquireBillingCompletenessScopeAsync(scope, cancellationToken)
                do! afterScopeLockAsync scope cancellationToken
            }

        member _.TryInsertRawUsageFactAsync(rawFact, cancellationToken) = inner.TryInsertRawUsageFactAsync(rawFact, cancellationToken)

        member _.TryInsertReplayedArchivedUsageFactAsync(rawFact, pointer, cancellationToken) =
            inner.TryInsertReplayedArchivedUsageFactAsync(rawFact, pointer, cancellationToken)

        member _.AddToUsageAggregateMinuteAsync(aggregate, cancellationToken) =
            task {
                do! inner.AddToUsageAggregateMinuteAsync(aggregate, cancellationToken)
                do! afterUsageAggregateAsync aggregate cancellationToken
            }

        member _.RecordScopedUsageFactRejectionAsync(rejection, cancellationToken) = inner.RecordScopedUsageFactRejectionAsync(rejection, cancellationToken)

        member _.RecordUnscopedUsageFactRejectionAsync(rejection, cancellationToken) = inner.RecordUnscopedUsageFactRejectionAsync(rejection, cancellationToken)

        member _.ResolveScopedUsageFactRejectionAsync(usageFactId, scope, cancellationToken) =
            inner.ResolveScopedUsageFactRejectionAsync(usageFactId, scope, cancellationToken)

        member _.HasActiveScopedUsageFactRejectionAsync(scope, cancellationToken) = inner.HasActiveScopedUsageFactRejectionAsync(scope, cancellationToken)

/// Wraps a real SQL transaction scope with deterministic lock-grant callbacks used only by concurrency proofs.
type private ObservedOperationsUsageTransactionScope
    (
        inner: IOperationsUsageTransactionScope,
        beforeScopeLockAsync: BillingCompletenessScope -> CancellationToken -> Task,
        afterScopeLockAsync: BillingCompletenessScope -> CancellationToken -> Task,
        afterUsageAggregateAsync: UsageAggregateMinute -> CancellationToken -> Task
    ) =

    interface IOperationsUsageTransactionScope with

        member _.ExecuteAsync(operation, cancellationToken) =
            inner.ExecuteAsync(
                (fun transaction operationCancellationToken ->
                    operation
                        (ObservedOperationsUsageTransaction(transaction, beforeScopeLockAsync, afterScopeLockAsync, afterUsageAggregateAsync)
                        :> IOperationsUsageTransaction)
                        operationCancellationToken),
                cancellationToken
            )

/// Observes that rejection evidence is committed before a worker asks Service Bus to terminally settle its message.
type private InspectingUsageMessageActions(events: List<string>, beforeDeadLetterAsync: unit -> Task) =

    interface IOperationsUsageMessageActions with

        member _.CompleteAsync(_cancellationToken) = Task.CompletedTask

        member _.AbandonAsync(_cancellationToken) = Task.CompletedTask

        member _.DeadLetterAsync(_reason, _description, _cancellationToken) =
            task {
                do! beforeDeadLetterAsync ()
                events.Add("dead-letter")
            }
            :> Task

/// Proves the owner-month completeness coordination boundary against isolated real SQL Server databases.
[<TestFixture>]
[<NonParallelizable>]
type OperationsBillingCompletenessTests() =

    /// Names the explicit real-SQL connection supplied by the Operations validation profile.
    [<Literal>]
    let sqlConnectionStringEnvironmentVariable = "GRACE_OPERATIONS_SQL_TEST_CONNECTION_STRING"

    /// Creates a unique SQL database name so this fixture never shares durable coordination state with another run.
    let databaseName () = $"GraceOperationsBillingCompleteness_{Guid.NewGuid():N}"

    /// Obtains the real SQL Server connection configured for Operations coordination proofs.
    let requireSqlConnectionString () =
        let value = Environment.GetEnvironmentVariable sqlConnectionStringEnvironmentVariable

        if String.IsNullOrWhiteSpace value then
            Assert.Ignore($"{sqlConnectionStringEnvironmentVariable} is required for real SQL billing-completeness tests.")

        value

    /// Creates and migrates one isolated Operations database for a single real-SQL proof.
    let createDatabaseAsync () =
        task {
            let builder = SqlConnectionStringBuilder(requireSqlConnectionString ())
            builder.InitialCatalog <- databaseName ()
            let schema = OperationsUsageSchema(builder.ConnectionString, OperationsUsageSchemaBootstrapMode.CreateDatabaseIfMissing)
            do! schema.EnsureCreatedAsync CancellationToken.None
            return builder.ConnectionString
        }

    /// Removes an isolated test database after its proof completes.
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

    /// Runs an isolated SQL proof and guarantees its database does not survive a successful or failed assertion.
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

    /// Builds a valid UsageFact for the supplied scope, identity, and UTC observation time.
    let fact usageFactId ownerId organizationId repositoryId observedAt =
        UsageFact.RepositoryStorageBytesMinute(
            usageFactId,
            CorrelationId $"billing-completeness-{usageFactId}",
            ownerId,
            organizationId,
            repositoryId,
            StoragePoolId "billing-completeness-pool",
            4096L,
            observedAt
        )

    /// Serializes a UsageFact payload exactly as the production Operations storage boundary receives it.
    let payloadFor usageFact = JsonSerializer.SerializeToUtf8Bytes(usageFact, Constants.JsonSerializerOptions)

    /// Creates the supported worker envelope used by production-adapter ingestion proofs.
    let workerMessage usageFact =
        let properties = Dictionary<string, obj>()
        properties[OperationalFactEnvelope.UsageFactMessageTypeProperty] <- box OperationalFactEnvelope.UsageFactMessageType
        properties[OperationalFactEnvelope.UsageFactKindProperty] <- box "RepositoryStorageBytesMinute"

        {
            MessageId = $"billing-completeness-{usageFact.UsageFactId}"
            CorrelationId = "billing-completeness-worker"
            DeliveryCount = 1
            Subject = OperationalFactEnvelope.UsageFactSubject
            ApplicationProperties = properties :> IReadOnlyDictionary<string, obj>
            Body = payloadFor usageFact
        }

    /// Derives a validated owner-month scope or fails the test with contract details.
    let scopeFor ownerId organizationId repositoryId observedAt =
        match BillingCompletenessScope.tryCreate ownerId organizationId repositoryId observedAt with
        | Ok scope -> scope
        | Error errors ->
            Assert.Fail(String.Join("; ", errors))
            Unchecked.defaultof<BillingCompletenessScope>

    /// Gives a real SQL connection a unique observable application name for a single competing store operation.
    let withApplicationName connectionString applicationName =
        let builder = SqlConnectionStringBuilder(connectionString)
        builder.ApplicationName <- applicationName
        builder.ConnectionString

    /// Runs a parameterless real SQL command against an isolated Operations database.
    let executeNonQueryAsync connectionString commandText =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()
            command.CommandText <- commandText
            let! _ = command.ExecuteNonQueryAsync CancellationToken.None
            return ()
        }

    /// Returns a scalar integer from one real SQL command.
    let executeInt32Async connectionString commandText =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()
            command.CommandText <- commandText
            let! value = command.ExecuteScalarAsync CancellationToken.None
            return Convert.ToInt32 value
        }

    /// Reads whether SQL Server reports the named production operation has a waiting application-lock request.
    let isApplicationLockWaitAsync connectionString applicationName =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync CancellationToken.None
            use command = connection.CreateCommand()

            command.CommandText <-
                """
SELECT CASE WHEN EXISTS
(
    SELECT 1
    FROM sys.dm_tran_locks AS lockRequest
    INNER JOIN sys.dm_exec_sessions AS session
        ON session.session_id = lockRequest.request_session_id
    WHERE session.program_name = @ApplicationName
      AND lockRequest.resource_type = N'APPLICATION'
      AND lockRequest.request_status = N'WAIT'
)
THEN 1
ELSE 0
END;
"""

            command.Parameters.Add("@ApplicationName", SqlDbType.NVarChar, 128).Value <- applicationName
            let! value = command.ExecuteScalarAsync CancellationToken.None
            return Convert.ToInt32 value = 1
        }

    /// Waits for SQL Server to report that the named production operation reached a waiting application-lock request.
    let waitForApplicationLockWaitAsync connectionString applicationName =
        let rec observe remainingAttempts =
            task {
                if remainingAttempts = 0 then
                    Assert.Fail($"SQL Server never reported an application-lock request for '{applicationName}'.")
                else
                    let! isWaiting = isApplicationLockWaitAsync connectionString applicationName

                    if isWaiting then
                        return ()
                    else
                        do! Task.Delay(TimeSpan.FromMilliseconds 20.0)
                        return! observe (remainingAttempts - 1)
            }

        observe 500

    /// Creates a production store whose transaction wrapper pauses only after a real SQL lock has been granted.
    let heldStore (connectionString: string) (lockGranted: TaskCompletionSource<unit>) (releaseLock: TaskCompletionSource<unit>) =
        let afterScopeLockAsync (_: BillingCompletenessScope) (cancellationToken: CancellationToken) =
            (task {
                lockGranted.TrySetResult() |> ignore
                do! releaseLock.Task.WaitAsync cancellationToken
            }
            :> Task)

        OperationsUsageStore(
            ObservedOperationsUsageTransactionScope(
                SqlOperationsUsageTransactionScope connectionString,
                (fun _ _ -> Task.CompletedTask),
                afterScopeLockAsync,
                (fun _ _ -> Task.CompletedTask)
            )
        )

    /// Creates a production store that signals after a real SQL lock grant without pausing its durable operation.
    let observedStore (connectionString: string) (lockGranted: TaskCompletionSource<unit>) =
        let afterScopeLockAsync (_: BillingCompletenessScope) (_: CancellationToken) =
            lockGranted.TrySetResult() |> ignore
            Task.CompletedTask

        OperationsUsageStore(
            ObservedOperationsUsageTransactionScope(
                SqlOperationsUsageTransactionScope connectionString,
                (fun _ _ -> Task.CompletedTask),
                afterScopeLockAsync,
                (fun _ _ -> Task.CompletedTask)
            )
        )

    /// Creates a production store that cancels only after a real transaction has staged its aggregate under the granted scope lock.
    let cancellingStore
        (connectionString: string)
        (lockGranted: TaskCompletionSource<unit>)
        (usageStaged: TaskCompletionSource<unit>)
        (releaseCancellation: TaskCompletionSource<unit>)
        (cancellation: CancellationTokenSource)
        =
        let afterScopeLockAsync (_: BillingCompletenessScope) (_: CancellationToken) =
            lockGranted.TrySetResult() |> ignore
            Task.CompletedTask

        let afterUsageAggregateAsync (_: UsageAggregateMinute) (cancellationToken: CancellationToken) =
            (task {
                usageStaged.TrySetResult() |> ignore
                do! releaseCancellation.Task.WaitAsync CancellationToken.None
                cancellation.Cancel()
                cancellationToken.ThrowIfCancellationRequested()
            }
            :> Task)

        OperationsUsageStore(
            ObservedOperationsUsageTransactionScope(
                SqlOperationsUsageTransactionScope connectionString,
                (fun _ _ -> Task.CompletedTask),
                afterScopeLockAsync,
                afterUsageAggregateAsync
            )
        )

    /// Creates scoped active rejection evidence for the supplied exact accepted-fact tuple.
    let scopedRejection usageFactId scope =
        {
            RejectionId = Guid.NewGuid()
            UsageFactId = Some usageFactId
            Scope = Some scope
            ReportedScope = None
            Reason = "transient ingestion rejection"
            IsActive = true
        }

    /// Replays a valid fact through the production archived-fact insertion seam.
    let replayAsync (store: OperationsUsageStore) usageFact =
        let pointer =
            {
                UsageFactId = usageFact.UsageFactId
                BlobName = $"billing-completeness/{usageFact.UsageFactId:N}.json.gz"
                ChecksumSha256Hex = String.replicate 64 "a"
                ByteLength = 4096L
            }

        store.ReplayArchivedUsageFactAsync(usageFact, payloadFor usageFact, pointer, CancellationToken.None)

    /// Asserts that two production store actions for one scope serialize through the same SQL application-lock resource.
    let runSameScopeRaceAsync connectionString firstAction secondAction =
        task {
            let firstLockGranted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let releaseFirst = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let firstConnection = withApplicationName connectionString $"GraceBillingFirst-{Guid.NewGuid():N}"
            let secondApplicationName = $"GraceBillingSecond-{Guid.NewGuid():N}"
            let secondConnection = withApplicationName connectionString secondApplicationName
            let firstStore = heldStore firstConnection firstLockGranted releaseFirst
            let secondStore = OperationsUsageStore(SqlOperationsUsageTransactionScope secondConnection)
            let first = firstAction firstStore
            do! firstLockGranted.Task.WaitAsync(TimeSpan.FromSeconds 10.0)

            let second =
                backgroundTask {
                    do! Task.Yield()
                    return! secondAction secondStore
                }

            do! waitForApplicationLockWaitAsync connectionString secondApplicationName
            releaseFirst.TrySetResult() |> ignore
            let! firstResult = first
            let! secondResult = second
            return firstResult, secondResult
        }

    /// Formats one nullable GUID SQL literal without allowing test evidence to use a generated default scope.
    let nullableGuidSql (value: Guid option) =
        match value with
        | Some guid -> $"CONVERT(uniqueidentifier, '{guid:D}')"
        | None -> "NULL"

    /// Formats one nullable UTC timestamp SQL literal for direct rejection-table validation tests.
    let nullableDateTimeSql (value: DateTime option) =
        match value with
        | Some dateTime ->
            let formatted = dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", Globalization.CultureInfo.InvariantCulture)
            $"CONVERT(datetime2(7), '{formatted}')"
        | None -> "NULL"

    /// Builds one direct SQL insertion that bypasses EF and Operations storage validation to exercise durable checks.
    let directRejectionInsertSql rejectionId usageFactId ownerId organizationId repositoryId monthStartUtc reason isActive resolvedAtUtc =
        $"""
INSERT INTO ops.UsageFactRejection
(
    RejectionId,
    UsageFactId,
    OwnerId,
    OrganizationId,
    RepositoryId,
    MonthStartUtc,
    Reason,
    IsActive,
    ResolvedAtUtc
)
VALUES
(
    CONVERT(uniqueidentifier, '{rejectionId:D}'),
    {nullableGuidSql usageFactId},
    {nullableGuidSql ownerId},
    {nullableGuidSql organizationId},
    {nullableGuidSql repositoryId},
    {nullableDateTimeSql monthStartUtc},
    N'{reason}',
    {if isActive then 1 else 0},
    {nullableDateTimeSql resolvedAtUtc}
);
"""

    /// Requires direct SQL to reject a malformed durable rejection row rather than relying on EF validation.
    let assertDirectRejectionInsertFailsAsync connectionString statement =
        task {
            Assert.ThrowsAsync<SqlException>(Func<Task>(fun () -> executeNonQueryAsync connectionString statement :> Task))
            |> ignore
        }

    /// Proves each rejection constraint rejects malformed SQL while valid partially scoped evidence remains durable.
    [<Test>]
    member _.DatabaseRejectsMalformedRejectionEvidenceAndKeepsValidPartialEvidence() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId = Guid.NewGuid()
                let organizationId = Guid.NewGuid()
                let repositoryId = Guid.NewGuid()
                let usageFactId = Guid.NewGuid()
                let monthStartUtc = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
                let resolvedAtUtc = DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc)

                do!
                    assertDirectRejectionInsertFailsAsync
                        connectionString
                        (directRejectionInsertSql Guid.Empty None None None None None "zero rejection id" true None)

                do!
                    assertDirectRejectionInsertFailsAsync
                        connectionString
                        (directRejectionInsertSql (Guid.NewGuid()) (Some Guid.Empty) None None None None "zero fact id" true None)

                do!
                    assertDirectRejectionInsertFailsAsync
                        connectionString
                        (directRejectionInsertSql (Guid.NewGuid()) None (Some Guid.Empty) None None None "zero owner id" true None)

                do!
                    assertDirectRejectionInsertFailsAsync
                        connectionString
                        (directRejectionInsertSql (Guid.NewGuid()) None None (Some Guid.Empty) None None "zero organization id" true None)

                do!
                    assertDirectRejectionInsertFailsAsync
                        connectionString
                        (directRejectionInsertSql (Guid.NewGuid()) None None None (Some Guid.Empty) None "zero repository id" true None)

                do!
                    assertDirectRejectionInsertFailsAsync
                        connectionString
                        (directRejectionInsertSql
                            (Guid.NewGuid())
                            None
                            (Some ownerId)
                            (Some organizationId)
                            (Some repositoryId)
                            (Some monthStartUtc)
                            "missing complete-scope fact id"
                            true
                            None)

                do!
                    assertDirectRejectionInsertFailsAsync
                        connectionString
                        (directRejectionInsertSql
                            (Guid.NewGuid())
                            (Some usageFactId)
                            (Some ownerId)
                            (Some organizationId)
                            (Some repositoryId)
                            (Some(monthStartUtc.AddDays 1.0))
                            "not a month start"
                            true
                            None)

                do!
                    assertDirectRejectionInsertFailsAsync
                        connectionString
                        (directRejectionInsertSql (Guid.NewGuid()) None None None None None "active rows cannot be resolved" true (Some resolvedAtUtc))

                do!
                    assertDirectRejectionInsertFailsAsync
                        connectionString
                        (directRejectionInsertSql (Guid.NewGuid()) None None None None None "inactive rows require resolution" false None)

                do! assertDirectRejectionInsertFailsAsync connectionString (directRejectionInsertSql (Guid.NewGuid()) None None None None None "   " true None)

                let validPartial =
                    directRejectionInsertSql
                        (Guid.NewGuid())
                        (Some usageFactId)
                        (Some ownerId)
                        None
                        (Some repositoryId)
                        (Some monthStartUtc)
                        "visible partial operator evidence"
                        true
                        None

                do! executeNonQueryAsync connectionString validPartial
                let! count = executeInt32Async connectionString "SELECT COUNT(*) FROM ops.UsageFactRejection;"
                Assert.That(count, Is.EqualTo(1))
            })

    /// Proves a scoped rejection racing online acceptance cannot produce a completeness gap in either commit order.
    [<Test>]
    member _.ScopedRejectionAndOnlineAcceptanceSerializeInBothCommitOrders() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId = Guid.NewGuid()
                let organizationId = Guid.NewGuid()
                let repositoryId = Guid.NewGuid()
                let usageFact = fact (Guid.NewGuid()) ownerId organizationId repositoryId (Instant.FromUtc(2026, 8, 4, 12, 0))
                let scope = scopeFor ownerId organizationId repositoryId usageFact.ObservedAt

                let! firstRejection, secondAcceptance =
                    runSameScopeRaceAsync
                        connectionString
                        (fun store -> store.RecordUsageFactRejectionAsync(scopedRejection usageFact.UsageFactId scope, CancellationToken.None))
                        (fun store -> store.StoreUsageFactAsync(usageFact, payloadFor usageFact, CancellationToken.None))

                Assert.That(firstRejection.IsOk, Is.True)
                Assert.That(secondAcceptance.IsOk, Is.True)

                let completedAfterAcceptance =
                    OperationsUsageStore(SqlOperationsUsageTransactionScope connectionString)
                        .EvaluateBillingCompletenessAsync(scope, CancellationToken.None)

                let! complete = completedAfterAcceptance
                Assert.That(complete, Is.EqualTo(Complete))

                let laterFact = fact (Guid.NewGuid()) ownerId organizationId repositoryId (Instant.FromUtc(2026, 8, 5, 12, 0))
                let laterScope = scopeFor ownerId organizationId repositoryId laterFact.ObservedAt

                let! firstAcceptance, secondRejection =
                    runSameScopeRaceAsync
                        connectionString
                        (fun store -> store.StoreUsageFactAsync(laterFact, payloadFor laterFact, CancellationToken.None))
                        (fun store -> store.RecordUsageFactRejectionAsync(scopedRejection laterFact.UsageFactId laterScope, CancellationToken.None))

                Assert.That(firstAcceptance.IsOk, Is.True)
                Assert.That(secondRejection.IsOk, Is.True)

                let! blocked =
                    OperationsUsageStore(SqlOperationsUsageTransactionScope connectionString)
                        .EvaluateBillingCompletenessAsync(laterScope, CancellationToken.None)

                Assert.That(blocked, Is.EqualTo(BlockedByActiveScopedRejection))
            })

    /// Proves replay and online ingestion use one scope lock and accept one duplicate fact exactly once.
    [<Test>]
    member _.ReplayAndOnlineAcceptanceSerializeWithoutDoubleCounting() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId = Guid.NewGuid()
                let organizationId = Guid.NewGuid()
                let repositoryId = Guid.NewGuid()
                let usageFact = fact (Guid.NewGuid()) ownerId organizationId repositoryId (Instant.FromUtc(2026, 8, 4, 12, 0))

                let! online, replay =
                    runSameScopeRaceAsync
                        connectionString
                        (fun store -> store.StoreUsageFactAsync(usageFact, payloadFor usageFact, CancellationToken.None))
                        (fun store -> replayAsync store usageFact)

                Assert.That(online.IsOk, Is.True)
                Assert.That(replay.IsOk, Is.True)

                let acceptedCount =
                    [ online; replay ]
                    |> List.filter (Result.exists (fun result -> result.Status = UsageFactPersistenceStatus.Accepted))
                    |> List.length

                let! rawFactCount = executeInt32Async connectionString "SELECT COUNT(*) FROM ops.RawUsageFact;"
                let! aggregateCount = executeInt32Async connectionString "SELECT COUNT(*) FROM ops.UsageAggregateMinute;"
                let! quantity = executeInt32Async connectionString "SELECT Quantity FROM ops.UsageAggregateMinute;"

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(acceptedCount, Is.EqualTo(1))
                        Assert.That(rawFactCount, Is.EqualTo(1))
                        Assert.That(aggregateCount, Is.EqualTo(1))
                        Assert.That(quantity, Is.EqualTo(4096)))
                )
            })

    /// Proves replay repair and completeness evaluation observe committed blocker state in both writer-reader orders.
    [<Test>]
    member _.ReplayRepairAndCompletenessReaderSerializeInBothCommitOrders() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId = Guid.NewGuid()
                let organizationId = Guid.NewGuid()
                let repositoryId = Guid.NewGuid()
                let usageFact = fact (Guid.NewGuid()) ownerId organizationId repositoryId (Instant.FromUtc(2026, 8, 4, 12, 0))
                let scope = scopeFor ownerId organizationId repositoryId usageFact.ObservedAt
                let setupStore = OperationsUsageStore(SqlOperationsUsageTransactionScope connectionString)
                let! recorded = setupStore.RecordUsageFactRejectionAsync(scopedRejection usageFact.UsageFactId scope, CancellationToken.None)
                Assert.That(recorded.IsOk, Is.True)

                let! replay, readerAfterReplay =
                    runSameScopeRaceAsync
                        connectionString
                        (fun store -> replayAsync store usageFact)
                        (fun store -> store.EvaluateBillingCompletenessAsync(scope, CancellationToken.None))

                Assert.That(replay.IsOk, Is.True)
                Assert.That(readerAfterReplay, Is.EqualTo(Complete))

                let nextFact = fact (Guid.NewGuid()) ownerId organizationId repositoryId (Instant.FromUtc(2026, 8, 5, 12, 0))
                let nextScope = scopeFor ownerId organizationId repositoryId nextFact.ObservedAt
                let! nextRecorded = setupStore.RecordUsageFactRejectionAsync(scopedRejection nextFact.UsageFactId nextScope, CancellationToken.None)
                Assert.That(nextRecorded.IsOk, Is.True)

                let! readerBeforeReplay, replayAfterReader =
                    runSameScopeRaceAsync
                        connectionString
                        (fun store -> store.EvaluateBillingCompletenessAsync(nextScope, CancellationToken.None))
                        (fun store -> replayAsync store nextFact)

                Assert.That(readerBeforeReplay, Is.EqualTo(BlockedByActiveScopedRejection))
                Assert.That(replayAfterReader.IsOk, Is.True)

                let! complete = setupStore.EvaluateBillingCompletenessAsync(nextScope, CancellationToken.None)

                Assert.That(complete, Is.EqualTo(Complete))
            })

    /// Proves cancellation after staged repair and usage rolls back, releases the real scope lock, and leaves completeness truthful.
    [<Test>]
    member _.CancellationAfterLockGrantRollsBackStagedRepairAndUsageBeforeTheNextStoreAction() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId = Guid.NewGuid()
                let organizationId = Guid.NewGuid()
                let repositoryId = Guid.NewGuid()
                let usageFact = fact (Guid.NewGuid()) ownerId organizationId repositoryId (Instant.FromUtc(2026, 8, 4, 12, 0))
                let scope = scopeFor ownerId organizationId repositoryId usageFact.ObservedAt
                let setupStore = OperationsUsageStore(SqlOperationsUsageTransactionScope connectionString)
                let! rejection = setupStore.RecordUsageFactRejectionAsync(scopedRejection usageFact.UsageFactId scope, CancellationToken.None)
                Assert.That(rejection.IsOk, Is.True)

                use cancellation = new CancellationTokenSource()
                let lockGranted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                let usageStaged = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                let releaseCancellation = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                let blockedReaderLockGranted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                let recoveryLockGranted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                let failingConnection = withApplicationName connectionString $"GraceBillingCancellationFirst-{Guid.NewGuid():N}"
                let blockedReaderApplicationName = $"GraceBillingCancellationReader-{Guid.NewGuid():N}"
                let blockedReaderConnection = withApplicationName connectionString blockedReaderApplicationName
                let failingStore = cancellingStore failingConnection lockGranted usageStaged releaseCancellation cancellation
                let blockedReader = observedStore blockedReaderConnection blockedReaderLockGranted
                let recoveryStore = observedStore connectionString recoveryLockGranted
                let failedWrite = failingStore.StoreUsageFactAsync(usageFact, payloadFor usageFact, cancellation.Token)

                try
                    do! lockGranted.Task.WaitAsync(TimeSpan.FromSeconds 10.0)
                    do! usageStaged.Task.WaitAsync(TimeSpan.FromSeconds 10.0)
                    let reader = blockedReader.EvaluateBillingCompletenessAsync(scope, CancellationToken.None)
                    do! waitForApplicationLockWaitAsync connectionString blockedReaderApplicationName
                    releaseCancellation.TrySetResult() |> ignore

                    Assert.ThrowsAsync<OperationCanceledException>(Func<Task>(fun () -> failedWrite :> Task))
                    |> ignore

                    let! completenessAfterRollback = reader
                    do! blockedReaderLockGranted.Task.WaitAsync(TimeSpan.FromSeconds 10.0)
                    let! rawFactCount = executeInt32Async connectionString "SELECT COUNT(*) FROM ops.RawUsageFact;"
                    let! aggregateCount = executeInt32Async connectionString "SELECT COUNT(*) FROM ops.UsageAggregateMinute;"
                    let! activeBlockerCount = executeInt32Async connectionString "SELECT COUNT(*) FROM ops.UsageFactRejection WHERE IsActive = 1;"

                    Assert.Multiple(
                        Action (fun () ->
                            Assert.That(completenessAfterRollback, Is.EqualTo(BlockedByActiveScopedRejection))
                            Assert.That(rawFactCount, Is.Zero)
                            Assert.That(aggregateCount, Is.Zero)
                            Assert.That(activeBlockerCount, Is.EqualTo(1)))
                    )

                    let! recovered = recoveryStore.StoreUsageFactAsync(usageFact, payloadFor usageFact, CancellationToken.None)
                    do! recoveryLockGranted.Task.WaitAsync(TimeSpan.FromSeconds 10.0)
                    let! completenessAfterRecovery = setupStore.EvaluateBillingCompletenessAsync(scope, CancellationToken.None)
                    let! rawFactCountAfterRecovery = executeInt32Async connectionString "SELECT COUNT(*) FROM ops.RawUsageFact;"
                    let! aggregateCountAfterRecovery = executeInt32Async connectionString "SELECT COUNT(*) FROM ops.UsageAggregateMinute;"
                    let! activeBlockerCountAfterRecovery = executeInt32Async connectionString "SELECT COUNT(*) FROM ops.UsageFactRejection WHERE IsActive = 1;"

                    Assert.Multiple(
                        Action (fun () ->
                            Assert.That(recovered.IsOk, Is.True)

                            Assert.That(
                                recovered
                                |> Result.exists (fun result -> result.Status = UsageFactPersistenceStatus.Accepted),
                                Is.True
                            )

                            Assert.That(completenessAfterRecovery, Is.EqualTo(Complete))
                            Assert.That(rawFactCountAfterRecovery, Is.EqualTo(1))
                            Assert.That(aggregateCountAfterRecovery, Is.EqualTo(1))
                            Assert.That(activeBlockerCountAfterRecovery, Is.Zero))
                    )
                finally
                    releaseCancellation.TrySetResult() |> ignore
                    cancellation.Cancel()
            })

    /// Proves different repository and organization tuples acquire real SQL locks independently while one scope remains held.
    [<Test>]
    member _.DistinctRepositoryAndOrganizationScopesProceedIndependently() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId = Guid.NewGuid()
                let organizationId = Guid.NewGuid()
                let repositoryId = Guid.NewGuid()
                let observedAt = Instant.FromUtc(2026, 8, 4, 12, 0)
                let heldFact = fact (Guid.NewGuid()) ownerId organizationId repositoryId observedAt
                let firstLockGranted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                let releaseFirst = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                let repositoryLockGranted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                let organizationLockGranted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                let held = heldStore connectionString firstLockGranted releaseFirst
                let differentRepository = observedStore connectionString repositoryLockGranted
                let differentOrganization = observedStore connectionString organizationLockGranted
                let first = held.StoreUsageFactAsync(heldFact, payloadFor heldFact, CancellationToken.None)
                do! firstLockGranted.Task.WaitAsync(TimeSpan.FromSeconds 10.0)

                let repositoryFact = fact (Guid.NewGuid()) ownerId organizationId (Guid.NewGuid()) observedAt

                let organizationFact = fact (Guid.NewGuid()) ownerId (Guid.NewGuid()) repositoryId observedAt

                let repositoryWrite = differentRepository.StoreUsageFactAsync(repositoryFact, payloadFor repositoryFact, CancellationToken.None)

                let organizationWrite = differentOrganization.StoreUsageFactAsync(organizationFact, payloadFor organizationFact, CancellationToken.None)

                do! repositoryLockGranted.Task.WaitAsync(TimeSpan.FromSeconds 10.0)
                do! organizationLockGranted.Task.WaitAsync(TimeSpan.FromSeconds 10.0)
                releaseFirst.TrySetResult() |> ignore
                let! firstResult = first
                let! repositoryResult = repositoryWrite
                let! organizationResult = organizationWrite

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(firstResult.IsOk, Is.True)
                        Assert.That(repositoryResult.IsOk, Is.True)
                        Assert.That(organizationResult.IsOk, Is.True))
                )
            })

    /// Proves scoped rejection idempotency, completeness blocking, repair-through-acceptance, and unscoped visibility without invented scope.
    [<Test>]
    member _.ScopedRejectionBlocksUntilAcceptedFactRepairsItWhileUnscopedEvidenceDoesNotBlock() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId = Guid.NewGuid()
                let organizationId = Guid.NewGuid()
                let repositoryId = Guid.NewGuid()
                let usageFactId = Guid.NewGuid()
                let usageFact = fact usageFactId ownerId organizationId repositoryId (Instant.FromUtc(2026, 8, 4, 12, 0))
                let scope = scopeFor ownerId organizationId repositoryId usageFact.ObservedAt
                let store = OperationsUsageStore(SqlOperationsUsageTransactionScope connectionString)
                let rejection = scopedRejection usageFactId scope

                let! first = store.RecordUsageFactRejectionAsync(rejection, CancellationToken.None)
                let! duplicate = store.RecordUsageFactRejectionAsync({ rejection with RejectionId = Guid.NewGuid() }, CancellationToken.None)

                let canonicalFirst =
                    first
                    |> Result.defaultWith (fun errors -> failwith (String.Join("; ", errors)))
                    |> Option.get

                let canonicalDuplicate =
                    duplicate
                    |> Result.defaultWith (fun errors -> failwith (String.Join("; ", errors)))
                    |> Option.get

                Assert.That(canonicalDuplicate.RejectionId, Is.EqualTo(canonicalFirst.RejectionId))

                let! blocked = store.EvaluateBillingCompletenessAsync(scope, CancellationToken.None)
                Assert.That(blocked, Is.EqualTo(BlockedByActiveScopedRejection))

                let! accepted = store.StoreUsageFactAsync(usageFact, payloadFor usageFact, CancellationToken.None)
                Assert.That(accepted.IsOk, Is.True)
                let! complete = store.EvaluateBillingCompletenessAsync(scope, CancellationToken.None)
                Assert.That(complete, Is.EqualTo(Complete))

                let unscoped =
                    {
                        RejectionId = Guid.NewGuid()
                        UsageFactId = None
                        Scope = None
                        ReportedScope =
                            Some { OwnerId = Some ownerId; OrganizationId = None; RepositoryId = Some repositoryId; ObservedAt = Some usageFact.ObservedAt }
                        Reason = "malformed message with incomplete scope"
                        IsActive = true
                    }

                let! unscopedRecorded = store.RecordUsageFactRejectionAsync(unscoped, CancellationToken.None)
                Assert.That(unscopedRecorded.IsOk, Is.True)
                let! stillComplete = store.EvaluateBillingCompletenessAsync(scope, CancellationToken.None)
                Assert.That(stillComplete, Is.EqualTo(Complete))
            })

    /// Proves the production worker records a SQL-shape rejection before dead-lettering, blocks only that scope, and accepts replay repair.
    [<Test>]
    member _.WorkerAdapterRecordsSqlShapeRejectionBeforeDeadLetterAndCorrectedAcceptanceRepairsCompleteness() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId = Guid.NewGuid()
                let organizationId = Guid.NewGuid()
                let repositoryId = Guid.NewGuid()
                let usageFactId = Guid.NewGuid()
                let observedAt = Instant.FromUtc(2026, 8, 4, 12, 0)
                let scope = scopeFor ownerId organizationId repositoryId observedAt
                let unrelatedScope = scopeFor ownerId organizationId (Guid.NewGuid()) observedAt

                let rejectedFact =
                    UsageFact.RepositoryStorageBytesMinute(
                        usageFactId,
                        CorrelationId(String('c', OperationsUsageSql.CorrelationIdMaxLength + 1)),
                        ownerId,
                        organizationId,
                        repositoryId,
                        StoragePoolId(String('s', OperationsUsageSql.StoragePoolIdMaxLength + 1)),
                        4096L,
                        observedAt
                    )

                let repairedFact = fact usageFactId ownerId organizationId repositoryId observedAt
                let store = OperationsUsageStore(SqlOperationsUsageTransactionScope connectionString)

                let processor =
                    OperationsUsageIngestionProcessor(
                        OperationsUsageFactStoreAdapter(store),
                        NullLogger<OperationsUsageIngestionProcessor>
                            .Instance
                    )

                let events = List<string>()
                let mutable completenessAtDeadLetter = None
                let mutable unrelatedCompletenessAtDeadLetter = None
                let mutable activeRejectionCountAtDeadLetter = None

                let beforeDeadLetterAsync () =
                    task {
                        let! scopedCompleteness = store.EvaluateBillingCompletenessAsync(scope, CancellationToken.None)
                        let! unrelatedCompleteness = store.EvaluateBillingCompletenessAsync(unrelatedScope, CancellationToken.None)
                        let! activeRejectionCount = executeInt32Async connectionString "SELECT COUNT(*) FROM ops.UsageFactRejection WHERE IsActive = 1;"
                        completenessAtDeadLetter <- Some scopedCompleteness
                        unrelatedCompletenessAtDeadLetter <- Some unrelatedCompleteness
                        activeRejectionCountAtDeadLetter <- Some activeRejectionCount
                    }
                    :> Task

                let actions = InspectingUsageMessageActions(events, beforeDeadLetterAsync)
                do! processor.ProcessMessageAsync(workerMessage rejectedFact, actions, CancellationToken.None)

                let! repaired = store.StoreUsageFactAsync(repairedFact, payloadFor repairedFact, CancellationToken.None)
                let! completenessAfterRepair = store.EvaluateBillingCompletenessAsync(scope, CancellationToken.None)
                let! activeRejectionCountAfterRepair = executeInt32Async connectionString "SELECT COUNT(*) FROM ops.UsageFactRejection WHERE IsActive = 1;"

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(String.Join("|", events), Is.EqualTo("dead-letter"))
                        Assert.That(completenessAtDeadLetter, Is.EqualTo(Some BlockedByActiveScopedRejection))
                        Assert.That(unrelatedCompletenessAtDeadLetter, Is.EqualTo(Some Complete))
                        Assert.That(activeRejectionCountAtDeadLetter, Is.EqualTo(Some 1))
                        Assert.That(repaired.IsOk, Is.True)

                        Assert.That(
                            repaired
                            |> Result.exists (fun result -> result.Status = UsageFactPersistenceStatus.Accepted),
                            Is.True
                        )

                        Assert.That(completenessAfterRepair, Is.EqualTo(Complete))
                        Assert.That(activeRejectionCountAfterRepair, Is.Zero))
                )
            })

    /// Proves the first instant of the next UTC month derives a different scope and lock identity from the prior month.
    [<Test>]
    member _.UtcMonthBoundaryUsesAHalfOpenInterval() =
        let ownerId = Guid.NewGuid()
        let organizationId = Guid.NewGuid()
        let repositoryId = Guid.NewGuid()
        let august = scopeFor ownerId organizationId repositoryId (Instant.FromUtc(2026, 8, 31, 23, 59, 59))
        let september = scopeFor ownerId organizationId repositoryId (Instant.FromUtc(2026, 9, 1, 0, 0, 0))

        Assert.Multiple(
            Action (fun () ->
                Assert.That(BillingCompletenessScope.nextMonthStart august, Is.EqualTo(september.MonthStart))
                Assert.That(BillingCompletenessScope.databaseLockIdentity august, Is.Not.EqualTo(BillingCompletenessScope.databaseLockIdentity september)))
        )

    /// Verifies tuple and fact identities are rejected before they could acquire an ambiguous owner-month lock.
    [<Test>]
    member _.EmptyStableIdentifiersAreRejectedAtCoordinationBoundaries() =
        let observedAt = Instant.FromUtc(2026, 8, 1, 0, 0)
        let validScope = scopeFor (Guid.NewGuid()) (Guid.NewGuid()) (Guid.NewGuid()) observedAt

        let scoped =
            {
                RejectionId = Guid.NewGuid()
                UsageFactId = Some Guid.Empty
                Scope = Some validScope
                ReportedScope = None
                Reason = "empty fact identity"
                IsActive = true
            }

        Assert.Multiple(
            Action (fun () ->
                Assert.That(
                    BillingCompletenessScope.tryCreate Guid.Empty (Guid.NewGuid()) (Guid.NewGuid()) observedAt
                    |> Result.isError,
                    Is.True
                )

                Assert.That(
                    BillingCompletenessScope.tryCreate (Guid.NewGuid()) Guid.Empty (Guid.NewGuid()) observedAt
                    |> Result.isError,
                    Is.True
                )

                Assert.That(
                    BillingCompletenessScope.tryCreate (Guid.NewGuid()) (Guid.NewGuid()) Guid.Empty observedAt
                    |> Result.isError,
                    Is.True
                )

                Assert.That(
                    UsageFactRejection.validate scoped
                    |> Result.isError,
                    Is.True
                ))
        )
