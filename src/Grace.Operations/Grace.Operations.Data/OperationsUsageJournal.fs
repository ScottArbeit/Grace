namespace Grace.Operations.Data

open Grace.Types.Usage
open Grace.Types.Common
open Microsoft.Data.SqlClient
open NodaTime
open System
open System.Data
open System.Threading
open System.Threading.Tasks

/// Represents the only durable lifecycle for a supported fact before and during Operations ingestion.
type UsageFactJournalState =
    | Pending = 0
    | Accepted = 1
    | Rejected = 2

/// Carries the immutable journal payload and exact identity read by dispatch immediately before send.
type UsageFactJournalEntry =
    {
        UsageFactId: UsageFactId
        RawPayload: byte array
        CorrelationId: CorrelationId
        FactKind: UsageFactKind
        OwnerId: OwnerId
        OrganizationId: OrganizationId
        RepositoryId: RepositoryId
        StoragePoolId: StoragePoolId
        Quantity: int64
        ObservedAt: Instant
        State: UsageFactJournalState
    }

/// Reports whether an append created durable pre-send journal truth or repeated the same immutable fact.
type UsageFactJournalAppendResult =
    | AppendedPending
    | AlreadyPending
    | AlreadyTerminal of UsageFactJournalState

/// Reports how a delivered signal interacted with current journal truth.
type UsageFactJournalProcessResult =
    | AcceptedFromJournal
    | AlreadyAccepted
    | AlreadyRejected
    | MissingJournal
    | JournalConflict

/// Reports how an explicit deterministic supported-fact rejection interacted with current journal truth.
type UsageFactJournalRejectResult =
    | RejectedFromJournal
    | AlreadyRejected
    | RejectMissingJournal
    | RejectJournalConflict
    | RejectAlreadyAccepted

/// Pauses a ProcessAsync transaction only after its raw and aggregate mutations have been staged for deterministic rollback proof.
type internal IOperationsUsageJournalTransactionInterleaving =

    /// Observes the transaction-local point immediately before journal acceptance can commit.
    abstract AfterRawAndAggregateStagedAsync: cancellationToken: CancellationToken -> Task

/// Leaves production journal transactions uninterrupted when no deterministic proof interleaving is supplied.
type private NoOperationsUsageJournalTransactionInterleaving() =

    interface IOperationsUsageJournalTransactionInterleaving with

        member _.AfterRawAndAggregateStagedAsync(_cancellationToken) = Task.CompletedTask

/// Owns the internal Operations append, dispatch scan, and transactionally verified journal processing seam.
type IOperationsUsageJournalStore =

    /// Commits a complete supported fact as immutable canonical Pending truth before any broker send.
    abstract AppendAsync: fact: UsageFact * cancellationToken: CancellationToken -> Task<Result<UsageFactJournalAppendResult, string list>>

    /// Reads a bounded stable set of currently Pending immutable facts for retryable Service Bus signalling.
    abstract ListPendingAsync: batchSize: int * cancellationToken: CancellationToken -> Task<UsageFactJournalEntry list>

    /// Rereads one candidate immediately before send and returns it only while its durable state remains Pending.
    abstract TryGetPendingAsync: usageFactId: UsageFactId * cancellationToken: CancellationToken -> Task<UsageFactJournalEntry option>

    /// Rechecks the matching immutable journal row inside the raw, aggregate, and state transaction before accepting it.
    abstract ProcessAsync: fact: UsageFact * rawPayload: byte array * cancellationToken: CancellationToken -> Task<UsageFactJournalProcessResult>

    /// Atomically records scoped rejection evidence and the Rejected state for an already journaled supported fact.
    abstract RejectAsync: fact: UsageFact * rawPayload: byte array * reason: string * cancellationToken: CancellationToken -> Task<UsageFactJournalRejectResult>

    /// Explicitly replays one matching Rejected fact into accepted raw and aggregate storage.
    abstract RepairAsync: fact: UsageFact * cancellationToken: CancellationToken -> Task<UsageFactJournalProcessResult>

/// Stores immutable usage facts in the SQL journal and treats Service Bus delivery as a retryable signal only.
type SqlOperationsUsageJournalStore private (connectionString: string, transactionInterleaving: IOperationsUsageJournalTransactionInterleaving) =

    /// Opens the SQL connection used by one append, scan, or processing transaction.
    let openConnectionAsync cancellationToken =
        task {
            let connection = new SqlConnection(connectionString)
            do! connection.OpenAsync cancellationToken
            return connection
        }

    /// Converts an instant to the UTC database timestamp representation used by Operations usage tables.
    let toUtcDateTime (instant: Instant) = instant.ToDateTimeUtc()

    /// Converts a database timestamp back to the immutable Operations instant representation.
    let toInstant (dateTime: DateTime) =
        DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
        |> Instant.FromDateTimeUtc

    /// Adds a scalar parameter to a transaction-bound SQL command.
    let addParameter (command: SqlCommand) name sqlDbType value =
        let parameter = command.Parameters.Add(name, sqlDbType)
        parameter.Value <- value

    /// Adds a bounded string parameter to a transaction-bound SQL command.
    let addStringParameter (command: SqlCommand) name length (value: string) =
        let parameter = command.Parameters.Add(name, SqlDbType.NVarChar, length)
        parameter.Value <- value

    /// Creates a command that remains inside the supplied SQL transaction.
    let createCommand (connection: SqlConnection) (transaction: SqlTransaction) commandText =
        let command = connection.CreateCommand()
        command.Transaction <- transaction
        command.CommandType <- CommandType.Text
        command.CommandText <- commandText
        command

    /// Acquires #877's exact scope lock before a completeness-affecting journal mutation.
    let acquireScopeAsync connection transaction (scope: BillingCompletenessScope) cancellationToken =
        task {
            use command = createCommand connection transaction OperationsUsageSql.AcquireBillingCompletenessScopeLock
            addStringParameter command "@BillingCompletenessLockResource" 255 (BillingCompletenessScope.databaseLockIdentity scope)
            addParameter command "@BillingCompletenessLockTimeoutMilliseconds" SqlDbType.Int 30000
            let! _ = command.ExecuteNonQueryAsync cancellationToken
            return ()
        }

    /// Adds an immutable fact's complete identity, payload, and resource fields to the supplied command.
    let addFactParameters (command: SqlCommand) (rawFact: RawUsageFact) =
        addParameter command "@UsageFactId" SqlDbType.UniqueIdentifier rawFact.UsageFactId
        let payload = command.Parameters.Add("@RawPayload", SqlDbType.VarBinary, -1)
        payload.Value <- Array.copy rawFact.RawPayload
        addStringParameter command "@CorrelationId" OperationsUsageSql.CorrelationIdMaxLength rawFact.CorrelationId
        addParameter command "@FactKind" SqlDbType.Int (int rawFact.FactKind)
        addParameter command "@OwnerId" SqlDbType.UniqueIdentifier rawFact.OwnerId
        addParameter command "@OrganizationId" SqlDbType.UniqueIdentifier rawFact.OrganizationId
        addParameter command "@RepositoryId" SqlDbType.UniqueIdentifier rawFact.RepositoryId
        addStringParameter command "@StoragePoolId" OperationsUsageSql.StoragePoolIdMaxLength rawFact.StoragePoolId
        addParameter command "@Quantity" SqlDbType.BigInt rawFact.Quantity
        addParameter command "@ObservedAtUtc" SqlDbType.DateTime2 (toUtcDateTime rawFact.ObservedAt)

    /// Builds the exact completeness scope for a fact that has already passed supported fact validation.
    let scopeFor (rawFact: RawUsageFact) : BillingCompletenessScope =
        BillingCompletenessScope.tryCreate rawFact.OwnerId rawFact.OrganizationId rawFact.RepositoryId rawFact.ObservedAt
        |> Result.defaultWith (fun errors -> invalidOp (String.Join("; ", errors)))

    /// Reads a full journal row while an append or worker transaction keeps update locks on its identity.
    let readJournalForUpdateAsync (connection: SqlConnection) (transaction: SqlTransaction) (usageFactId: UsageFactId) (cancellationToken: CancellationToken) =
        task {
            use command =
                createCommand
                    connection
                    transaction
                    """
SELECT UsageFactId, RawPayload, CorrelationId, FactKind, OwnerId, OrganizationId, RepositoryId, StoragePoolId, Quantity, ObservedAtUtc, State
FROM ops.UsageFactJournal WITH (UPDLOCK, HOLDLOCK)
WHERE UsageFactId = @UsageFactId;
"""

            addParameter command "@UsageFactId" SqlDbType.UniqueIdentifier usageFactId
            use! reader = command.ExecuteReaderAsync cancellationToken
            let! hasRow = reader.ReadAsync cancellationToken

            if not hasRow then
                return None
            else
                return
                    Some
                        {
                            UsageFactId = reader.GetGuid 0
                            RawPayload = reader.GetFieldValue<byte array> 1
                            CorrelationId = reader.GetString 2
                            FactKind = enum<UsageFactKind> (reader.GetInt32 3)
                            OwnerId = reader.GetGuid 4
                            OrganizationId = reader.GetGuid 5
                            RepositoryId = reader.GetGuid 6
                            StoragePoolId = reader.GetString 7
                            Quantity = reader.GetInt64 8
                            ObservedAt = reader.GetDateTime 9 |> toInstant
                            State = enum<UsageFactJournalState> (reader.GetInt32 10)
                        }
        }

    /// Compares every immutable fact field so same-ID replay cannot cross scope or silently replace payload truth.
    let matches (rawFact: RawUsageFact) (entry: UsageFactJournalEntry) =
        entry.UsageFactId = rawFact.UsageFactId
        && entry.CorrelationId = rawFact.CorrelationId
        && entry.FactKind = rawFact.FactKind
        && entry.OwnerId = rawFact.OwnerId
        && entry.OrganizationId = rawFact.OrganizationId
        && entry.RepositoryId = rawFact.RepositoryId
        && entry.StoragePoolId = rawFact.StoragePoolId
        && entry.Quantity = rawFact.Quantity
        && entry.ObservedAt = rawFact.ObservedAt
        && entry
            .RawPayload
            .AsSpan()
            .SequenceEqual(rawFact.RawPayload)

    /// Inserts a validated complete fact as Pending without deriving journal state from any send result.
    let insertPendingAsync (connection: SqlConnection) (transaction: SqlTransaction) (rawFact: RawUsageFact) (cancellationToken: CancellationToken) =
        task {
            use command =
                createCommand
                    connection
                    transaction
                    """
INSERT INTO ops.UsageFactJournal
    (UsageFactId, RawPayload, CorrelationId, FactKind, OwnerId, OrganizationId, RepositoryId, StoragePoolId, Quantity, ObservedAtUtc, State)
VALUES
    (@UsageFactId, @RawPayload, @CorrelationId, @FactKind, @OwnerId, @OrganizationId, @RepositoryId, @StoragePoolId, @Quantity, @ObservedAtUtc, 0);
"""

            addFactParameters command rawFact
            let! _ = command.ExecuteNonQueryAsync cancellationToken
            return ()
        }

    /// Adds the accepted fact to raw usage only when a prior durable raw identity does not already exist.
    let tryInsertRawAsync (connection: SqlConnection) (transaction: SqlTransaction) (rawFact: RawUsageFact) (cancellationToken: CancellationToken) =
        task {
            use command = createCommand connection transaction OperationsUsageSql.TryInsertRawUsageFact
            addFactParameters command rawFact
            let! rows = command.ExecuteNonQueryAsync cancellationToken
            return rows = 1
        }

    /// Adds the newly accepted quantity to the minute aggregate under the same journal processing transaction.
    let addAggregateAsync (connection: SqlConnection) (transaction: SqlTransaction) (aggregate: UsageAggregateMinute) (cancellationToken: CancellationToken) =
        task {
            use command = createCommand connection transaction OperationsUsageSql.AddToUsageAggregateMinute
            addParameter command "@FactKind" SqlDbType.Int (int aggregate.Key.FactKind)
            addParameter command "@OwnerId" SqlDbType.UniqueIdentifier aggregate.Key.OwnerId
            addParameter command "@OrganizationId" SqlDbType.UniqueIdentifier aggregate.Key.OrganizationId
            addParameter command "@RepositoryId" SqlDbType.UniqueIdentifier aggregate.Key.RepositoryId
            addStringParameter command "@StoragePoolId" OperationsUsageSql.StoragePoolIdMaxLength aggregate.Key.StoragePoolId
            addParameter command "@BucketStartUtc" SqlDbType.DateTime2 (toUtcDateTime aggregate.Key.BucketStart)
            addParameter command "@Quantity" SqlDbType.BigInt aggregate.Quantity
            let! _ = command.ExecuteNonQueryAsync cancellationToken
            return ()
        }

    /// Marks one still-unresolved matching journal row Accepted only after raw and aggregate persistence succeeded.
    let markAcceptedAsync (connection: SqlConnection) (transaction: SqlTransaction) (usageFactId: UsageFactId) (cancellationToken: CancellationToken) =
        task {
            use command =
                createCommand
                    connection
                    transaction
                    """
UPDATE ops.UsageFactJournal
SET State = 1, TerminalAtUtc = SYSUTCDATETIME()
WHERE UsageFactId = @UsageFactId AND State IN (0, 2);
"""

            addParameter command "@UsageFactId" SqlDbType.UniqueIdentifier usageFactId
            let! _ = command.ExecuteNonQueryAsync cancellationToken
            return ()
        }

    /// Marks one still-unresolved matching journal row Rejected only after its scoped rejection evidence is durable.
    let markRejectedAsync (connection: SqlConnection) (transaction: SqlTransaction) (usageFactId: UsageFactId) (cancellationToken: CancellationToken) =
        task {
            use command =
                createCommand
                    connection
                    transaction
                    """
UPDATE ops.UsageFactJournal
SET State = 2, TerminalAtUtc = COALESCE(TerminalAtUtc, SYSUTCDATETIME())
WHERE UsageFactId = @UsageFactId AND State = 0;
"""

            addParameter command "@UsageFactId" SqlDbType.UniqueIdentifier usageFactId
            let! _ = command.ExecuteNonQueryAsync cancellationToken
            return ()
        }

    /// Records exact-scope rejection evidence using the #877 uniqueness and accepted-row guard inside this journal transaction.
    let recordScopedRejectionAsync
        (connection: SqlConnection)
        (transaction: SqlTransaction)
        (usageFactId: UsageFactId)
        (scope: BillingCompletenessScope)
        (reason: string)
        (cancellationToken: CancellationToken)
        =
        task {
            if String.IsNullOrWhiteSpace reason then
                invalidArg (nameof reason) "A deterministic rejection reason is required."

            let boundedReason =
                if reason.Length
                   <= OperationsUsageSql.ArchiveFailureReasonMaxLength then
                    reason
                else
                    reason.Substring(0, OperationsUsageSql.ArchiveFailureReasonMaxLength)

            use command = createCommand connection transaction OperationsUsageSql.RecordScopedUsageFactRejection
            addParameter command "@RejectionId" SqlDbType.UniqueIdentifier (Guid.NewGuid())
            addParameter command "@UsageFactId" SqlDbType.UniqueIdentifier usageFactId
            addParameter command "@OwnerId" SqlDbType.UniqueIdentifier scope.OwnerId
            addParameter command "@OrganizationId" SqlDbType.UniqueIdentifier scope.OrganizationId
            addParameter command "@RepositoryId" SqlDbType.UniqueIdentifier scope.RepositoryId
            addParameter command "@MonthStartUtc" SqlDbType.DateTime2 (toUtcDateTime scope.MonthStart)
            addParameter command "@NextMonthStartUtc" SqlDbType.DateTime2 (toUtcDateTime (BillingCompletenessScope.nextMonthStart scope))
            addStringParameter command "@Reason" OperationsUsageSql.ArchiveFailureReasonMaxLength boundedReason
            use! reader = command.ExecuteReaderAsync cancellationToken
            let! hasRow = reader.ReadAsync cancellationToken

            if not hasRow then
                invalidOp "A Pending journal fact cannot become Rejected after accepted raw usage already exists."
        }

    /// Releases rejected completeness evidence only inside the same successful accepted processing transaction.
    let resolveRejectionAsync
        (connection: SqlConnection)
        (transaction: SqlTransaction)
        (usageFactId: UsageFactId)
        (scope: BillingCompletenessScope)
        (cancellationToken: CancellationToken)
        =
        task {
            use command = createCommand connection transaction OperationsUsageSql.ResolveScopedUsageFactRejection
            addParameter command "@UsageFactId" SqlDbType.UniqueIdentifier usageFactId
            addParameter command "@OwnerId" SqlDbType.UniqueIdentifier scope.OwnerId
            addParameter command "@OrganizationId" SqlDbType.UniqueIdentifier scope.OrganizationId
            addParameter command "@RepositoryId" SqlDbType.UniqueIdentifier scope.RepositoryId
            addParameter command "@MonthStartUtc" SqlDbType.DateTime2 (toUtcDateTime scope.MonthStart)
            addParameter command "@NextMonthStartUtc" SqlDbType.DateTime2 (toUtcDateTime (BillingCompletenessScope.nextMonthStart scope))
            let! _ = command.ExecuteNonQueryAsync cancellationToken
            return ()
        }

    /// Rolls back a failed processing transaction without replacing its original error.
    let rollbackIgnoringFailuresAsync (transaction: SqlTransaction) =
        task {
            try
                do! transaction.RollbackAsync CancellationToken.None
            with
            | _ -> ()
        }

    /// Runs one journal operation inside a SQL transaction and preserves all-or-nothing journal truth.
    let executeAsync operation cancellationToken =
        task {
            use! connection = openConnectionAsync cancellationToken
            let! databaseTransaction = connection.BeginTransactionAsync cancellationToken
            use transaction = databaseTransaction :?> SqlTransaction

            try
                let! result = operation connection transaction cancellationToken
                do! transaction.CommitAsync cancellationToken
                return result
            with
            | ex ->
                do! rollbackIgnoringFailuresAsync transaction
                return raise ex
        }

    /// Creates the production journal store with no test interleaving in its transaction path.
    new(connectionString: string) =
        SqlOperationsUsageJournalStore(connectionString, NoOperationsUsageJournalTransactionInterleaving() :> IOperationsUsageJournalTransactionInterleaving)

    /// Creates an internal deterministic proof store that pauses only a transaction-local test interleaving point.
    static member internal CreateForTest(connectionString, transactionInterleaving) = SqlOperationsUsageJournalStore(connectionString, transactionInterleaving)

    /// Appends one supported fact with immutable-idempotence semantics before a dispatcher can observe it.
    member _.AppendAsync(fact: UsageFact, cancellationToken: CancellationToken) =
        task {
            match UsageFactPersistencePlan.tryCreateCanonical fact with
            | Error errors -> return Error errors
            | Ok plan ->
                let! result =
                    executeAsync
                        (fun connection transaction operationCancellationToken ->
                            task {
                                let scope = scopeFor plan.RawFact
                                do! acquireScopeAsync connection transaction scope operationCancellationToken
                                let! existing = readJournalForUpdateAsync connection transaction plan.RawFact.UsageFactId operationCancellationToken

                                match existing with
                                | Some entry when not (matches plan.RawFact entry) ->
                                    return raise (InvalidOperationException("UsageFactId is already bound to a different immutable journal payload or scope."))
                                | Some entry when entry.State = UsageFactJournalState.Pending -> return AlreadyPending
                                | Some entry -> return AlreadyTerminal entry.State
                                | None ->
                                    do! insertPendingAsync connection transaction plan.RawFact operationCancellationToken
                                    return AppendedPending
                            })
                        cancellationToken

                return Ok result
        }

    /// Lists current Pending rows in a stable bounded order; a later transaction still rechecks state before sending.
    member _.ListPendingAsync(batchSize: int, cancellationToken: CancellationToken) =
        task {
            if batchSize <= 0 then
                invalidArg (nameof batchSize) "Journal dispatch batch size must be greater than zero."

            use! connection = openConnectionAsync cancellationToken
            use command = connection.CreateCommand()
            command.CommandType <- CommandType.Text

            command.CommandText <-
                """
SELECT TOP (@BatchSize) UsageFactId, RawPayload, CorrelationId, FactKind, OwnerId, OrganizationId, RepositoryId, StoragePoolId, Quantity, ObservedAtUtc, State
FROM ops.UsageFactJournal WITH (READCOMMITTEDLOCK)
WHERE State = 0
ORDER BY CreatedAtUtc ASC, UsageFactId ASC;
"""

            addParameter command "@BatchSize" SqlDbType.Int batchSize
            use! reader = command.ExecuteReaderAsync cancellationToken
            let rows = ResizeArray<UsageFactJournalEntry>()

            let mutable reading = true

            while reading do
                let! hasRow = reader.ReadAsync cancellationToken

                if hasRow then
                    rows.Add(
                        {
                            UsageFactId = reader.GetGuid 0
                            RawPayload = reader.GetFieldValue<byte array> 1
                            CorrelationId = reader.GetString 2
                            FactKind = enum<UsageFactKind> (reader.GetInt32 3)
                            OwnerId = reader.GetGuid 4
                            OrganizationId = reader.GetGuid 5
                            RepositoryId = reader.GetGuid 6
                            StoragePoolId = reader.GetString 7
                            Quantity = reader.GetInt64 8
                            ObservedAt = reader.GetDateTime 9 |> toInstant
                            State = enum<UsageFactJournalState> (reader.GetInt32 10)
                        }
                    )
                else
                    reading <- false

            return rows |> Seq.toList
        }

    /// Rereads a selected identity immediately before Service Bus send so stale scans cannot signal a terminal row.
    member _.TryGetPendingAsync(usageFactId: UsageFactId, cancellationToken: CancellationToken) =
        task {
            use! connection = openConnectionAsync cancellationToken
            use command = connection.CreateCommand()
            command.CommandType <- CommandType.Text

            command.CommandText <-
                """
SELECT UsageFactId, RawPayload, CorrelationId, FactKind, OwnerId, OrganizationId, RepositoryId, StoragePoolId, Quantity, ObservedAtUtc, State
FROM ops.UsageFactJournal WITH (READCOMMITTEDLOCK)
WHERE UsageFactId = @UsageFactId AND State = 0;
"""

            addParameter command "@UsageFactId" SqlDbType.UniqueIdentifier usageFactId
            use! reader = command.ExecuteReaderAsync cancellationToken
            let! hasRow = reader.ReadAsync cancellationToken

            if not hasRow then
                return None
            else
                return
                    Some
                        {
                            UsageFactId = reader.GetGuid 0
                            RawPayload = reader.GetFieldValue<byte array> 1
                            CorrelationId = reader.GetString 2
                            FactKind = enum<UsageFactKind> (reader.GetInt32 3)
                            OwnerId = reader.GetGuid 4
                            OrganizationId = reader.GetGuid 5
                            RepositoryId = reader.GetGuid 6
                            StoragePoolId = reader.GetString 7
                            Quantity = reader.GetInt64 8
                            ObservedAt = reader.GetDateTime 9 |> toInstant
                            State = enum<UsageFactJournalState> (reader.GetInt32 10)
                        }
        }

    /// Processes only a matching journalled fact and atomically commits raw, aggregate, rejection repair, and Accepted.
    member _.ProcessAsync(fact: UsageFact, rawPayload: byte array, cancellationToken: CancellationToken) =
        task {
            match UsageFactPersistencePlan.tryCreateCanonical fact with
            | Error errors -> return raise (InvalidOperationException(String.Join("; ", errors)))
            | Ok plan ->
                return!
                    executeAsync
                        (fun connection transaction operationCancellationToken ->
                            task {
                                let scope = scopeFor plan.RawFact
                                do! acquireScopeAsync connection transaction scope operationCancellationToken
                                let! journal = readJournalForUpdateAsync connection transaction plan.RawFact.UsageFactId operationCancellationToken

                                match journal with
                                | None -> return MissingJournal
                                | Some entry when not (matches plan.RawFact entry) -> return JournalConflict
                                | Some entry when entry.State = UsageFactJournalState.Accepted -> return AlreadyAccepted
                                | Some entry when entry.State = UsageFactJournalState.Rejected -> return UsageFactJournalProcessResult.AlreadyRejected
                                | Some _ ->
                                    let! inserted = tryInsertRawAsync connection transaction plan.RawFact operationCancellationToken

                                    if inserted then
                                        do! addAggregateAsync connection transaction plan.Aggregate operationCancellationToken

                                    do! transactionInterleaving.AfterRawAndAggregateStagedAsync(operationCancellationToken)
                                    do! resolveRejectionAsync connection transaction plan.RawFact.UsageFactId scope operationCancellationToken
                                    do! markAcceptedAsync connection transaction plan.RawFact.UsageFactId operationCancellationToken
                                    return AcceptedFromJournal
                            })
                        cancellationToken
        }

    /// Explicitly repairs a matching Rejected fact by resolving its evidence and accepting it in one transaction.
    member _.RepairAsync(fact: UsageFact, cancellationToken: CancellationToken) =
        task {
            match UsageFactPersistencePlan.tryCreateCanonical fact with
            | Error errors -> return raise (InvalidOperationException(String.Join("; ", errors)))
            | Ok plan ->
                return!
                    executeAsync
                        (fun connection transaction operationCancellationToken ->
                            task {
                                let scope = scopeFor plan.RawFact
                                do! acquireScopeAsync connection transaction scope operationCancellationToken
                                let! journal = readJournalForUpdateAsync connection transaction plan.RawFact.UsageFactId operationCancellationToken

                                match journal with
                                | None -> return MissingJournal
                                | Some entry when not (matches plan.RawFact entry) -> return JournalConflict
                                | Some entry when entry.State = UsageFactJournalState.Accepted -> return AlreadyAccepted
                                | Some entry when entry.State = UsageFactJournalState.Pending -> return JournalConflict
                                | Some _ ->
                                    let! inserted = tryInsertRawAsync connection transaction plan.RawFact operationCancellationToken

                                    if inserted then
                                        do! addAggregateAsync connection transaction plan.Aggregate operationCancellationToken

                                    do! resolveRejectionAsync connection transaction plan.RawFact.UsageFactId scope operationCancellationToken
                                    do! markAcceptedAsync connection transaction plan.RawFact.UsageFactId operationCancellationToken
                                    return AcceptedFromJournal
                            })
                        cancellationToken
        }

    /// Marks an existing matching Pending journal row Rejected with its exact-scope evidence in one transaction.
    member _.RejectAsync(fact: UsageFact, rawPayload: byte array, reason: string, cancellationToken: CancellationToken) =
        task {
            match UsageFactPersistencePlan.tryCreateCanonical fact with
            | Error errors -> return raise (InvalidOperationException(String.Join("; ", errors)))
            | Ok plan ->
                return!
                    executeAsync
                        (fun connection transaction operationCancellationToken ->
                            task {
                                let scope = scopeFor plan.RawFact
                                do! acquireScopeAsync connection transaction scope operationCancellationToken
                                let! journal = readJournalForUpdateAsync connection transaction plan.RawFact.UsageFactId operationCancellationToken

                                match journal with
                                | None -> return RejectMissingJournal
                                | Some entry when not (matches plan.RawFact entry) -> return RejectJournalConflict
                                | Some entry when entry.State = UsageFactJournalState.Accepted -> return RejectAlreadyAccepted
                                | Some entry when entry.State = UsageFactJournalState.Rejected -> return AlreadyRejected
                                | Some _ ->
                                    do! recordScopedRejectionAsync connection transaction plan.RawFact.UsageFactId scope reason operationCancellationToken
                                    do! markRejectedAsync connection transaction plan.RawFact.UsageFactId operationCancellationToken
                                    return RejectedFromJournal
                            })
                        cancellationToken
        }

    interface IOperationsUsageJournalStore with

        member this.AppendAsync(fact, cancellationToken) = this.AppendAsync(fact, cancellationToken)
        member this.ListPendingAsync(batchSize, cancellationToken) = this.ListPendingAsync(batchSize, cancellationToken)
        member this.TryGetPendingAsync(usageFactId, cancellationToken) = this.TryGetPendingAsync(usageFactId, cancellationToken)
        member this.ProcessAsync(fact, rawPayload, cancellationToken) = this.ProcessAsync(fact, rawPayload, cancellationToken)
        member this.RejectAsync(fact, rawPayload, reason, cancellationToken) = this.RejectAsync(fact, rawPayload, reason, cancellationToken)
        member this.RepairAsync(fact, cancellationToken) = this.RepairAsync(fact, cancellationToken)
