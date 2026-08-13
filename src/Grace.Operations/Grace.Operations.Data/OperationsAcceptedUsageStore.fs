namespace Grace.Operations.Data

open Grace.Types.Common
open Grace.Types.Usage
open Microsoft.Data.SqlClient
open System
open System.Threading
open System.Threading.Tasks

/// Adapts the merged accepted-fact SQL mutation to the existing Operations usage transaction interface.
type private SqlAcceptedFactMutationExecutor(interleaving: IAcceptedFactMutationInterleaving) =
    let mutation = SqlAcceptedFactMutation(interleaving)

    interface IAcceptedFactMutationExecutor with

        member _.AcceptAsync(connection, transaction, plan, scope, rawInsertion, cancellationToken) =
            mutation.AcceptAsync(connection, transaction, plan, scope, rawInsertion, cancellationToken)

/// Runs Operations usage mutations in one caller-owned Azure SQL transaction using the shared accepted-fact mutation.
type SqlOperationsUsageTransactionScope private (connectionString: string, acceptedFactMutationExecutor: IAcceptedFactMutationExecutor) =

    /// Opens the SQL connection for one Operations usage transaction.
    let openConnectionAsync cancellationToken =
        task {
            let connection = new SqlConnection(connectionString)
            do! connection.OpenAsync cancellationToken
            return connection
        }

    /// Rolls back a failed transaction while preserving its primary error.
    let rollbackIgnoringFailuresAsync (transaction: SqlTransaction) =
        task {
            try
                do! transaction.RollbackAsync CancellationToken.None
            with
            | _ -> ()
        }

    /// Creates the production transaction scope without a test interleaving.
    new(connectionString: string) = SqlOperationsUsageTransactionScope(connectionString, SqlAcceptedFactMutationExecutor(null) :> IAcceptedFactMutationExecutor)

    /// Creates a transaction scope that exposes the shared post-staging interleaving for real-SQL rollback proof.
    static member internal CreateForTest(connectionString, interleaving) =
        SqlOperationsUsageTransactionScope(connectionString, SqlAcceptedFactMutationExecutor(interleaving) :> IAcceptedFactMutationExecutor)

    interface IOperationsUsageTransactionScope with

        member _.ExecuteAsync(operation, cancellationToken) =
            task {
                use! connection = openConnectionAsync cancellationToken
                let! databaseTransaction = connection.BeginTransactionAsync cancellationToken
                use transaction = databaseTransaction :?> SqlTransaction
                let operationsTransaction = SqlOperationsUsageTransaction(connection, transaction, acceptedFactMutationExecutor)

                try
                    let! result = operation operationsTransaction cancellationToken
                    do! transaction.CommitAsync cancellationToken
                    return result
                with
                | ex ->
                    do! rollbackIgnoringFailuresAsync transaction
                    return raise ex
            }

/// Persists online and archived usage through one caller transaction and the merged accepted-fact mutation.
type OperationsUsageStore(transactionScope: IOperationsUsageTransactionScope) =

    /// Acquires the central scope lock derived from accepted fact data before any completeness-affecting mutation.
    let acquireScopeAsync (transaction: IOperationsUsageTransaction) (rawFact: RawUsageFact) cancellationToken =
        task {
            match BillingCompletenessScope.tryCreate rawFact.OwnerId rawFact.OrganizationId rawFact.RepositoryId rawFact.ObservedAt with
            | Error errors -> return invalidOp (String.Join("; ", errors))
            | Ok scope ->
                do! transaction.AcquireBillingCompletenessScopeAsync(scope, cancellationToken)
                return scope
        }

    /// Projects the shared mutation outcome into the existing online storage result without duplicating accepted SQL.
    let persistenceResult plan outcome =
        match outcome with
        | ExistingSameScope -> { Status = UsageFactPersistenceStatus.AlreadyProcessed; UsageFactId = plan.RawFact.UsageFactId; Aggregate = None }
        | InsertedIntoOpenPeriod
        | InsertedIntoClosedPeriod -> { Status = UsageFactPersistenceStatus.Accepted; UsageFactId = plan.RawFact.UsageFactId; Aggregate = Some plan.Aggregate }

    /// Persists one plan through the caller transaction and a narrow raw-insertion adapter.
    let acceptAsync plan rawInsertion cancellationToken =
        task {
            let operation (transaction: IOperationsUsageTransaction) operationCancellationToken =
                task {
                    let! scope = acquireScopeAsync transaction plan.RawFact operationCancellationToken
                    let acceptedFactTransaction = transaction :?> IAcceptedFactMutationTransaction

                    let! outcome = acceptedFactTransaction.AcceptUsageFactAsync(plan, scope, rawInsertion, operationCancellationToken)
                    return persistenceResult plan outcome
                }

            return! transactionScope.ExecuteAsync(operation, cancellationToken)
        }

    /// Stores a usage fact exactly once by durable `UsageFactId` and projects aggregates only for newly accepted facts.
    member _.StoreUsageFactAsync(fact: UsageFact, rawPayload: byte array, cancellationToken: CancellationToken) =
        task {
            match UsageFactPersistencePlan.tryCreate fact rawPayload with
            | Error errors -> return Error errors
            | Ok plan ->
                let! result = acceptAsync plan HotRawFact cancellationToken
                return Ok result
        }

    /// Replays an archived usage fact into raw index and aggregate state without restoring hot SQL payload bytes.
    member _.ReplayArchivedUsageFactAsync(fact: UsageFact, rawPayload: byte array, pointer: RawUsageFactArchivePointer, cancellationToken: CancellationToken) =
        task {
            if pointer.UsageFactId <> fact.UsageFactId then
                return Error [ $"Replay pointer UsageFactId '{pointer.UsageFactId}' does not match payload UsageFactId '{fact.UsageFactId}'." ]
            else
                match UsageFactPersistencePlan.tryCreate fact rawPayload with
                | Error errors -> return Error errors
                | Ok plan ->
                    let! result = acceptAsync plan (ArchivedReplayRawFact pointer) cancellationToken
                    return Ok result
        }

    /// Records a scoped blocker under the same lock as accepted usage, or preserves partial evidence without a scope.
    member _.RecordUsageFactRejectionAsync(rejection: UsageFactRejection, cancellationToken: CancellationToken) =
        task {
            match UsageFactRejection.validate rejection with
            | Error errors -> return Error errors
            | Ok validRejection ->
                let operation (transaction: IOperationsUsageTransaction) (operationCancellationToken: CancellationToken) =
                    task {
                        match validRejection.Scope with
                        | Some scope ->
                            do! transaction.AcquireBillingCompletenessScopeAsync(scope, operationCancellationToken)

                            do!
                                transaction.EnsureUsageFactIdMatchesBillingCompletenessScopeAsync(
                                    validRejection.UsageFactId.Value,
                                    scope,
                                    operationCancellationToken
                                )

                            return! transaction.RecordScopedUsageFactRejectionAsync(validRejection, operationCancellationToken)
                        | None ->
                            do! transaction.RecordUnscopedUsageFactRejectionAsync(validRejection, operationCancellationToken)
                            return None
                    }

                let! result = transactionScope.ExecuteAsync(operation, cancellationToken)
                return Ok result
        }

    /// Reads completeness after acquiring the same transaction-owned lock used by mutations for the exact scope.
    member _.EvaluateBillingCompletenessAsync(scope: BillingCompletenessScope, cancellationToken: CancellationToken) =
        match BillingCompletenessScope.validate scope with
        | Error errors -> Task.FromException<BillingCompletenessResult>(ArgumentException(String.Join("; ", errors), nameof scope))
        | Ok validScope ->
            let operation (transaction: IOperationsUsageTransaction) (operationCancellationToken: CancellationToken) =
                task {
                    do! transaction.AcquireBillingCompletenessScopeAsync(validScope, operationCancellationToken)
                    let! blocked = transaction.HasActiveScopedUsageFactRejectionAsync(validScope, operationCancellationToken)
                    let! journalBlocked = transaction.HasUnresolvedUsageFactJournalAsync(validScope, operationCancellationToken)

                    return
                        if blocked then BlockedByActiveScopedRejection
                        elif journalBlocked then BlockedByUnresolvedUsageFactJournal
                        else Complete
                }

            transactionScope.ExecuteAsync(operation, cancellationToken)
