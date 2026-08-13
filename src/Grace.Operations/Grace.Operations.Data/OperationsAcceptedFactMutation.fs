namespace Grace.Operations.Data

open Microsoft.Data.SqlClient
open NodaTime
open System
open System.Data
open System.Threading
open System.Threading.Tasks

/// Observes the single point after all accepted-fact SQL mutations have staged and before control returns to a caller.
type internal IAcceptedFactMutationInterleaving =
    abstract AfterAcceptedFactMutationsStagedAsync: AcceptedFactMutationOutcome * CancellationToken -> Task

/// Supplies the inert production implementation of the narrow accepted-fact test interleaving.
type private NoAcceptedFactMutationInterleaving() =
    interface IAcceptedFactMutationInterleaving with
        member _.AfterAcceptedFactMutationsStagedAsync(_, _) = Task.CompletedTask

/// Stages one accepted fact inside a caller-owned SQL transaction without committing, rolling back, or changing journal state.
type internal SqlAcceptedFactMutation(interleaving: IAcceptedFactMutationInterleaving) =

    let interleaving =
        if isNull (box interleaving) then
            NoAcceptedFactMutationInterleaving() :> IAcceptedFactMutationInterleaving
        else
            interleaving

    /// Converts the Grace instant representation to the exact SQL timestamp shape used by Operations persistence.
    let toUtcDateTime (instant: Instant) = instant.ToDateTimeUtc()

    /// Creates a command that participates in the caller's existing connection and transaction.
    let createCommand (connection: SqlConnection) (transaction: SqlTransaction) commandText =
        let command = connection.CreateCommand()
        command.Transaction <- transaction
        command.CommandType <- CommandType.Text
        command.CommandText <- commandText
        command

    /// Adds one scalar SQL parameter without provider-default inference.
    let addParameter (command: SqlCommand) name sqlDbType value =
        let parameter = command.Parameters.Add(name, sqlDbType)
        parameter.Value <- value

    /// Adds a bounded Grace string alias parameter.
    let addStringParameter (command: SqlCommand) name length (value: string) =
        let parameter = command.Parameters.Add(name, SqlDbType.NVarChar, length)
        parameter.Value <- value

    /// Adds immutable raw-fact fields expected by the accepted-fact insert command.
    let addRawFactParameters (command: SqlCommand) (rawFact: RawUsageFact) =
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

    /// Adds immutable raw-fact fields for archive replay, which deliberately keeps the hot SQL payload empty.
    let addArchivedReplayRawFactParameters (command: SqlCommand) (rawFact: RawUsageFact) =
        addParameter command "@UsageFactId" SqlDbType.UniqueIdentifier rawFact.UsageFactId
        addStringParameter command "@CorrelationId" OperationsUsageSql.CorrelationIdMaxLength rawFact.CorrelationId
        addParameter command "@FactKind" SqlDbType.Int (int rawFact.FactKind)
        addParameter command "@OwnerId" SqlDbType.UniqueIdentifier rawFact.OwnerId
        addParameter command "@OrganizationId" SqlDbType.UniqueIdentifier rawFact.OrganizationId
        addParameter command "@RepositoryId" SqlDbType.UniqueIdentifier rawFact.RepositoryId
        addStringParameter command "@StoragePoolId" OperationsUsageSql.StoragePoolIdMaxLength rawFact.StoragePoolId
        addParameter command "@Quantity" SqlDbType.BigInt rawFact.Quantity
        addParameter command "@ObservedAtUtc" SqlDbType.DateTime2 (toUtcDateTime rawFact.ObservedAt)

    /// Adds verified archive-pointer fields required by the replay-only raw fact command.
    let addArchivedReplayPointerParameters (command: SqlCommand) (pointer: RawUsageFactArchivePointer) =
        addStringParameter command "@ArchiveBlobName" OperationsUsageSql.ArchiveBlobNameMaxLength pointer.BlobName
        let checksum = command.Parameters.Add("@ArchiveChecksumSha256Hex", SqlDbType.Char, OperationsUsageSql.ArchiveChecksumSha256HexLength)
        checksum.Value <- pointer.ChecksumSha256Hex
        addParameter command "@ArchiveByteLength" SqlDbType.BigInt pointer.ByteLength
        addParameter command "@ArchiveStateArchived" SqlDbType.Int (int RawUsageFactArchiveState.Archived)

    /// Adds exact owner, organization, repository, and UTC-month fields shared by identity and late-work staging.
    let addScopeParameters (command: SqlCommand) (scope: BillingCompletenessScope) =
        addParameter command "@OwnerId" SqlDbType.UniqueIdentifier scope.OwnerId
        addParameter command "@OrganizationId" SqlDbType.UniqueIdentifier scope.OrganizationId
        addParameter command "@RepositoryId" SqlDbType.UniqueIdentifier scope.RepositoryId
        addParameter command "@MonthStartUtc" SqlDbType.DateTime2 (toUtcDateTime scope.MonthStart)
        addParameter command "@NextMonthStartUtc" SqlDbType.DateTime2 (toUtcDateTime (BillingCompletenessScope.nextMonthStart scope))

    /// Rejects a previously bound raw identity before both idempotent and new-fact paths can continue.
    let ensureRawIdentityAsync connection transaction usageFactId scope cancellationToken =
        task {
            use command = createCommand connection transaction OperationsUsageSql.EnsureUsageFactIdMatchesBillingCompletenessScope
            addParameter command "@UsageFactId" SqlDbType.UniqueIdentifier usageFactId
            addScopeParameters command scope
            let! _ = command.ExecuteNonQueryAsync cancellationToken
            return ()
        }

    /// Inserts raw fact truth only when its durable identity has not already been accepted.
    let tryInsertRawFactAsync connection transaction rawFact rawInsertion cancellationToken =
        task {
            use command =
                match rawInsertion with
                | HotRawFact -> createCommand connection transaction OperationsUsageSql.TryInsertRawUsageFact
                | ArchivedReplayRawFact _ -> createCommand connection transaction OperationsUsageSql.TryInsertReplayedArchivedRawUsageFact

            match rawInsertion with
            | HotRawFact -> addRawFactParameters command rawFact
            | ArchivedReplayRawFact pointer ->
                addArchivedReplayRawFactParameters command rawFact
                addArchivedReplayPointerParameters command pointer

            let! rowsAffected = command.ExecuteNonQueryAsync cancellationToken
            return rowsAffected = 1
        }

    /// Repairs matching scoped rejection truth after the new raw fact is present in this transaction.
    let repairScopedRejectionAsync connection transaction usageFactId scope cancellationToken =
        task {
            use command = createCommand connection transaction OperationsUsageSql.ResolveScopedUsageFactRejection
            addParameter command "@UsageFactId" SqlDbType.UniqueIdentifier usageFactId
            addScopeParameters command scope
            let! _ = command.ExecuteNonQueryAsync cancellationToken
            return ()
        }

    /// Projects one newly accepted quantity into its exact UTC-minute aggregate row.
    let addAggregateAsync connection transaction (aggregate: UsageAggregateMinute) cancellationToken =
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

    /// Stages one Pending handoff only when the exact period is already closed.
    let stageLateWorkAsync connection transaction usageFactId scope cancellationToken =
        task {
            use command = createCommand connection transaction OperationsUsageSql.StageClosedPeriodLateWork
            addParameter command "@UsageFactId" SqlDbType.UniqueIdentifier usageFactId
            addScopeParameters command scope
            let! result = command.ExecuteScalarAsync cancellationToken
            return Convert.ToBoolean result
        }

    /// Verifies that the caller supplied the open connection and matching active transaction needed by this primitive.
    let validateCallerTransaction (connection: SqlConnection) (transaction: SqlTransaction) =
        if isNull connection then nullArg (nameof connection)
        if isNull transaction then nullArg (nameof transaction)

        if connection.State <> ConnectionState.Open then
            invalidArg (nameof connection) "Accepted-fact mutation requires an open SQL connection."

        if not (Object.ReferenceEquals(transaction.Connection, connection)) then
            invalidArg (nameof transaction) "Accepted-fact mutation requires a transaction owned by the supplied connection."

    /// Rejects a caller scope that does not exactly match the immutable identity carried by the raw fact plan.
    let validateRawFactScope (rawFact: RawUsageFact) (scope: BillingCompletenessScope) =
        match BillingCompletenessScope.tryCreate rawFact.OwnerId rawFact.OrganizationId rawFact.RepositoryId rawFact.ObservedAt with
        | Error errors -> invalidArg (nameof rawFact) (String.Join("; ", errors))
        | Ok rawFactScope when rawFactScope = scope -> ()
        | Ok _ -> invalidArg (nameof scope) "Accepted-fact mutation scope must match the raw fact identity and UTC month."

    /// Rejects an aggregate whose key, minute bucket, or quantity diverges from the immutable raw fact being accepted.
    let validateAggregateMatchesRawFact (rawFact: RawUsageFact) (aggregate: UsageAggregateMinute) =
        let bucketStart = UsageFactPersistencePlan.bucketObservedAt rawFact.ObservedAt

        let matchesRawFact =
            aggregate.Key.FactKind = rawFact.FactKind
            && aggregate.Key.OwnerId = rawFact.OwnerId
            && aggregate.Key.OrganizationId = rawFact.OrganizationId
            && aggregate.Key.RepositoryId = rawFact.RepositoryId
            && aggregate.Key.StoragePoolId = rawFact.StoragePoolId
            && aggregate.Key.BucketStart = bucketStart
            && aggregate.Quantity = rawFact.Quantity

        if not matchesRawFact then
            invalidArg (nameof aggregate) "Accepted-fact mutation aggregate must exactly match the raw fact identity, UTC-minute bucket, and quantity."

    /// Rejects an archive replay adapter whose pointer cannot preserve the immutable raw fact identity.
    let validateRawInsertion (rawFact: RawUsageFact) rawInsertion =
        match rawInsertion with
        | HotRawFact -> ()
        | ArchivedReplayRawFact pointer when pointer.UsageFactId = rawFact.UsageFactId -> ()
        | ArchivedReplayRawFact pointer ->
            invalidArg
                (nameof rawInsertion)
                $"Archive replay pointer UsageFactId '{pointer.UsageFactId}' does not match raw UsageFactId '{rawFact.UsageFactId}'."

    /// Stages raw fact, rejection repair, aggregate, and conditional Pending handoff using only the caller-owned transaction.
    member _.AcceptAsync
        (
            connection: SqlConnection,
            transaction: SqlTransaction,
            plan: UsageFactPersistencePlan,
            scope: BillingCompletenessScope,
            rawInsertion: AcceptedFactRawInsertion,
            cancellationToken: CancellationToken
        ) =
        task {
            validateCallerTransaction connection transaction

            match BillingCompletenessScope.validate scope with
            | Error errors -> invalidArg (nameof scope) (String.Join("; ", errors))
            | Ok _ -> ()

            validateRawFactScope plan.RawFact scope
            validateAggregateMatchesRawFact plan.RawFact plan.Aggregate
            validateRawInsertion plan.RawFact rawInsertion

            do! ensureRawIdentityAsync connection transaction plan.RawFact.UsageFactId scope cancellationToken
            let! inserted = tryInsertRawFactAsync connection transaction plan.RawFact rawInsertion cancellationToken

            if not inserted then
                do! ensureRawIdentityAsync connection transaction plan.RawFact.UsageFactId scope cancellationToken
                return ExistingSameScope
            else
                do! repairScopedRejectionAsync connection transaction plan.RawFact.UsageFactId scope cancellationToken
                do! addAggregateAsync connection transaction plan.Aggregate cancellationToken
                let! isClosedPeriod = stageLateWorkAsync connection transaction plan.RawFact.UsageFactId scope cancellationToken
                let outcome = if isClosedPeriod then InsertedIntoClosedPeriod else InsertedIntoOpenPeriod
                do! interleaving.AfterAcceptedFactMutationsStagedAsync(outcome, cancellationToken)
                return outcome
        }

    /// Stages the ordinary hot-payload accepted fact shape retained by the #937 primitive contract.
    member this.AcceptAsync
        (
            connection: SqlConnection,
            transaction: SqlTransaction,
            plan: UsageFactPersistencePlan,
            scope: BillingCompletenessScope,
            cancellationToken: CancellationToken
        ) =
        this.AcceptAsync(connection, transaction, plan, scope, HotRawFact, cancellationToken)
