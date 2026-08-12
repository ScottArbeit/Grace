namespace Grace.Operations.Tests

open Grace.Operations.Data
open Grace.Shared
open Grace.Types.Common
open Grace.Types.Usage
open Microsoft.Data.SqlClient
open NodaTime
open NUnit.Framework
open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Proves the owner-month completeness coordination boundary against a real SQL Server transaction and application lock.
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

    /// Derives a validated owner-month scope or fails the test with contract details.
    let scopeFor ownerId organizationId repositoryId observedAt =
        match BillingCompletenessScope.tryCreate ownerId organizationId repositoryId observedAt with
        | Ok scope -> scope
        | Error errors ->
            Assert.Fail(String.Join("; ", errors))
            Unchecked.defaultof<BillingCompletenessScope>

    /// Acquires one scope lock and keeps it until the supplied release task completes.
    let holdScopeAsync
        (transactionScope: IOperationsUsageTransactionScope)
        (scope: BillingCompletenessScope)
        (started: TaskCompletionSource<unit>)
        (release: TaskCompletionSource<unit>)
        =
        transactionScope.ExecuteAsync(
            (fun transaction cancellationToken ->
                task {
                    do! transaction.AcquireBillingCompletenessScopeAsync(scope, cancellationToken)
                    started.SetResult()
                    do! release.Task.WaitAsync cancellationToken
                    return ()
                }),
            CancellationToken.None
        )

    /// Acquires one scope lock and signals when SQL has granted it.
    let acquireScopeAsync (transactionScope: IOperationsUsageTransactionScope) (scope: BillingCompletenessScope) (acquired: TaskCompletionSource<unit>) =
        transactionScope.ExecuteAsync(
            (fun transaction cancellationToken ->
                task {
                    do! transaction.AcquireBillingCompletenessScopeAsync(scope, cancellationToken)
                    acquired.SetResult()
                    return ()
                }),
            CancellationToken.None
        )

    /// Proves that two same-scope transactions serialize on the central SQL application-lock resource.
    [<Test>]
    member _.SameScopeOperationsSerializeThroughTheDatabase() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId = Guid.NewGuid()
                let organizationId = Guid.NewGuid()
                let repositoryId = Guid.NewGuid()
                let scope = scopeFor ownerId organizationId repositoryId (Instant.FromUtc(2026, 8, 4, 12, 0))
                let transactionScope = SqlOperationsUsageTransactionScope connectionString :> IOperationsUsageTransactionScope
                let firstStarted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                let releaseFirst = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                let secondAcquired = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                let first = holdScopeAsync transactionScope scope firstStarted releaseFirst
                do! firstStarted.Task.WaitAsync(TimeSpan.FromSeconds 10.0)
                let second = acquireScopeAsync transactionScope scope secondAcquired
                do! Task.Delay 250
                Assert.That(secondAcquired.Task.IsCompleted, Is.False)
                releaseFirst.SetResult()
                do! first
                do! second
                Assert.That(secondAcquired.Task.IsCompleted, Is.True)
            })

    /// Proves that database locks do not collide between distinct owner-repository-month tuples.
    [<Test>]
    member _.DifferentScopesProceedIndependently() =
        withDatabaseAsync (fun connectionString ->
            task {
                let ownerId = Guid.NewGuid()
                let organizationId = Guid.NewGuid()
                let firstScope = scopeFor ownerId organizationId (Guid.NewGuid()) (Instant.FromUtc(2026, 8, 4, 12, 0))
                let secondScope = scopeFor ownerId organizationId (Guid.NewGuid()) (Instant.FromUtc(2026, 8, 4, 12, 0))
                let transactionScope = SqlOperationsUsageTransactionScope connectionString :> IOperationsUsageTransactionScope
                let firstStarted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                let releaseFirst = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                let secondAcquired = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                let first = holdScopeAsync transactionScope firstScope firstStarted releaseFirst
                do! firstStarted.Task.WaitAsync(TimeSpan.FromSeconds 10.0)
                let second = acquireScopeAsync transactionScope secondScope secondAcquired
                do! secondAcquired.Task.WaitAsync(TimeSpan.FromSeconds 3.0)
                releaseFirst.SetResult()
                do! first
                do! second
            })

    /// Proves a failed transaction releases its database lock and leaves no durable completion state behind.
    [<Test>]
    member _.RollbackReleasesTheDatabaseLock() =
        withDatabaseAsync (fun connectionString ->
            task {
                let scope = scopeFor (Guid.NewGuid()) (Guid.NewGuid()) (Guid.NewGuid()) (Instant.FromUtc(2026, 8, 4, 12, 0))
                let transactionScope = SqlOperationsUsageTransactionScope connectionString :> IOperationsUsageTransactionScope

                Assert.ThrowsAsync<InvalidOperationException>(
                    Func<Task> (fun () ->
                        transactionScope.ExecuteAsync(
                            (fun transaction cancellationToken ->
                                task {
                                    do! transaction.AcquireBillingCompletenessScopeAsync(scope, cancellationToken)
                                    return raise (InvalidOperationException "forced rollback")
                                }),
                            CancellationToken.None
                        )
                        :> Task)
                )
                |> ignore

                let acquired = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                do! acquireScopeAsync transactionScope scope acquired
                Assert.That(acquired.Task.IsCompleted, Is.True)
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

                let rejection =
                    {
                        RejectionId = Guid.NewGuid()
                        UsageFactId = Some usageFactId
                        Scope = Some scope
                        ReportedScope = None
                        Reason = "transient ingestion rejection"
                        IsActive = true
                    }

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

                let payload = JsonSerializer.SerializeToUtf8Bytes(usageFact, Constants.JsonSerializerOptions)
                let! accepted = store.StoreUsageFactAsync(usageFact, payload, CancellationToken.None)
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

        let scopedRejection =
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
                    UsageFactRejection.validate scopedRejection
                    |> Result.isError,
                    Is.True
                ))
        )
