namespace Grace.Operations.Data

open Grace.Types.Usage
open Microsoft.Data.SqlClient
open NodaTime
open System
open System.Data
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

/// Names the durable lifecycle values for a direct billing-period close.
[<RequireQualifiedAccess>]
type BillingPeriodState =
    | Open = 0
    | Provisional = 1
    | Closed = 2

/// Carries an exact billing scope and the scheduled-operation provenance recorded with a close.
type BillingPeriodCloseRequest = { Scope: BillingCompletenessScope; ScheduledOperationProvenance: string }

/// Reports the observable outcome of an attempted preview or final close.
type BillingPeriodCloseResult =
    | NotEligible
    | Blocked of diagnostic: string
    | Provisional of billingPeriodId: Guid * previewLineCount: int
    | Closed of billingPeriodId: Guid * chargeCount: int

/// Holds the pure database-time threshold policy separately from the SQL clock source and ordering proof.
[<RequireQualifiedAccess>]
module internal BillingPeriodCloseEligibility =

    /// Accepts equality at the documented preview or final-close boundary and rejects the preceding SQL tick.
    let isEligible isFinal (nextMonthStartUtc: DateTime) (databaseUtcNow: DateTime) =
        let threshold = nextMonthStartUtc.AddHours(if isFinal then 72.0 else 24.0)
        databaseUtcNow >= threshold

/// Signals that an empty close lacks one required collective pricing grain.
type private ZeroFactPricingCoverageException(diagnostic: string) =
    inherit InvalidOperationException(diagnostic)

/// Exposes the internal transaction stages used by real-SQL rollback and contention proofs.
type internal IBillingPeriodCloseTransactionInterleaving =
    /// Records the production SQL session immediately before it attempts the exact shared lock resource.
    abstract BeforeScopeLockAcquisitionAsync: sessionId: int * resource: string * cancellationToken: CancellationToken -> Task

    /// Runs after the shared scope lock has been granted and before database time is read.
    abstract AfterScopeLockGrantedAsync: sessionId: int * resource: string * cancellationToken: CancellationToken -> Task

    /// Records the database-owned close instant read after the shared lock grant.
    abstract AfterDatabaseClockReadAsync: sessionId: int * databaseUtcNow: DateTime * cancellationToken: CancellationToken -> Task

    /// Runs after replacement preview rows are staged but before immutable postings are inserted.
    abstract AfterPreviewReplacementAsync: cancellationToken: CancellationToken -> Task

    /// Runs after immutable postings are staged but before close evidence is inserted.
    abstract AfterChargeInsertionAsync: cancellationToken: CancellationToken -> Task

    /// Runs after immutable close evidence is staged but before the period is marked closed.
    abstract AfterCloseEvidenceStagedAsync: cancellationToken: CancellationToken -> Task

    /// Runs after one required kind's pricing boundaries are captured and before its effective rows are selected.
    abstract AfterZeroFactPricingBoundaryEnumerationAsync: factKind: UsageFactKind * cancellationToken: CancellationToken -> Task

    /// Runs immediately before a captured boundary is evaluated against the same serializable pricing view.
    abstract BeforeZeroFactPricingSelectionAsync: factKind: UsageFactKind * boundary: DateTime * cancellationToken: CancellationToken -> Task

/// Provides production execution with no injected work between close-transaction stages.
type private NoBillingPeriodCloseTransactionInterleaving() =
    interface IBillingPeriodCloseTransactionInterleaving with
        member _.BeforeScopeLockAcquisitionAsync(_, _, _) = Task.CompletedTask
        member _.AfterScopeLockGrantedAsync(_, _, _) = Task.CompletedTask
        member _.AfterDatabaseClockReadAsync(_, _, _) = Task.CompletedTask
        member _.AfterPreviewReplacementAsync _ = Task.CompletedTask
        member _.AfterChargeInsertionAsync _ = Task.CompletedTask
        member _.AfterCloseEvidenceStagedAsync _ = Task.CompletedTask
        member _.AfterZeroFactPricingBoundaryEnumerationAsync(_, _) = Task.CompletedTask
        member _.BeforeZeroFactPricingSelectionAsync(_, _, _) = Task.CompletedTask

/// Adapts close-transaction test stages to the pricing evaluator without widening the evaluator's SQL contract.
type private ZeroFactPricingCoverageInterleaving(transactionInterleaving: IBillingPeriodCloseTransactionInterleaving) =
    interface IZeroFactPricingCoverageInterleaving with
        member _.AfterBoundaryEnumerationAsync(factKind, cancellationToken) =
            transactionInterleaving.AfterZeroFactPricingBoundaryEnumerationAsync(factKind, cancellationToken)

        member _.BeforeSelectionAsync(factKind, boundary, cancellationToken) =
            transactionInterleaving.BeforeZeroFactPricingSelectionAsync(factKind, boundary, cancellationToken)

/// Reads the database clock on the connection and transaction that already hold the scope lock.
type internal IBillingPeriodCloseClock =
    /// Returns SQL Server UTC time used by one close decision.
    abstract UtcNowAsync: connection: SqlConnection * transaction: SqlTransaction * cancellationToken: CancellationToken -> Task<DateTime>

/// Uses the production SQL Server clock with no process-time alternative.
type private DatabaseBillingPeriodCloseClock(transactionInterleaving: IBillingPeriodCloseTransactionInterleaving) =
    interface IBillingPeriodCloseClock with
        member _.UtcNowAsync(connection, transaction, cancellationToken) =
            task {
                use clockCommand = connection.CreateCommand()
                clockCommand.Transaction <- transaction
                clockCommand.CommandText <- "SELECT @@SPID, SYSUTCDATETIME();"
                use! reader = clockCommand.ExecuteReaderAsync cancellationToken
                let! hasRow = reader.ReadAsync cancellationToken

                if not hasRow then
                    invalidOp "SQL Server did not return a billing-period close clock value."

                let sessionId = Convert.ToInt32(reader.GetValue 0, CultureInfo.InvariantCulture)
                let databaseUtcNow = reader.GetDateTime 1
                do! transactionInterleaving.AfterDatabaseClockReadAsync(sessionId, databaseUtcNow, cancellationToken)
                return databaseUtcNow
            }

/// Exposes the bounded preview and final-close calls used by later scheduled-operation hosting.
type IBillingPeriodCloser =
    /// Rebuilds a provisional preview when database time has reached the preview threshold.
    abstract PreviewAsync: request: BillingPeriodCloseRequest * cancellationToken: CancellationToken -> Task<BillingPeriodCloseResult>

    /// Rebuilds a final preview and atomically posts its immutable initial charges when eligible.
    abstract CloseAsync: request: BillingPeriodCloseRequest * cancellationToken: CancellationToken -> Task<BillingPeriodCloseResult>

/// Rebuilds and closes one nonempty billing scope under the shared transaction-owned SQL lock.
type SqlBillingPeriodCloser
    private
    (
        connectionString: string,
        transactionInterleaving: IBillingPeriodCloseTransactionInterleaving,
        clock: IBillingPeriodCloseClock
    ) =

    /// Derives a stable billing period identity from the complete owner, repository, and UTC month tuple.
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

        Guid(
            SHA256
                .HashData(Encoding.UTF8.GetBytes canonical)
                .AsSpan(0, 16)
        )

    /// Derives a stable immutable initial-posting identity from its period and preview-line identities.
    let chargeId periodId previewLineId =
        let canonical = $"Grace.Operations.InitialCharge.v1|{periodId:D}|{previewLineId:D}"

        Guid(
            SHA256
                .HashData(Encoding.UTF8.GetBytes canonical)
                .AsSpan(0, 16)
        )

    /// Adds the exact owner/repository/half-open-month tuple used by all close SQL commands.
    let addScope (command: SqlCommand) (scope: BillingCompletenessScope) =
        command.Parameters.Add("@OwnerId", SqlDbType.UniqueIdentifier).Value <- scope.OwnerId
        command.Parameters.Add("@OrganizationId", SqlDbType.UniqueIdentifier).Value <- scope.OrganizationId
        command.Parameters.Add("@RepositoryId", SqlDbType.UniqueIdentifier).Value <- scope.RepositoryId
        command.Parameters.Add("@MonthStartUtc", SqlDbType.DateTime2).Value <- scope.MonthStart.ToDateTimeUtc()

        command.Parameters.Add("@NextMonthStartUtc", SqlDbType.DateTime2).Value <- (BillingCompletenessScope.nextMonthStart scope)
            .ToDateTimeUtc()

    /// Creates a SQL command bound to the active close transaction.
    let command (connection: SqlConnection) (transaction: SqlTransaction) text =
        let value = connection.CreateCommand()
        value.Transaction <- transaction
        value.CommandText <- text
        value

    /// Rolls back without hiding the error that caused the close to abort.
    let rollbackIgnoringFailureAsync (transaction: SqlTransaction) =
        task {
            try
                do! transaction.RollbackAsync CancellationToken.None
            with
            | _ -> ()
        }

    /// Acquires exactly the journal/completeness lock identity established by issue #879.
    let acquireScopeLockAsync connection transaction scope cancellationToken =
        task {
            use sessionCommand = command connection transaction "SELECT @@SPID;"
            let! sessionId = sessionCommand.ExecuteScalarAsync cancellationToken
            let resource = BillingCompletenessScope.databaseLockIdentity scope
            do! transactionInterleaving.BeforeScopeLockAcquisitionAsync(Convert.ToInt32 sessionId, resource, cancellationToken)
            use lockCommand = command connection transaction OperationsUsageSql.AcquireBillingCompletenessScopeLock
            lockCommand.Parameters.Add("@BillingCompletenessLockResource", SqlDbType.NVarChar, 255).Value <- BillingCompletenessScope.databaseLockIdentity scope
            lockCommand.Parameters.Add("@BillingCompletenessLockTimeoutMilliseconds", SqlDbType.Int).Value <- 60000
            let! _ = lockCommand.ExecuteNonQueryAsync cancellationToken
            do! transactionInterleaving.AfterScopeLockGrantedAsync(Convert.ToInt32 sessionId, resource, cancellationToken)
        }

    /// Inserts the deterministic period if needed, then returns its state while its exact row remains locked.
    let ensurePeriodAsync connection transaction scope cancellationToken =
        task {
            use insert =
                command
                    connection
                    transaction
                    """
INSERT INTO ops.BillingPeriod (BillingPeriodId,OwnerId,OrganizationId,RepositoryId,MonthStartUtc,NextMonthStartUtc,State)
SELECT @BillingPeriodId,@OwnerId,@OrganizationId,@RepositoryId,@MonthStartUtc,@NextMonthStartUtc,0
WHERE NOT EXISTS (SELECT 1 FROM ops.BillingPeriod WITH (UPDLOCK,HOLDLOCK) WHERE OwnerId=@OwnerId AND OrganizationId=@OrganizationId AND RepositoryId=@RepositoryId AND MonthStartUtc=@MonthStartUtc AND NextMonthStartUtc=@NextMonthStartUtc);
"""

            addScope insert scope
            insert.Parameters.Add("@BillingPeriodId", SqlDbType.UniqueIdentifier).Value <- billingPeriodId scope
            let! _ = insert.ExecuteNonQueryAsync cancellationToken

            use read =
                command
                    connection
                    transaction
                    "SELECT BillingPeriodId,State FROM ops.BillingPeriod WITH (UPDLOCK,HOLDLOCK) WHERE OwnerId=@OwnerId AND OrganizationId=@OrganizationId AND RepositoryId=@RepositoryId AND MonthStartUtc=@MonthStartUtc AND NextMonthStartUtc=@NextMonthStartUtc;"

            addScope read scope
            use! reader = read.ExecuteReaderAsync cancellationToken
            let! exists = reader.ReadAsync cancellationToken

            if not exists then
                invalidOp "The exact billing period could not be materialized under its scope lock."

            return reader.GetGuid 0, enum<BillingPeriodState> (Convert.ToInt32(reader.GetValue 1, CultureInfo.InvariantCulture))
        }

    /// Returns the first committed exact-scope blocker while the shared lock prevents a close race.
    let completenessDiagnosticAsync connection transaction scope cancellationToken =
        task {
            use check =
                command
                    connection
                    transaction
                    """
IF EXISTS (SELECT 1 FROM ops.UsageFactJournal WHERE OwnerId=@OwnerId AND OrganizationId=@OrganizationId AND RepositoryId=@RepositoryId AND ObservedAtUtc>=@MonthStartUtc AND ObservedAtUtc<@NextMonthStartUtc AND State IN (0,2))
    SELECT CAST('Unresolved UsageFact journal rows block billing close.' AS nvarchar(400));
ELSE IF EXISTS (SELECT 1 FROM ops.UsageFactRejection WHERE OwnerId=@OwnerId AND OrganizationId=@OrganizationId AND RepositoryId=@RepositoryId AND MonthStartUtc=@MonthStartUtc AND IsActive=1)
    SELECT CAST('Active scoped usage rejection blocks billing close.' AS nvarchar(400));
ELSE SELECT CAST(NULL AS nvarchar(400));
"""

            addScope check scope
            let! value = check.ExecuteScalarAsync cancellationToken
            return if Convert.IsDBNull value then None else Some(value :?> string)
        }

    /// Writes the bounded retry diagnostic only while the period remains nonterminal.
    let setDiagnosticAsync connection transaction periodId diagnostic cancellationToken =
        task {
            use update =
                command
                    connection
                    transaction
                    "UPDATE ops.BillingPeriod SET RetryDiagnostic=@Diagnostic,RetryDiagnosticAtUtc=CASE WHEN @Diagnostic IS NULL THEN NULL ELSE SYSUTCDATETIME() END,UpdatedAtUtc=SYSUTCDATETIME() WHERE BillingPeriodId=@BillingPeriodId AND State IN (0,1);"

            update.Parameters.Add("@BillingPeriodId", SqlDbType.UniqueIdentifier).Value <- periodId
            let parameter = update.Parameters.Add("@Diagnostic", SqlDbType.NVarChar, 400)

            parameter.Value <-
                diagnostic
                |> Option.map box
                |> Option.defaultValue DBNull.Value

            let! _ = update.ExecuteNonQueryAsync cancellationToken
            return ()
        }

    /// Reads current committed facts and their complete current pricing prerequisites inside the close transaction.
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
            let facts = ResizeArray<ChargePreviewPricedFact>()
            let mutable reading = true

            while reading do
                let! hasRow = reader.ReadAsync cancellationToken
                reading <- hasRow

                if hasRow then
                    let prerequisite =
                        ChargePreviewCalculation.missingPrerequisite
                            (not (reader.IsDBNull 4))
                            (not (reader.IsDBNull 6))
                            (not (reader.IsDBNull 7))
                            (not (reader.IsDBNull 9))

                    match prerequisite with
                    | Some missing ->
                        let previewScope =
                            {
                                OwnerId = scope.OwnerId
                                OrganizationId = scope.OrganizationId
                                RepositoryId = scope.RepositoryId
                                PeriodFromUtc = scope.MonthStart.ToDateTimeUtc()
                                PeriodToUtc =
                                    (BillingCompletenessScope.nextMonthStart scope)
                                        .ToDateTimeUtc()
                            }

                        raise (ChargePreviewRebuildException(previewScope, reader.GetGuid 0, missing))
                    | None ->
                        facts.Add
                            {
                                UsageFactId = reader.GetGuid 0
                                FactKind = Convert.ToInt32(reader.GetValue 1, CultureInfo.InvariantCulture)
                                Quantity = reader.GetInt64 2
                                ObservedAtUtc = reader.GetDateTime 3
                                PricingAssignmentId = reader.GetGuid 4
                                PricingPlanId = reader.GetGuid 6
                                BillableUsageKindMappingId = reader.GetGuid 7
                                BillableUsageKind = Convert.ToInt32(reader.GetValue 8, CultureInfo.InvariantCulture)
                                PricingRateId = reader.GetGuid 9
                                CurrencyCode = reader.GetString 10
                                UnitName = reader.GetString 11
                                UnitQuantity = reader.GetInt64 12
                                UnitPriceMicros = reader.GetInt64 13
                                EffectiveFromUtc = reader.GetDateTime 14
                                EffectiveToUtc = reader.GetDateTime 15
                            }

            return facts |> Seq.toList
        }

    /// Replaces only this period's rebuildable preview rows after every new line has been calculated.
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

    /// Hashes a deterministic sequence of canonical evidence records using uppercase SHA-256 hex.
    let digest values =
        values
        |> String.concat "\n"
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString

    /// Reads the independent accepted-fact digest from committed raw facts after the final preview rebuild.
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
                    values.Add(
                        $"{reader.GetGuid(0):D}|{Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture)}|{reader.GetInt64(2)}|{reader.GetDateTime(3).Ticks}"
                    )

            return values |> Seq.toList |> digest
        }

    /// Hashes every persisted pricing and preview field so evidence covers values as well as line identities.
    let pricingPreviewDigest (lines: ChargePreviewLineEntity array) =
        lines
        |> Seq.sortBy (fun line -> line.ChargePreviewLineId)
        |> Seq.map (fun line ->
            String.Join(
                "|",
                [|
                    line.ChargePreviewLineId.ToString("D")
                    line.OwnerId.ToString("D")
                    line.OrganizationId.ToString("D")
                    line.RepositoryId.ToString("D")
                    line.PeriodFromUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                    line.PeriodToUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                    line.FactKind.ToString(CultureInfo.InvariantCulture)
                    line.BillableUsageKindMappingId.ToString("D")
                    line.BillableUsageKind.ToString(CultureInfo.InvariantCulture)
                    line.PricingAssignmentId.ToString("D")
                    line.PricingPlanId.ToString("D")
                    line.PricingRateId.ToString("D")
                    line.CurrencyCode
                    line.UnitName
                    line.UnitQuantity.ToString(CultureInfo.InvariantCulture)
                    line.UnitPriceMicros.ToString(CultureInfo.InvariantCulture)
                    line.EffectiveFromUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                    line.EffectiveToUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                    line.TotalQuantity.ToString(CultureInfo.InvariantCulture)
                    line.ChargeMicros.ToString(CultureInfo.InvariantCulture)
                |]
            ))
        |> Seq.toList
        |> digest

    /// Inserts full immutable posting provenance, evidence, and the terminal period state in the active transaction.
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
                        "INSERT INTO ops.Charge (ChargeId,OwnerId,OrganizationId,RepositoryId,BillingPeriodId,ChargePreviewLineId,PeriodFromUtc,PeriodToUtc,FactKind,BillableUsageKindMappingId,BillableUsageKind,PricingAssignmentId,PricingPlanId,PricingRateId,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc,EffectiveToUtc,TotalQuantity,ChargeMicros) VALUES (@ChargeId,@OwnerId,@OrganizationId,@RepositoryId,@BillingPeriodId,@PreviewLineId,@PeriodFrom,@PeriodTo,@FactKind,@MappingId,@BillableKind,@AssignmentId,@PlanId,@RateId,@Currency,@UnitName,@UnitQuantity,@UnitPrice,@EffectiveFrom,@EffectiveTo,@TotalQuantity,@Charge);"

                let add name dbType value = insert.Parameters.Add(name, dbType).Value <- value
                add "@ChargeId" SqlDbType.UniqueIdentifier (chargeId periodId line.ChargePreviewLineId)
                add "@OwnerId" SqlDbType.UniqueIdentifier line.OwnerId
                add "@OrganizationId" SqlDbType.UniqueIdentifier line.OrganizationId
                add "@RepositoryId" SqlDbType.UniqueIdentifier line.RepositoryId
                add "@BillingPeriodId" SqlDbType.UniqueIdentifier periodId
                add "@PreviewLineId" SqlDbType.UniqueIdentifier line.ChargePreviewLineId
                add "@PeriodFrom" SqlDbType.DateTime2 line.PeriodFromUtc
                add "@PeriodTo" SqlDbType.DateTime2 line.PeriodToUtc
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

            do! transactionInterleaving.AfterChargeInsertionAsync cancellationToken
            let! factDigest = acceptedFactDigestAsync connection transaction request.Scope cancellationToken

            use evidence =
                command
                    connection
                    transaction
                    "INSERT INTO ops.BillingPeriodCloseEvidence (BillingPeriodId,AcceptedFactDigestSha256Hex,PricingPreviewDigestSha256Hex,ClosedAtUtc,ScheduledOperationProvenance) VALUES (@BillingPeriodId,@FactDigest,@PreviewDigest,@ClosedAtUtc,@Provenance);"

            evidence.Parameters.Add("@BillingPeriodId", SqlDbType.UniqueIdentifier).Value <- periodId
            evidence.Parameters.Add("@FactDigest", SqlDbType.Char, 64).Value <- factDigest
            evidence.Parameters.Add("@PreviewDigest", SqlDbType.Char, 64).Value <- pricingPreviewDigest lines
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
        }

    /// Runs preview or final close as one lock-held SQL transaction, preserving nonterminal retry truth for expected failures.
    let executeAsync isFinal request cancellationToken =
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
                let! periodId, state = ensurePeriodAsync connection transaction request.Scope cancellationToken

                if state = BillingPeriodState.Closed then
                    use count = command connection transaction "SELECT COUNT(*) FROM ops.Charge WHERE BillingPeriodId=@BillingPeriodId;"
                    count.Parameters.Add("@BillingPeriodId", SqlDbType.UniqueIdentifier).Value <- periodId
                    let! chargeCount = count.ExecuteScalarAsync cancellationToken
                    do! transaction.CommitAsync cancellationToken
                    return Closed(periodId, Convert.ToInt32 chargeCount)
                else
                    let! databaseUtcNow = clock.UtcNowAsync(connection, transaction, cancellationToken)

                    let nextMonthStartUtc =
                        (BillingCompletenessScope.nextMonthStart request.Scope)
                            .ToDateTimeUtc()

                    if not (BillingPeriodCloseEligibility.isEligible isFinal nextMonthStartUtc databaseUtcNow) then
                        do! transaction.CommitAsync cancellationToken
                        return NotEligible
                    else
                        let! blocker = completenessDiagnosticAsync connection transaction request.Scope cancellationToken

                        match blocker with
                        | Some diagnostic ->
                            do! setDiagnosticAsync connection transaction periodId (Some diagnostic) cancellationToken
                            do! transaction.CommitAsync cancellationToken
                            return Blocked diagnostic
                        | None ->
                            let! facts = readPricedFactsAsync connection transaction request.Scope cancellationToken

                            if List.isEmpty facts then
                                use pricingCatalogIsolation = command connection transaction "SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;"

                                let! _ = pricingCatalogIsolation.ExecuteNonQueryAsync cancellationToken

                                let coverage =
                                    SqlZeroFactPricingCoverage(ZeroFactPricingCoverageInterleaving(transactionInterleaving)) :> IZeroFactPricingCoverage

                                match! coverage.EvaluateAsync(connection, transaction, request.Scope, cancellationToken) with
                                | Incomplete diagnostic -> raise (ZeroFactPricingCoverageException(diagnostic))
                                | Complete -> ()

                            let previewScope: ChargePreviewScope =
                                {
                                    OwnerId = request.Scope.OwnerId
                                    OrganizationId = request.Scope.OrganizationId
                                    RepositoryId = request.Scope.RepositoryId
                                    PeriodFromUtc = request.Scope.MonthStart.ToDateTimeUtc()
                                    PeriodToUtc = nextMonthStartUtc
                                }

                            let lines = ChargePreviewCalculation.buildLines previewScope facts
                            do! replacePreviewAsync connection transaction request.Scope lines cancellationToken
                            do! transactionInterleaving.AfterPreviewReplacementAsync cancellationToken

                            if isFinal then
                                do! postCloseAsync connection transaction periodId request databaseUtcNow lines cancellationToken
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
                do! rollbackIgnoringFailureAsync transaction
                return raise ex
        }

    interface IBillingPeriodCloser with
        member _.PreviewAsync(request, cancellationToken) = executeAsync false request cancellationToken
        member _.CloseAsync(request, cancellationToken) = executeAsync true request cancellationToken

    /// Creates the production closer using only SQL Server time and no test interleaving.
    new(connectionString: string) =
        SqlBillingPeriodCloser(
            connectionString,
            NoBillingPeriodCloseTransactionInterleaving() :> IBillingPeriodCloseTransactionInterleaving,
            DatabaseBillingPeriodCloseClock(NoBillingPeriodCloseTransactionInterleaving() :> IBillingPeriodCloseTransactionInterleaving)
            :> IBillingPeriodCloseClock
        )

    /// Creates a real-SQL close seam that retains the production SQL clock while exposing transaction stages to tests.
    static member internal CreateForTest(connectionString, transactionInterleaving) =
        SqlBillingPeriodCloser(connectionString, transactionInterleaving, DatabaseBillingPeriodCloseClock(transactionInterleaving) :> IBillingPeriodCloseClock)

    /// Creates an internal policy-boundary seam with controlled database-time values while retaining the production transaction.
    static member internal CreateForTest(connectionString, transactionInterleaving, clock) =
        SqlBillingPeriodCloser(connectionString, transactionInterleaving, clock)
