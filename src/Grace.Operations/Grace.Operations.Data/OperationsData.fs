namespace Grace.Operations.Data

open Grace.Types.Common
open Grace.Types.Usage
open Microsoft.EntityFrameworkCore
open Microsoft.Data.SqlClient
open NodaTime
open System
open System.Data
open System.Threading
open System.Threading.Tasks

/// Identifies the Grace operations data assembly while persistence contracts remain owned by later slices.
[<AbstractClass; Sealed>]
type internal OperationsDataAssembly =
    class
    end

/// Describes a raw immutable usage fact row in `ops.RawUsageFact`.
type RawUsageFact =
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
    }

/// Identifies one UTC minute aggregate row in `ops.UsageAggregateMinute`.
type UsageAggregateMinuteKey =
    {
        FactKind: UsageFactKind
        OwnerId: OwnerId
        OrganizationId: OrganizationId
        RepositoryId: RepositoryId
        StoragePoolId: StoragePoolId
        BucketStart: Instant
    }

/// Describes the aggregate quantity projected for one Grace repository resource and UTC minute.
type UsageAggregateMinute = { Key: UsageAggregateMinuteKey; Quantity: int64 }

/// Carries the raw insert and aggregate update that must commit or roll back together.
type UsageFactPersistencePlan = { RawFact: RawUsageFact; Aggregate: UsageAggregateMinute }

/// Reports whether a usage fact changed storage state or was already present.
type UsageFactPersistenceStatus =
    | Accepted = 1
    | AlreadyProcessed = 2

/// Reports the outcome of transactionally storing a usage fact.
type UsageFactPersistenceResult = { Status: UsageFactPersistenceStatus; UsageFactId: UsageFactId; Aggregate: UsageAggregateMinute option }

/// Tracks whether a raw usage fact payload is still hot in SQL, verified in Blob, or fully archived.
type RawUsageFactArchiveState =
    | Hot = 0
    | ArchiveVerified = 1
    | Archived = 2

/// Describes one raw usage fact that is ready for Blob archive processing or verified cleanup.
type RawUsageFactArchiveCandidate =
    {
        UsageFactId: UsageFactId
        RawPayload: byte array option
        CorrelationId: CorrelationId
        FactKind: UsageFactKind
        OwnerId: OwnerId
        OrganizationId: OrganizationId
        RepositoryId: RepositoryId
        StoragePoolId: StoragePoolId
        Quantity: int64
        ObservedAt: Instant
        ArchiveState: RawUsageFactArchiveState
        ArchiveBlobName: string option
        ArchiveChecksumSha256Hex: string option
        ArchiveByteLength: int64 option
    }

/// Carries the Blob authority that must match before Operations clears a hot SQL payload.
type RawUsageFactArchivePointer = { UsageFactId: UsageFactId; BlobName: string; ChecksumSha256Hex: string; ByteLength: int64 }

/// Limits archive replay or support rehydration to an explicit Grace repository scope.
type RawUsageFactArchiveScope = { OwnerId: OwnerId option; OrganizationId: OrganizationId option; RepositoryId: RepositoryId option }

/// Carries the last archived row seen so replay pages advance through stable SQL ordering.
type RawUsageFactArchiveReplayCursor = { ObservedAt: Instant; UsageFactId: UsageFactId }

/// Describes an archived raw usage fact whose compact SQL index can authorize Blob replay.
type RawUsageFactArchiveReplayItem =
    {
        UsageFactId: UsageFactId
        CorrelationId: CorrelationId
        FactKind: UsageFactKind
        OwnerId: OwnerId
        OrganizationId: OrganizationId
        RepositoryId: RepositoryId
        StoragePoolId: StoragePoolId
        Quantity: int64
        ObservedAt: Instant
        Pointer: RawUsageFactArchivePointer
    }

/// Records the local support-audit evidence for one temporarily rehydrated archived payload.
type RawUsageFactRehydrationAuditEntry =
    {
        UsageFactId: UsageFactId
        Scope: RawUsageFactArchiveScope
        Pointer: RawUsageFactArchivePointer
        RequestedBy: string
        Reason: string
        RehydratedAt: Instant
        ExpiresAt: Instant
        RestoredByteLength: int
        ChangedSqlState: bool
    }

/// Lists and transitions raw usage facts through the hot/cold archive boundary.
type IOperationsUsageArchiveStore =

    /// Returns deterministic archive candidates older than the hot-retention cutoff.
    abstract ListArchiveCandidatesAsync:
        observedBefore: Instant * batchSize: int * cancellationToken: CancellationToken -> Task<RawUsageFactArchiveCandidate list>

    /// Records verified Blob authority while the hot SQL payload is still present.
    abstract MarkArchiveVerifiedAsync: pointer: RawUsageFactArchivePointer * cancellationToken: CancellationToken -> Task<bool>

    /// Clears the hot SQL payload only when the verified SQL pointer still matches the supplied Blob authority.
    abstract CompleteArchiveAsync: pointer: RawUsageFactArchivePointer * cancellationToken: CancellationToken -> Task<bool>

    /// Returns archived rows whose compact SQL pointer is the authority for replay or support rehydration.
    abstract ListArchivedUsageFactsAsync:
        scope: RawUsageFactArchiveScope option * after: RawUsageFactArchiveReplayCursor option * batchSize: int * cancellationToken: CancellationToken ->
            Task<RawUsageFactArchiveReplayItem list>

    /// Temporarily restores archived payload bytes only when the SQL pointer still matches Blob authority.
    abstract RehydrateArchivedPayloadAsync: pointer: RawUsageFactArchivePointer * rawPayload: byte array * cancellationToken: CancellationToken -> Task<bool>

    /// Clears a temporary support rehydration only when the SQL pointer still matches Blob authority.
    abstract CleanupRehydratedPayloadAsync: pointer: RawUsageFactArchivePointer * cancellationToken: CancellationToken -> Task<bool>

/// Chooses whether schema initialization may connect to `master` to create the operations database.
type OperationsUsageSchemaBootstrapMode =

    /// Opens only the configured target database, which supports least-privilege users without `master` access.
    | TargetDatabaseOnly = 1

    /// Opens `master` first to create the configured target database when an admin bootstrap caller opts in.
    | CreateDatabaseIfMissing = 2

/// Describes the SQL connections needed for an operations usage schema initialization pass.
type internal OperationsUsageSchemaBootstrapPlan =
    {
        TargetDatabaseName: string option
        SchemaConnectionString: string
        DatabaseCreationConnectionString: string option
    }

/// Builds schema initialization plans without opening SQL connections.
[<RequireQualifiedAccess>]
module internal OperationsUsageSchemaBootstrapPlan =

    /// Creates a plan that uses the target database by default and connects to `master` only for explicit bootstrap.
    let create connectionString mode =
        let builder = SqlConnectionStringBuilder(connectionString)

        let targetDatabaseName =
            if String.IsNullOrWhiteSpace builder.InitialCatalog then
                None
            else
                Some builder.InitialCatalog

        let databaseCreationConnectionString =
            match mode, targetDatabaseName with
            | OperationsUsageSchemaBootstrapMode.CreateDatabaseIfMissing, Some _ ->
                let masterBuilder = SqlConnectionStringBuilder(connectionString)
                masterBuilder.InitialCatalog <- "master"
                Some masterBuilder.ConnectionString
            | _ -> None

        {
            TargetDatabaseName = targetDatabaseName
            SchemaConnectionString = connectionString
            DatabaseCreationConnectionString = databaseCreationConnectionString
        }

/// Builds deterministic raw fact and aggregate projection plans from the shared usage contract.
[<RequireQualifiedAccess>]
module UsageFactPersistencePlan =

    /// Rounds a fact timestamp to the UTC minute bucket used for operations aggregates.
    let bucketObservedAt (observedAt: Instant) = UsageFact.NormalizeObservedAtToMinute observedAt

    /// Validates a usage fact and converts it into the transaction plan required by operations storage.
    let tryCreate (fact: UsageFact) (rawPayload: byte array) =
        match UsageFact.Validate fact with
        | Error errors -> Error errors
        | Ok () ->
            let errors = ResizeArray<string>()

            if isNull rawPayload || rawPayload.Length = 0 then
                errors.Add("Raw UsageFact payload is required for operations SQL storage.")

            if fact.CorrelationId.Length > OperationsUsageSql.CorrelationIdMaxLength then
                errors.Add($"CorrelationId must be {OperationsUsageSql.CorrelationIdMaxLength} characters or fewer for operations SQL storage.")

            if fact.Resource.StoragePoolId.Length > OperationsUsageSql.StoragePoolIdMaxLength then
                errors.Add($"Resource.StoragePoolId must be {OperationsUsageSql.StoragePoolIdMaxLength} characters or fewer for operations SQL storage.")

            if errors.Count > 0 then
                Error(List.ofSeq errors)
            else
                let bucketStart = bucketObservedAt fact.ObservedAt

                let rawFact =
                    {
                        UsageFactId = fact.UsageFactId
                        RawPayload = Array.copy rawPayload
                        CorrelationId = fact.CorrelationId
                        FactKind = fact.FactKind
                        OwnerId = fact.Scope.OwnerId
                        OrganizationId = fact.Scope.OrganizationId
                        RepositoryId = fact.Scope.RepositoryId
                        StoragePoolId = fact.Resource.StoragePoolId
                        Quantity = fact.Quantity
                        ObservedAt = bucketStart
                    }

                let aggregate =
                    {
                        Key =
                            {
                                FactKind = fact.FactKind
                                OwnerId = fact.Scope.OwnerId
                                OrganizationId = fact.Scope.OrganizationId
                                RepositoryId = fact.Scope.RepositoryId
                                StoragePoolId = fact.Resource.StoragePoolId
                                BucketStart = bucketStart
                            }
                        Quantity = fact.Quantity
                    }

                Ok { RawFact = rawFact; Aggregate = aggregate }

/// Represents the commands available inside one durable operations usage transaction.
type IOperationsUsageTransaction =

    /// Attempts to insert the raw fact, returning `false` when `UsageFactId` already exists.
    abstract TryInsertRawUsageFactAsync: rawFact: RawUsageFact * cancellationToken: CancellationToken -> Task<bool>

    /// Attempts to insert an archived replay fact without restoring hot SQL payload bytes.
    abstract TryInsertReplayedArchivedUsageFactAsync:
        rawFact: RawUsageFact * pointer: RawUsageFactArchivePointer * cancellationToken: CancellationToken -> Task<bool>

    /// Adds the accepted raw fact quantity to the derived minute aggregate.
    abstract AddToUsageAggregateMinuteAsync: aggregate: UsageAggregateMinute * cancellationToken: CancellationToken -> Task

/// Runs operations usage mutations inside one storage transaction boundary.
type IOperationsUsageTransactionScope =

    /// Executes the supplied operation and commits only when it completes successfully.
    abstract ExecuteAsync<'T> : operation: (IOperationsUsageTransaction -> CancellationToken -> Task<'T>) * cancellationToken: CancellationToken -> Task<'T>

/// Executes operations usage SQL commands through an already-open SQL connection and transaction.
type private SqlOperationsUsageTransaction(connection: SqlConnection, transaction: SqlTransaction) =

    /// Adds a SQL parameter and assigns either the supplied value or database null.
    let addParameter (command: SqlCommand) name sqlDbType value =
        let parameter = command.Parameters.Add(name, sqlDbType)
        parameter.Value <- value

    /// Adds a SQL parameter for a Grace string alias value.
    let addStringParameter (command: SqlCommand) name length (value: string) =
        let parameter = command.Parameters.Add(name, SqlDbType.NVarChar, length)
        parameter.Value <- value

    /// Adds a SQL parameter for the exact broker payload bytes stored with a raw fact.
    let addRawPayloadParameter (command: SqlCommand) (value: byte array) =
        let parameter = command.Parameters.Add("@RawPayload", SqlDbType.VarBinary, -1)
        parameter.Value <- Array.copy value

    /// Creates a command that participates in the current operations usage transaction.
    let createCommand commandText =
        let command = connection.CreateCommand()
        command.Transaction <- transaction
        command.CommandType <- CommandType.Text
        command.CommandText <- commandText
        command

    /// Converts a NodaTime instant to the UTC SQL timestamp shape used by operations usage tables.
    let toUtcDateTime (instant: Instant) = instant.ToDateTimeUtc()

    /// Adds the raw usage fact parameters expected by `OperationsUsageSql.TryInsertRawUsageFact`.
    let addRawUsageFactParameters (command: SqlCommand) (rawFact: RawUsageFact) =
        addParameter command "@UsageFactId" SqlDbType.UniqueIdentifier rawFact.UsageFactId
        addRawPayloadParameter command rawFact.RawPayload
        addStringParameter command "@CorrelationId" OperationsUsageSql.CorrelationIdMaxLength rawFact.CorrelationId
        addParameter command "@FactKind" SqlDbType.Int (int rawFact.FactKind)
        addParameter command "@OwnerId" SqlDbType.UniqueIdentifier rawFact.OwnerId
        addParameter command "@OrganizationId" SqlDbType.UniqueIdentifier rawFact.OrganizationId
        addParameter command "@RepositoryId" SqlDbType.UniqueIdentifier rawFact.RepositoryId
        addStringParameter command "@StoragePoolId" OperationsUsageSql.StoragePoolIdMaxLength rawFact.StoragePoolId
        addParameter command "@Quantity" SqlDbType.BigInt rawFact.Quantity
        addParameter command "@ObservedAtUtc" SqlDbType.DateTime2 (toUtcDateTime rawFact.ObservedAt)

    /// Adds raw fact parameters for archive replay inserts that intentionally omit hot payload bytes.
    let addReplayedRawUsageFactParameters (command: SqlCommand) (rawFact: RawUsageFact) =
        addParameter command "@UsageFactId" SqlDbType.UniqueIdentifier rawFact.UsageFactId
        addStringParameter command "@CorrelationId" OperationsUsageSql.CorrelationIdMaxLength rawFact.CorrelationId
        addParameter command "@FactKind" SqlDbType.Int (int rawFact.FactKind)
        addParameter command "@OwnerId" SqlDbType.UniqueIdentifier rawFact.OwnerId
        addParameter command "@OrganizationId" SqlDbType.UniqueIdentifier rawFact.OrganizationId
        addParameter command "@RepositoryId" SqlDbType.UniqueIdentifier rawFact.RepositoryId
        addStringParameter command "@StoragePoolId" OperationsUsageSql.StoragePoolIdMaxLength rawFact.StoragePoolId
        addParameter command "@Quantity" SqlDbType.BigInt rawFact.Quantity
        addParameter command "@ObservedAtUtc" SqlDbType.DateTime2 (toUtcDateTime rawFact.ObservedAt)

    /// Adds archive pointer parameters for replay inserts that keep hot payload bytes out of SQL.
    let addReplayedArchivePointerParameters (command: SqlCommand) (pointer: RawUsageFactArchivePointer) =
        addStringParameter command "@ArchiveBlobName" OperationsUsageSql.ArchiveBlobNameMaxLength pointer.BlobName

        let checksumParameter = command.Parameters.Add("@ArchiveChecksumSha256Hex", SqlDbType.Char, OperationsUsageSql.ArchiveChecksumSha256HexLength)

        checksumParameter.Value <- pointer.ChecksumSha256Hex
        addParameter command "@ArchiveByteLength" SqlDbType.BigInt pointer.ByteLength
        addParameter command "@ArchiveStateArchived" SqlDbType.Int (int RawUsageFactArchiveState.Archived)

    /// Adds the aggregate parameters expected by `OperationsUsageSql.AddToUsageAggregateMinute`.
    let addUsageAggregateMinuteParameters (command: SqlCommand) (aggregate: UsageAggregateMinute) =
        addParameter command "@FactKind" SqlDbType.Int (int aggregate.Key.FactKind)
        addParameter command "@OwnerId" SqlDbType.UniqueIdentifier aggregate.Key.OwnerId
        addParameter command "@OrganizationId" SqlDbType.UniqueIdentifier aggregate.Key.OrganizationId
        addParameter command "@RepositoryId" SqlDbType.UniqueIdentifier aggregate.Key.RepositoryId
        addStringParameter command "@StoragePoolId" OperationsUsageSql.StoragePoolIdMaxLength aggregate.Key.StoragePoolId
        addParameter command "@BucketStartUtc" SqlDbType.DateTime2 (toUtcDateTime aggregate.Key.BucketStart)
        addParameter command "@Quantity" SqlDbType.BigInt aggregate.Quantity

    interface IOperationsUsageTransaction with

        member _.TryInsertRawUsageFactAsync(rawFact, cancellationToken) =
            task {
                use command = createCommand OperationsUsageSql.TryInsertRawUsageFact
                addRawUsageFactParameters command rawFact
                let! rowsAffected = command.ExecuteNonQueryAsync cancellationToken
                return rowsAffected = 1
            }

        member _.TryInsertReplayedArchivedUsageFactAsync(rawFact, pointer, cancellationToken) =
            task {
                use command = createCommand OperationsUsageSql.TryInsertReplayedArchivedRawUsageFact
                addReplayedRawUsageFactParameters command rawFact
                addReplayedArchivePointerParameters command pointer
                let! rowsAffected = command.ExecuteNonQueryAsync cancellationToken
                return rowsAffected = 1
            }

        member _.AddToUsageAggregateMinuteAsync(aggregate, cancellationToken) =
            task {
                use command = createCommand OperationsUsageSql.AddToUsageAggregateMinute
                addUsageAggregateMinuteParameters command aggregate
                let! _ = command.ExecuteNonQueryAsync cancellationToken
                return ()
            }

/// Runs operations usage mutations inside a concrete Azure SQL transaction boundary.
type SqlOperationsUsageTransactionScope(connectionString: string) =

    /// Opens a SQL connection for one operations usage transaction.
    let openConnectionAsync cancellationToken =
        task {
            let connection = new SqlConnection(connectionString)
            do! connection.OpenAsync cancellationToken
            return connection
        }

    /// Rolls back a failed transaction while preserving the original failure when rollback also fails.
    let rollbackIgnoringFailuresAsync (transaction: SqlTransaction) =
        task {
            try
                do! transaction.RollbackAsync CancellationToken.None
            with
            | _ -> ()
        }

    interface IOperationsUsageTransactionScope with

        member _.ExecuteAsync(operation, cancellationToken) =
            task {
                use! connection = openConnectionAsync cancellationToken
                let! dbTransaction = connection.BeginTransactionAsync cancellationToken
                use transaction = dbTransaction :?> SqlTransaction
                let operationsTransaction = SqlOperationsUsageTransaction(connection, transaction)

                try
                    let! result = operation operationsTransaction cancellationToken
                    do! transaction.CommitAsync cancellationToken
                    return result
                with
                | ex ->
                    do! rollbackIgnoringFailuresAsync transaction
                    return raise ex
            }

/// Lists and updates archive state through reviewed SQL commands that preserve Blob authority ordering.
type SqlOperationsUsageArchiveStore(connectionString: string) =

    /// Opens a SQL connection for archive queries and state transitions.
    let openConnectionAsync cancellationToken =
        task {
            let connection = new SqlConnection(connectionString)
            do! connection.OpenAsync cancellationToken
            return connection
        }

    /// Converts a NodaTime instant to the UTC SQL timestamp shape used by operations usage tables.
    let toUtcDateTime (instant: Instant) = instant.ToDateTimeUtc()

    /// Converts a UTC SQL timestamp into the instant used by archive layout decisions.
    let toInstant (dateTime: DateTime) =
        let utc =
            if dateTime.Kind = DateTimeKind.Utc then
                dateTime
            else
                DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)

        Instant.FromDateTimeUtc utc

    /// Adds a SQL parameter and assigns either the supplied value or database null.
    let addParameter (command: SqlCommand) name sqlDbType value =
        let parameter = command.Parameters.Add(name, sqlDbType)
        parameter.Value <- value

    /// Adds a string SQL parameter with a fixed column length.
    let addStringParameter (command: SqlCommand) name length (value: string) =
        let parameter = command.Parameters.Add(name, SqlDbType.NVarChar, length)
        parameter.Value <- value

    /// Adds archive-state parameters shared by archive SQL commands.
    let addArchiveStateParameters (command: SqlCommand) =
        addParameter command "@ArchiveStateHot" SqlDbType.Int (int RawUsageFactArchiveState.Hot)
        addParameter command "@ArchiveStateVerified" SqlDbType.Int (int RawUsageFactArchiveState.ArchiveVerified)
        addParameter command "@ArchiveStateArchived" SqlDbType.Int (int RawUsageFactArchiveState.Archived)

    /// Adds Blob pointer parameters that guard archive idempotency and hot-payload cleanup.
    let addArchivePointerParameters (command: SqlCommand) (pointer: RawUsageFactArchivePointer) =
        addParameter command "@UsageFactId" SqlDbType.UniqueIdentifier pointer.UsageFactId
        addStringParameter command "@ArchiveBlobName" OperationsUsageSql.ArchiveBlobNameMaxLength pointer.BlobName

        let checksumParameter = command.Parameters.Add("@ArchiveChecksumSha256Hex", SqlDbType.Char, OperationsUsageSql.ArchiveChecksumSha256HexLength)

        checksumParameter.Value <- pointer.ChecksumSha256Hex
        addParameter command "@ArchiveByteLength" SqlDbType.BigInt pointer.ByteLength

    /// Adds optional scope parameters that constrain replay and support rehydration scans.
    let addArchiveScopeParameters (command: SqlCommand) (scope: RawUsageFactArchiveScope option) =
        let addOptionalGuidParameter name value =
            let parameter = command.Parameters.Add(name, SqlDbType.UniqueIdentifier)

            parameter.Value <-
                match value with
                | Some id -> box id
                | None -> DBNull.Value

        let ownerId = scope |> Option.bind (fun value -> value.OwnerId)

        let organizationId =
            scope
            |> Option.bind (fun value -> value.OrganizationId)

        let repositoryId =
            scope
            |> Option.bind (fun value -> value.RepositoryId)

        addOptionalGuidParameter "@OwnerId" ownerId
        addOptionalGuidParameter "@OrganizationId" organizationId
        addOptionalGuidParameter "@RepositoryId" repositoryId

    /// Executes an archive transition command and returns whether this call changed SQL state.
    let executeArchiveTransitionAsync commandText pointer cancellationToken =
        task {
            use! connection = openConnectionAsync cancellationToken
            use command = connection.CreateCommand()
            command.CommandType <- CommandType.Text
            command.CommandText <- commandText
            addArchiveStateParameters command
            addArchivePointerParameters command pointer

            let! scalar = command.ExecuteScalarAsync cancellationToken

            return
                match scalar with
                | :? int as value -> value = 1
                | :? int64 as value -> value = 1L
                | :? decimal as value -> value = 1M
                | _ -> false
        }

    /// Executes a single-row archive payload update guarded by the exact Blob pointer.
    let executeArchivePayloadTransitionAsync commandText pointer rawPayload cancellationToken =
        task {
            use! connection = openConnectionAsync cancellationToken
            use command = connection.CreateCommand()
            command.CommandType <- CommandType.Text
            command.CommandText <- commandText
            addArchiveStateParameters command
            addArchivePointerParameters command pointer

            match rawPayload with
            | Some payload ->
                let parameter = command.Parameters.Add("@RawPayload", SqlDbType.VarBinary, -1)
                parameter.Value <- Array.copy payload
            | None -> ()

            let! scalar = command.ExecuteScalarAsync cancellationToken

            return
                match scalar with
                | :? int as value -> value = 1
                | :? int64 as value -> value = 1L
                | :? decimal as value -> value = 1M
                | _ -> false
        }

    interface IOperationsUsageArchiveStore with

        member _.ListArchiveCandidatesAsync(observedBefore, batchSize, cancellationToken) =
            task {
                if batchSize <= 0 then
                    invalidArg (nameof batchSize) "Archive batch size must be greater than zero."

                use! connection = openConnectionAsync cancellationToken
                use command = connection.CreateCommand()
                command.CommandType <- CommandType.Text
                command.CommandText <- OperationsUsageSql.SelectRawUsageFactsForArchive
                addParameter command "@BatchSize" SqlDbType.Int batchSize
                addParameter command "@ObservedBeforeUtc" SqlDbType.DateTime2 (toUtcDateTime observedBefore)
                addArchiveStateParameters command

                use! reader = command.ExecuteReaderAsync cancellationToken
                let candidates = ResizeArray<RawUsageFactArchiveCandidate>()

                let usageFactIdOrdinal = reader.GetOrdinal("UsageFactId")
                let rawPayloadOrdinal = reader.GetOrdinal("RawPayload")
                let correlationIdOrdinal = reader.GetOrdinal("CorrelationId")
                let factKindOrdinal = reader.GetOrdinal("FactKind")
                let ownerIdOrdinal = reader.GetOrdinal("OwnerId")
                let organizationIdOrdinal = reader.GetOrdinal("OrganizationId")
                let repositoryIdOrdinal = reader.GetOrdinal("RepositoryId")
                let storagePoolIdOrdinal = reader.GetOrdinal("StoragePoolId")
                let quantityOrdinal = reader.GetOrdinal("Quantity")
                let observedAtOrdinal = reader.GetOrdinal("ObservedAtUtc")
                let archiveStateOrdinal = reader.GetOrdinal("ArchiveState")
                let archiveBlobNameOrdinal = reader.GetOrdinal("ArchiveBlobName")
                let archiveChecksumOrdinal = reader.GetOrdinal("ArchiveChecksumSha256Hex")
                let archiveByteLengthOrdinal = reader.GetOrdinal("ArchiveByteLength")

                let mutable reading = true

                while reading do
                    let! hasRow = reader.ReadAsync cancellationToken

                    if hasRow then
                        let rawPayload =
                            if reader.IsDBNull rawPayloadOrdinal then
                                None
                            else
                                Some(
                                    reader.GetFieldValue<byte array>(rawPayloadOrdinal)
                                    |> Array.copy
                                )

                        let readOptionalString ordinal = if reader.IsDBNull ordinal then None else Some(reader.GetString ordinal)

                        let archiveByteLength =
                            if reader.IsDBNull archiveByteLengthOrdinal then
                                None
                            else
                                Some(reader.GetInt64 archiveByteLengthOrdinal)

                        candidates.Add
                            {
                                UsageFactId = reader.GetGuid usageFactIdOrdinal
                                RawPayload = rawPayload
                                CorrelationId = CorrelationId(reader.GetString correlationIdOrdinal)
                                FactKind = enum<UsageFactKind> (reader.GetInt32 factKindOrdinal)
                                OwnerId = reader.GetGuid ownerIdOrdinal
                                OrganizationId = reader.GetGuid organizationIdOrdinal
                                RepositoryId = reader.GetGuid repositoryIdOrdinal
                                StoragePoolId = StoragePoolId(reader.GetString storagePoolIdOrdinal)
                                Quantity = reader.GetInt64 quantityOrdinal
                                ObservedAt = toInstant (reader.GetDateTime observedAtOrdinal)
                                ArchiveState = enum<RawUsageFactArchiveState> (reader.GetInt32 archiveStateOrdinal)
                                ArchiveBlobName = readOptionalString archiveBlobNameOrdinal
                                ArchiveChecksumSha256Hex = readOptionalString archiveChecksumOrdinal
                                ArchiveByteLength = archiveByteLength
                            }
                    else
                        reading <- false

                return candidates |> Seq.toList
            }

        member _.MarkArchiveVerifiedAsync(pointer, cancellationToken) =
            executeArchiveTransitionAsync OperationsUsageSql.MarkRawUsageFactArchiveVerified pointer cancellationToken

        member _.CompleteArchiveAsync(pointer, cancellationToken) =
            executeArchiveTransitionAsync OperationsUsageSql.CompleteRawUsageFactArchive pointer cancellationToken

        member _.ListArchivedUsageFactsAsync(scope, after, batchSize, cancellationToken) =
            task {
                if batchSize <= 0 then
                    invalidArg (nameof batchSize) "Archive replay batch size must be greater than zero."

                use! connection = openConnectionAsync cancellationToken
                use command = connection.CreateCommand()
                command.CommandType <- CommandType.Text
                command.CommandText <- OperationsUsageSql.SelectArchivedRawUsageFactsForReplay
                addParameter command "@BatchSize" SqlDbType.Int batchSize
                addArchiveStateParameters command
                addArchiveScopeParameters command scope

                match after with
                | Some cursor ->
                    addParameter command "@AfterObservedAtUtc" SqlDbType.DateTime2 (toUtcDateTime cursor.ObservedAt)
                    addParameter command "@AfterUsageFactId" SqlDbType.UniqueIdentifier cursor.UsageFactId
                | None ->
                    addParameter command "@AfterObservedAtUtc" SqlDbType.DateTime2 DBNull.Value
                    addParameter command "@AfterUsageFactId" SqlDbType.UniqueIdentifier DBNull.Value

                use! reader = command.ExecuteReaderAsync cancellationToken
                let items = ResizeArray<RawUsageFactArchiveReplayItem>()

                let usageFactIdOrdinal = reader.GetOrdinal("UsageFactId")
                let correlationIdOrdinal = reader.GetOrdinal("CorrelationId")
                let factKindOrdinal = reader.GetOrdinal("FactKind")
                let ownerIdOrdinal = reader.GetOrdinal("OwnerId")
                let organizationIdOrdinal = reader.GetOrdinal("OrganizationId")
                let repositoryIdOrdinal = reader.GetOrdinal("RepositoryId")
                let storagePoolIdOrdinal = reader.GetOrdinal("StoragePoolId")
                let quantityOrdinal = reader.GetOrdinal("Quantity")
                let observedAtOrdinal = reader.GetOrdinal("ObservedAtUtc")
                let archiveBlobNameOrdinal = reader.GetOrdinal("ArchiveBlobName")
                let archiveChecksumOrdinal = reader.GetOrdinal("ArchiveChecksumSha256Hex")
                let archiveByteLengthOrdinal = reader.GetOrdinal("ArchiveByteLength")
                let mutable reading = true

                while reading do
                    let! hasRow = reader.ReadAsync cancellationToken

                    if hasRow then
                        let usageFactId = reader.GetGuid usageFactIdOrdinal

                        let pointer =
                            {
                                UsageFactId = usageFactId
                                BlobName = reader.GetString archiveBlobNameOrdinal
                                ChecksumSha256Hex = reader.GetString archiveChecksumOrdinal
                                ByteLength = reader.GetInt64 archiveByteLengthOrdinal
                            }

                        items.Add
                            {
                                UsageFactId = usageFactId
                                CorrelationId = CorrelationId(reader.GetString correlationIdOrdinal)
                                FactKind = enum<UsageFactKind> (reader.GetInt32 factKindOrdinal)
                                OwnerId = reader.GetGuid ownerIdOrdinal
                                OrganizationId = reader.GetGuid organizationIdOrdinal
                                RepositoryId = reader.GetGuid repositoryIdOrdinal
                                StoragePoolId = StoragePoolId(reader.GetString storagePoolIdOrdinal)
                                Quantity = reader.GetInt64 quantityOrdinal
                                ObservedAt = toInstant (reader.GetDateTime observedAtOrdinal)
                                Pointer = pointer
                            }
                    else
                        reading <- false

                return items |> Seq.toList
            }

        member _.RehydrateArchivedPayloadAsync(pointer, rawPayload, cancellationToken) =
            if isNull rawPayload || rawPayload.Length = 0 then
                invalidArg (nameof rawPayload) "Rehydrated raw payload bytes are required."

            executeArchivePayloadTransitionAsync OperationsUsageSql.RehydrateArchivedRawUsageFactPayload pointer (Some rawPayload) cancellationToken

        member _.CleanupRehydratedPayloadAsync(pointer, cancellationToken) =
            executeArchivePayloadTransitionAsync OperationsUsageSql.CleanupRehydratedRawUsageFactPayload pointer None cancellationToken

/// Ensures the operations usage SQL schema exists before ingestion starts consuming durable messages.
type OperationsUsageSchema(connectionString: string, ?bootstrapMode: OperationsUsageSchemaBootstrapMode) =

    /// Describes whether this schema pass is target-only or explicit admin bootstrap.
    let bootstrapMode = defaultArg bootstrapMode OperationsUsageSchemaBootstrapMode.TargetDatabaseOnly

    /// Captures the connection strings used by this schema pass without opening SQL connections.
    let bootstrapPlan = OperationsUsageSchemaBootstrapPlan.create connectionString bootstrapMode

    /// Opens the SQL connection used for one schema initialization pass.
    let openConnectionAsync databaseConnectionString cancellationToken =
        task {
            let connection = new SqlConnection(databaseConnectionString)
            do! connection.OpenAsync cancellationToken
            return connection
        }

    /// Creates the configured operations database when the connection string points at a database that is absent.
    let ensureDatabaseCreatedAsync cancellationToken =
        task {
            match bootstrapPlan.DatabaseCreationConnectionString, bootstrapPlan.TargetDatabaseName with
            | Some databaseCreationConnectionString, Some databaseName ->
                use! connection = openConnectionAsync databaseCreationConnectionString cancellationToken
                use command = connection.CreateCommand()
                command.CommandType <- CommandType.Text
                command.CommandText <- OperationsUsageSql.CreateDatabaseIfMissing

                let parameter = command.Parameters.Add("@DatabaseName", SqlDbType.NVarChar, 128)
                parameter.Value <- databaseName
                let! _ = command.ExecuteNonQueryAsync cancellationToken
                return ()
            | _ -> return ()
        }

    /// Opens the target database and creates the operations schema before EF migrations touch history state.
    let ensureSchemaCreatedAsync cancellationToken =
        task {
            use! connection = openConnectionAsync bootstrapPlan.SchemaConnectionString cancellationToken
            use command = connection.CreateCommand()
            command.CommandType <- CommandType.Text
            command.CommandText <- OperationsUsageSql.CreateSchemaIfMissing
            let! _ = command.ExecuteNonQueryAsync cancellationToken
            return ()
        }

    /// Applies reviewed EF Core migrations for the operations usage schema.
    member _.EnsureCreatedAsync(cancellationToken: CancellationToken) =
        task {
            do! ensureDatabaseCreatedAsync cancellationToken
            do! ensureSchemaCreatedAsync cancellationToken
            use context = OperationsDbContextFactory.create bootstrapPlan.SchemaConnectionString
            do! context.Database.MigrateAsync cancellationToken
        }

/// Persists usage facts through a transaction-scoped raw insert and aggregate projection.
type OperationsUsageStore(transactionScope: IOperationsUsageTransactionScope) =

    /// Stores a usage fact exactly once by durable `UsageFactId` and projects aggregates only for newly accepted facts.
    member _.StoreUsageFactAsync(fact: UsageFact, rawPayload: byte array, cancellationToken: CancellationToken) =
        task {
            match UsageFactPersistencePlan.tryCreate fact rawPayload with
            | Error errors -> return Error errors
            | Ok plan ->
                let operation (transaction: IOperationsUsageTransaction) (operationCancellationToken: CancellationToken) =
                    task {
                        let! accepted = transaction.TryInsertRawUsageFactAsync(plan.RawFact, operationCancellationToken)

                        if accepted then
                            do! transaction.AddToUsageAggregateMinuteAsync(plan.Aggregate, operationCancellationToken)

                            return { Status = UsageFactPersistenceStatus.Accepted; UsageFactId = plan.RawFact.UsageFactId; Aggregate = Some plan.Aggregate }
                        else
                            return { Status = UsageFactPersistenceStatus.AlreadyProcessed; UsageFactId = plan.RawFact.UsageFactId; Aggregate = None }
                    }

                let! result = transactionScope.ExecuteAsync(operation, cancellationToken)
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
                    let operation (transaction: IOperationsUsageTransaction) (operationCancellationToken: CancellationToken) =
                        task {
                            let! accepted = transaction.TryInsertReplayedArchivedUsageFactAsync(plan.RawFact, pointer, operationCancellationToken)

                            if accepted then
                                do! transaction.AddToUsageAggregateMinuteAsync(plan.Aggregate, operationCancellationToken)

                                return { Status = UsageFactPersistenceStatus.Accepted; UsageFactId = plan.RawFact.UsageFactId; Aggregate = Some plan.Aggregate }
                            else
                                return { Status = UsageFactPersistenceStatus.AlreadyProcessed; UsageFactId = plan.RawFact.UsageFactId; Aggregate = None }
                        }

                    let! result = transactionScope.ExecuteAsync(operation, cancellationToken)
                    return Ok result
        }
