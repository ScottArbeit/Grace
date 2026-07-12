namespace Grace.Operations.Data

open Microsoft.Data.SqlClient
open System
open System.Data
open System.Diagnostics
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

/// Describes a close pass result without exposing mutable internals outside Operations.
[<RequireQualifiedAccess>]
type BillingCloseOutcome =
    | Closed
    | NotEligible
    | Blocked of string
    | AlreadyTerminal

/// Defines a callable lifecycle, repair, correction, and close boundary for the hosted worker.
type IBillingPeriodService =
    /// Materializes eligible owner/month periods, advances lifecycle, closes eligible periods, and processes pending correction work.
    abstract RunAsync: nowUtc: DateTime * cancellationToken: CancellationToken -> Task

    /// Retries one close without bypassing lifecycle timing or completeness checks.
    abstract RetryCloseAsync:
        scope: BillingPeriodScope * nowUtc: DateTime * provenance: BillingOperationProvenance * cancellationToken: CancellationToken ->
            Task<BillingCloseOutcome>

    /// Appends a validated manual adjustment or reversal and transitions a terminal period to Corrected.
    abstract ApplyManualCorrectionAsync:
        correction: ManualBillingCorrection * provenance: BillingOperationProvenance * cancellationToken: CancellationToken -> Task

    /// Transactionally enqueues an accepted late fact against an existing Closed or Corrected owner/month period.
    abstract RecordAcceptedLateFactAsync:
        ownerId: Guid * organizationId: Guid * repositoryId: Guid * observedAtUtc: DateTime * usageFactId: Guid * cancellationToken: CancellationToken -> Task

/// Defines repairable billing-relevant rejected-message evidence.
type IBillingIngestionFailureRecorder =
    /// Records the first active non-empty UsageFactId failure as canonical and settles conflicting duplicates without retry poison.
    abstract RecordFailureAsync:
        usageFactId: Guid option *
        ownerId: Guid option *
        organizationId: Guid option *
        repositoryId: Guid option *
        observedAtUtc: DateTime option *
        correlationId: string *
        failureCode: string *
        detail: string *
        cancellationToken: CancellationToken ->
            Task

    /// Resolves active evidence only with explicit repair provenance.
    abstract ResolveFailureAsync: usageFactId: Guid * resolutionDetail: string * cancellationToken: CancellationToken -> Task

/// Derives stable failure identities without inventing a second billing identity.
[<RequireQualifiedAccess>]
module BillingIngestionFailureIdentity =
    /// Returns a deterministic failure identity from the supplied evidence tuple.
    let failureId
        (usageFactId: Guid option)
        (ownerId: Guid option)
        (organizationId: Guid option)
        (repositoryId: Guid option)
        (observedAtUtc: DateTime option)
        failureCode
        messageIdentity
        =
        BillingPeriodRules.deterministicId [ usageFactId
                                             |> Option.map (fun value -> value.ToString("D"))
                                             |> Option.defaultValue "missing"
                                             ownerId
                                             |> Option.map (fun value -> value.ToString("D"))
                                             |> Option.defaultValue "scope:missing"
                                             organizationId
                                             |> Option.map (fun value -> value.ToString("D"))
                                             |> Option.defaultValue "scope:missing"
                                             repositoryId
                                             |> Option.map (fun value -> value.ToString("D"))
                                             |> Option.defaultValue "scope:missing"
                                             observedAtUtc
                                             |> Option.map (fun value -> value.Ticks.ToString(CultureInfo.InvariantCulture))
                                             |> Option.defaultValue "time:missing"
                                             failureCode
                                             messageIdentity ]

/// Persists bounded failure evidence while preserving the first active fact identity as canonical.
type SqlBillingIngestionFailureRecorder(connectionString: string) =
    let bounded maximum value =
        if String.IsNullOrWhiteSpace(value) then "unspecified"
        elif value.Length <= maximum then value
        else value.Substring(0, maximum)

    let optionalGuid (command: SqlCommand) name (value: Guid option) =
        let parameter = command.Parameters.Add(name, SqlDbType.UniqueIdentifier)

        parameter.Value <-
            value
            |> Option.map box
            |> Option.defaultValue DBNull.Value

    let optionalUtc (command: SqlCommand) name (value: DateTime option) =
        let parameter = command.Parameters.Add(name, SqlDbType.DateTime2)

        parameter.Value <-
            value
            |> Option.map box
            |> Option.defaultValue DBNull.Value

    interface IBillingIngestionFailureRecorder with
        member _.RecordFailureAsync(usageFactId, ownerId, organizationId, repositoryId, observedAtUtc, correlationId, failureCode, detail, cancellationToken) =
            task {
                use connection = new SqlConnection(connectionString)
                do! connection.OpenAsync(cancellationToken)
                use! rawTransaction = connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                use transaction = rawTransaction :?> SqlTransaction

                try
                    // A non-empty fact is canonical: conflicting rejects may only observe and settle it.
                    let canonical =
                        usageFactId
                        |> Option.bind (fun factId ->
                            use check = connection.CreateCommand()
                            check.Transaction <- transaction

                            check.CommandText <-
                                "SELECT BillingIngestionFailureId FROM ops.BillingIngestionFailure WITH(UPDLOCK,HOLDLOCK) WHERE UsageFactId=@UsageFactId AND ResolvedAtUtc IS NULL;"

                            check.Parameters.Add("@UsageFactId", SqlDbType.UniqueIdentifier).Value <- factId

                            match check.ExecuteScalar() with
                            | null -> None
                            | value -> Some(value :?> Guid))

                    match canonical with
                    | Some canonicalId ->
                        // The first active fact evidence remains canonical; the duplicate is settled and leaves no retry poison.
                        let factLabel =
                            usageFactId
                            |> Option.map (fun value -> value.ToString("D"))
                            |> Option.defaultValue "missing"

                        Trace.TraceWarning(
                            $"Settled conflicting billing failure duplicate for UsageFactId {factLabel}; canonical failure {canonicalId:D} remains active."
                        )
                    | None ->
                        let id =
                            BillingIngestionFailureIdentity.failureId
                                usageFactId
                                ownerId
                                organizationId
                                repositoryId
                                observedAtUtc
                                (bounded 64 failureCode)
                                (bounded 1024 detail)

                        use insert = connection.CreateCommand()
                        insert.Transaction <- transaction

                        insert.CommandText <-
                            """
INSERT INTO ops.BillingIngestionFailure(BillingIngestionFailureId,UsageFactId,OwnerId,OrganizationId,RepositoryId,ObservedAtUtc,FailureCode,FailureDetail,CorrelationId)
VALUES(@Id,@UsageFactId,@OwnerId,@OrganizationId,@RepositoryId,@ObservedAtUtc,@FailureCode,@Detail,@Correlation);
"""

                        insert.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value <- id
                        optionalGuid insert "@UsageFactId" usageFactId
                        optionalGuid insert "@OwnerId" ownerId
                        optionalGuid insert "@OrganizationId" organizationId
                        optionalGuid insert "@RepositoryId" repositoryId
                        optionalUtc insert "@ObservedAtUtc" observedAtUtc
                        insert.Parameters.Add("@FailureCode", SqlDbType.NVarChar, 64).Value <- bounded 64 failureCode
                        insert.Parameters.Add("@Detail", SqlDbType.NVarChar, 1024).Value <- bounded 1024 detail

                        insert.Parameters.Add("@Correlation", SqlDbType.NVarChar, OperationsUsageSql.CorrelationIdMaxLength).Value <- bounded
                                                                                                                                          OperationsUsageSql.CorrelationIdMaxLength
                                                                                                                                          correlationId

                        let! _ = insert.ExecuteNonQueryAsync(cancellationToken)
                        ()

                    do! transaction.CommitAsync(cancellationToken)
                with
                | ex ->
                    do! transaction.RollbackAsync(CancellationToken.None)
                    return raise ex
            }

        member _.ResolveFailureAsync(usageFactId, resolutionDetail, cancellationToken) =
            task {
                use connection = new SqlConnection(connectionString)
                do! connection.OpenAsync(cancellationToken)
                use command = connection.CreateCommand()

                command.CommandText <-
                    "UPDATE ops.BillingIngestionFailure SET ResolvedAtUtc=SYSUTCDATETIME(), ResolutionDetail=@Detail WHERE UsageFactId=@UsageFactId AND ResolvedAtUtc IS NULL;"

                command.Parameters.Add("@UsageFactId", SqlDbType.UniqueIdentifier).Value <- usageFactId
                command.Parameters.Add("@Detail", SqlDbType.NVarChar, 1024).Value <- bounded 1024 resolutionDetail
                let! _ = command.ExecuteNonQueryAsync(cancellationToken)
                ()
            }

/// Applies owner-scoped billing lifecycle work under serializable transactions and the shared preview lock.
type SqlBillingPeriodService(connectionString: string) =
    let add (command: SqlCommand) name dbType value = command.Parameters.Add(name, dbType).Value <- value

    let addScope (command: SqlCommand) (scope: BillingPeriodScope) =
        add command "@OwnerId" SqlDbType.UniqueIdentifier scope.OwnerId
        add command "@OrganizationId" SqlDbType.UniqueIdentifier scope.OrganizationId
        add command "@RepositoryId" SqlDbType.UniqueIdentifier scope.RepositoryId
        add command "@PeriodFromUtc" SqlDbType.DateTime2 scope.PeriodFromUtc
        add command "@PeriodToUtc" SqlDbType.DateTime2 scope.PeriodToUtc

    let lockScope (connection: SqlConnection) (transaction: SqlTransaction) (scope: BillingPeriodScope) (cancellationToken: CancellationToken) =
        task {
            use lockCommand = connection.CreateCommand()
            lockCommand.Transaction <- transaction
            lockCommand.CommandText <- OperationsBillingSql.AcquireScopeLock
            lockCommand.CommandTimeout <- 65

            let resource =
                $"ops:charge-preview:{scope.OwnerId:D}:{scope.OrganizationId:D}:{scope.RepositoryId:D}:{scope.PeriodFromUtc.Ticks}:{scope.PeriodToUtc.Ticks}"

            let hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resource)))
            lockCommand.Parameters.Add("@LockResource", SqlDbType.NVarChar, 255).Value <- $"ops:charge-preview:{hash}"
            let! _ = lockCommand.ExecuteNonQueryAsync(cancellationToken)
            ()
        }

    let readPeriod (connection: SqlConnection) (transaction: SqlTransaction) (scope: BillingPeriodScope) (cancellationToken: CancellationToken) =
        task {
            use command = connection.CreateCommand()
            command.Transaction <- transaction

            command.CommandText <-
                """
SELECT BillingPeriodId,State FROM ops.BillingPeriod WITH(UPDLOCK,HOLDLOCK)
WHERE OwnerId=@OwnerId AND OrganizationId=@OrganizationId AND RepositoryId=@RepositoryId AND PeriodFromUtc=@PeriodFromUtc AND PeriodToUtc=@PeriodToUtc;
"""

            addScope command scope
            use! reader = command.ExecuteReaderAsync(cancellationToken)
            let! found = reader.ReadAsync(cancellationToken)

            if found then
                return Some(reader.GetGuid(0), reader.GetInt32(1))
            else
                return None
        }

    let createPeriodIfMissing
        (connection: SqlConnection)
        (transaction: SqlTransaction)
        (scope: BillingPeriodScope)
        (nowUtc: DateTime)
        (cancellationToken: CancellationToken)
        =
        task {
            // Range locking protects the absence check; residual 2601/2627 races are deliberately reread by the caller.
            let! existing = readPeriod connection transaction scope cancellationToken

            match existing with
            | Some value -> return value
            | None ->
                let periodId = BillingPeriodRules.periodId scope
                use insert = connection.CreateCommand()
                insert.Transaction <- transaction

                insert.CommandText <-
                    """
INSERT INTO ops.BillingPeriod(BillingPeriodId,OwnerId,OrganizationId,RepositoryId,PeriodFromUtc,PeriodToUtc,State,ConsecutiveCloseFailureCount)
VALUES(@BillingPeriodId,@OwnerId,@OrganizationId,@RepositoryId,@PeriodFromUtc,@PeriodToUtc,@State,0);
"""

                add insert "@BillingPeriodId" SqlDbType.UniqueIdentifier periodId
                addScope insert scope
                add insert "@State" SqlDbType.Int (int (BillingPeriodRules.stateAt scope.PeriodToUtc nowUtc))
                let! _ = insert.ExecuteNonQueryAsync(cancellationToken)
                return periodId, int (BillingPeriodRules.stateAt scope.PeriodToUtc nowUtc)
        }

    let hasAssignmentCoverage (connection: SqlConnection) (transaction: SqlTransaction) (scope: BillingPeriodScope) (cancellationToken: CancellationToken) =
        task {
            use command = connection.CreateCommand()
            command.Transaction <- transaction

            command.CommandText <-
                """
SELECT CASE WHEN EXISTS(
SELECT 1 FROM ops.PricingAssignment a WITH(UPDLOCK,HOLDLOCK)
WHERE a.OwnerId=@OwnerId AND a.OrganizationId=@OrganizationId AND a.RepositoryId=@RepositoryId
AND a.EffectiveFromUtc < @PeriodToUtc AND (a.EffectiveToUtc IS NULL OR a.EffectiveToUtc > @PeriodFromUtc)) THEN 1 ELSE 0 END;
"""

            addScope command scope
            let! value = command.ExecuteScalarAsync(cancellationToken)
            return Convert.ToInt32(value, CultureInfo.InvariantCulture) = 1
        }

    let acceptedFactCount (connection: SqlConnection) (transaction: SqlTransaction) (scope: BillingPeriodScope) (cancellationToken: CancellationToken) =
        task {
            use command = connection.CreateCommand()
            command.Transaction <- transaction

            command.CommandText <-
                "SELECT COUNT_BIG(1) FROM ops.RawUsageFact WITH(UPDLOCK,HOLDLOCK) WHERE OwnerId=@OwnerId AND OrganizationId=@OrganizationId AND RepositoryId=@RepositoryId AND ObservedAtUtc>=@PeriodFromUtc AND ObservedAtUtc<@PeriodToUtc;"

            addScope command scope
            let! value = command.ExecuteScalarAsync(cancellationToken)
            return Convert.ToInt64(value, CultureInfo.InvariantCulture)
        }

    let hasActiveFailure (connection: SqlConnection) (transaction: SqlTransaction) (scope: BillingPeriodScope) (cancellationToken: CancellationToken) =
        task {
            use command = connection.CreateCommand()
            command.Transaction <- transaction

            command.CommandText <-
                """
SELECT CASE WHEN EXISTS(
SELECT 1 FROM ops.BillingIngestionFailure WITH(UPDLOCK,HOLDLOCK)
WHERE ResolvedAtUtc IS NULL AND OwnerId=@OwnerId AND OrganizationId=@OrganizationId AND RepositoryId=@RepositoryId
AND ObservedAtUtc>=@PeriodFromUtc AND ObservedAtUtc<@PeriodToUtc) THEN 1 ELSE 0 END;
"""

            addScope command scope
            let! value = command.ExecuteScalarAsync(cancellationToken)
            return Convert.ToInt32(value, CultureInfo.InvariantCulture) = 1
        }

    let writeBlock
        (connection: SqlConnection)
        (transaction: SqlTransaction)
        (periodId: Guid)
        (code: string)
        (detail: string)
        (cancellationToken: CancellationToken)
        =
        task {
            use command = connection.CreateCommand()
            command.Transaction <- transaction

            command.CommandText <-
                "UPDATE ops.BillingPeriod SET CloseBlockedCode=@Code,CloseBlockedDetail=@Detail,LastCloseAttemptAtUtc=SYSUTCDATETIME(),ConsecutiveCloseFailureCount=ConsecutiveCloseFailureCount+1 WHERE BillingPeriodId=@BillingPeriodId;"

            add command "@BillingPeriodId" SqlDbType.UniqueIdentifier periodId
            add command "@Code" SqlDbType.NVarChar code
            add command "@Detail" SqlDbType.NVarChar (if detail.Length > 1024 then detail.Substring(0, 1024) else detail)
            let! _ = command.ExecuteNonQueryAsync(cancellationToken)
            ()
        }

    /// Rebuilds and records the final preview under the caller's transaction-owned billing scope lock.
    let rebuildFinalPreview
        (connection: SqlConnection)
        (transaction: SqlTransaction)
        (scope: BillingPeriodScope)
        (periodId: Guid)
        (cancellationToken: CancellationToken)
        =
        task {
            let previewScope: ChargePreviewScope =
                {
                    OwnerId = scope.OwnerId
                    OrganizationId = scope.OrganizationId
                    RepositoryId = scope.RepositoryId
                    PeriodFromUtc = scope.PeriodFromUtc
                    PeriodToUtc = scope.PeriodToUtc
                }

            use read = connection.CreateCommand()
            read.Transaction <- transaction
            read.CommandText <- OperationsChargePreviewSql.SelectSourceAndPricing
            addScope read scope
            use! reader = read.ExecuteReaderAsync(cancellationToken)
            let facts = ResizeArray<ChargePreviewPricedFact>()
            let mutable hasRow = true

            while hasRow do
                let! next = reader.ReadAsync(cancellationToken)
                hasRow <- next

                if hasRow then
                    let usageFactId = reader.GetGuid(0)

                    match
                        ChargePreviewCalculation.missingPrerequisite
                            (not (reader.IsDBNull(4)))
                            (not (reader.IsDBNull(6)))
                            (not (reader.IsDBNull(7)))
                            (not (reader.IsDBNull(9)))
                        with
                    | Some prerequisite -> raise (ChargePreviewRebuildException(previewScope, usageFactId, prerequisite))
                    | None ->
                        facts.Add
                            {
                                UsageFactId = usageFactId
                                FactKind = reader.GetInt32(1)
                                Quantity = reader.GetInt64(2)
                                ObservedAtUtc = reader.GetDateTime(3)
                                PricingAssignmentId = reader.GetGuid(4)
                                PricingPlanId = reader.GetGuid(6)
                                BillableUsageKindMappingId = reader.GetGuid(7)
                                BillableUsageKind = reader.GetInt32(8)
                                PricingRateId = reader.GetGuid(9)
                                CurrencyCode = reader.GetString(10)
                                UnitName = reader.GetString(11)
                                UnitQuantity = reader.GetInt64(12)
                                UnitPriceMicros = reader.GetInt64(13)
                                EffectiveFromUtc = reader.GetDateTime(14)
                                EffectiveToUtc = reader.GetDateTime(15)
                            }

            do! reader.CloseAsync()
            let lines = ChargePreviewCalculation.buildLines previewScope facts

            use replace = connection.CreateCommand()
            replace.Transaction <- transaction
            replace.CommandText <- OperationsChargePreviewSql.DeleteScope
            addScope replace scope
            let! _ = replace.ExecuteNonQueryAsync(cancellationToken)

            for line in lines do
                use insert = connection.CreateCommand()
                insert.Transaction <- transaction

                insert.CommandText <-
                    "INSERT INTO ops.ChargePreviewLine(ChargePreviewLineId,OwnerId,OrganizationId,RepositoryId,PeriodFromUtc,PeriodToUtc,FactKind,BillableUsageKindMappingId,BillableUsageKind,PricingAssignmentId,PricingPlanId,PricingRateId,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc,EffectiveToUtc,TotalQuantity,ChargeMicros) VALUES(@Id,@OwnerId,@OrganizationId,@RepositoryId,@PeriodFromUtc,@PeriodToUtc,@FactKind,@MappingId,@BillableKind,@AssignmentId,@PlanId,@RateId,@Currency,@UnitName,@UnitQuantity,@UnitPrice,@EffectiveFrom,@EffectiveTo,@Quantity,@Charge);"

                add insert "@Id" SqlDbType.UniqueIdentifier line.ChargePreviewLineId
                addScope insert scope
                add insert "@FactKind" SqlDbType.Int line.FactKind
                add insert "@MappingId" SqlDbType.UniqueIdentifier line.BillableUsageKindMappingId
                add insert "@BillableKind" SqlDbType.Int line.BillableUsageKind
                add insert "@AssignmentId" SqlDbType.UniqueIdentifier line.PricingAssignmentId
                add insert "@PlanId" SqlDbType.UniqueIdentifier line.PricingPlanId
                add insert "@RateId" SqlDbType.UniqueIdentifier line.PricingRateId
                insert.Parameters.Add("@Currency", SqlDbType.VarChar, 3).Value <- line.CurrencyCode
                insert.Parameters.Add("@UnitName", SqlDbType.NVarChar, OperationsPricingSql.UnitNameMaxLength).Value <- line.UnitName
                add insert "@UnitQuantity" SqlDbType.BigInt line.UnitQuantity
                add insert "@UnitPrice" SqlDbType.BigInt line.UnitPriceMicros
                add insert "@EffectiveFrom" SqlDbType.DateTime2 line.EffectiveFromUtc
                add insert "@EffectiveTo" SqlDbType.DateTime2 line.EffectiveToUtc
                add insert "@Quantity" SqlDbType.BigInt line.TotalQuantity
                add insert "@Charge" SqlDbType.BigInt line.ChargeMicros
                let! _ = insert.ExecuteNonQueryAsync(cancellationToken)
                ()

            let digest (parts: string seq) =
                parts
                |> String.concat "|"
                |> Encoding.UTF8.GetBytes
                |> SHA256.HashData
                |> Convert.ToHexString

            let factsDigest =
                facts
                |> Seq.sortBy (fun fact -> fact.UsageFactId)
                |> Seq.map (fun fact -> $"{fact.UsageFactId:D}:{fact.FactKind}:{fact.Quantity}:{fact.ObservedAtUtc.Ticks}")
                |> digest

            let pricingDigest =
                lines
                |> Seq.map (fun line ->
                    $"{line.ChargePreviewLineId:D}:{line.PricingAssignmentId:D}:{line.PricingPlanId:D}:{line.BillableUsageKindMappingId:D}:{line.PricingRateId:D}:{line.ChargeMicros}")
                |> digest

            use freshness = connection.CreateCommand()
            freshness.Transaction <- transaction

            freshness.CommandText <-
                "DELETE FROM ops.ChargePreviewFreshness WHERE BillingPeriodId=@BillingPeriodId; INSERT INTO ops.ChargePreviewFreshness(BillingPeriodId,AcceptedFactsDigest,PricingDigest,PreviewCommittedAtUtc) VALUES(@BillingPeriodId,@FactsDigest,@PricingDigest,SYSUTCDATETIME());"

            add freshness "@BillingPeriodId" SqlDbType.UniqueIdentifier periodId
            freshness.Parameters.Add("@FactsDigest", SqlDbType.Char, 64).Value <- factsDigest
            freshness.Parameters.Add("@PricingDigest", SqlDbType.Char, 64).Value <- pricingDigest
            let! _ = freshness.ExecuteNonQueryAsync(cancellationToken)
            return lines
        }

    /// Verifies a manual correction's complete immutable pricing grain against the locked period scope.
    let validateManualPricingGrain
        (connection: SqlConnection)
        (transaction: SqlTransaction)
        (scope: BillingPeriodScope)
        (correction: ManualBillingCorrection)
        (cancellationToken: CancellationToken)
        =
        task {
            use command = connection.CreateCommand()
            command.Transaction <- transaction

            command.CommandText <-
                """
SELECT CASE WHEN EXISTS(
    SELECT 1
    FROM ops.PricingAssignment a WITH(UPDLOCK,HOLDLOCK)
    JOIN ops.PricingPlan p WITH(UPDLOCK,HOLDLOCK) ON p.PricingPlanId=a.PricingPlanId
    JOIN ops.BillableUsageKindMapping m WITH(UPDLOCK,HOLDLOCK) ON m.BillableUsageKindMappingId=@MappingId
    JOIN ops.PricingRate r WITH(UPDLOCK,HOLDLOCK) ON r.PricingRateId=@RateId AND r.PricingPlanId=p.PricingPlanId
    WHERE a.PricingAssignmentId=@AssignmentId AND a.PricingPlanId=@PlanId
      AND a.OwnerId=@OwnerId AND a.OrganizationId=@OrganizationId AND a.RepositoryId=@RepositoryId
      AND m.FactKind=@FactKind AND m.BillableUsageKind=@BillableKind
      AND r.BillableUsageKind=@BillableKind AND r.CurrencyCode=@Currency AND r.UnitName=@UnitName
      AND r.UnitQuantity=@UnitQuantity AND r.UnitPriceMicros=@UnitPrice
      AND a.EffectiveFromUtc<=@EffectiveFrom AND (a.EffectiveToUtc IS NULL OR a.EffectiveToUtc>=@EffectiveTo)
      AND p.EffectiveFromUtc<=@EffectiveFrom AND (p.EffectiveToUtc IS NULL OR p.EffectiveToUtc>=@EffectiveTo)
      AND m.EffectiveFromUtc<=@EffectiveFrom AND (m.EffectiveToUtc IS NULL OR m.EffectiveToUtc>=@EffectiveTo)
      AND r.EffectiveFromUtc<=@EffectiveFrom AND (r.EffectiveToUtc IS NULL OR r.EffectiveToUtc>=@EffectiveTo)
) THEN 1 ELSE 0 END;
"""

            addScope command scope
            add command "@AssignmentId" SqlDbType.UniqueIdentifier correction.PricingAssignmentId
            add command "@PlanId" SqlDbType.UniqueIdentifier correction.PricingPlanId
            add command "@MappingId" SqlDbType.UniqueIdentifier correction.BillableUsageKindMappingId
            add command "@RateId" SqlDbType.UniqueIdentifier correction.PricingRateId
            add command "@FactKind" SqlDbType.Int correction.FactKind
            add command "@BillableKind" SqlDbType.Int correction.BillableUsageKind
            command.Parameters.Add("@Currency", SqlDbType.VarChar, 3).Value <- correction.CurrencyCode
            command.Parameters.Add("@UnitName", SqlDbType.NVarChar, 64).Value <- correction.UnitName
            add command "@UnitQuantity" SqlDbType.BigInt correction.UnitQuantity
            add command "@UnitPrice" SqlDbType.BigInt correction.UnitPriceMicros
            add command "@EffectiveFrom" SqlDbType.DateTime2 correction.EffectiveFromUtc
            add command "@EffectiveTo" SqlDbType.DateTime2 correction.EffectiveToUtc
            let! valid = command.ExecuteScalarAsync(cancellationToken)

            if
                Convert.ToInt32(valid, CultureInfo.InvariantCulture)
                <> 1
            then
                invalidArg "correction" "Manual correction pricing grain is not applicable to the locked owner period."
        }

    /// Verifies that an explicit manual predecessor belongs to the same period and immutable pricing grain.
    let validateManualPrior
        (connection: SqlConnection)
        (transaction: SqlTransaction)
        (correction: ManualBillingCorrection)
        (cancellationToken: CancellationToken)
        =
        task {
            match correction.PriorChargeLedgerEntryId with
            | None -> ()
            | Some priorId ->
                use command = connection.CreateCommand()
                command.Transaction <- transaction

                command.CommandText <-
                    "SELECT BillingPeriodId,FactKind,BillableUsageKindMappingId,BillableUsageKind,PricingAssignmentId,PricingPlanId,PricingRateId,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc,EffectiveToUtc FROM ops.ChargeLedgerEntry WITH(UPDLOCK,HOLDLOCK) WHERE ChargeLedgerEntryId=@PriorId;"

                add command "@PriorId" SqlDbType.UniqueIdentifier priorId
                use! reader = command.ExecuteReaderAsync(cancellationToken)
                let! found = reader.ReadAsync(cancellationToken)

                if not found then
                    invalidArg "PriorChargeLedgerEntryId" "Manual correction predecessor does not exist."

                let matches =
                    reader.GetGuid(0) = correction.BillingPeriodId
                    && reader.GetInt32(1) = correction.FactKind
                    && reader.GetGuid(2) = correction.BillableUsageKindMappingId
                    && reader.GetInt32(3) = correction.BillableUsageKind
                    && reader.GetGuid(4) = correction.PricingAssignmentId
                    && reader.GetGuid(5) = correction.PricingPlanId
                    && reader.GetGuid(6) = correction.PricingRateId
                    && reader.GetString(7) = correction.CurrencyCode
                    && reader.GetString(8) = correction.UnitName
                    && reader.GetInt64(9) = correction.UnitQuantity
                    && reader.GetInt64(10) = correction.UnitPriceMicros
                    && reader.GetDateTime(11) = correction.EffectiveFromUtc
                    && reader.GetDateTime(12) = correction.EffectiveToUtc

                if not matches then
                    invalidArg "PriorChargeLedgerEntryId" "Manual correction predecessor has an incompatible period or pricing grain."
        }

    let close
        (connection: SqlConnection)
        (transaction: SqlTransaction)
        (scope: BillingPeriodScope)
        (periodId: Guid)
        (nowUtc: DateTime)
        (provenance: BillingOperationProvenance)
        (cancellationToken: CancellationToken)
        =
        task {
            let! failures = hasActiveFailure connection transaction scope cancellationToken
            let! coverage = hasAssignmentCoverage connection transaction scope cancellationToken
            let! factCount = acceptedFactCount connection transaction scope cancellationToken

            if failures then
                do!
                    writeBlock
                        connection
                        transaction
                        periodId
                        "ActiveIngestionFailure"
                        "Unresolved owner-scoped billing ingestion evidence blocks close."
                        cancellationToken

                return BillingCloseOutcome.Blocked("ActiveIngestionFailure")
            elif not coverage then
                if factCount = 0L then
                    // Q2=A: stale assignment removes only unposted mutable zero-fact state.
                    use delete = connection.CreateCommand()
                    delete.Transaction <- transaction

                    delete.CommandText <-
                        "DELETE FROM ops.BillingPeriod WHERE BillingPeriodId=@BillingPeriodId AND State IN(0,1) AND NOT EXISTS(SELECT 1 FROM ops.ChargeLedgerEntry WHERE BillingPeriodId=@BillingPeriodId);"

                    add delete "@BillingPeriodId" SqlDbType.UniqueIdentifier periodId
                    let! _ = delete.ExecuteNonQueryAsync(cancellationToken)
                    return BillingCloseOutcome.Blocked("AssignmentCoverageMissing")
                else
                    do!
                        writeBlock
                            connection
                            transaction
                            periodId
                            "AssignmentCoverageMissing"
                            "Accepted facts require an effective owner-scoped pricing assignment before close."
                            cancellationToken

                    return BillingCloseOutcome.Blocked("AssignmentCoverageMissing")
            else
                try
                    // Final preview replacement, freshness evidence, immutable posting, and Closed transition are one transaction.
                    // Empty previews intentionally produce a zero-entry close under Q1=C.
                    let! _ = rebuildFinalPreview connection transaction scope periodId cancellationToken
                    use post = connection.CreateCommand()
                    post.Transaction <- transaction

                    post.CommandText <-
                        """
INSERT INTO ops.ChargeLedgerEntry(ChargeLedgerEntryId,BillingPeriodId,EntryKind,SourceChargePreviewLineId,FactKind,BillableUsageKindMappingId,BillableUsageKind,PricingAssignmentId,PricingPlanId,PricingRateId,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc,EffectiveToUtc,Quantity,ChargeMicros,InitiatedByPrincipalId,ReasonCode,ReasonText,CorrelationId)
SELECT NEWID(),@BillingPeriodId,0,l.ChargePreviewLineId,l.FactKind,l.BillableUsageKindMappingId,l.BillableUsageKind,l.PricingAssignmentId,l.PricingPlanId,l.PricingRateId,l.CurrencyCode,l.UnitName,l.UnitQuantity,l.UnitPriceMicros,l.EffectiveFromUtc,l.EffectiveToUtc,l.TotalQuantity,l.ChargeMicros,N'Grace.Operations',N'FinalClose',N'Final preview ledger posting.',@Correlation
FROM ops.ChargePreviewLine l WITH(UPDLOCK,HOLDLOCK)
WHERE l.OwnerId=@OwnerId AND l.OrganizationId=@OrganizationId AND l.RepositoryId=@RepositoryId AND l.PeriodFromUtc=@PeriodFromUtc AND l.PeriodToUtc=@PeriodToUtc
AND NOT EXISTS(SELECT 1 FROM ops.ChargeLedgerEntry e WITH(UPDLOCK,HOLDLOCK) WHERE e.BillingPeriodId=@BillingPeriodId AND e.SourceChargePreviewLineId=l.ChargePreviewLineId);
UPDATE ops.BillingPeriod SET State=2,ClosedAtUtc=SYSUTCDATETIME(),CloseBlockedCode=NULL,CloseBlockedDetail=NULL,LastCloseAttemptAtUtc=SYSUTCDATETIME(),ConsecutiveCloseFailureCount=0,CloseInitiatedByPrincipalId=@Principal,CloseReasonCode=@ReasonCode,CloseReasonText=@ReasonText,CloseCorrelationId=@Correlation WHERE BillingPeriodId=@BillingPeriodId AND State=1;
"""

                    add post "@BillingPeriodId" SqlDbType.UniqueIdentifier periodId
                    addScope post scope
                    add post "@Principal" SqlDbType.NVarChar provenance.InitiatedByPrincipalId
                    add post "@ReasonCode" SqlDbType.NVarChar provenance.ReasonCode
                    add post "@ReasonText" SqlDbType.NVarChar provenance.ReasonText
                    add post "@Correlation" SqlDbType.NVarChar provenance.CorrelationId
                    let! _ = post.ExecuteNonQueryAsync(cancellationToken)
                    return BillingCloseOutcome.Closed
                with
                | :? ChargePreviewRebuildException as ex ->
                    do!
                        writeBlock
                            connection
                            transaction
                            periodId
                            "MissingPricing"
                            $"Final preview requires {ex.Prerequisite} for usage fact {ex.UsageFactId:D}."
                            cancellationToken

                    return BillingCloseOutcome.Blocked("MissingPricing")
        }

    let runScope (scope: BillingPeriodScope) (nowUtc: DateTime) (cancellationToken: CancellationToken) =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync(cancellationToken)
            use! rawTransaction = connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            use transaction = rawTransaction :?> SqlTransaction

            try
                do! lockScope connection transaction scope cancellationToken
                let! periodId, state = createPeriodIfMissing connection transaction scope nowUtc cancellationToken
                let nextState = BillingPeriodRules.stateAt scope.PeriodToUtc nowUtc

                if state = int BillingPeriodState.Open
                   && nextState = BillingPeriodState.Provisional then
                    use advance = connection.CreateCommand()
                    advance.Transaction <- transaction
                    advance.CommandText <- "UPDATE ops.BillingPeriod SET State=1 WHERE BillingPeriodId=@BillingPeriodId AND State=0;"
                    add advance "@BillingPeriodId" SqlDbType.UniqueIdentifier periodId
                    let! _ = advance.ExecuteNonQueryAsync(cancellationToken)
                    ()

                let! current = readPeriod connection transaction scope cancellationToken

                let outcome =
                    match current with
                    | Some (_, currentState) when
                        currentState = int BillingPeriodState.Provisional
                        && BillingPeriodRules.isCloseEligible scope.PeriodToUtc nowUtc
                        ->
                        close
                            connection
                            transaction
                            scope
                            periodId
                            nowUtc
                            {
                                InitiatedByPrincipalId = "Grace.Operations"
                                ReasonCode = "ScheduledClose"
                                ReasonText = "Scheduled owner billing close."
                                CorrelationId = $"billing-close:{periodId:D}"
                            }
                            cancellationToken
                    | Some (_, currentState) when currentState >= int BillingPeriodState.Closed -> Task.FromResult(BillingCloseOutcome.AlreadyTerminal)
                    | _ -> Task.FromResult(BillingCloseOutcome.NotEligible)

                let! result = outcome
                do! transaction.CommitAsync(cancellationToken)
                return result
            with
            | ex ->
                do! transaction.RollbackAsync(CancellationToken.None)
                return raise ex
        }

    let materializedScopes nowUtc cancellationToken =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync(cancellationToken)
            use command = connection.CreateCommand()

            command.CommandText <-
                "SELECT OwnerId,OrganizationId,RepositoryId,EffectiveFromUtc,EffectiveToUtc FROM ops.PricingAssignment WITH(READCOMMITTEDLOCK) WHERE EffectiveFromUtc < @NowUtc;"

            add command "@NowUtc" SqlDbType.DateTime2 nowUtc
            use! reader = command.ExecuteReaderAsync(cancellationToken)
            let scopes = ResizeArray<BillingPeriodScope>()
            let mutable hasRow = true

            while hasRow do
                let! next = reader.ReadAsync(cancellationToken)
                hasRow <- next

                if hasRow then
                    let endUtc = if reader.IsDBNull(4) then None else Some(reader.GetDateTime(4))

                    for fromUtc, toUtc in BillingPeriodRules.intersectingMonths (reader.GetDateTime(3)) endUtc nowUtc do
                        scopes.Add(
                            {
                                OwnerId = reader.GetGuid(0)
                                OrganizationId = reader.GetGuid(1)
                                RepositoryId = reader.GetGuid(2)
                                PeriodFromUtc = fromUtc
                                PeriodToUtc = toUtc
                            }: BillingPeriodScope
                        )

            do! reader.CloseAsync()
            use existing = connection.CreateCommand()

            existing.CommandText <-
                "SELECT OwnerId,OrganizationId,RepositoryId,PeriodFromUtc,PeriodToUtc FROM ops.BillingPeriod WITH(READCOMMITTEDLOCK) WHERE State IN(0,1);"

            use! existingReader = existing.ExecuteReaderAsync(cancellationToken)
            let mutable hasExisting = true

            while hasExisting do
                let! next = existingReader.ReadAsync(cancellationToken)
                hasExisting <- next

                if hasExisting then
                    scopes.Add(
                        {
                            OwnerId = existingReader.GetGuid(0)
                            OrganizationId = existingReader.GetGuid(1)
                            RepositoryId = existingReader.GetGuid(2)
                            PeriodFromUtc = existingReader.GetDateTime(3)
                            PeriodToUtc = existingReader.GetDateTime(4)
                        }: BillingPeriodScope
                    )

            return
                scopes
                |> Seq.distinctBy (fun scope -> scope.OwnerId, scope.OrganizationId, scope.RepositoryId, scope.PeriodFromUtc, scope.PeriodToUtc)
                |> Seq.toArray
        }

    /// Reads a bounded batch of durable late-fact work identities; each item is revalidated under its own scope lock.
    let pendingCorrectionWork cancellationToken =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync(cancellationToken)
            use command = connection.CreateCommand()

            command.CommandText <-
                "SELECT TOP (100) BillingCorrectionWorkId FROM ops.BillingCorrectionWork WITH(READPAST) WHERE CompletedAtUtc IS NULL ORDER BY CreatedAtUtc,BillingCorrectionWorkId;"

            use! reader = command.ExecuteReaderAsync(cancellationToken)
            let work = ResizeArray<Guid>()
            let mutable hasRow = true

            while hasRow do
                let! next = reader.ReadAsync(cancellationToken)
                hasRow <- next

                if hasRow then work.Add(reader.GetGuid(0))

            return work |> Seq.toArray
        }

    /// Posts exactly one pricing-complete automatic late-usage adjustment or leaves its durable work visibly pending.
    let processCorrectionWork (workId: Guid) cancellationToken =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync(cancellationToken)
            use! rawTransaction = connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            use transaction = rawTransaction :?> SqlTransaction

            try
                use work = connection.CreateCommand()
                work.Transaction <- transaction

                work.CommandText <-
                    "SELECT w.BillingPeriodId,w.UsageFactId,p.OwnerId,p.OrganizationId,p.RepositoryId,p.PeriodFromUtc,p.PeriodToUtc,p.State FROM ops.BillingCorrectionWork w WITH(UPDLOCK,HOLDLOCK) JOIN ops.BillingPeriod p WITH(UPDLOCK,HOLDLOCK) ON p.BillingPeriodId=w.BillingPeriodId WHERE w.BillingCorrectionWorkId=@WorkId AND w.CompletedAtUtc IS NULL;"

                add work "@WorkId" SqlDbType.UniqueIdentifier workId
                use! workReader = work.ExecuteReaderAsync(cancellationToken)
                let! foundWork = workReader.ReadAsync(cancellationToken)

                if not foundWork then
                    do! transaction.CommitAsync(cancellationToken)
                else
                    let periodId = workReader.GetGuid(0)
                    let usageFactId = workReader.GetGuid(1)

                    let scope: BillingPeriodScope =
                        {
                            OwnerId = workReader.GetGuid(2)
                            OrganizationId = workReader.GetGuid(3)
                            RepositoryId = workReader.GetGuid(4)
                            PeriodFromUtc = workReader.GetDateTime(5)
                            PeriodToUtc = workReader.GetDateTime(6)
                        }

                    let state = workReader.GetInt32(7)
                    do! workReader.CloseAsync()
                    do! lockScope connection transaction scope cancellationToken

                    if state < int BillingPeriodState.Closed then
                        invalidOp "Automatic correction work must reference a terminal billing period."

                    use fact = connection.CreateCommand()
                    fact.Transaction <- transaction

                    fact.CommandText <-
                        "SELECT FactKind,Quantity,ObservedAtUtc FROM ops.RawUsageFact WITH(UPDLOCK,HOLDLOCK) WHERE UsageFactId=@UsageFactId AND OwnerId=@OwnerId AND OrganizationId=@OrganizationId AND RepositoryId=@RepositoryId AND ObservedAtUtc>=@PeriodFromUtc AND ObservedAtUtc<@PeriodToUtc;"

                    add fact "@UsageFactId" SqlDbType.UniqueIdentifier usageFactId
                    addScope fact scope
                    use! factReader = fact.ExecuteReaderAsync(cancellationToken)
                    let! foundFact = factReader.ReadAsync(cancellationToken)

                    if not foundFact then
                        invalidOp "Automatic correction work no longer has its accepted owner-scoped source fact."

                    let factKind = factReader.GetInt32(0)
                    let quantity = factReader.GetInt64(1)
                    let observedAtUtc = factReader.GetDateTime(2)
                    do! factReader.CloseAsync()

                    use price = connection.CreateCommand()
                    price.Transaction <- transaction
                    price.CommandText <- OperationsPricingSql.SelectEffectivePricingRate
                    add price "@OwnerId" SqlDbType.UniqueIdentifier scope.OwnerId
                    add price "@OrganizationId" SqlDbType.UniqueIdentifier scope.OrganizationId
                    add price "@RepositoryId" SqlDbType.UniqueIdentifier scope.RepositoryId
                    add price "@FactKind" SqlDbType.Int factKind
                    add price "@ObservedAtUtc" SqlDbType.DateTime2 observedAtUtc
                    use! priceReader = price.ExecuteReaderAsync(cancellationToken)
                    let! foundPrice = priceReader.ReadAsync(cancellationToken)

                    if not foundPrice then
                        do! priceReader.CloseAsync()
                        use block = connection.CreateCommand()
                        block.Transaction <- transaction

                        block.CommandText <-
                            "UPDATE ops.BillingCorrectionWork SET BlockedCode=N'MissingPricing',BlockedDetail=N'Late accepted usage has no complete owner-scoped pricing.' WHERE BillingCorrectionWorkId=@WorkId AND CompletedAtUtc IS NULL;"

                        add block "@WorkId" SqlDbType.UniqueIdentifier workId
                        let! _ = block.ExecuteNonQueryAsync(cancellationToken)
                        do! transaction.CommitAsync(cancellationToken)
                    else
                        let assignmentId = priceReader.GetGuid(0)
                        let planId = priceReader.GetGuid(4)
                        let mappingId = priceReader.GetGuid(6)
                        let billableKind = priceReader.GetInt32(7)
                        let rateId = priceReader.GetGuid(8)
                        let currency = priceReader.GetString(9)
                        let unitName = priceReader.GetString(10)
                        let unitQuantity = priceReader.GetInt64(11)
                        let unitPrice = priceReader.GetInt64(12)
                        let effectiveFrom = priceReader.GetDateTime(13)

                        let effectiveTo =
                            if priceReader.IsDBNull(14) then
                                scope.PeriodToUtc
                            else
                                priceReader.GetDateTime(14)

                        do! priceReader.CloseAsync()
                        let charge = ChargePreviewCalculation.calculateChargeMicros quantity unitPrice unitQuantity

                        use prior = connection.CreateCommand()
                        prior.Transaction <- transaction

                        prior.CommandText <-
                            "SELECT TOP (1) ChargeLedgerEntryId FROM ops.ChargeLedgerEntry WITH(UPDLOCK,HOLDLOCK) WHERE BillingPeriodId=@BillingPeriodId AND FactKind=@FactKind AND BillableUsageKindMappingId=@MappingId AND BillableUsageKind=@BillableKind AND PricingAssignmentId=@AssignmentId AND PricingPlanId=@PlanId AND PricingRateId=@RateId AND CurrencyCode=@Currency AND UnitName=@UnitName AND UnitQuantity=@UnitQuantity AND UnitPriceMicros=@UnitPrice AND EffectiveFromUtc=@EffectiveFrom AND EffectiveToUtc=@EffectiveTo AND (EntryKind=0 OR (EntryKind=1 AND ReasonCode=N'AutomaticLateUsage')) ORDER BY CreatedAtUtc DESC,ChargeLedgerEntryId DESC;"

                        add prior "@BillingPeriodId" SqlDbType.UniqueIdentifier periodId
                        add prior "@FactKind" SqlDbType.Int factKind
                        add prior "@MappingId" SqlDbType.UniqueIdentifier mappingId
                        add prior "@BillableKind" SqlDbType.Int billableKind
                        add prior "@AssignmentId" SqlDbType.UniqueIdentifier assignmentId
                        add prior "@PlanId" SqlDbType.UniqueIdentifier planId
                        add prior "@RateId" SqlDbType.UniqueIdentifier rateId
                        prior.Parameters.Add("@Currency", SqlDbType.VarChar, 3).Value <- currency
                        prior.Parameters.Add("@UnitName", SqlDbType.NVarChar, 64).Value <- unitName
                        add prior "@UnitQuantity" SqlDbType.BigInt unitQuantity
                        add prior "@UnitPrice" SqlDbType.BigInt unitPrice
                        add prior "@EffectiveFrom" SqlDbType.DateTime2 effectiveFrom
                        add prior "@EffectiveTo" SqlDbType.DateTime2 effectiveTo
                        let! priorValue = prior.ExecuteScalarAsync(cancellationToken)
                        let priorId = if isNull priorValue then None else Some(priorValue :?> Guid)

                        let entryId =
                            BillingPeriodRules.deterministicId [ workId.ToString("D")
                                                                 "automatic-late-usage" ]

                        use post = connection.CreateCommand()
                        post.Transaction <- transaction

                        post.CommandText <-
                            "INSERT INTO ops.ChargeLedgerEntry(ChargeLedgerEntryId,BillingPeriodId,EntryKind,PriorChargeLedgerEntryId,BillingCorrectionWorkId,FactKind,BillableUsageKindMappingId,BillableUsageKind,PricingAssignmentId,PricingPlanId,PricingRateId,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc,EffectiveToUtc,Quantity,ChargeMicros,InitiatedByPrincipalId,ReasonCode,ReasonText,CorrelationId) SELECT @EntryId,@BillingPeriodId,1,@PriorId,@WorkId,@FactKind,@MappingId,@BillableKind,@AssignmentId,@PlanId,@RateId,@Currency,@UnitName,@UnitQuantity,@UnitPrice,@EffectiveFrom,@EffectiveTo,@Quantity,@Charge,N'Grace.Operations',N'AutomaticLateUsage',N'Accepted late usage correction.',CONVERT(nvarchar(200),@WorkId) WHERE NOT EXISTS(SELECT 1 FROM ops.ChargeLedgerEntry WITH(UPDLOCK,HOLDLOCK) WHERE BillingCorrectionWorkId=@WorkId); UPDATE ops.BillingCorrectionWork SET CompletedAtUtc=SYSUTCDATETIME(),BlockedCode=NULL,BlockedDetail=NULL WHERE BillingCorrectionWorkId=@WorkId AND CompletedAtUtc IS NULL; UPDATE ops.BillingPeriod SET State=3 WHERE BillingPeriodId=@BillingPeriodId AND State=2;"

                        add post "@EntryId" SqlDbType.UniqueIdentifier entryId
                        add post "@BillingPeriodId" SqlDbType.UniqueIdentifier periodId
                        let priorParameter = post.Parameters.Add("@PriorId", SqlDbType.UniqueIdentifier)

                        priorParameter.Value <-
                            priorId
                            |> Option.map box
                            |> Option.defaultValue DBNull.Value

                        add post "@WorkId" SqlDbType.UniqueIdentifier workId
                        add post "@FactKind" SqlDbType.Int factKind
                        add post "@MappingId" SqlDbType.UniqueIdentifier mappingId
                        add post "@BillableKind" SqlDbType.Int billableKind
                        add post "@AssignmentId" SqlDbType.UniqueIdentifier assignmentId
                        add post "@PlanId" SqlDbType.UniqueIdentifier planId
                        add post "@RateId" SqlDbType.UniqueIdentifier rateId
                        post.Parameters.Add("@Currency", SqlDbType.VarChar, 3).Value <- currency
                        post.Parameters.Add("@UnitName", SqlDbType.NVarChar, 64).Value <- unitName
                        add post "@UnitQuantity" SqlDbType.BigInt unitQuantity
                        add post "@UnitPrice" SqlDbType.BigInt unitPrice
                        add post "@EffectiveFrom" SqlDbType.DateTime2 effectiveFrom
                        add post "@EffectiveTo" SqlDbType.DateTime2 effectiveTo
                        add post "@Quantity" SqlDbType.BigInt quantity
                        add post "@Charge" SqlDbType.BigInt charge
                        let! _ = post.ExecuteNonQueryAsync(cancellationToken)
                        do! transaction.CommitAsync(cancellationToken)
            with
            | ex ->
                do! transaction.RollbackAsync(CancellationToken.None)
                return raise ex
        }

    interface IBillingPeriodService with
        member _.RunAsync(nowUtc, cancellationToken) =
            task {
                if nowUtc.Kind <> DateTimeKind.Utc then
                    invalidArg "nowUtc" "Billing worker time must be UTC."

                let! scopes = materializedScopes nowUtc cancellationToken

                for scope in scopes do
                    let! _ = runScope scope nowUtc cancellationToken
                    ()

                let! pendingWork = pendingCorrectionWork cancellationToken

                for workId in pendingWork do
                    do! processCorrectionWork workId cancellationToken
            }

        member _.RetryCloseAsync(scope, nowUtc, provenance, cancellationToken) =
            task {
                BillingProvenance.validate provenance
                return! runScope scope nowUtc cancellationToken
            }

        member _.RecordAcceptedLateFactAsync(ownerId, organizationId, repositoryId, observedAtUtc, usageFactId, cancellationToken) =
            task {
                let fromUtc, toUtc = BillingPeriodRules.monthContaining observedAtUtc

                let scope: BillingPeriodScope =
                    { OwnerId = ownerId; OrganizationId = organizationId; RepositoryId = repositoryId; PeriodFromUtc = fromUtc; PeriodToUtc = toUtc }

                use connection = new SqlConnection(connectionString)
                do! connection.OpenAsync(cancellationToken)
                use! rawTransaction = connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                use transaction = rawTransaction :?> SqlTransaction

                try
                    do! lockScope connection transaction scope cancellationToken
                    // Existing period routing is assignment-independent, so deleted/stale assignments cannot drop late facts.
                    use enqueue = connection.CreateCommand()
                    enqueue.Transaction <- transaction

                    enqueue.CommandText <-
                        """
DECLARE @PeriodId uniqueidentifier;
SELECT @PeriodId=BillingPeriodId FROM ops.BillingPeriod period WITH(UPDLOCK,HOLDLOCK)
WHERE OwnerId=@OwnerId AND OrganizationId=@OrganizationId AND RepositoryId=@RepositoryId AND PeriodFromUtc=@PeriodFromUtc AND PeriodToUtc=@PeriodToUtc AND State IN(2,3);
IF @PeriodId IS NOT NULL
BEGIN
    INSERT INTO ops.BillingCorrectionWork(BillingCorrectionWorkId,BillingPeriodId,UsageFactId)
    SELECT NEWID(),@PeriodId,@UsageFactId
    WHERE NOT EXISTS(SELECT 1 FROM ops.BillingCorrectionWork WITH(UPDLOCK,HOLDLOCK) WHERE BillingPeriodId=@PeriodId AND UsageFactId=@UsageFactId);
END;
"""

                    add enqueue "@OwnerId" SqlDbType.UniqueIdentifier ownerId
                    add enqueue "@OrganizationId" SqlDbType.UniqueIdentifier organizationId
                    add enqueue "@RepositoryId" SqlDbType.UniqueIdentifier repositoryId
                    add enqueue "@PeriodFromUtc" SqlDbType.DateTime2 fromUtc
                    add enqueue "@PeriodToUtc" SqlDbType.DateTime2 toUtc
                    add enqueue "@UsageFactId" SqlDbType.UniqueIdentifier usageFactId
                    let! _ = enqueue.ExecuteNonQueryAsync(cancellationToken)

                    use resolve = connection.CreateCommand()
                    resolve.Transaction <- transaction

                    resolve.CommandText <-
                        "UPDATE ops.BillingIngestionFailure SET ResolvedAtUtc=SYSUTCDATETIME(),ResolutionDetail=N'Accepted usage fact resolved canonical failure evidence.' WHERE UsageFactId=@UsageFactId AND ResolvedAtUtc IS NULL;"

                    add resolve "@UsageFactId" SqlDbType.UniqueIdentifier usageFactId
                    let! _ = resolve.ExecuteNonQueryAsync(cancellationToken)
                    do! transaction.CommitAsync(cancellationToken)
                with
                | ex ->
                    do! transaction.RollbackAsync(CancellationToken.None)
                    return raise ex
            }

        member _.ApplyManualCorrectionAsync(correction, provenance, cancellationToken) =
            task {
                BillingProvenance.validate provenance
                ManualBillingCorrectionValidation.validatePricingGrain correction
                use connection = new SqlConnection(connectionString)
                do! connection.OpenAsync(cancellationToken)
                use! rawTransaction = connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                use transaction = rawTransaction :?> SqlTransaction

                try
                    use period = connection.CreateCommand()
                    period.Transaction <- transaction

                    period.CommandText <-
                        "SELECT OwnerId,OrganizationId,RepositoryId,PeriodFromUtc,PeriodToUtc FROM ops.BillingPeriod WITH(UPDLOCK,HOLDLOCK) WHERE BillingPeriodId=@BillingPeriodId AND State IN(2,3);"

                    add period "@BillingPeriodId" SqlDbType.UniqueIdentifier correction.BillingPeriodId
                    use! reader = period.ExecuteReaderAsync(cancellationToken)
                    let! found = reader.ReadAsync(cancellationToken)

                    if not found then
                        invalidArg "BillingPeriodId" "Manual corrections require a closed or corrected billing period."

                    let scope: BillingPeriodScope =
                        {
                            OwnerId = reader.GetGuid(0)
                            OrganizationId = reader.GetGuid(1)
                            RepositoryId = reader.GetGuid(2)
                            PeriodFromUtc = reader.GetDateTime(3)
                            PeriodToUtc = reader.GetDateTime(4)
                        }

                    do! reader.CloseAsync()
                    do! lockScope connection transaction scope cancellationToken
                    ManualBillingCorrectionValidation.validateApplicability scope.PeriodFromUtc scope.PeriodToUtc correction
                    do! validateManualPricingGrain connection transaction scope correction cancellationToken
                    do! validateManualPrior connection transaction correction cancellationToken
                    let entryId = ManualBillingCorrectionIdentity.entryId correction provenance.CorrelationId
                    use insert = connection.CreateCommand()
                    insert.Transaction <- transaction

                    insert.CommandText <-
                        """
INSERT INTO ops.ChargeLedgerEntry(ChargeLedgerEntryId,BillingPeriodId,EntryKind,PriorChargeLedgerEntryId,FactKind,BillableUsageKindMappingId,BillableUsageKind,PricingAssignmentId,PricingPlanId,PricingRateId,CurrencyCode,UnitName,UnitQuantity,UnitPriceMicros,EffectiveFromUtc,EffectiveToUtc,Quantity,ChargeMicros,InitiatedByPrincipalId,ReasonCode,ReasonText,CorrelationId)
VALUES(@Id,@BillingPeriodId,@EntryKind,@PriorId,@FactKind,@MappingId,@BillableKind,@AssignmentId,@PlanId,@RateId,@Currency,@UnitName,@UnitQuantity,@UnitPrice,@EffectiveFrom,@EffectiveTo,@Quantity,@Charge,@Principal,@ReasonCode,@ReasonText,@Correlation);
UPDATE ops.BillingPeriod SET State=3 WHERE BillingPeriodId=@BillingPeriodId AND State=2;
"""

                    add insert "@Id" SqlDbType.UniqueIdentifier entryId
                    add insert "@BillingPeriodId" SqlDbType.UniqueIdentifier correction.BillingPeriodId
                    add insert "@EntryKind" SqlDbType.Int (int correction.EntryKind)
                    let prior = insert.Parameters.Add("@PriorId", SqlDbType.UniqueIdentifier)

                    prior.Value <-
                        correction.PriorChargeLedgerEntryId
                        |> Option.map box
                        |> Option.defaultValue DBNull.Value

                    add insert "@FactKind" SqlDbType.Int correction.FactKind
                    add insert "@MappingId" SqlDbType.UniqueIdentifier correction.BillableUsageKindMappingId
                    add insert "@BillableKind" SqlDbType.Int correction.BillableUsageKind
                    add insert "@AssignmentId" SqlDbType.UniqueIdentifier correction.PricingAssignmentId
                    add insert "@PlanId" SqlDbType.UniqueIdentifier correction.PricingPlanId
                    add insert "@RateId" SqlDbType.UniqueIdentifier correction.PricingRateId
                    add insert "@Currency" SqlDbType.VarChar correction.CurrencyCode
                    add insert "@UnitName" SqlDbType.NVarChar correction.UnitName
                    add insert "@UnitQuantity" SqlDbType.BigInt correction.UnitQuantity
                    add insert "@UnitPrice" SqlDbType.BigInt correction.UnitPriceMicros
                    add insert "@EffectiveFrom" SqlDbType.DateTime2 correction.EffectiveFromUtc
                    add insert "@EffectiveTo" SqlDbType.DateTime2 correction.EffectiveToUtc
                    add insert "@Quantity" SqlDbType.BigInt correction.QuantityDelta
                    add insert "@Charge" SqlDbType.BigInt correction.ChargeMicrosDelta
                    add insert "@Principal" SqlDbType.NVarChar provenance.InitiatedByPrincipalId
                    add insert "@ReasonCode" SqlDbType.NVarChar provenance.ReasonCode
                    add insert "@ReasonText" SqlDbType.NVarChar provenance.ReasonText
                    add insert "@Correlation" SqlDbType.NVarChar provenance.CorrelationId
                    let! _ = insert.ExecuteNonQueryAsync(cancellationToken)
                    do! transaction.CommitAsync(cancellationToken)
                with
                | ex ->
                    do! transaction.RollbackAsync(CancellationToken.None)
                    return raise ex
            }
