namespace Grace.Operations.Data

open Microsoft.Data.SqlClient
open NodaTime
open System
open System.Data
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

/// Names the nonterminal and terminal billing-period states owned by the initial-close contract.
[<RequireQualifiedAccess>]
type BillingPeriodState =
    | Open = 0
    | Provisional = 1
    | Closed = 2

/// Carries a close request whose provenance identifies the bounded scheduled operation that initiated it.
type BillingPeriodCloseRequest = { Scope: BillingCompletenessScope; ScheduledOperationProvenance: string }

/// Reports whether a period is below threshold, visibly blocked, provisionally rebuilt, or closed.
type BillingPeriodCloseResult =
    | NotEligible
    | Blocked of diagnostic: string
    | Provisional of billingPeriodId: Guid * previewLineCount: int
    | Closed of billingPeriodId: Guid * chargeCount: int

/// Signals a retryable zero-fact pricing-coverage failure after the scope lock has been acquired.
type private ZeroFactPricingCoverageException(diagnostic: string) =
    inherit Exception(diagnostic)

/// Exposes the bounded preview and final-close operations used by the scheduled shell.
type IBillingPeriodCloser =
    /// Rebuilds an eligible preview under the exact shared scope lock.
    abstract PreviewAsync: request: BillingPeriodCloseRequest * cancellationToken: CancellationToken -> Task<BillingPeriodCloseResult>

    /// Rebuilds and posts an eligible final close under the exact shared scope lock.
    abstract CloseAsync: request: BillingPeriodCloseRequest * cancellationToken: CancellationToken -> Task<BillingPeriodCloseResult>

/// Exposes test-only failure points within the one transaction that owns a billing-period close.
type internal IBillingPeriodCloseTransactionInterleaving =
    /// Runs after the current preview rows have been replaced but before any immutable posting is appended.
    abstract AfterPreviewReplacementAsync: cancellationToken: CancellationToken -> Task

    /// Runs after the initial charge set has been appended but before close evidence is written.
    abstract AfterLedgerInsertionAsync: cancellationToken: CancellationToken -> Task

    /// Runs after close evidence has been staged but before the period becomes Closed.
    abstract AfterCloseEvidenceStagedAsync: cancellationToken: CancellationToken -> Task

/// Provides production close execution with no injected work between transactional stages.
type private NoBillingPeriodCloseTransactionInterleaving() =
    interface IBillingPeriodCloseTransactionInterleaving with
        member _.AfterPreviewReplacementAsync _ = Task.CompletedTask
        member _.AfterLedgerInsertionAsync _ = Task.CompletedTask
        member _.AfterCloseEvidenceStagedAsync _ = Task.CompletedTask

/// Rebuilds previews and immutable initial postings under the same SQL lock used by accepted fact processing.
type SqlBillingPeriodCloser private (connectionString: string, transactionInterleaving: IBillingPeriodCloseTransactionInterleaving) =

    /// Derives the deterministic period identity from every exact scope boundary without insertion-order dependence.
    let billingPeriodId (scope: BillingCompletenessScope) =
        let canonical =
            String.Join(
                "|",
                [|
                    "Grace.Operations.BillingPeriod.v1"
                    scope.OwnerId.ToString("D")
                    scope.OrganizationId.ToString("D")
                    scope.RepositoryId.ToString("D")
                    scope
                        .MonthStart
                        .ToDateTimeUtc()
                        .Ticks.ToString(CultureInfo.InvariantCulture)
                    (BillingCompletenessScope.nextMonthStart scope)
                        .ToDateTimeUtc()
                        .Ticks.ToString(CultureInfo.InvariantCulture)
                |]
            )

        let hash = SHA256.HashData(Encoding.UTF8.GetBytes canonical)
        Guid(hash[0..15])

    /// Derives an immutable posting identity from the period and already deterministic preview-line identity.
    let chargeId periodId previewLineId =
        let canonical = $"Grace.Operations.InitialCharge.v1|{periodId:D}|{previewLineId:D}"
        let hash = SHA256.HashData(Encoding.UTF8.GetBytes canonical)
        Guid(hash[0..15])

    /// Converts a persisted UTC value into the exact NodaTime instant expected by Operations scope validation.
    let toInstant (value: DateTime) =
        let utc =
            if value.Kind = DateTimeKind.Utc then
                value
            else
                DateTime.SpecifyKind(value, DateTimeKind.Utc)

        Instant.FromDateTimeUtc utc

    /// Adds the exact scope and half-open month parameters to a SQL command.
    let addScope (command: SqlCommand) (scope: BillingCompletenessScope) =
        command.Parameters.Add("@OwnerId", SqlDbType.UniqueIdentifier).Value <- scope.OwnerId
        command.Parameters.Add("@OrganizationId", SqlDbType.UniqueIdentifier).Value <- scope.OrganizationId
        command.Parameters.Add("@RepositoryId", SqlDbType.UniqueIdentifier).Value <- scope.RepositoryId
        command.Parameters.Add("@MonthStartUtc", SqlDbType.DateTime2).Value <- scope.MonthStart.ToDateTimeUtc()

        command.Parameters.Add("@NextMonthStartUtc", SqlDbType.DateTime2).Value <- (BillingCompletenessScope.nextMonthStart scope)
            .ToDateTimeUtc()

    /// Creates a command bound to the close transaction so every mutation shares its lock lifetime.
    let command (connection: SqlConnection) (transaction: SqlTransaction) text =
        let value = connection.CreateCommand()
        value.Transaction <- transaction
        value.CommandText <- text
        value

    /// Acquires the coordination primitive used by journal acceptance before examining committed completeness.
    let acquireScopeLockAsync
        (connection: SqlConnection)
        (transaction: SqlTransaction)
        (scope: BillingCompletenessScope)
        (cancellationToken: CancellationToken)
        =
        task {
            use lockCommand = command connection transaction OperationsUsageSql.AcquireBillingCompletenessScopeLock
            lockCommand.Parameters.Add("@BillingCompletenessLockResource", SqlDbType.NVarChar, 255).Value <- BillingCompletenessScope.databaseLockIdentity scope
            lockCommand.Parameters.Add("@BillingCompletenessLockTimeoutMilliseconds", SqlDbType.Int).Value <- 60000
            let! _ = lockCommand.ExecuteNonQueryAsync cancellationToken
            return ()
        }

    /// Reads database UTC inside the mutation transaction so callers cannot decide persisted eligibility.
    let databaseUtcNowAsync (connection: SqlConnection) (transaction: SqlTransaction) (cancellationToken: CancellationToken) =
        task {
            use nowCommand = command connection transaction "SELECT SYSUTCDATETIME();"
            let! value = nowCommand.ExecuteScalarAsync cancellationToken
            return value :?> DateTime
        }

    /// Inserts the deterministic period if discovery has not already materialized it, then locks its row for this close.
    let ensurePeriodAsync (connection: SqlConnection) (transaction: SqlTransaction) (scope: BillingCompletenessScope) (cancellationToken: CancellationToken) =
        task {
            let periodId = billingPeriodId scope

            use insert =
                command
                    connection
                    transaction
                    """
INSERT INTO ops.BillingPeriod (BillingPeriodId,OwnerId,OrganizationId,RepositoryId,MonthStartUtc,NextMonthStartUtc,State)
SELECT @BillingPeriodId,@OwnerId,@OrganizationId,@RepositoryId,@MonthStartUtc,@NextMonthStartUtc,0
WHERE NOT EXISTS
(
    SELECT 1 FROM ops.BillingPeriod WITH (UPDLOCK,HOLDLOCK)
    WHERE OwnerId=@OwnerId AND OrganizationId=@OrganizationId AND RepositoryId=@RepositoryId
      AND MonthStartUtc=@MonthStartUtc AND NextMonthStartUtc=@NextMonthStartUtc
);
"""

            addScope insert scope
            insert.Parameters.Add("@BillingPeriodId", SqlDbType.UniqueIdentifier).Value <- periodId
            let! _ = insert.ExecuteNonQueryAsync cancellationToken

            use select =
                command
                    connection
                    transaction
                    """
SELECT BillingPeriodId, State FROM ops.BillingPeriod WITH (UPDLOCK,HOLDLOCK)
WHERE OwnerId=@OwnerId AND OrganizationId=@OrganizationId AND RepositoryId=@RepositoryId
  AND MonthStartUtc=@MonthStartUtc AND NextMonthStartUtc=@NextMonthStartUtc;
"""

            addScope select scope
            use! reader = select.ExecuteReaderAsync cancellationToken
            let! exists = reader.ReadAsync cancellationToken

            if not exists then
                invalidOp "The exact billing period could not be materialized under its scope lock."

            return (reader.GetGuid 0, enum<BillingPeriodState> (reader.GetInt32 1))
        }

    /// Returns a bounded completeness reason directly from committed rows while the shared lock prevents a race.
    let completenessDiagnosticAsync
        (connection: SqlConnection)
        (transaction: SqlTransaction)
        (scope: BillingCompletenessScope)
        (cancellationToken: CancellationToken)
        =
        task {
            use check =
                command
                    connection
                    transaction
                    """
IF EXISTS
(
    SELECT 1 FROM ops.UsageFactJournal
    WHERE OwnerId=@OwnerId AND OrganizationId=@OrganizationId AND RepositoryId=@RepositoryId
      AND ObservedAtUtc >= @MonthStartUtc AND ObservedAtUtc < @NextMonthStartUtc AND State IN (0,2)
)
    SELECT CAST('Unresolved UsageFact journal rows block billing close.' AS nvarchar(400));
ELSE IF EXISTS
(
    SELECT 1 FROM ops.UsageFactRejection
    WHERE OwnerId=@OwnerId AND OrganizationId=@OrganizationId AND RepositoryId=@RepositoryId
      AND MonthStartUtc=@MonthStartUtc AND IsActive=1
)
    SELECT CAST('Active scoped usage rejection blocks billing close.' AS nvarchar(400));
ELSE
    SELECT CAST(NULL AS nvarchar(400));
"""

            addScope check scope
            let! result = check.ExecuteScalarAsync cancellationToken
            return if Convert.IsDBNull result then None else Some(result :?> string)
        }

    /// Writes or clears the bounded retry diagnostic without changing a nonterminal period to a failure state.
    let setDiagnosticAsync
        (connection: SqlConnection)
        (transaction: SqlTransaction)
        (periodId: Guid)
        (diagnostic: string option)
        (cancellationToken: CancellationToken)
        =
        task {
            use update =
                command
                    connection
                    transaction
                    """
UPDATE ops.BillingPeriod
SET RetryDiagnostic=@Diagnostic,
    RetryDiagnosticAtUtc=CASE WHEN @Diagnostic IS NULL THEN NULL ELSE SYSUTCDATETIME() END,
    UpdatedAtUtc=SYSUTCDATETIME()
WHERE BillingPeriodId=@BillingPeriodId AND State IN (0,1);
"""

            update.Parameters.Add("@BillingPeriodId", SqlDbType.UniqueIdentifier).Value <- periodId
            let parameter = update.Parameters.Add("@Diagnostic", SqlDbType.NVarChar, 400)

            parameter.Value <-
                match diagnostic with
                | Some value -> box value
                | None -> DBNull.Value

            let! _ = update.ExecuteNonQueryAsync cancellationToken
            return ()
        }

    /// Reads all source facts with their independently effective pricing prerequisites using the shared preview query.
    let readPricedFactsAsync
        (connection: SqlConnection)
        (transaction: SqlTransaction)
        (scope: BillingCompletenessScope)
        (cancellationToken: CancellationToken)
        =
        task {
            use read = command connection transaction OperationsChargePreviewSql.SelectSourceAndPricing
            read.Parameters.Add("@OwnerId", SqlDbType.UniqueIdentifier).Value <- scope.OwnerId
            read.Parameters.Add("@OrganizationId", SqlDbType.UniqueIdentifier).Value <- scope.OrganizationId
            read.Parameters.Add("@RepositoryId", SqlDbType.UniqueIdentifier).Value <- scope.RepositoryId
            read.Parameters.Add("@PeriodFromUtc", SqlDbType.DateTime2).Value <- scope.MonthStart.ToDateTimeUtc()

            read.Parameters.Add("@PeriodToUtc", SqlDbType.DateTime2).Value <- (BillingCompletenessScope.nextMonthStart scope)
                .ToDateTimeUtc()

            use! reader = read.ExecuteReaderAsync cancellationToken
            let rows = ResizeArray<ChargePreviewPricedFact>()
            let mutable reading = true

            while reading do
                let! hasRow = reader.ReadAsync cancellationToken
                reading <- hasRow

                if hasRow then
                    let usageFactId = reader.GetGuid 0

                    match
                        ChargePreviewCalculation.missingPrerequisite
                            (not (reader.IsDBNull 4))
                            (not (reader.IsDBNull 6))
                            (not (reader.IsDBNull 7))
                            (not (reader.IsDBNull 9))
                        with
                    | Some prerequisite ->
                        let previewScope: ChargePreviewScope =
                            {
                                OwnerId = scope.OwnerId
                                OrganizationId = scope.OrganizationId
                                RepositoryId = scope.RepositoryId
                                PeriodFromUtc = scope.MonthStart.ToDateTimeUtc()
                                PeriodToUtc =
                                    (BillingCompletenessScope.nextMonthStart scope)
                                        .ToDateTimeUtc()
                            }

                        raise (ChargePreviewRebuildException(previewScope, usageFactId, prerequisite))
                    | None ->
                        let pricedFact: ChargePreviewPricedFact =
                            {
                                UsageFactId = usageFactId
                                FactKind = reader.GetInt32 1
                                Quantity = reader.GetInt64 2
                                ObservedAtUtc = reader.GetDateTime 3
                                PricingAssignmentId = reader.GetGuid 4
                                PricingPlanId = reader.GetGuid 6
                                BillableUsageKindMappingId = reader.GetGuid 7
                                BillableUsageKind = reader.GetInt32 8
                                PricingRateId = reader.GetGuid 9
                                CurrencyCode = reader.GetString 10
                                UnitName = reader.GetString 11
                                UnitQuantity = reader.GetInt64 12
                                UnitPriceMicros = reader.GetInt64 13
                                EffectiveFromUtc = reader.GetDateTime 14
                                EffectiveToUtc = reader.GetDateTime 15
                            }

                        rows.Add pricedFact

            return rows |> Seq.toList
        }

    /// Verifies that a zero-fact period still has one complete current pricing grain before it can be closed.
    let zeroFactPricingCoverageDiagnosticAsync
        (connection: SqlConnection)
        (transaction: SqlTransaction)
        (scope: BillingCompletenessScope)
        (cancellationToken: CancellationToken)
        =
        task {
            use check =
                command
                    connection
                    transaction
                    """
IF NOT EXISTS
(
    SELECT 1
    FROM ops.PricingAssignment AS assignment
    INNER JOIN ops.PricingPlan AS pricingPlan ON pricingPlan.PricingPlanId=assignment.PricingPlanId
    WHERE assignment.OwnerId=@OwnerId AND assignment.OrganizationId=@OrganizationId AND assignment.RepositoryId=@RepositoryId
      AND assignment.EffectiveFromUtc<=@MonthStartUtc
      AND (assignment.EffectiveToUtc IS NULL OR assignment.EffectiveToUtc>=@NextMonthStartUtc)
      AND pricingPlan.EffectiveFromUtc<=@MonthStartUtc
      AND (pricingPlan.EffectiveToUtc IS NULL OR pricingPlan.EffectiveToUtc>=@NextMonthStartUtc)
)
    SELECT CAST('Complete pricing assignment and plan coverage is required for zero-fact billing close.' AS nvarchar(400));
ELSE IF NOT EXISTS
(
    SELECT 1
    FROM ops.BillableUsageKindMapping AS mapping
    WHERE mapping.EffectiveFromUtc<=@MonthStartUtc
      AND (mapping.EffectiveToUtc IS NULL OR mapping.EffectiveToUtc>=@NextMonthStartUtc)
)
    SELECT CAST('Complete billable usage-kind mapping coverage is required for zero-fact billing close.' AS nvarchar(400));
ELSE IF EXISTS
(
    SELECT 1
    FROM ops.BillableUsageKindMapping AS mapping
    WHERE mapping.EffectiveFromUtc<=@MonthStartUtc
      AND (mapping.EffectiveToUtc IS NULL OR mapping.EffectiveToUtc>=@NextMonthStartUtc)
      AND NOT EXISTS
      (
          SELECT 1
          FROM ops.PricingAssignment AS assignment
          INNER JOIN ops.PricingPlan AS pricingPlan ON pricingPlan.PricingPlanId=assignment.PricingPlanId
          INNER JOIN ops.PricingRate AS rate ON rate.PricingPlanId=pricingPlan.PricingPlanId
          WHERE assignment.OwnerId=@OwnerId AND assignment.OrganizationId=@OrganizationId AND assignment.RepositoryId=@RepositoryId
            AND assignment.EffectiveFromUtc<=@MonthStartUtc
            AND (assignment.EffectiveToUtc IS NULL OR assignment.EffectiveToUtc>=@NextMonthStartUtc)
            AND pricingPlan.EffectiveFromUtc<=@MonthStartUtc
            AND (pricingPlan.EffectiveToUtc IS NULL OR pricingPlan.EffectiveToUtc>=@NextMonthStartUtc)
            AND rate.BillableUsageKind=mapping.BillableUsageKind
            AND rate.EffectiveFromUtc<=@MonthStartUtc
            AND (rate.EffectiveToUtc IS NULL OR rate.EffectiveToUtc>=@NextMonthStartUtc)
      )
)
    SELECT CAST('Complete pricing-rate coverage is required for zero-fact billing close.' AS nvarchar(400));
ELSE
    SELECT CAST(NULL AS nvarchar(400));
"""

            addScope check scope
            let! result = check.ExecuteScalarAsync cancellationToken
            return if Convert.IsDBNull result then None else Some(result :?> string)
        }

    /// Replaces only the current period preview after successful calculation has produced every replacement line.
    let replacePreviewAsync
        (connection: SqlConnection)
        (transaction: SqlTransaction)
        (scope: BillingCompletenessScope)
        (lines: ChargePreviewLineEntity array)
        (cancellationToken: CancellationToken)
        =
        task {
            use delete = command connection transaction OperationsChargePreviewSql.DeleteScope
            delete.Parameters.Add("@OwnerId", SqlDbType.UniqueIdentifier).Value <- scope.OwnerId
            delete.Parameters.Add("@OrganizationId", SqlDbType.UniqueIdentifier).Value <- scope.OrganizationId
            delete.Parameters.Add("@RepositoryId", SqlDbType.UniqueIdentifier).Value <- scope.RepositoryId
            delete.Parameters.Add("@PeriodFromUtc", SqlDbType.DateTime2).Value <- scope.MonthStart.ToDateTimeUtc()

            delete.Parameters.Add("@PeriodToUtc", SqlDbType.DateTime2).Value <- (BillingCompletenessScope.nextMonthStart scope)
                .ToDateTimeUtc()

            let! _ = delete.ExecuteNonQueryAsync cancellationToken
            let mutable index = 0

            while index < lines.Length do
                let line = lines[index]

                use insert =
                    command
                        connection
                        transaction
                        "INSERT INTO ops.ChargePreviewLine (ChargePreviewLineId,OwnerId,OrganizationId,RepositoryId,PeriodFromUtc,PeriodToUtc,FactKind,BillableUsageKindMappingId,BillableUsageKind,PricingAssignmentId,PricingPlanId,PricingRateId,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc,EffectiveToUtc,TotalQuantity,ChargeMicros) VALUES (@Id,@OwnerId,@OrganizationId,@RepositoryId,@PeriodFromUtc,@PeriodToUtc,@FactKind,@MappingId,@BillableKind,@AssignmentId,@PlanId,@RateId,@Currency,@UnitName,@UnitQuantity,@UnitPrice,@EffectiveFrom,@EffectiveTo,@TotalQuantity,@Charge);"

                let add name dbType value = insert.Parameters.Add(name, dbType).Value <- value
                add "@Id" SqlDbType.UniqueIdentifier line.ChargePreviewLineId
                add "@OwnerId" SqlDbType.UniqueIdentifier line.OwnerId
                add "@OrganizationId" SqlDbType.UniqueIdentifier line.OrganizationId
                add "@RepositoryId" SqlDbType.UniqueIdentifier line.RepositoryId
                add "@PeriodFromUtc" SqlDbType.DateTime2 line.PeriodFromUtc
                add "@PeriodToUtc" SqlDbType.DateTime2 line.PeriodToUtc
                add "@FactKind" SqlDbType.Int line.FactKind
                add "@MappingId" SqlDbType.UniqueIdentifier line.BillableUsageKindMappingId
                add "@BillableKind" SqlDbType.Int line.BillableUsageKind
                add "@AssignmentId" SqlDbType.UniqueIdentifier line.PricingAssignmentId
                add "@PlanId" SqlDbType.UniqueIdentifier line.PricingPlanId
                add "@RateId" SqlDbType.UniqueIdentifier line.PricingRateId
                insert.Parameters.Add("@Currency", SqlDbType.VarChar, 3).Value <- line.CurrencyCode
                insert.Parameters.Add("@UnitName", SqlDbType.NVarChar, OperationsPricingSql.UnitNameMaxLength).Value <- line.UnitName
                add "@UnitQuantity" SqlDbType.BigInt line.UnitQuantity
                add "@UnitPrice" SqlDbType.BigInt line.UnitPriceMicros
                add "@EffectiveFrom" SqlDbType.DateTime2 line.EffectiveFromUtc
                add "@EffectiveTo" SqlDbType.DateTime2 line.EffectiveToUtc
                add "@TotalQuantity" SqlDbType.BigInt line.TotalQuantity
                add "@Charge" SqlDbType.BigInt line.ChargeMicros
                let! _ = insert.ExecuteNonQueryAsync cancellationToken
                index <- index + 1

            return ()
        }

    /// Hashes canonical fact or preview-line values so close evidence remains reproducible after future pricing edits.
    let digest values =
        values
        |> String.concat "\n"
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString

    /// Reads canonical accepted-fact identities from the immutable raw source for close evidence.
    let acceptedFactDigestAsync
        (connection: SqlConnection)
        (transaction: SqlTransaction)
        (scope: BillingCompletenessScope)
        (cancellationToken: CancellationToken)
        =
        task {
            use read =
                command
                    connection
                    transaction
                    "SELECT UsageFactId,FactKind,Quantity,ObservedAtUtc FROM ops.RawUsageFact WHERE OwnerId=@OwnerId AND OrganizationId=@OrganizationId AND RepositoryId=@RepositoryId AND ObservedAtUtc>=@MonthStartUtc AND ObservedAtUtc<@NextMonthStartUtc ORDER BY ObservedAtUtc,UsageFactId;"

            addScope read scope
            use! reader = read.ExecuteReaderAsync cancellationToken
            let values = ResizeArray<string>()
            let mutable reading = true

            while reading do
                let! hasRow = reader.ReadAsync cancellationToken
                reading <- hasRow

                if hasRow then
                    values.Add($"{reader.GetGuid(0):D}|{reader.GetInt32(1)}|{reader.GetInt64(2)}|{reader.GetDateTime(3).Ticks}")

            return values |> Seq.toList |> digest
        }

    /// Posts each calculated line once, then records close evidence and the terminal state in the same transaction.
    let postCloseAsync
        (connection: SqlConnection)
        (transaction: SqlTransaction)
        (periodId: Guid)
        (request: BillingPeriodCloseRequest)
        (closedAt: DateTime)
        (lines: ChargePreviewLineEntity array)
        (cancellationToken: CancellationToken)
        =
        task {
            let mutable index = 0

            while index < lines.Length do
                let line = lines[index]

                use insert =
                    command
                        connection
                        transaction
                        "INSERT INTO ops.Charge (ChargeId,BillingPeriodId,ChargePreviewLineId,CurrencyCode,ChargeMicros) VALUES (@ChargeId,@BillingPeriodId,@ChargePreviewLineId,@CurrencyCode,@ChargeMicros);"

                insert.Parameters.Add("@ChargeId", SqlDbType.UniqueIdentifier).Value <- chargeId periodId line.ChargePreviewLineId
                insert.Parameters.Add("@BillingPeriodId", SqlDbType.UniqueIdentifier).Value <- periodId
                insert.Parameters.Add("@ChargePreviewLineId", SqlDbType.UniqueIdentifier).Value <- line.ChargePreviewLineId
                insert.Parameters.Add("@CurrencyCode", SqlDbType.VarChar, 3).Value <- line.CurrencyCode
                insert.Parameters.Add("@ChargeMicros", SqlDbType.BigInt).Value <- line.ChargeMicros
                let! _ = insert.ExecuteNonQueryAsync cancellationToken
                index <- index + 1

            do! transactionInterleaving.AfterLedgerInsertionAsync cancellationToken

            let! factDigest = acceptedFactDigestAsync connection transaction request.Scope cancellationToken

            let previewDigest =
                lines
                |> Seq.map (fun line -> $"{line.ChargePreviewLineId:D}|{line.CurrencyCode}|{line.ChargeMicros}|{line.TotalQuantity}")
                |> Seq.toList
                |> digest

            use evidence =
                command
                    connection
                    transaction
                    "INSERT INTO ops.BillingPeriodCloseEvidence (BillingPeriodId,AcceptedFactDigestSha256Hex,PricingPreviewDigestSha256Hex,ClosedAtUtc,ScheduledOperationProvenance) VALUES (@BillingPeriodId,@FactDigest,@PreviewDigest,@ClosedAtUtc,@Provenance);"

            evidence.Parameters.Add("@BillingPeriodId", SqlDbType.UniqueIdentifier).Value <- periodId
            evidence.Parameters.Add("@FactDigest", SqlDbType.Char, 64).Value <- factDigest
            evidence.Parameters.Add("@PreviewDigest", SqlDbType.Char, 64).Value <- previewDigest
            evidence.Parameters.Add("@ClosedAtUtc", SqlDbType.DateTime2).Value <- closedAt
            evidence.Parameters.Add("@Provenance", SqlDbType.NVarChar, 200).Value <- request.ScheduledOperationProvenance
            let! _ = evidence.ExecuteNonQueryAsync cancellationToken
            do! transactionInterleaving.AfterCloseEvidenceStagedAsync cancellationToken

            use close =
                command
                    connection
                    transaction
                    "UPDATE ops.BillingPeriod SET State=2,RetryDiagnostic=NULL,RetryDiagnosticAtUtc=NULL,UpdatedAtUtc=SYSUTCDATETIME() WHERE BillingPeriodId=@BillingPeriodId AND State IN (0,1);"

            close.Parameters.Add("@BillingPeriodId", SqlDbType.UniqueIdentifier).Value <- periodId
            let! affected = close.ExecuteNonQueryAsync cancellationToken

            if affected <> 1 then
                invalidOp "Billing-period state changed before terminal close could commit."

            return ()
        }

    /// Rolls back a failed close without masking its originating exception.
    let rollbackAsync (transaction: SqlTransaction) =
        task {
            try
                do! transaction.RollbackAsync CancellationToken.None
            with
            | _ -> ()
        }

    /// Runs either preview or close in one scope-locked transaction and converts expected retry conditions into durable diagnostics.
    let executeAsync (close: bool) (request: BillingPeriodCloseRequest) (cancellationToken: CancellationToken) =
        task {
            if String.IsNullOrWhiteSpace request.ScheduledOperationProvenance
               || request.ScheduledOperationProvenance.Length > 200 then
                invalidArg (nameof request) "Scheduled operation provenance is required and must be 200 characters or fewer."

            match BillingCompletenessScope.validate request.Scope with
            | Error errors -> invalidArg (nameof request) (String.Join("; ", errors))
            | Ok _ -> ()

            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync cancellationToken
            use! rawTransaction = connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            use transaction = rawTransaction :?> SqlTransaction

            try
                do! acquireScopeLockAsync connection transaction request.Scope cancellationToken
                let! (periodId, state) = ensurePeriodAsync connection transaction request.Scope cancellationToken

                if state = BillingPeriodState.Closed then
                    use count = command connection transaction "SELECT COUNT(*) FROM ops.Charge WHERE BillingPeriodId=@BillingPeriodId;"
                    count.Parameters.Add("@BillingPeriodId", SqlDbType.UniqueIdentifier).Value <- periodId
                    let! chargeCount = count.ExecuteScalarAsync cancellationToken
                    do! transaction.CommitAsync cancellationToken
                    return Closed(periodId, Convert.ToInt32 chargeCount)
                else
                    let! now = databaseUtcNowAsync connection transaction cancellationToken

                    let threshold =
                        (BillingCompletenessScope.nextMonthStart request.Scope)
                            .ToDateTimeUtc()
                            .AddHours(if close then 72.0 else 24.0)

                    if now < threshold then
                        do! transaction.CommitAsync cancellationToken
                        return NotEligible
                    else
                        let! blocked = completenessDiagnosticAsync connection transaction request.Scope cancellationToken

                        match blocked with
                        | Some diagnostic ->
                            do! setDiagnosticAsync connection transaction periodId (Some diagnostic) cancellationToken
                            do! transaction.CommitAsync cancellationToken
                            return Blocked diagnostic
                        | None ->
                            let! pricedFacts = readPricedFactsAsync connection transaction request.Scope cancellationToken

                            let! zeroFactPricingDiagnostic =
                                if List.isEmpty pricedFacts then
                                    zeroFactPricingCoverageDiagnosticAsync connection transaction request.Scope cancellationToken
                                else
                                    Task.FromResult None

                            match zeroFactPricingDiagnostic with
                            | Some diagnostic -> raise (ZeroFactPricingCoverageException(diagnostic))
                            | None -> ()

                            let previewScope: ChargePreviewScope =
                                {
                                    OwnerId = request.Scope.OwnerId
                                    OrganizationId = request.Scope.OrganizationId
                                    RepositoryId = request.Scope.RepositoryId
                                    PeriodFromUtc = request.Scope.MonthStart.ToDateTimeUtc()
                                    PeriodToUtc =
                                        (BillingCompletenessScope.nextMonthStart request.Scope)
                                            .ToDateTimeUtc()
                                }

                            let lines = ChargePreviewCalculation.buildLines previewScope pricedFacts
                            do! replacePreviewAsync connection transaction request.Scope lines cancellationToken
                            do! transactionInterleaving.AfterPreviewReplacementAsync cancellationToken

                            if close then
                                do! postCloseAsync connection transaction periodId request now lines cancellationToken
                                do! transaction.CommitAsync cancellationToken
                                return Closed(periodId, lines.Length)
                            else
                                use provisional =
                                    command
                                        connection
                                        transaction
                                        "UPDATE ops.BillingPeriod SET State=1,RetryDiagnostic=NULL,RetryDiagnosticAtUtc=NULL,UpdatedAtUtc=SYSUTCDATETIME() WHERE BillingPeriodId=@BillingPeriodId AND State IN (0,1);"

                                provisional.Parameters.Add("@BillingPeriodId", SqlDbType.UniqueIdentifier).Value <- periodId
                                let! _ = provisional.ExecuteNonQueryAsync cancellationToken
                                do! transaction.CommitAsync cancellationToken
                                return Provisional(periodId, lines.Length)
            with
            | :? ChargePreviewRebuildException as ex ->
                do! setDiagnosticAsync connection transaction (billingPeriodId request.Scope) (Some ex.Message) cancellationToken
                do! transaction.CommitAsync cancellationToken
                return Blocked ex.Message
            | :? OverflowException as ex ->
                do! setDiagnosticAsync connection transaction (billingPeriodId request.Scope) (Some ex.Message) cancellationToken
                do! transaction.CommitAsync cancellationToken
                return Blocked ex.Message
            | :? ZeroFactPricingCoverageException as ex ->
                do! setDiagnosticAsync connection transaction (billingPeriodId request.Scope) (Some ex.Message) cancellationToken
                do! transaction.CommitAsync cancellationToken
                return Blocked ex.Message
            | ex ->
                do! rollbackAsync transaction
                return raise ex
        }

    interface IBillingPeriodCloser with
        member _.PreviewAsync(request, cancellationToken) = executeAsync false request cancellationToken
        member _.CloseAsync(request, cancellationToken) = executeAsync true request cancellationToken

    /// Creates the production closer without test-only failure injection.
    new(connectionString: string) =
        SqlBillingPeriodCloser(connectionString, NoBillingPeriodCloseTransactionInterleaving() :> IBillingPeriodCloseTransactionInterleaving)

    /// Creates a real-SQL closer that pauses or fails only at a named transaction-local proof seam.
    static member internal CreateForTest(connectionString, transactionInterleaving) = SqlBillingPeriodCloser(connectionString, transactionInterleaving)
