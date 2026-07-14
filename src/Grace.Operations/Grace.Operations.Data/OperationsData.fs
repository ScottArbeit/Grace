namespace Grace.Operations.Data

open Grace.Types.Common
open Grace.Types.Usage
open Microsoft.EntityFrameworkCore
open Microsoft.Data.SqlClient
open NodaTime
open System
open System.Data
open System.Runtime.ExceptionServices
open System.Security.Cryptography
open System.Text
open System.Text.Json
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

/// Carries one owner, repository scope, fact kind, and timestamp for effective pricing lookup.
type PricingRateSelectionQuery = { OwnerId: OwnerId; OrganizationId: OrganizationId; RepositoryId: RepositoryId; FactKind: UsageFactKind; ObservedAt: Instant }

/// Describes the effective owner rate selected for a tracked usage fact.
type EffectivePricingRate =
    {
        PricingAssignmentId: Guid
        OwnerId: OwnerId
        OrganizationId: OrganizationId
        RepositoryId: RepositoryId
        PricingPlanId: Guid
        PlanCode: string
        BillableUsageKindMappingId: Guid
        BillableUsageKind: int
        PricingRateId: Guid
        CurrencyCode: string
        UnitName: string
        UnitQuantity: int64
        UnitPriceMicros: int64
        EffectiveFrom: Instant
        EffectiveTo: Instant option
    }

/// Describes an effective-dated pricing plan used by deterministic selection tests and seed validation.
type PricingPlan = { PricingPlanId: Guid; PlanCode: string; EffectiveFrom: Instant; EffectiveTo: Instant option }

/// Describes an effective-dated mapping from tracked usage to an internal billable usage kind.
type BillableUsageKindMapping =
    {
        BillableUsageKindMappingId: Guid
        FactKind: UsageFactKind
        BillableUsageKind: int
        EffectiveFrom: Instant
        EffectiveTo: Instant option
    }

/// Describes an effective-dated rate for one plan and billable usage kind.
type PricingRate =
    {
        PricingRateId: Guid
        PricingPlanId: Guid
        BillableUsageKind: int
        CurrencyCode: string
        UnitName: string
        UnitQuantity: int64
        UnitPriceMicros: int64
        EffectiveFrom: Instant
        EffectiveTo: Instant option
    }

/// Describes an effective-dated owner assignment to one pricing plan for a Grace repository scope.
type PricingAssignment =
    {
        PricingAssignmentId: Guid
        OwnerId: OwnerId
        OrganizationId: OrganizationId
        RepositoryId: RepositoryId
        PricingPlanId: Guid
        EffectiveFrom: Instant
        EffectiveTo: Instant option
    }

/// Carries the in-memory pricing rows used to prove the same invariant enforced by Operations SQL.
type PricingCatalogSnapshot =
    {
        PricingPlans: PricingPlan list
        BillableUsageKindMappings: BillableUsageKindMapping list
        PricingRates: PricingRate list
        PricingAssignments: PricingAssignment list
    }

/// Selects owner rates only when every effective-dated pricing prerequisite is present.
[<RequireQualifiedAccess>]
module PricingRateSelection =

    /// Returns true when a half-open effective interval contains the supplied timestamp.
    let contains observedAt effectiveFrom effectiveTo =
        effectiveFrom <= observedAt
        && (effectiveTo
            |> Option.forall (fun ending -> observedAt < ending))

    /// Selects the latest effective row by start time and stable identity.
    let private latestBy effectiveFrom identity rows =
        rows
        |> List.sortWith (fun left right ->
            let fromComparison = compare (effectiveFrom right) (effectiveFrom left)

            if fromComparison <> 0 then
                fromComparison
            else
                compare (identity left) (identity right))
        |> List.tryHead

    /// Intersects selected pricing prerequisites into the complete owner-price applicability window.
    let private intersectEffectiveWindows windows =
        let effectiveFrom = windows |> List.map fst |> List.max

        let effectiveTo =
            match windows |> List.choose snd with
            | [] -> None
            | finiteEnds -> finiteEnds |> List.min |> Some

        effectiveFrom, effectiveTo

    /// Selects the effective rate for a owner usage fact without making unmapped tracked facts billable.
    let trySelect (query: PricingRateSelectionQuery) (catalog: PricingCatalogSnapshot) =
        let assignment =
            catalog.PricingAssignments
            |> List.filter (fun candidate ->
                candidate.OwnerId = query.OwnerId
                && candidate.OrganizationId = query.OrganizationId
                && candidate.RepositoryId = query.RepositoryId
                && contains query.ObservedAt candidate.EffectiveFrom candidate.EffectiveTo)
            |> latestBy (fun candidate -> candidate.EffectiveFrom) (fun candidate -> candidate.PricingAssignmentId)

        let plan =
            assignment
            |> Option.bind (fun selectedAssignment ->
                catalog.PricingPlans
                |> List.filter (fun candidate ->
                    candidate.PricingPlanId = selectedAssignment.PricingPlanId
                    && contains query.ObservedAt candidate.EffectiveFrom candidate.EffectiveTo)
                |> latestBy (fun candidate -> candidate.EffectiveFrom) (fun candidate -> candidate.PricingPlanId))

        let mapping =
            catalog.BillableUsageKindMappings
            |> List.filter (fun candidate ->
                candidate.FactKind = query.FactKind
                && contains query.ObservedAt candidate.EffectiveFrom candidate.EffectiveTo)
            |> latestBy (fun candidate -> candidate.EffectiveFrom) (fun candidate -> candidate.BillableUsageKindMappingId)

        let rate =
            match plan, mapping with
            | Some selectedPlan, Some selectedMapping ->
                catalog.PricingRates
                |> List.filter (fun candidate ->
                    candidate.PricingPlanId = selectedPlan.PricingPlanId
                    && candidate.BillableUsageKind = selectedMapping.BillableUsageKind
                    && contains query.ObservedAt candidate.EffectiveFrom candidate.EffectiveTo)
                |> latestBy (fun candidate -> candidate.EffectiveFrom) (fun candidate -> candidate.PricingRateId)
            | _ -> None

        match assignment, plan, mapping, rate with
        | Some selectedAssignment, Some selectedPlan, Some selectedMapping, Some selectedRate ->
            let effectiveFrom, effectiveTo =
                intersectEffectiveWindows [ selectedAssignment.EffectiveFrom, selectedAssignment.EffectiveTo
                                            selectedPlan.EffectiveFrom, selectedPlan.EffectiveTo
                                            selectedMapping.EffectiveFrom, selectedMapping.EffectiveTo
                                            selectedRate.EffectiveFrom, selectedRate.EffectiveTo ]

            Some
                {
                    PricingAssignmentId = selectedAssignment.PricingAssignmentId
                    OwnerId = selectedAssignment.OwnerId
                    OrganizationId = selectedAssignment.OrganizationId
                    RepositoryId = selectedAssignment.RepositoryId
                    PricingPlanId = selectedPlan.PricingPlanId
                    PlanCode = selectedPlan.PlanCode
                    BillableUsageKindMappingId = selectedMapping.BillableUsageKindMappingId
                    BillableUsageKind = selectedMapping.BillableUsageKind
                    PricingRateId = selectedRate.PricingRateId
                    CurrencyCode = selectedRate.CurrencyCode
                    UnitName = selectedRate.UnitName
                    UnitQuantity = selectedRate.UnitQuantity
                    UnitPriceMicros = selectedRate.UnitPriceMicros
                    EffectiveFrom = effectiveFrom
                    EffectiveTo = effectiveTo
                }
        | _ -> None

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

/// Selects archived UsageFacts for replay or scoped support rehydration without reading Blob payloads.
type RawUsageFactArchiveQuery =
    {
        Scope: RawUsageFactArchiveScope option
        ObservedAfterUtc: Instant option
        ObservedBeforeUtc: Instant option
        CorrelationIds: CorrelationId list
        UsageFactIds: UsageFactId list
    }

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

/// Carries one verified archived payload that should become temporarily hot in SQL.
type RawUsageFactRehydrationItem = { Pointer: RawUsageFactArchivePointer; RawPayload: byte array }

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

    /// Records a row-scoped archive failure so operators can inspect and repair durable retention blockers.
    abstract RecordArchiveFailureAsync: usageFactId: UsageFactId * reason: string * cancellationToken: CancellationToken -> Task

    /// Returns archived rows whose compact SQL pointer identifies the archived payload for replay or support rehydration.
    abstract ListArchivedUsageFactsAsync:
        query: RawUsageFactArchiveQuery * after: RawUsageFactArchiveReplayCursor option * batchSize: int * cancellationToken: CancellationToken ->
            Task<RawUsageFactArchiveReplayItem list>

    /// Counts archived rows matching a support predicate without materializing Blob payloads.
    abstract CountArchivedUsageFactsAsync: query: RawUsageFactArchiveQuery * cancellationToken: CancellationToken -> Task<int64>

    /// Temporarily restores or refreshes archived payload bytes only when SQL pointer fields still match the Blob checksum row.
    abstract RehydrateArchivedPayloadsAsync:
        items: RawUsageFactRehydrationItem list * expiresAt: Instant * cancellationToken: CancellationToken -> Task<UsageFactId list>

    /// Clears expired temporary support rehydrations left behind after caller or process failure.
    abstract CleanupExpiredRehydratedPayloadsAsync: expiresBefore: Instant * batchSize: int * cancellationToken: CancellationToken -> Task<int>

/// Selects effective owner pricing rates from the Operations pricing catalog.
type IOperationsPricingCatalog =

    /// Returns a rate only when assignment, plan, usage-kind mapping, and rate rows are all effective.
    abstract TrySelectEffectiveRateAsync: query: PricingRateSelectionQuery * cancellationToken: CancellationToken -> Task<EffectivePricingRate option>

/// Executes effective-rate selection through reviewed Operations SQL.
type SqlOperationsPricingCatalog(connectionString: string) =

    /// Opens one SQL connection for an effective-rate lookup.
    let openConnectionAsync cancellationToken =
        task {
            let connection = new SqlConnection(connectionString)
            do! connection.OpenAsync cancellationToken
            return connection
        }

    /// Converts a NodaTime instant to the UTC SQL timestamp shape used by pricing tables.
    let toUtcDateTime (instant: Instant) = instant.ToDateTimeUtc()

    /// Converts a SQL UTC timestamp to the NodaTime shape returned to callers.
    let toInstant (dateTime: DateTime) =
        DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
        |> Instant.FromDateTimeUtc

    /// Adds a SQL parameter and assigns the supplied value.
    let addParameter (command: SqlCommand) name sqlDbType value =
        let parameter = command.Parameters.Add(name, sqlDbType)
        parameter.Value <- value

    /// Adds the effective-rate lookup parameters with the repository scope required to prevent leakage.
    let addSelectionParameters (command: SqlCommand) (query: PricingRateSelectionQuery) =
        addParameter command "@OwnerId" SqlDbType.UniqueIdentifier query.OwnerId
        addParameter command "@OrganizationId" SqlDbType.UniqueIdentifier query.OrganizationId
        addParameter command "@RepositoryId" SqlDbType.UniqueIdentifier query.RepositoryId
        addParameter command "@FactKind" SqlDbType.Int (int query.FactKind)
        addParameter command "@ObservedAtUtc" SqlDbType.DateTime2 (toUtcDateTime query.ObservedAt)

    /// Reads the nullable effective end timestamp for the selected rate.
    let readEffectiveTo (reader: SqlDataReader) ordinal =
        if reader.IsDBNull ordinal then
            None
        else
            reader.GetDateTime ordinal |> toInstant |> Some

    /// Reads one effective pricing-rate result from the SQL reader.
    let readEffectiveRate (reader: SqlDataReader) =
        let effectiveToOrdinal = reader.GetOrdinal("EffectiveToUtc")

        {
            PricingAssignmentId = reader.GetGuid(reader.GetOrdinal("PricingAssignmentId"))
            OwnerId = reader.GetGuid(reader.GetOrdinal("OwnerId"))
            OrganizationId = reader.GetGuid(reader.GetOrdinal("OrganizationId"))
            RepositoryId = reader.GetGuid(reader.GetOrdinal("RepositoryId"))
            PricingPlanId = reader.GetGuid(reader.GetOrdinal("PricingPlanId"))
            PlanCode = reader.GetString(reader.GetOrdinal("PlanCode"))
            BillableUsageKindMappingId = reader.GetGuid(reader.GetOrdinal("BillableUsageKindMappingId"))
            BillableUsageKind = reader.GetInt32(reader.GetOrdinal("BillableUsageKind"))
            PricingRateId = reader.GetGuid(reader.GetOrdinal("PricingRateId"))
            CurrencyCode = reader.GetString(reader.GetOrdinal("CurrencyCode"))
            UnitName = reader.GetString(reader.GetOrdinal("UnitName"))
            UnitQuantity = reader.GetInt64(reader.GetOrdinal("UnitQuantity"))
            UnitPriceMicros = reader.GetInt64(reader.GetOrdinal("UnitPriceMicros"))
            EffectiveFrom =
                reader.GetDateTime(reader.GetOrdinal("EffectiveFromUtc"))
                |> toInstant
            EffectiveTo = readEffectiveTo reader effectiveToOrdinal
        }

    /// Returns the effective owner rate selected by SQL, or None for non-billable usage.
    member _.TrySelectEffectiveRateAsync(query: PricingRateSelectionQuery, cancellationToken: CancellationToken) =
        task {
            use! connection = openConnectionAsync cancellationToken
            use command = connection.CreateCommand()
            command.CommandType <- CommandType.Text
            command.CommandText <- OperationsPricingSql.SelectEffectivePricingRate
            addSelectionParameters command query

            use! reader = command.ExecuteReaderAsync cancellationToken
            let! hasRow = reader.ReadAsync cancellationToken

            if hasRow then return Some(readEffectiveRate reader) else return None
        }

    interface IOperationsPricingCatalog with

        member this.TrySelectEffectiveRateAsync(query, cancellationToken) = this.TrySelectEffectiveRateAsync(query, cancellationToken)

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

    /// Clears terminal raw-insert trust on this physical SQL session so pooled connections cannot retain an insertion capability.
    let clearTrustedRawUsageFactInsertAsync cancellationToken =
        task {
            use command = createCommand "EXEC sys.sp_set_session_context @key=@Key, @value=NULL;"
            command.Parameters.Add("@Key", SqlDbType.NVarChar, 128).Value <- OperationsUsageSql.TrustedRawUsageFactInsertSessionKey
            let! _ = command.ExecuteNonQueryAsync(cancellationToken)
            return ()
        }

    /// Converts a NodaTime instant to the UTC SQL timestamp shape used by operations usage tables.
    let toUtcDateTime (instant: Instant) = instant.ToDateTimeUtc()

    /// Adds the owner, organization, repository, and month lock shared by accepted ingestion, replay, preview replacement, and close.
    let addBillingScopeLockParameter (command: SqlCommand) (rawFact: RawUsageFact) =
        let periodFromUtc, periodToUtc = BillingPeriodRules.monthContaining (toUtcDateTime rawFact.ObservedAt)

        let lockIdentity =
            $"ops:charge-preview:{rawFact.OwnerId:D}:{rawFact.OrganizationId:D}:{rawFact.RepositoryId:D}:{periodFromUtc.Ticks}:{periodToUtc.Ticks}"

        command.Parameters.Add("@LockResource", SqlDbType.NVarChar, 255).Value <- $"ops:charge-preview:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes lockIdentity))}"

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
        addBillingScopeLockParameter command rawFact

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
        addBillingScopeLockParameter command rawFact

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
                try
                    use command = createCommand OperationsUsageSql.TryInsertRawUsageFact
                    command.CommandTimeout <- OperationsUsageSql.RawUsageFactInsertCommandTimeoutSeconds
                    addRawUsageFactParameters command rawFact
                    let! rowsAffected = command.ExecuteNonQueryAsync cancellationToken
                    do! clearTrustedRawUsageFactInsertAsync CancellationToken.None
                    return rowsAffected = 1
                with
                | ex ->
                    try
                        do! clearTrustedRawUsageFactInsertAsync CancellationToken.None
                    with
                    | _ -> ()

                    return raise ex
            }

        member _.TryInsertReplayedArchivedUsageFactAsync(rawFact, pointer, cancellationToken) =
            task {
                try
                    use command = createCommand OperationsUsageSql.TryInsertReplayedArchivedRawUsageFact
                    command.CommandTimeout <- OperationsUsageSql.RawUsageFactInsertCommandTimeoutSeconds
                    addReplayedRawUsageFactParameters command rawFact
                    addReplayedArchivePointerParameters command pointer
                    let! rowsAffected = command.ExecuteNonQueryAsync cancellationToken
                    do! clearTrustedRawUsageFactInsertAsync CancellationToken.None
                    return rowsAffected = 1
                with
                | ex ->
                    try
                        do! clearTrustedRawUsageFactInsertAsync CancellationToken.None
                    with
                    | _ -> ()

                    return raise ex
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

    /// Adds optional archived-row predicate parameters used by replay and support rehydration scans.
    let addArchiveQueryParameters (command: SqlCommand) (query: RawUsageFactArchiveQuery) =
        addArchiveScopeParameters command query.Scope

        addParameter
            command
            "@ObservedAfterUtc"
            SqlDbType.DateTime2
            (query.ObservedAfterUtc
             |> Option.map (fun value -> box (toUtcDateTime value))
             |> Option.defaultValue DBNull.Value)

        addParameter
            command
            "@ObservedBeforeUtc"
            SqlDbType.DateTime2
            (query.ObservedBeforeUtc
             |> Option.map (fun value -> box (toUtcDateTime value))
             |> Option.defaultValue DBNull.Value)

        let serializedFilterValues values =
            if List.isEmpty values then
                box DBNull.Value
            else
                values
                |> List.map string
                |> List.toArray
                |> JsonSerializer.Serialize
                |> box

        let usageFactIdsJson = command.Parameters.Add("@UsageFactIdsJson", SqlDbType.NVarChar, -1)
        usageFactIdsJson.Value <- serializedFilterValues query.UsageFactIds

        let correlationIdsJson = command.Parameters.Add("@CorrelationIdsJson", SqlDbType.NVarChar, -1)
        correlationIdsJson.Value <- serializedFilterValues query.CorrelationIds

    /// Declares and populates bounded filter-list tables from JSON parameters so SQL command shape stays constant.
    let archiveFilterStagingSql =
        """
DECLARE @UsageFactIdFilters table
(
    UsageFactId uniqueidentifier NOT NULL PRIMARY KEY
);

IF @UsageFactIdsJson IS NOT NULL
BEGIN
    INSERT INTO @UsageFactIdFilters (UsageFactId)
    SELECT DISTINCT UsageFactId
    FROM OPENJSON(@UsageFactIdsJson)
    WITH (UsageFactId uniqueidentifier '$')
    WHERE UsageFactId IS NOT NULL;
END;

DECLARE @CorrelationIdFilters table
(
    CorrelationId nvarchar(200) NOT NULL PRIMARY KEY
);

IF @CorrelationIdsJson IS NOT NULL
BEGIN
    INSERT INTO @CorrelationIdFilters (CorrelationId)
    SELECT DISTINCT CorrelationId
    FROM OPENJSON(@CorrelationIdsJson)
    WITH (CorrelationId nvarchar(max) '$')
    WHERE CorrelationId IS NOT NULL
    AND LEN(CorrelationId) <= 200;
END;
"""

    /// Builds a deterministic archived-row SQL predicate for index-only count and page scans.
    let archivedUsageFactPredicateSql (query: RawUsageFactArchiveQuery) (includeCursor: bool) =
        let builder = StringBuilder()

        builder.AppendLine("WHERE ArchiveState = @ArchiveStateArchived")
        |> ignore

        builder.AppendLine("AND ArchiveBlobName IS NOT NULL")
        |> ignore

        builder.AppendLine("AND ArchiveChecksumSha256Hex IS NOT NULL")
        |> ignore

        builder.AppendLine("AND ArchiveByteLength IS NOT NULL")
        |> ignore

        builder.AppendLine("AND (@OwnerId IS NULL OR OwnerId = @OwnerId)")
        |> ignore

        builder.AppendLine("AND (@OrganizationId IS NULL OR OrganizationId = @OrganizationId)")
        |> ignore

        builder.AppendLine("AND (@RepositoryId IS NULL OR RepositoryId = @RepositoryId)")
        |> ignore

        builder.AppendLine("AND (@ObservedAfterUtc IS NULL OR ObservedAtUtc >= @ObservedAfterUtc)")
        |> ignore

        builder.AppendLine("AND (@ObservedBeforeUtc IS NULL OR ObservedAtUtc <= @ObservedBeforeUtc)")
        |> ignore

        builder.AppendLine("AND (NOT EXISTS (SELECT 1 FROM @UsageFactIdFilters) OR UsageFactId IN (SELECT UsageFactId FROM @UsageFactIdFilters))")
        |> ignore

        builder.AppendLine("AND (NOT EXISTS (SELECT 1 FROM @CorrelationIdFilters) OR CorrelationId IN (SELECT CorrelationId FROM @CorrelationIdFilters))")
        |> ignore

        if includeCursor then
            builder.AppendLine("AND") |> ignore
            builder.AppendLine("(") |> ignore

            builder.AppendLine("    @AfterObservedAtUtc IS NULL")
            |> ignore

            builder.AppendLine("    OR ObservedAtUtc > @AfterObservedAtUtc")
            |> ignore

            builder.AppendLine("    OR (ObservedAtUtc = @AfterObservedAtUtc AND UsageFactId > @AfterUsageFactId)")
            |> ignore

            builder.AppendLine(")") |> ignore

        builder.ToString()

    /// Builds the archived-row page scan used by replay and support rehydration exploration.
    let archivedUsageFactPageSql query =
        $"""
{archiveFilterStagingSql}

SELECT TOP (@BatchSize)
    UsageFactId,
    CorrelationId,
    FactKind,
    OwnerId,
    OrganizationId,
    RepositoryId,
    StoragePoolId,
    Quantity,
    ObservedAtUtc,
    ArchiveState,
    ArchiveBlobName,
    ArchiveChecksumSha256Hex,
    ArchiveByteLength
FROM ops.RawUsageFact WITH (READCOMMITTEDLOCK)
{archivedUsageFactPredicateSql query true}ORDER BY ObservedAtUtc ASC, UsageFactId ASC;
"""

    /// Builds the index-only archived-row count used before support rehydration mutates SQL.
    let archivedUsageFactCountSql query =
        $"""
{archiveFilterStagingSql}

SELECT COUNT_BIG(1)
FROM ops.RawUsageFact WITH (READCOMMITTEDLOCK)
{archivedUsageFactPredicateSql query false};
"""

    /// Records one archive failure without reading raw payload bytes or changing archive authority.
    let recordArchiveFailureAsync usageFactId reason cancellationToken =
        task {
            let boundedReason =
                if String.IsNullOrWhiteSpace reason then
                    "Archive retention failed."
                elif reason.Length
                     <= OperationsUsageSql.ArchiveFailureReasonMaxLength then
                    reason
                else
                    reason.Substring(0, OperationsUsageSql.ArchiveFailureReasonMaxLength)

            use! connection = openConnectionAsync cancellationToken
            use command = connection.CreateCommand()
            command.CommandType <- CommandType.Text
            command.CommandText <- OperationsUsageSql.RecordRawUsageFactArchiveFailure
            addArchiveStateParameters command
            addParameter command "@UsageFactId" SqlDbType.UniqueIdentifier usageFactId
            addStringParameter command "@LastArchiveFailureReason" OperationsUsageSql.ArchiveFailureReasonMaxLength boundedReason
            addParameter command "@ArchiveFailureRetirementThreshold" SqlDbType.Int OperationsUsageSql.ArchiveFailureRetirementThreshold
            let! _ = command.ExecuteNonQueryAsync cancellationToken
            return ()
        }

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

    /// Builds the parameterized insert block for one rehydration payload batch.
    let rehydrationBatchInsertSql (items: RawUsageFactRehydrationItem array) =
        let builder = StringBuilder()

        builder.AppendLine(OperationsUsageSql.DeclareRehydratedRawUsageFactBatch.Trim())
        |> ignore

        builder.AppendLine() |> ignore

        builder.AppendLine("INSERT INTO @RehydrationRows")
        |> ignore

        builder.AppendLine("(") |> ignore
        builder.AppendLine("    UsageFactId,") |> ignore
        builder.AppendLine("    RawPayload,") |> ignore

        builder.AppendLine("    ArchiveBlobName,")
        |> ignore

        builder.AppendLine("    ArchiveChecksumSha256Hex,")
        |> ignore

        builder.AppendLine("    ArchiveByteLength")
        |> ignore

        builder.AppendLine(")") |> ignore
        builder.AppendLine("VALUES") |> ignore

        let mutable index = 0

        while index < items.Length do
            let suffix = if index = items.Length - 1 then ";" else ","

            builder
                .Append("    (@UsageFactId")
                .Append(index)
                .Append(", @RawPayload")
                .Append(index)
                .Append(", @ArchiveBlobName")
                .Append(index)
                .Append(", @ArchiveChecksumSha256Hex")
                .Append(index)
                .Append(", @ArchiveByteLength")
                .Append(index)
                .AppendLine($"){suffix}")
            |> ignore

            index <- index + 1

        builder.AppendLine() |> ignore

        builder.AppendLine(OperationsUsageSql.RehydrateArchivedRawUsageFactPayloadBatch.Trim())
        |> ignore

        builder.ToString()

    /// Executes one bounded rehydration batch inside the request transaction and returns refreshed expiry rows.
    let executeRehydrationPayloadBatchAsync
        (connection: SqlConnection)
        (transaction: SqlTransaction)
        (items: RawUsageFactRehydrationItem array)
        (expiresAt: Instant)
        (cancellationToken: CancellationToken)
        =
        task {
            use command = connection.CreateCommand()
            command.Transaction <- transaction
            command.CommandType <- CommandType.Text
            command.CommandText <- rehydrationBatchInsertSql items
            addArchiveStateParameters command
            addParameter command "@RehydrationExpiresAtUtc" SqlDbType.DateTime2 (toUtcDateTime expiresAt)

            let mutable index = 0

            while index < items.Length do
                let item = items[index]
                addParameter command $"@UsageFactId{index}" SqlDbType.UniqueIdentifier item.Pointer.UsageFactId
                let parameter = command.Parameters.Add("@RawPayload", SqlDbType.VarBinary, -1)
                parameter.ParameterName <- $"@RawPayload{index}"
                parameter.Value <- Array.copy item.RawPayload
                addStringParameter command $"@ArchiveBlobName{index}" OperationsUsageSql.ArchiveBlobNameMaxLength item.Pointer.BlobName

                let checksumParameter =
                    command.Parameters.Add($"@ArchiveChecksumSha256Hex{index}", SqlDbType.Char, OperationsUsageSql.ArchiveChecksumSha256HexLength)

                checksumParameter.Value <- item.Pointer.ChecksumSha256Hex
                addParameter command $"@ArchiveByteLength{index}" SqlDbType.BigInt item.Pointer.ByteLength
                index <- index + 1

            use! reader = command.ExecuteReaderAsync cancellationToken
            let changed = ResizeArray<UsageFactId>()
            let mutable reading = true

            while reading do
                let! hasRow = reader.ReadAsync cancellationToken

                if hasRow then changed.Add(reader.GetGuid 0) else reading <- false

            return changed |> Seq.toList
        }

    /// Rolls back a failed rehydration request while preserving the original SQL or cancellation failure.
    let rollbackRehydrationIgnoringFailuresAsync (transaction: SqlTransaction) =
        task {
            try
                do! transaction.RollbackAsync CancellationToken.None
            with
            | _ -> ()
        }

    /// Clears one bounded batch of expired temporary payloads and returns the rows recovered from durable expiry state.
    let cleanupExpiredRehydratedPayloadsAsync expiresBefore batchSize cancellationToken =
        task {
            use! connection = openConnectionAsync cancellationToken
            use command = connection.CreateCommand()
            command.CommandType <- CommandType.Text
            command.CommandText <- OperationsUsageSql.CleanupExpiredRehydratedRawUsageFactPayloads
            addArchiveStateParameters command
            addParameter command "@ExpiresBeforeUtc" SqlDbType.DateTime2 (toUtcDateTime expiresBefore)
            addParameter command "@BatchSize" SqlDbType.Int batchSize

            let! scalar = command.ExecuteScalarAsync cancellationToken

            return
                match scalar with
                | :? int as value -> value
                | :? int64 as value -> int value
                | :? decimal as value -> int value
                | _ -> 0
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

        member _.RecordArchiveFailureAsync(usageFactId, reason, cancellationToken) = recordArchiveFailureAsync usageFactId reason cancellationToken

        member _.ListArchivedUsageFactsAsync(query, after, batchSize, cancellationToken) =
            task {
                if batchSize <= 0 then
                    invalidArg (nameof batchSize) "Archive replay batch size must be greater than zero."

                use! connection = openConnectionAsync cancellationToken
                use command = connection.CreateCommand()
                command.CommandType <- CommandType.Text
                command.CommandText <- archivedUsageFactPageSql query
                addParameter command "@BatchSize" SqlDbType.Int batchSize
                addArchiveStateParameters command
                addArchiveQueryParameters command query

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

        member _.CountArchivedUsageFactsAsync(query, cancellationToken) =
            task {
                use! connection = openConnectionAsync cancellationToken
                use command = connection.CreateCommand()
                command.CommandType <- CommandType.Text
                command.CommandText <- archivedUsageFactCountSql query
                addArchiveStateParameters command
                addArchiveQueryParameters command query

                let! scalar = command.ExecuteScalarAsync cancellationToken

                return
                    match scalar with
                    | :? int as value -> int64 value
                    | :? int64 as value -> value
                    | :? decimal as value -> int64 value
                    | _ -> 0L
            }

        member _.RehydrateArchivedPayloadsAsync(items, expiresAt, cancellationToken) =
            task {
                let itemArray = items |> List.toArray

                if itemArray.Length = 0 then
                    return []
                else
                    let mutable index = 0

                    while index < itemArray.Length do
                        let item = itemArray[index]

                        if isNull item.RawPayload
                           || item.RawPayload.Length = 0 then
                            invalidArg (nameof items) "Rehydrated raw payload bytes are required."

                        index <- index + 1

                    use! connection = openConnectionAsync cancellationToken
                    let! dbTransaction = connection.BeginTransactionAsync cancellationToken
                    use transaction = dbTransaction :?> SqlTransaction

                    try
                        let changed = ResizeArray<UsageFactId>()
                        let mutable offset = 0

                        while offset < itemArray.Length do
                            let count = Math.Min(OperationsUsageSql.RehydrationPayloadBatchSize, itemArray.Length - offset)
                            let batch = Array.zeroCreate<RawUsageFactRehydrationItem> count
                            Array.Copy(itemArray, offset, batch, 0, count)
                            let! batchChanged = executeRehydrationPayloadBatchAsync connection transaction batch expiresAt cancellationToken
                            changed.AddRange batchChanged
                            offset <- offset + count

                        do! transaction.CommitAsync cancellationToken
                        return changed |> Seq.toList
                    with
                    | ex ->
                        do! rollbackRehydrationIgnoringFailuresAsync transaction
                        return raise ex
            }

        member _.CleanupExpiredRehydratedPayloadsAsync(expiresBefore, batchSize, cancellationToken) =
            if batchSize <= 0 then
                invalidArg (nameof batchSize) "Temporary-hot cleanup batch size must be greater than zero."

            let boundedBatchSize = Math.Min(batchSize, OperationsUsageSql.TemporaryHotCleanupBatchSize)
            cleanupExpiredRehydratedPayloadsAsync expiresBefore boundedBatchSize cancellationToken

/// Initializes the Operations usage schema before hosted services query or mutate operations tables.
type IOperationsUsageSchemaInitializer =

    /// Applies the reviewed Operations usage schema migrations before runtime processing starts.
    abstract EnsureCreatedAsync: cancellationToken: CancellationToken -> Task

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

    interface IOperationsUsageSchemaInitializer with

        member this.EnsureCreatedAsync(cancellationToken) = this.EnsureCreatedAsync cancellationToken

/// Carries one shared Operations schema initialization attempt and the token that can stop it during host shutdown.
type private OperationsUsageSchemaInitializationAttempt = { Task: Task; Cancellation: CancellationTokenSource }

/// Shares one Operations schema initialization attempt across hosted services before SQL runtime work begins.
type OperationsUsageSchemaInitializationBarrier(schema: IOperationsUsageSchemaInitializer) =
    let gate = obj ()
    let mutable initialized = false
    let mutable inFlight: OperationsUsageSchemaInitializationAttempt option = None

    /// Links a joining caller's shutdown token to the shared schema attempt.
    let cancelAttemptWhenCallerCancels (attempt: OperationsUsageSchemaInitializationAttempt) (cancellationToken: CancellationToken) =
        if cancellationToken.CanBeCanceled then
            let registration =
                cancellationToken.Register(
                    Action (fun () ->
                        try
                            attempt.Cancellation.Cancel()
                        with
                        | :? ObjectDisposedException -> ())
                )

            attempt.Task.ContinueWith(Action<Task>(fun _ -> registration.Dispose()))
            |> ignore

    /// Starts or joins the shared schema initialization attempt.
    member _.EnsureCreatedAsync(cancellationToken: CancellationToken) =
        let initialization =
            lock gate (fun () ->
                if initialized then
                    Task.CompletedTask
                else
                    match inFlight with
                    | Some attempt ->
                        cancelAttemptWhenCallerCancels attempt cancellationToken
                        attempt.Task
                    | None ->
                        let attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)

                        let task =
                            task {
                                try
                                    try
                                        do! schema.EnsureCreatedAsync attemptCancellation.Token

                                        lock gate (fun () ->
                                            initialized <- true
                                            inFlight <- None)
                                    with
                                    | ex ->
                                        lock gate (fun () -> inFlight <- None)
                                        ExceptionDispatchInfo.Capture(ex).Throw()
                                finally
                                    attemptCancellation.Dispose()
                            }
                            :> Task

                        inFlight <- Some { Task = task; Cancellation = attemptCancellation }
                        task)

        initialization.WaitAsync cancellationToken

    interface IOperationsUsageSchemaInitializer with

        member this.EnsureCreatedAsync(cancellationToken) = this.EnsureCreatedAsync cancellationToken

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
